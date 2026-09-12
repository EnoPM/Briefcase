using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Owns immutable copies of the profile. No Unreal pointer or container leaves
/// a patch callback, so protocol work can safely happen on background threads.
/// </summary>
internal sealed class CommunityBalanceStore
{
    private const int MaximumProfileBytes = 64 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _profilePath;
    private StoredProfile? _observed;
    private StoredProfile? _persisted;
    private long? _pendingReloadRevision;
    private long? _pendingSynchronizationRevision;
    private DateTime _synchronizeAfterUtc;
    private long _revision;

    public CommunityBalanceStore(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        _profilePath = Path.GetFullPath(profilePath);
    }

    public string ProfilePath => _profilePath;

    /// <summary>
    /// Loads the profile that the dedicated server will consume on its next
    /// start. This makes remote editing available before any player connects
    /// and triggers the vanilla profile synchronization RPC.
    /// </summary>
    public BalanceProfileEnvelope? LoadPersistedProfile()
    {
        if (!File.Exists(_profilePath)) return null;

        var file = new FileInfo(_profilePath);
        if (file.Length is <= 0 or > MaximumProfileBytes)
            throw new InvalidDataException(
                $"The persisted profile must contain between 1 and " +
                $"{MaximumProfileBytes:N0} bytes.");

        var utf8 = File.ReadAllBytes(_profilePath);
        if (utf8.Length is <= 0 or > MaximumProfileBytes)
            throw new InvalidDataException(
                "The persisted profile changed to an invalid size while it was read.");

        var json = new UTF8Encoding(false, true).GetString(utf8);
        using (JsonDocument.Parse(utf8)) { }

        var compressed = Compress(utf8);
        var contentHash = Convert.ToHexString(SHA256.HashData(utf8));
        const string gameHash = "pending-server-restart";
        var profile = new StoredProfile(
            compressed,
            utf8.Length,
            gameHash,
            new BalanceProfileEnvelope(
                json, contentHash, gameHash,
                compressed.Length, utf8.Length, true));

        lock (_gate)
        {
            _persisted = profile;
            _revision++;
            return profile.Envelope;
        }
    }

    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

    public void Observe(byte[] compressed, int uncompressedBytes, string gameHash)
    {
        var profile = Decode(compressed, uncompressedBytes, gameHash, isStaged: false);
        lock (_gate)
        {
            var replacesPersisted = _persisted is not null &&
                                    _persisted.Envelope.ContentSha256.Equals(
                                        profile.Envelope.ContentSha256,
                                        StringComparison.Ordinal);
            var changed = _observed is null ||
                          !_observed.Envelope.ContentSha256.Equals(
                              profile.Envelope.ContentSha256, StringComparison.Ordinal) ||
                          !_observed.GameHash.Equals(profile.GameHash, StringComparison.Ordinal);
            _observed = profile;
            if (replacesPersisted) _persisted = null;
            // Asking the game for the same profile is a refresh, not a new
            // revision. This keeps an editor snapshot valid while the client
            // refreshes its view before applying a change.
            if (changed && !replacesPersisted) _revision++;
        }
    }

    public BalanceProfileEnvelope? Snapshot()
    {
        lock (_gate) return (_persisted ?? _observed)?.Envelope;
    }

    public BalanceProfileEnvelope Stage(
        string json, string? gameHash, long? expectedRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var utf8 = new UTF8Encoding(false, true).GetBytes(json);
        if (utf8.Length > MaximumProfileBytes)
            throw new InvalidDataException("The profile exceeds the 64 MiB limit.");
        using (JsonDocument.Parse(utf8)) { }

        var compressed = Compress(utf8);
        var contentHash = Convert.ToHexString(SHA256.HashData(utf8));
        // The 40-character hash sent by Deceive Inc. is produced by the game
        // while it loads the profile. It is not a plain SHA-1 of either the JSON
        // or the zlib payload. Reusing the old value with edited JSON makes the
        // receiving client reject synchronization and leave the server.
        //
        // Deceive Inc. reloads this file during the next dedicated-server
        // startup and calculates its own valid transport hash.
        const string effectiveGameHash = "pending-server-restart";
        var profile = new StoredProfile(
            compressed,
            utf8.Length,
            effectiveGameHash,
            new BalanceProfileEnvelope(
                json, contentHash, effectiveGameHash,
                compressed.Length, utf8.Length, true));

        lock (_gate)
        {
            if (expectedRevision is not null && expectedRevision != _revision)
                throw new InvalidOperationException(
                    $"The profile changed: expected revision {expectedRevision}, current revision {_revision}.");
            PersistAtomically(json);
            _persisted = profile;
            _revision++;
            _pendingReloadRevision = null;
            _pendingSynchronizationRevision = null;
            return profile.Envelope;
        }
    }

    public bool TryTakeReloadRequest(out long revision)
    {
        lock (_gate)
        {
            if (_pendingReloadRevision is not { } pending)
            {
                revision = 0;
                return false;
            }
            _pendingReloadRevision = null;
            revision = pending;
            return true;
        }
    }

    public void ScheduleSynchronization(long revision)
    {
        lock (_gate)
        {
            _pendingSynchronizationRevision = revision;
            // The native loader is synchronous today, but separating the RPC
            // onto a later game tick also supports an asynchronous loader.
            _synchronizeAfterUtc = DateTime.UtcNow.AddMilliseconds(500);
        }
    }

    public void CancelSynchronization(long revision)
    {
        lock (_gate)
        {
            if (_pendingSynchronizationRevision == revision)
                _pendingSynchronizationRevision = null;
        }
    }

    public bool TryTakeSynchronizationRequest(out long revision)
    {
        lock (_gate)
        {
            if (_pendingSynchronizationRevision is not { } pending ||
                DateTime.UtcNow < _synchronizeAfterUtc)
            {
                revision = 0;
                return false;
            }

            _pendingSynchronizationRevision = null;
            revision = pending;
            return true;
        }
    }

    public void ClearStaged(long? expectedRevision)
    {
        lock (_gate)
        {
            if (expectedRevision is not null && expectedRevision != _revision)
                throw new InvalidOperationException(
                    $"The profile changed: expected revision {expectedRevision}, current revision {_revision}.");
            _persisted = null;
            _pendingReloadRevision = null;
            _pendingSynchronizationRevision = null;
            _revision++;
        }
    }

    private void PersistAtomically(string json)
    {
        var directory = Path.GetDirectoryName(_profilePath) ??
                        throw new InvalidOperationException(
                            "The community balance profile path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = _profilePath + ".briefcase.tmp";
        var backupPath = _profilePath + ".briefcase.bak";
        try
        {
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false, true));
            if (File.Exists(_profilePath))
            {
                File.Copy(_profilePath, backupPath, overwrite: true);
                File.Move(temporaryPath, _profilePath, overwrite: true);
            }
            else
            {
                File.Move(temporaryPath, _profilePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static StoredProfile Decode(
        byte[] compressed, int expectedBytes, string gameHash, bool isStaged)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (compressed.Length == 0 || expectedBytes is <= 0 or > MaximumProfileBytes)
            throw new InvalidDataException("The observed community profile has invalid sizes.");

        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedBytes);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = zlib.Read(buffer);
            if (read == 0) break;
            if (output.Length + read > MaximumProfileBytes)
                throw new InvalidDataException("The observed profile exceeds 64 MiB.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != expectedBytes)
            throw new InvalidDataException(
                $"Expected {expectedBytes:N0} bytes; decoded {output.Length:N0}.");

        var utf8 = output.ToArray();
        using (JsonDocument.Parse(utf8)) { }
        var json = new UTF8Encoding(false, true).GetString(utf8);
        var contentHash = Convert.ToHexString(SHA256.HashData(utf8));
        return new StoredProfile(
            compressed.ToArray(), expectedBytes, gameHash,
            new BalanceProfileEnvelope(
                json, contentHash, gameHash,
                compressed.Length, expectedBytes, isStaged));
    }

    private static byte[] Compress(ReadOnlySpan<byte> utf8)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(utf8);
        return output.ToArray();
    }

    internal sealed record StoredProfile(
        byte[] Compressed,
        int UncompressedBytes,
        string GameHash,
        BalanceProfileEnvelope Envelope);
}
