using System.Collections.Concurrent;
using Briefcase.ModApi;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Validates player manifests and keeps short-lived compatibility sessions.
/// Socket ownership and channel routing belong to BriefcaseServerEndpoint.
/// </summary>
internal sealed class ModHandshakeServer : IDisposable
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromSeconds(30);
    private readonly ModContext _context;
    private readonly ModInfo _builtIn;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, ModHandshakeClientSnapshot> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    public ModHandshakeServer(ModContext context, ModInfo builtIn)
    {
        _context = context;
        _builtIn = builtIn;
    }

    public ModHandshakeServerHello Handle(
        ModHandshakeClientHello hello, string remoteAddress)
    {
        var error = Validate(hello);
        if (error is null)
        {
            _clients[hello.ClientInstanceId] = new ModHandshakeClientSnapshot(
                hello.ClientInstanceId,
                remoteAddress,
                DateTimeOffset.UtcNow,
                hello.FrameworkVersion,
                hello.GameBuild,
                hello.Mods.ToArray());
        }

        var gameBuild = _context.GameBuild;
        return new ModHandshakeServerHello(
            ModHandshakeProtocol.Version,
            hello.RequestId,
            error is null,
            error ?? "Client and server manifests exchanged.",
            _instanceId,
            _context.FrameworkVersion.ToString(3),
            new ModHandshakeBuild(gameBuild.PeTimestamp, gameBuild.ImageSize),
            ModHandshakeInventory.Capture(_context, _builtIn, "Server"),
            [], [], [],
            DownloadSupported: false);
    }

    public ModHandshakeClientSnapshot[] SnapshotClients()
    {
        var cutoff = DateTimeOffset.UtcNow - SessionLifetime;
        foreach (var pair in _clients)
            if (pair.Value.LastSeenUtc < cutoff)
                _clients.TryRemove(pair.Key, out _);
        return _clients.Values
            .Where(client => client.LastSeenUtc >= cutoff)
            .OrderBy(client => client.RemoteAddress, StringComparer.OrdinalIgnoreCase)
            .ThenBy(client => client.ClientInstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose()
    {
        _clients.Clear();
    }

    private static string? Validate(ModHandshakeClientHello hello)
    {
        if (!string.Equals(
                hello.Channel, BriefcaseChannels.Handshake, StringComparison.Ordinal))
            return "The request is not a handshake message.";
        if (hello.ProtocolVersion != ModHandshakeProtocol.Version)
            return $"Unsupported handshake protocol version {hello.ProtocolVersion}.";
        if (string.IsNullOrWhiteSpace(hello.RequestId) || hello.RequestId.Length > 64)
            return "A bounded RequestId is required.";
        if (string.IsNullOrWhiteSpace(hello.ClientInstanceId) ||
            hello.ClientInstanceId.Length > 64)
            return "A bounded ClientInstanceId is required.";
        if (hello.Mods is null || hello.Mods.Count > 512)
            return "The client manifest contains too many mods.";
        foreach (var mod in hello.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Id) || mod.Id.Length > 128 ||
                mod.FileName.Length > 260 || mod.DisplayName.Length > 256 ||
                mod.Version.Length > 64 || mod.Sha256.Length > 64 ||
                mod.Dependencies.Count > 64)
                return "The client manifest contains an invalid mod descriptor.";
        }
        return null;
    }
}
