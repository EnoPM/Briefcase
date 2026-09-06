using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Owns the single Briefcase TCP socket and routes bounded JSON frames by
/// channel. Unreal continues to own the UDP socket with the same port number.
/// </summary>
internal sealed class BriefcaseServerEndpoint : IDisposable
{
    private readonly ServerAdminServer _administration;
    private readonly ModHandshakeServer _handshake;
    private readonly ServerAdminAuthenticator _authenticator;
    private readonly Action<string> _warning;
    private readonly CancellationTokenSource _stop = new();
    private readonly TcpListener _listener;
    private readonly Task _acceptLoop;

    public BriefcaseServerEndpoint(
        ServerAdminServer administration,
        ModHandshakeServer handshake,
        string administrationSecret,
        string listenAddress,
        int port,
        Action<string> info,
        Action<string> warning)
    {
        _administration = administration;
        _handshake = handshake;
        _authenticator = new ServerAdminAuthenticator(administrationSecret);
        _warning = warning;
        if (!IPAddress.TryParse(listenAddress?.Trim(), out var address))
            throw new FormatException("The Briefcase listen address must be IPv4 or IPv6.");
        _listener = new TcpListener(address, port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_stop.Token);
        info($"Unified Briefcase endpoint listening on {address}:{port}/TCP " +
             $"(admin v{ServerAdminProtocol.Version}, handshake v{ModHandshakeProtocol.Version}, " +
             $"authentication={(_authenticator.IsConfigured ? "enabled" : "disabled")}).");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item =>
                item is OperationCanceledException or SocketException)) { }
        _stop.Dispose();
        _authenticator.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }
            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using var stream = client.GetStream();
                var payload = await BriefcaseWireProtocol.ReadFrameAsync(stream, timeout.Token);
                if (payload is null) return;
                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("channel", out var channelElement))
                    throw new InvalidDataException("The Briefcase channel is required.");
                var channel = channelElement.GetString();
                var remote = client.Client.RemoteEndPoint as IPEndPoint;

                switch (channel)
                {
                    case BriefcaseChannels.Handshake:
                    {
                        if (payload.Length > ModHandshakeProtocol.MaximumFrameBytes)
                            throw new InvalidDataException("The handshake frame exceeds one MiB.");
                        var hello = JsonSerializer.Deserialize<ModHandshakeClientHello>(
                            payload, ModHandshakeProtocol.JsonOptions) ??
                            throw new InvalidDataException("The handshake hello is empty.");
                        var response = _handshake.Handle(
                            hello, remote?.Address.ToString() ?? "unknown");
                        await ModHandshakeProtocol.WriteAsync(stream, response, timeout.Token);
                        break;
                    }
                    case BriefcaseChannels.Administration:
                    {
                        var challengeRequest = JsonSerializer.Deserialize<ServerAdminChallengeRequest>(
                            payload, ServerAdminProtocol.JsonOptions) ??
                            throw new InvalidDataException("The authentication challenge is empty.");
                        var challengeResponse = _authenticator.CreateChallenge(
                            challengeRequest, out var challenge);
                        await ServerAdminProtocol.WriteAsync(
                            stream, challengeResponse, timeout.Token);
                        if (!challengeResponse.Success || challenge is null) break;

                        var request = await ServerAdminProtocol.ReadAsync<ServerAdminRequest>(
                                          stream, timeout.Token) ??
                                      throw new InvalidDataException(
                                          "The authenticated administration request is empty.");
                        var authenticated = _authenticator.Authenticate(request, challenge);
                        var response = _administration.Handle(request, authenticated);
                        await ServerAdminProtocol.WriteAsync(stream, response, timeout.Token);
                        break;
                    }
                    default:
                        throw new InvalidDataException($"Unknown Briefcase channel '{channel}'.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _warning($"Unified Briefcase connection failed: {exception.Message}");
            }
        }
    }
}
