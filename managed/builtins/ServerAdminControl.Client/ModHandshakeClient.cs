using System.Net.Sockets;
using Briefcase.ModApi;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Client;

/// <summary>
/// Periodically exchanges the local manifest with the public player endpoint.
/// The connection is outbound-only and carries no administrator credentials.
/// </summary>
internal sealed class ModHandshakeClient : IDisposable
{
    private readonly ModContext _context;
    private readonly ModInfo _builtIn;
    private readonly Func<string> _endpoint;
    private readonly Action<string> _info;
    private readonly Action<string> _warning;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(0, 1);
    private readonly Task _loop;
    private ModHandshakeServerHello? _latest;
    private ModHandshakeMod[] _localMods = [];
    private string _status = "Waiting for the first mod handshake.";
    private string? _lastReportedState;

    public ModHandshakeClient(
        ModContext context,
        ModInfo builtIn,
        Func<string> endpoint,
        Action<string> info,
        Action<string> warning)
    {
        _context = context;
        _builtIn = builtIn;
        _endpoint = endpoint;
        _info = info;
        _warning = warning;
        _loop = RunAsync(_stop.Token);
    }

    public string Status => Volatile.Read(ref _status);
    public ModHandshakeServerHello? Latest => Volatile.Read(ref _latest);
    public ModHandshakeMod[] LocalMods => Volatile.Read(ref _localMods);

    public void Refresh()
    {
        if (_refresh.CurrentCount == 0) _refresh.Release();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is OperationCanceledException)) { }
        _refresh.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var succeeded = false;
            try
            {
                var endpoint = _endpoint();
                var (host, port) = ParseEndpoint(endpoint);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, timeout.Token);
                using var stream = client.GetStream();
                var build = _context.GameBuild;
                var localMods = ModHandshakeInventory.Capture(_context, _builtIn, "Client");
                Volatile.Write(ref _localMods, localMods);
                var hello = new ModHandshakeClientHello(
                    ModHandshakeProtocol.Version,
                    Guid.NewGuid().ToString("N"),
                    _instanceId,
                    _context.FrameworkVersion.ToString(3),
                    new ModHandshakeBuild(build.PeTimestamp, build.ImageSize),
                    localMods);
                await ModHandshakeProtocol.WriteAsync(stream, hello, timeout.Token);
                var response = await ModHandshakeProtocol.ReadAsync<ModHandshakeServerHello>(
                    stream, timeout.Token) ??
                    throw new EndOfStreamException("The server ended the handshake without a reply.");
                if (!string.Equals(response.RequestId, hello.RequestId, StringComparison.Ordinal))
                    throw new InvalidDataException("The handshake response RequestId does not match.");
                if (response.ProtocolVersion != ModHandshakeProtocol.Version)
                    throw new InvalidDataException(
                        $"Unsupported server handshake version {response.ProtocolVersion}.");

                Volatile.Write(ref _latest, response);
                Volatile.Write(ref _status, response.Status);
                succeeded = true;
                ReportState(
                    response.Accepted ? $"accepted:{response.ServerInstanceId}" :
                    $"rejected:{response.Status}",
                    response.Accepted
                        ? $"Mod handshake accepted by {host}:{port}; " +
                          $"received {response.Mods.Count} server component(s)."
                        : $"Mod handshake rejected by {host}:{port}: {response.Status}",
                    response.Accepted);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _latest, null);
                Volatile.Write(ref _status, $"Handshake unavailable: {exception.Message}");
                ReportState($"error:{exception.Message}",
                    $"Player mod handshake unavailable: {exception.Message}", false);
            }

            try
            {
                await _refresh.WaitAsync(
                    succeeded ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ReportState(string state, string message, bool informational)
    {
        if (string.Equals(_lastReportedState, state, StringComparison.Ordinal)) return;
        _lastReportedState = state;
        if (informational) _info(message); else _warning(message);
    }

    private static (string Host, int Port) ParseEndpoint(string value)
    {
        var endpoint = value.Trim();
        if (endpoint.Length == 0) throw new FormatException("The handshake endpoint is empty.");
        string host;
        string portText;
        if (endpoint[0] == '[')
        {
            var end = endpoint.IndexOf(']');
            if (end <= 1 || end + 1 >= endpoint.Length || endpoint[end + 1] != ':')
                throw new FormatException("An IPv6 endpoint must use [ADDRESS]:PORT.");
            host = endpoint[1..end];
            portText = endpoint[(end + 2)..];
        }
        else
        {
            var separator = endpoint.LastIndexOf(':');
            if (separator <= 0 || separator == endpoint.Length - 1)
                throw new FormatException("The endpoint must use HOST:PORT.");
            host = endpoint[..separator].Trim();
            portText = endpoint[(separator + 1)..].Trim();
        }
        if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new FormatException("The handshake port must be between 1 and 65535.");
        return (host, port);
    }
}
