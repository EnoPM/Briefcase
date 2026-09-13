using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerAdminControl.Protocol;

/// <summary>
/// Wire contract shared as source by the client and server mods. Keeping this
/// contract in source avoids shipping a dependency DLL beside each mod.
/// </summary>
public static class ServerAdminProtocol
{
    public const uint Version = 8;
    public const int MaximumFrameBytes = 64 * 1024 * 1024;
    // Briefcase.PlayerCap raises the Solo and Duo game ceilings to the Trio
    // ceiling. Both administration endpoints use this shared limit so the UI
    // and server-side validation cannot drift apart.
    public const int MaximumPlayerCount = 12;

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
            throw new InvalidDataException(
                $"Invalid protocol frame length: {FormatCount(length)} bytes.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public static async ValueTask WriteAsync<T>(
        Stream stream, T message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (payload.Length > MaximumFrameBytes)
            throw new InvalidDataException(
                $"Protocol frame exceeds {FormatCount(MaximumFrameBytes)} bytes.");

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

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
                throw new EndOfStreamException("The protocol frame header is incomplete.");
            }
            read += count;
        }
        return true;
    }
}

public static class ServerAdminOperations
{
    public const string Status = "status";
    public const string GetProfile = "get-profile";
    public const string StageProfile = "stage-profile";
    public const string ClearStagedProfile = "clear-staged-profile";
    public const string GetMods = "get-mods";
    public const string RefreshMods = "refresh-mods";
    public const string SetModEnabled = "set-mod-enabled";
    public const string LoadMod = "load-mod";
    public const string ReloadMod = "reload-mod";
    public const string UnloadMod = "unload-mod";
    public const string SetModConfiguration = "set-mod-configuration";
    public const string SetModConfigurationBatch = "set-mod-configuration-batch";
    public const string GetServerConfiguration = "get-server-configuration";
    public const string UpdateServerConfiguration = "update-server-configuration";
    public const string RestartServer = "restart-server";
    public const string GetPlayers = "get-players";
    public const string KickPlayer = "kick-player";
    public const string GetModHandshakes = "get-mod-handshakes";
}

/// <summary>
/// First administration frame on a new TCP connection. The server answers
/// with a one-use nonce before accepting the actual administration request.
/// </summary>
public sealed record ServerAdminChallengeRequest(
    uint ProtocolVersion,
    string RequestId,
    string RequestedOperation,
    string Channel = BriefcaseChannels.Administration);

public sealed record ServerAdminChallengeResponse(
    uint ProtocolVersion,
    string RequestId,
    bool Success,
    string Status,
    string? Error = null,
    string? ChallengeId = null,
    string? Nonce = null,
    string? Salt = null,
    int Iterations = 0,
    long ExpiresAtUnixMilliseconds = 0,
    string Channel = BriefcaseChannels.Administration);

public sealed record ServerAdminRequest(
    uint ProtocolVersion,
    string RequestId,
    string Operation,
    long? ExpectedRevision = null,
    string? ProfileJson = null,
    string? GameProfileHash = null,
    string? ModFileName = null,
    bool? ModEnabled = null,
    string? ModSettingSection = null,
    string? ModSettingKey = null,
    JsonElement? ModSettingValue = null,
    ServerConfigurationEnvelope? ServerConfiguration = null,
    string? PlayerToken = null,
    string? KickReason = null,
    string Channel = BriefcaseChannels.Administration,
    string? AuthenticationChallengeId = null,
    string? AuthenticationProof = null,
    IReadOnlyList<ServerModConfigurationChange>? ModConfigurationChanges = null);

public sealed record ServerModConfigurationChange(
    string Section,
    string Key,
    JsonElement Value);

public sealed record ServerAdminResponse(
    uint ProtocolVersion,
    string RequestId,
    bool Success,
    string Status,
    long Revision,
    string? Error = null,
    BalanceProfileEnvelope? Profile = null,
    IReadOnlyList<ServerModEnvelope>? Mods = null,
    ServerConfigurationEnvelope? ServerConfiguration = null,
    IReadOnlyList<ServerPlayerEnvelope>? Players = null,
    bool RestartScheduled = false,
    IReadOnlyList<ModHandshakeClientSnapshot>? HandshakeClients = null,
    string Channel = BriefcaseChannels.Administration);

public sealed record ServerPlayerEnvelope(
    string PlayerToken,
    string DisplayName,
    int PlayerId,
    bool IsBot,
    byte PlatformType,
    byte FactionId);

/// <summary>
/// Typed projection of Deceive Inc.'s TripwireServer.ini. The server keeps
/// unknown INI keys intact, so new game settings remain compatible with an
/// older Briefcase client.
/// </summary>
public sealed record ServerConfigurationEnvelope(
    string ServerName,
    string Region,
    string GameMode,
    IReadOnlyList<string> MapRotation,
    string Password,
    string AdminPassword,
    bool Crossplay,
    bool IsPublic,
    int GamePort,
    int QueryPort,
    bool EnableUpnp,
    float AutoShutdownEmptyMinutes,
    bool SandboxMode,
    bool FillWithBots,
    string BotsDifficulty,
    int BotsAmount,
    int MaxPlayers,
    bool RandomizeMap,
    int CivilianHeatPercent,
    int StaffHeatPercent,
    int GuardHeatPercent,
    int TechnicianHeatPercent,
    int VipHeatPercent,
    float ScoldHeatPerSecond,
    float SpyHitHeatDelaySeconds,
    float PassiveHeatGainDelaySeconds,
    float AggroAfterCoverHeatDelaySeconds,
    float HeatDecayDelaySeconds,
    float HeatDecayRate,
    int ProcessId,
    string ExecutablePath,
    string ConfigurationPath);

public sealed record BalanceProfileEnvelope(
    string Json,
    string ContentSha256,
    string GameProfileHash,
    int CompressedBytes,
    int UncompressedBytes,
    bool IsStagedOverride);

public sealed record ServerModEnvelope(
    string FileName,
    string DisplayName,
    string Version,
    string Description,
    bool Enabled,
    bool Loaded,
    string? LastError,
    bool CanManage,
    string? ManagementNote)
{
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> ActiveDependents { get; init; } = [];
    public IReadOnlyList<ServerModConfigurationEntry> Configuration { get; init; } = [];
    public bool CanStop => CanManage && ActiveDependents.Count == 0;
    public string? StopBlockReason => !CanManage
        ? ManagementNote
        : ActiveDependents.Count == 0
            ? null
            : $"Disable these dependent mods first: {string.Join(", ", ActiveDependents)}.";
}

public sealed record ServerModConfigurationEntry(
    string Section,
    string Key,
    string Description,
    string ValueType,
    JsonElement Value,
    JsonElement? Minimum,
    JsonElement? Maximum,
    IReadOnlyList<string> Choices);
