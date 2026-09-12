using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace ServerAdminControl.Server;

/// <summary>
/// Captures the server's community profile and hosts the first version of the
/// client/server editing protocol. Unreal calls stay on the game thread; the
/// local protocol endpoint only handles immutable managed copies.
/// </summary>
public sealed class ServerAdminControlServerMod : BriefcaseMod
{
    private static readonly GameBuild SupportedServerBuild =
        new(0x6A966107, 0x05B60000);
    private const ulong LoadAndApplyCommunityProfileRva = 0x0116F540;
    private static ServerAdminControlServerMod? _active;

    private readonly CommunityBalanceStore _store = new(ResolveCommunityProfilePath());
    private readonly ServerConfigurationService _serverConfiguration = new();
    private readonly ServerPlayerRegistry _players = new();
    private ModContext _context;
    private ConfigEntry<bool>? _endpointEnabled;
    private ConfigEntry<string>? _listenAddress;
    private ConfigEntry<int>? _listenPort;
    private ModHandshakeServer? _handshakeServer;
    private BriefcaseServerEndpoint? _serverEndpoint;

    private static string ResolveCommunityProfilePath()
    {
        var executable = Environment.ProcessPath;
        var executableDirectory = executable is null
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
        // DeceiveIncServer-Win64-Shipping.exe lives in DeceiveInc/Binaries/Win64.
        return Path.GetFullPath(Path.Combine(
            executableDirectory, "..", "..", "CommunityBalanceProfile.json"));
    }

    public override ModInfo Info { get; } = new(
        Id: "server-admin-control.server",
        Name: "Server Admin Control Server",
        Author: "EnoPM",
        Version: "0.7.0-dev",
        Description: "Briefcase Core endpoint for vanilla balancing and server mod administration.",
        RequiredCapabilities: BriefcaseAbi.CoreCapability |
                              BriefcaseAbi.UnrealReflectionCapability |
                              BriefcaseAbi.UnrealInvocationCapability |
                              BriefcaseAbi.PatchingCapability |
                              BriefcaseAbi.ModManagementCapability);

    public override void Load(ModContext context)
    {
        _context = context;
        _active = this;
        try
        {
            var persisted = _store.LoadPersistedProfile();
            if (persisted is null)
            {
                context.Warning(
                    $"No community balance profile exists at {_store.ProfilePath}. " +
                    "The editor will become available after the game creates one.");
            }
            else
            {
                context.Info(
                    $"Loaded community balance profile from {_store.ProfilePath} " +
                    $"({persisted.UncompressedBytes:N0} bytes).");
            }
        }
        catch (Exception exception)
        {
            context.Warning(
                $"Could not load the persisted community balance profile: " +
                exception.Message);
        }
        context.Info("ServerAdminControl.Server: binding endpoint configuration.");
        _endpointEnabled = context.Configuration.Bind(
            "Unified endpoint", "Enabled", true,
            "Starts the Briefcase TCP endpoint for players and local administration.");
        _listenAddress = context.Configuration.Bind(
            "Unified endpoint", "Listen address", "0.0.0.0",
            "Network interface used by the public handshake channel.");
        _listenPort = context.Configuration.Bind(
            "Unified endpoint", "Port", 47000,
            "Dedicated TCP port for administration and mod handshakes. Keep it separate from the Unreal game port.",
            new ConfigurationRange<int>(1024, 65535));
        _endpointEnabled.ValueChanged += EndpointSettingChanged;
        _listenAddress.ValueChanged += EndpointSettingChanged;
        _listenPort.ValueChanged += EndpointSettingChanged;
        context.Info("ServerAdminControl.Server: starting unified endpoint.");
        RestartEndpoint();
        context.Info("ServerAdminControl.Server loaded; unified endpoint is ready.");
    }

    public override void Unload()
    {
        _active = null;
        if (_endpointEnabled is not null) _endpointEnabled.ValueChanged -= EndpointSettingChanged;
        if (_listenAddress is not null) _listenAddress.ValueChanged -= EndpointSettingChanged;
        if (_listenPort is not null) _listenPort.ValueChanged -= EndpointSettingChanged;
        _serverEndpoint?.Dispose();
        _serverEndpoint = null;
        _handshakeServer?.Dispose();
        _handshakeServer = null;
        _players.Clear();
    }

    private void EndpointSettingChanged<T>(T _) => RestartEndpoint();

    private void RestartEndpoint()
    {
        _serverEndpoint?.Dispose();
        _serverEndpoint = null;
        _handshakeServer?.Dispose();
        _handshakeServer = null;
        if (_endpointEnabled?.Value != true) return;
        try
        {
            _context.Info("ServerAdminControl.Server: creating handshake service.");
            _handshakeServer = new ModHandshakeServer(_context, Info);
            var administration = new ServerAdminServer(
                _store,
                _context.Mods,
                _serverConfiguration,
                _players,
                () => _handshakeServer?.SnapshotClients() ?? [],
                _context.Info);
            _context.Info("ServerAdminControl.Server: reading server configuration.");
            var administrationSecret = _serverConfiguration.Snapshot().AdminPassword;
            _context.Info("ServerAdminControl.Server: creating TCP listener.");
            _serverEndpoint = new BriefcaseServerEndpoint(
                administration,
                _handshakeServer,
                administrationSecret,
                _listenAddress?.Value ?? "0.0.0.0",
                _listenPort?.Value ?? 47000,
                _context.Info,
                _context.Warning);
        }
        catch (Exception exception)
        {
            _handshakeServer?.Dispose();
            _handshakeServer = null;
            _context.Error($"Could not start the unified Briefcase endpoint: {exception.Message}");
        }
    }

    // This client RPC is invoked by the authoritative server before Unreal
    // serializes it for the owning connection. We copy its containers while
    // they are valid and never expose native addresses to worker threads.
    [UnrealPrefixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.Client_ReceiveCommunityBalanceProfile))]
    private static void CaptureOutgoingProfile(
        byte[] CompressedProfilePayload,
        int UncompressedProfileBytes,
        string ExpectedProfileHash)
    {
        var mod = _active;
        if (mod is null) return;
        try
        {
            mod._store.Observe(
                CompressedProfilePayload, UncompressedProfileBytes, ExpectedProfileHash);
            mod._context.Info(
                $"Captured outgoing community profile: {CompressedProfilePayload.Length:N0} -> " +
                $"{UncompressedProfileBytes:N0} bytes, revision {mod._store.Revision}.");
        }
        catch (Exception exception)
        {
            mod._context.Warning($"Could not capture outgoing community profile: {exception.Message}");
        }
    }

    // Every profile request identifies a live player controller. Balance edits
    // are deliberately not injected here: Deceive Inc. creates a game-specific
    // profile hash while loading CommunityBalanceProfile.json. Reusing an old
    // hash with new bytes makes the client reject synchronization and disconnect.
    [UnrealPostfixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.Server_RequestCommunityBalanceProfile))]
    private static void ObserveProfileRequester(DeceiveIncPlayerController __instance)
    {
        var mod = _active;
        if (mod is null) return;

        // Every connecting client requests this profile. Observe the controller
        // even when no balancing override is staged; returning before this step
        // was the reason lobby players were missing from administration.
        mod._players.ObserveAndPump(
            mod._context.Unreal, __instance, mod._context.Info, mod._context.Warning);
    }

    // PlayerController ticks are already dispatched on Unreal's game thread.
    // They provide a safe place to read PlayerState fields and execute queued
    // administration RPCs requested by the TCP worker thread.
    [UnrealPostfixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.ReceiveTick))]
    private static void ObserveConnectedPlayer(DeceiveIncPlayerController __instance)
    {
        var mod = _active;
        if (mod is null) return;
        mod._players.ObserveAndPump(
            mod._context.Unreal, __instance, mod._context.Info, mod._context.Warning);
        mod.ApplyPendingCommunityProfile();
    }

    // K2_PostLogin is emitted once by the authoritative GameMode for every real
    // connection, including the pregame lobby before a Spy pawn exists.
    [UnrealPostfixPatch(
        typeof(DeceiveIncGameModeBase),
        nameof(DeceiveIncGameModeBase.K2_PostLogin))]
    private static void RegisterConnectedPlayer(UnrealObjectReference NewPlayer)
    {
        var mod = _active;
        if (mod is null) return;
        mod._players.Register(
            mod._context.Unreal, NewPlayer, mod._context.Info, mod._context.Warning);
        mod.ApplyPendingCommunityProfile();
    }

    // GameMode only exists on the authoritative server. Its tick supplies the
    // recurring game-thread callback used to refresh players and execute kicks.
    [UnrealPostfixPatch(
        typeof(DeceiveIncGameModeBase),
        nameof(DeceiveIncGameModeBase.ReceiveTick))]
    private static void PumpPlayerAdministration()
    {
        var mod = _active;
        if (mod is null) return;
        mod.ApplyPendingCommunityProfile();
        mod._players.Pump(
            mod._context.Unreal, mod._context.Info, mod._context.Warning);
    }

    private void ApplyPendingCommunityProfile()
    {
        if (_store.TryTakeReloadRequest(out var reloadRevision))
        {
            // Record phase two before entering game code. Even if Unreal yields
            // or completes the managed callback unusually, a later tick still
            // performs the vanilla client synchronization.
            _store.ScheduleSynchronization(reloadRevision);
            try
            {
                var actualBuild = _context.GameBuild;
                if (actualBuild != SupportedServerBuild)
                    throw new InvalidOperationException(
                        $"Live balancing reload is unavailable for server build " +
                        $"{actualBuild.PeTimestamp:X8}-{actualBuild.ImageSize:X8}; " +
                        $"expected {SupportedServerBuild.PeTimestamp:X8}-" +
                        $"{SupportedServerBuild.ImageSize:X8}.");

                var subsystem = _context.Unreal
                    .FindObjects(DIBalancingManagerSubsytem.StaticClass)
                    .FirstOrDefault(candidate =>
                        !candidate.Name.Contains("Default__", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        "The live DIBalancingManagerSubsytem instance was not found.");

                if (!_context.Unreal.InvokeNativeBoolean(
                        subsystem, SupportedServerBuild,
                        LoadAndApplyCommunityProfileRva))
                    throw new InvalidOperationException(
                        "Deceive Inc. rejected CommunityBalanceProfile.json.");
            }
            catch (Exception exception)
            {
                _store.CancelSynchronization(reloadRevision);
                _context.Error(
                    $"Live community balance reload failed: {exception.Message} " +
                    "The saved profile will be applied on the next server restart.");
            }
        }

        if (_store.TryTakeSynchronizationRequest(out var synchronizedRevision))
        {
            var recipients = _players.BroadcastCommunityBalanceProfile(
                _context.Unreal, _context.Warning);
            _context.Info(
                $"Applied community balance revision {synchronizedRevision} live and requested " +
                $"vanilla profile synchronization for {recipients} client(s).");
        }
    }

    [UnrealPostfixPatch(
        typeof(DeceiveIncGameModeBase),
        nameof(DeceiveIncGameModeBase.K2_OnLogout))]
    private static void ForgetDisconnectedPlayer(UnrealObjectReference ExitingController)
    {
        _active?._players.Remove(ExitingController);
    }
}
