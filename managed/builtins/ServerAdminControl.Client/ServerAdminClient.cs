using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Client;

internal static class ServerAdminClient
{
    public static async Task<ServerAdminResponse> SendAsync(
        string endpoint,
        string administrationSecret,
        ServerAdminRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(administrationSecret))
            throw new InvalidOperationException(
                "Enter the server administration password first.");
        var (host, port) = ParseEndpoint(endpoint);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        using var stream = client.GetStream();

        var challengeRequest = new ServerAdminChallengeRequest(
            ServerAdminProtocol.Version, request.RequestId, request.Operation);
        await ServerAdminProtocol.WriteAsync(stream, challengeRequest, cancellationToken);
        var challenge = await ServerAdminProtocol.ReadAsync<ServerAdminChallengeResponse>(
                            stream, cancellationToken) ??
                        throw new EndOfStreamException(
                            "The server closed before issuing an authentication challenge.");
        if (!challenge.Success)
            throw new InvalidOperationException(challenge.Error ?? challenge.Status);
        if (challenge.ProtocolVersion != ServerAdminProtocol.Version ||
            !string.Equals(challenge.Channel, BriefcaseChannels.Administration,
                StringComparison.Ordinal) ||
            !string.Equals(challenge.RequestId, request.RequestId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(challenge.ChallengeId) ||
            string.IsNullOrWhiteSpace(challenge.Nonce) ||
            string.IsNullOrWhiteSpace(challenge.Salt) ||
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >
            challenge.ExpiresAtUnixMilliseconds)
            throw new InvalidDataException(
                "The server returned an invalid or expired authentication challenge.");

        var key = DerivedKeyCache.Get(
            endpoint, administrationSecret, challenge.Salt, challenge.Iterations);
        var proof = ServerAdminAuthentication.CreateProof(
            key,
            request.ProtocolVersion,
            request.RequestId,
            request.Operation,
            challenge.ChallengeId,
            challenge.Nonce);
        var authenticatedRequest = request with
        {
            AuthenticationChallengeId = challenge.ChallengeId,
            AuthenticationProof = proof
        };
        await ServerAdminProtocol.WriteAsync(
            stream, authenticatedRequest, cancellationToken);
        var response = await ServerAdminProtocol.ReadAsync<ServerAdminResponse>(
                           stream, cancellationToken) ??
                       throw new EndOfStreamException(
                           "The server closed without an administration response.");
        if (response.ProtocolVersion != ServerAdminProtocol.Version ||
            !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(response.Channel, BriefcaseChannels.Administration,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The server returned a response for a different request.");
        return response;
    }

    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1 ||
            !int.TryParse(endpoint[(separator + 1)..], out var port) ||
            port is <= 0 or > 65535)
            throw new FormatException("Use an endpoint in HOST:PORT format.");
        var host = endpoint[..separator].Trim();
        if (host.Length == 0) throw new FormatException("The endpoint host is empty.");
        return (host, port);
    }

    private static class DerivedKeyCache
    {
        private static readonly object Gate = new();
        private static string _identity = "";
        private static byte[]? _key;

        public static byte[] Get(
            string endpoint, string secret, string salt, int iterations)
        {
            var secretBytes = Encoding.UTF8.GetBytes(secret);
            var fingerprint = Convert.ToHexString(SHA256.HashData(secretBytes));
            CryptographicOperations.ZeroMemory(secretBytes);
            var identity = $"{endpoint}\n{salt}\n{iterations}\n{fingerprint}";
            lock (Gate)
            {
                if (_key is not null && string.Equals(
                        _identity, identity, StringComparison.Ordinal))
                    return _key;
                if (_key is not null) CryptographicOperations.ZeroMemory(_key);
                _key = ServerAdminAuthentication.DeriveKey(secret, salt, iterations);
                _identity = identity;
                return _key;
            }
        }
    }
}
