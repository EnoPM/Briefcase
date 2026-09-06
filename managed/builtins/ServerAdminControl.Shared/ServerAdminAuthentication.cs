using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ServerAdminControl.Protocol;

/// <summary>
/// Cryptographic primitives shared by the administration client and server.
/// PBKDF2 turns the configured password into a fixed-size key. HMAC binds a
/// proof to one server nonce, request ID and operation without transmitting the
/// password itself.
/// </summary>
public static class ServerAdminAuthentication
{
    public const int DefaultIterations = 120_000;
    public const int SaltBytes = 16;
    public const int NonceBytes = 32;
    public const int DerivedKeyBytes = 32;

    public static byte[] DeriveKey(
        string secret, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(
                "The administration password is empty.");
        if (iterations is < 50_000 or > 1_000_000)
            throw new InvalidDataException(
                "The authentication iteration count is outside the accepted range.");

        byte[] salt;
        try { salt = Convert.FromBase64String(saltBase64); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The authentication salt is invalid.", exception);
        }
        if (salt.Length != SaltBytes)
            throw new InvalidDataException("The authentication salt has an invalid length.");

        return Rfc2898DeriveBytes.Pbkdf2(
            secret, salt, iterations, HashAlgorithmName.SHA256, DerivedKeyBytes);
    }

    public static string CreateProof(
        ReadOnlySpan<byte> derivedKey,
        uint protocolVersion,
        string requestId,
        string operation,
        string challengeId,
        string nonce)
    {
        if (derivedKey.Length != DerivedKeyBytes)
            throw new ArgumentException("The derived authentication key is invalid.", nameof(derivedKey));
        var payload = BuildProofPayload(
            protocolVersion, requestId, operation, challengeId, nonce);
        return Convert.ToBase64String(HMACSHA256.HashData(derivedKey, payload));
    }

    public static bool VerifyProof(
        ReadOnlySpan<byte> derivedKey,
        uint protocolVersion,
        string requestId,
        string operation,
        string challengeId,
        string nonce,
        string proofBase64)
    {
        byte[] supplied;
        try { supplied = Convert.FromBase64String(proofBase64); }
        catch (FormatException) { return false; }
        if (supplied.Length != 32) return false;

        var expected = Convert.FromBase64String(CreateProof(
            derivedKey, protocolVersion, requestId, operation, challengeId, nonce));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public static string CreateSalt() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(SaltBytes));

    public static string CreateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceBytes));

    private static byte[] BuildProofPayload(
        uint protocolVersion,
        string requestId,
        string operation,
        string challengeId,
        string nonce)
    {
        using var stream = new MemoryStream();
        Span<byte> version = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(version, protocolVersion);
        stream.Write(version);
        WriteString(stream, requestId);
        WriteString(stream, operation);
        WriteString(stream, challengeId);
        WriteString(stream, nonce);
        return stream.ToArray();
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
