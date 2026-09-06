using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Briefcase.ModApi;

namespace ServerAdminControl.Protocol;

public static class BriefcaseChannels
{
    public const string Administration = "administration";
    public const string Handshake = "handshake";
}

/// <summary>Common framing used by every channel on the unified TCP endpoint.</summary>
public static class BriefcaseWireProtocol
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;

    public static async ValueTask<byte[]?> ReadFrameAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read), cancellationToken);
            if (count == 0)
            {
                if (read == 0) return null;
                throw new EndOfStreamException("The Briefcase frame header is incomplete.");
            }
            read += count;
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException($"Invalid Briefcase frame length: {length} bytes.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}

/// <summary>
/// Public compatibility protocol used by players. It is deliberately separate
/// from the administrator protocol and never carries administrative commands.
/// </summary>
public static class ModHandshakeProtocol
{
    public const uint Version = 1;
    public const int MaximumFrameBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async ValueTask<T?> ReadAsync<T>(
        Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken)) return default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes)
            throw new InvalidDataException($"Invalid handshake frame length: {length} bytes.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException("The handshake frame exceeds one MiB.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async ValueTask<bool> ReadExactlyOrEndAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException("The handshake frame header is incomplete.");
            }
            read += count;
        }
        return true;
    }
}

public sealed record ModHandshakeBuild(uint PeTimestamp, uint ImageSize)
{
    public override string ToString() => $"{PeTimestamp:X8}-{ImageSize:X8}";
}

public sealed record ModHandshakeMod(
    string Id,
    string FileName,
    string DisplayName,
    string Version,
    string Sha256,
    bool Enabled,
    bool Loaded,
    IReadOnlyList<string> Dependencies,
    string Component);

public sealed record ModHandshakeClientHello(
    uint ProtocolVersion,
    string RequestId,
    string ClientInstanceId,
    string FrameworkVersion,
    ModHandshakeBuild GameBuild,
    IReadOnlyList<ModHandshakeMod> Mods,
    string Channel = BriefcaseChannels.Handshake);

public sealed record ModHandshakeServerHello(
    uint ProtocolVersion,
    string RequestId,
    bool Accepted,
    string Status,
    string ServerInstanceId,
    string FrameworkVersion,
    ModHandshakeBuild GameBuild,
    IReadOnlyList<ModHandshakeMod> Mods,
    IReadOnlyList<ModRequirement> RequiredClientMods,
    IReadOnlyList<ModRequirement> OptionalClientMods,
    IReadOnlyList<string> ForbiddenClientMods,
    bool DownloadSupported,
    string Channel = BriefcaseChannels.Handshake);

/// <summary>
/// Reserved now so policy and download support can be added without replacing
/// the version-1 hello envelope.
/// </summary>
public sealed record ModRequirement(string Id, string VersionRange);

public sealed record ModHandshakeClientSnapshot(
    string ClientInstanceId,
    string RemoteAddress,
    DateTimeOffset LastSeenUtc,
    string FrameworkVersion,
    ModHandshakeBuild GameBuild,
    IReadOnlyList<ModHandshakeMod> Mods);

public static class ModHandshakeInventory
{
    public static ModHandshakeMod[] Capture(
        ModContext context, ModInfo builtIn, string component)
    {
        var result = new List<ModHandshakeMod>
        {
            new(
                "briefcase.core", "", "Briefcase Core",
                context.FrameworkVersion.ToString(3), "", true, true, [], component),
            new(
                builtIn.Id, "", builtIn.Name, builtIn.Version, "",
                true, true, builtIn.Dependencies, $"Core/{component}")
        };

        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
        var modsDirectory = Path.Combine(gameDirectory, "Briefcase", "Mods");
        result.AddRange(context.Mods.GetInstalled().Select(mod => new ModHandshakeMod(
            string.IsNullOrWhiteSpace(mod.Id)
                ? Path.GetFileNameWithoutExtension(mod.FileName)
                : mod.Id,
            mod.FileName,
            mod.DisplayName,
            mod.Version,
            HashFile(Path.Combine(modsDirectory, mod.FileName)),
            mod.Enabled,
            mod.Loaded,
            mod.Dependencies,
            component)));
        return result
            .OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }
}
