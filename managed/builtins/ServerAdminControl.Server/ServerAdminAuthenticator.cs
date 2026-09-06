using System.Security.Cryptography;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Owns the server-side derived key for one endpoint lifetime. Derivation is
/// deliberately performed once during startup rather than for every incoming
/// challenge, preventing unauthenticated clients from causing expensive work.
/// </summary>
internal sealed class ServerAdminAuthenticator : IDisposable
{
    internal sealed record Challenge(
        string Id,
        string RequestId,
        string Operation,
        string Nonce,
        long ExpiresAtUnixMilliseconds);

    private readonly byte[]? _derivedKey;
    private readonly string _salt;

    public ServerAdminAuthenticator(string secret)
    {
        _salt = ServerAdminAuthentication.CreateSalt();
        if (!string.IsNullOrEmpty(secret))
            _derivedKey = ServerAdminAuthentication.DeriveKey(
                secret, _salt, ServerAdminAuthentication.DefaultIterations);
    }

    public bool IsConfigured => _derivedKey is not null;

    public ServerAdminChallengeResponse CreateChallenge(
        ServerAdminChallengeRequest request,
        out Challenge? challenge)
    {
        challenge = null;
        if (!string.Equals(
                request.Channel, BriefcaseChannels.Administration,
                StringComparison.Ordinal))
            return Failure(request, "The request is not an administration challenge.");
        if (request.ProtocolVersion != ServerAdminProtocol.Version)
            return Failure(request,
                $"Unsupported protocol version {request.ProtocolVersion}.");
        if (string.IsNullOrWhiteSpace(request.RequestId))
            return Failure(request, "RequestId is required.");
        if (request.RequestId.Length > 128)
            return Failure(request, "RequestId is too long.");
        if (string.IsNullOrWhiteSpace(request.RequestedOperation))
            return Failure(request, "RequestedOperation is required.");
        if (request.RequestedOperation.Length > 128)
            return Failure(request, "RequestedOperation is too long.");
        if (!IsConfigured)
            return Failure(request,
                "Server administration is disabled because AdminPassword is empty.");

        var expires = DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds();
        challenge = new Challenge(
            Guid.NewGuid().ToString("N"),
            request.RequestId,
            request.RequestedOperation,
            ServerAdminAuthentication.CreateNonce(),
            expires);
        return new ServerAdminChallengeResponse(
            ServerAdminProtocol.Version,
            request.RequestId,
            true,
            "Authentication challenge issued.",
            ChallengeId: challenge.Id,
            Nonce: challenge.Nonce,
            Salt: _salt,
            Iterations: ServerAdminAuthentication.DefaultIterations,
            ExpiresAtUnixMilliseconds: expires);
    }

    public bool Authenticate(ServerAdminRequest request, Challenge challenge)
    {
        if (_derivedKey is null ||
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >
            challenge.ExpiresAtUnixMilliseconds ||
            request.ProtocolVersion != ServerAdminProtocol.Version ||
            !string.Equals(request.Channel, BriefcaseChannels.Administration,
                StringComparison.Ordinal) ||
            !string.Equals(request.RequestId, challenge.RequestId,
                StringComparison.Ordinal) ||
            !string.Equals(request.Operation, challenge.Operation,
                StringComparison.Ordinal) ||
            !string.Equals(request.AuthenticationChallengeId, challenge.Id,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.AuthenticationProof))
            return false;

        return ServerAdminAuthentication.VerifyProof(
            _derivedKey,
            request.ProtocolVersion,
            request.RequestId,
            request.Operation,
            challenge.Id,
            challenge.Nonce,
            request.AuthenticationProof);
    }

    public void Dispose()
    {
        if (_derivedKey is not null)
            CryptographicOperations.ZeroMemory(_derivedKey);
    }

    private static ServerAdminChallengeResponse Failure(
        ServerAdminChallengeRequest request, string error) =>
        new(ServerAdminProtocol.Version, request.RequestId ?? "", false,
            "Authentication challenge rejected.", error);
}
