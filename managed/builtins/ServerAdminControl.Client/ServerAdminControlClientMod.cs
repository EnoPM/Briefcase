using Briefcase.ClientModApi;
using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using ServerAdminControl.Protocol;
using System.Globalization;
using System.Text.Json;

namespace ServerAdminControl.Client;

/// <summary>
/// Displays and edits the server-admin-control profile. The game RPC remains
/// the source of truth for what the client applies; the local TCP protocol only
/// stages managed JSON on the dedicated server.
/// </summary>
public sealed partial class ServerAdminControlClientMod : BriefcaseMod
{
    private static readonly (string Value, string Label)[] Regions =
    [
        ("", "Auto-detect (closest)"),
        ("us-east", "us-east"),
        ("us-central", "us-central"),
        ("us-west", "us-west"),
        ("eu", "eu"),
        ("oce", "oce"),
        ("br", "br"),
        ("asia", "asia"),
        ("me", "me")
    ];
    private static readonly (string Code, string Label)[] Maps =
    [
        ("DI_Hardsell", "Hard Sell"),
        ("DI_SR", "Silver Reef"),
        ("DI_DS", "Diamond Spire"),
        ("DI_FS", "Fragrant Shore"),
        ("DI_SE", "Sound Eclipse"),
        ("DI_FSN", "Fragrant Shore (Night)"),
        ("DI_HSD", "Hard Sell (Morning)")
    ];
    private static ServerAdminControlClientMod? _active;

    private ModContext _context;
    private IDisposable? _panel;
    private ConfigEntry<string>? _endpoint;
    private ConfigEntry<string>? _administrationSecret;
    private CancellationTokenSource? _networkLifetime;
    private Task? _networkTask;
    private ModHandshakeClient? _handshake;
    private string _filter = "";
    private string _protocolStatus = "The server protocol has not been queried yet.";
    private string _selectedCategory = "";
    private long _serverRevision = -1;
    private long _nextProfileRequest;
    private int _requestInProgress;
    private int _networkBusy;
    private int _dirty;
    private int _requestProfileAfterStage;
    private int _receivedGameProfile;
    private int _refreshWorkspaceAfterLoad;
    private ServerModEnvelope[] _serverMods = [];
    private readonly Dictionary<string, int> _serverModIntegerDrafts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _serverModFloatDrafts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _serverModDoubleDrafts =
        new(StringComparer.Ordinal);
    private ServerPlayerEnvelope[] _serverPlayers = [];
    private ModHandshakeClientSnapshot[] _handshakeClients = [];
    private ServerConfigurationEnvelope? _serverConfiguration;
    private ServerPage _selectedServerPage;
    private int _serverConfigurationDirty;
    private bool _confirmRestart;
    private bool _showServerPassword;
    private bool _showAuthenticationSecret;
    private string _kickReason = "Removed by the server administrator.";
    private string? _pendingKickToken;
    private string? _pendingReturnReason;
    private long _nextReturnReasonDisplayAttempt;
    private long _nextPlayerRefresh;
    private long _nextHandshakeRefresh;

    public override ModInfo Info { get; } = new(
        Id: "server-admin-control.client",
        Name: "Server Admin Control",
        Author: "EnoPM",
        Version: "0.7.0-dev",
        Description: "Briefcase Core view for vanilla balancing and server mod administration.",
        RequiredCapabilities: BriefcaseAbi.CoreCapability |
                              BriefcaseAbi.UnrealReflectionCapability |
                              BriefcaseAbi.UnrealInvocationCapability |
                              BriefcaseAbi.RenderingCapability |
                              BriefcaseAbi.PatchingCapability |
                              BriefcaseAbi.ModManagementCapability)
    {
        ShowConfigurationTab = false
    };

    public override void Load(ModContext context)
    {
        _context = context;
        _active = this;
        _networkLifetime = new CancellationTokenSource();
        _endpoint = context.Configuration.Bind(
            "Briefcase server", "Endpoint", "127.0.0.1:47000",
            "Dedicated Briefcase administration and mod-handshake endpoint in HOST:PORT format.");
        _administrationSecret = context.Configuration.Bind(
            "Briefcase server", "Administration password", "",
            "Must match AdminPassword in the dedicated server's TripwireServer.ini.",
            secret: true);
        _handshake = new ModHandshakeClient(
            context, Info,
            () => _endpoint?.Value ?? "127.0.0.1:47000",
            context.Info, context.Warning);
        CommunityBalanceState.Reset();
        _panel = context.Ui().RegisterServerPanel("Administration", BuildComponentPanel());
        // Network work starts from the first patched tick, after Load has
        // returned and every hook is attached. This keeps initialization and
        // hot reload deterministic even when the profile is large.
        Interlocked.Exchange(ref _refreshWorkspaceAfterLoad, 1);
        context.Info(
            "ServerAdminControl.Client loaded; requesting server state.");
    }

    public override void Unload()
    {
        _active = null;
        Interlocked.Exchange(ref _pendingReturnReason, null);
        _handshake?.Dispose();
        _handshake = null;
        _networkLifetime?.Cancel();
        try { _networkTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException exception) when (
            exception.InnerExceptions.All(item => item is OperationCanceledException)) { }
        _networkLifetime?.Dispose();
        _networkLifetime = null;
        _panel?.Dispose();
        _panel = null;
        CommunityBalanceState.Reset();
    }

    private ServerConfigurationEnvelope UpdateServerConfiguration(
        ServerConfigurationEnvelope configuration)
    {
        Volatile.Write(ref _serverConfiguration, configuration);
        Interlocked.Exchange(ref _serverConfigurationDirty, 1);
        _confirmRestart = false;
        return configuration;
    }

    private static string FormatPlatform(byte platformType) =>
        platformType == 0 ? "Unknown" : $"Type {platformType}";

    private void RefreshFromProtocol()
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.GetProfile),
            publishProfile: true,
            requestGameProfile: false,
            publishConfiguration: true);
    }

    private void RequestServerConfiguration()
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.GetServerConfiguration),
            publishProfile: false,
            requestGameProfile: false,
            publishConfiguration: true);
    }

    private void SaveServerConfiguration(ServerConfigurationEnvelope configuration)
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.UpdateServerConfiguration,
                ServerConfiguration: configuration),
            publishProfile: false,
            requestGameProfile: false,
            publishConfiguration: true);
    }

    private void RestartServer()
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.RestartServer),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void RequestServerMods(bool refreshDirectory)
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                refreshDirectory
                    ? ServerAdminOperations.RefreshMods
                    : ServerAdminOperations.GetMods),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void RequestPlayers()
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.GetPlayers),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void RequestModHandshakes()
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.GetModHandshakes),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void SendKickPlayer(string playerToken, string reason)
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.KickPlayer,
                PlayerToken: playerToken,
                KickReason: reason),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void SendModCommand(string operation, string fileName, bool? enabled = null)
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                operation,
                ModFileName: fileName,
                ModEnabled: enabled),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void SendModSetting(
        string fileName,
        string section,
        string key,
        JsonElement value)
    {
        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.SetModConfiguration,
                ModFileName: fileName,
                ModSettingSection: section,
                ModSettingKey: key,
                ModSettingValue: value),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void RequestGameProfile()
    {
        Interlocked.Exchange(ref _receivedGameProfile, 0);
        Interlocked.Exchange(ref _requestProfileAfterStage, 1);
        CommunityBalanceState.WaitingForServer();
    }

    private void StageChanges(CommunityBalanceSnapshot snapshot)
    {
        string json;
        try { json = snapshot.SerializeWithEdits(); }
        catch (Exception exception)
        {
            Volatile.Write(ref _protocolStatus, $"Could not serialize edits: {exception.Message}");
            return;
        }

        StartRequest(
            new ServerAdminRequest(
                ServerAdminProtocol.Version,
                Guid.NewGuid().ToString("N"),
                ServerAdminOperations.StageProfile,
                _serverRevision < 0 ? null : _serverRevision,
                json,
                snapshot.Hash),
            publishProfile: false,
            requestGameProfile: false);
    }

    private void StartRequest(
        ServerAdminRequest request,
        bool publishProfile,
        bool requestGameProfile,
        bool publishConfiguration = false)
    {
        var lifetime = _networkLifetime;
        var endpoint = _endpoint?.Value;
        var administrationSecret = _administrationSecret?.Value;
        if (lifetime is null || string.IsNullOrWhiteSpace(endpoint) ||
            Interlocked.CompareExchange(ref _networkBusy, 1, 0) != 0)
            return;

        if (string.IsNullOrEmpty(administrationSecret))
        {
            Interlocked.Exchange(ref _networkBusy, 0);
            Volatile.Write(ref _protocolStatus,
                "Enter the administration password in the Server page.");
            return;
        }

        Volatile.Write(ref _protocolStatus, $"Sending {request.Operation} to {endpoint}...");
        _networkTask = Task.Run(async () =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                var response = await ServerAdminClient.SendAsync(
                    endpoint, administrationSecret, request, timeout.Token);
                _serverRevision = response.Revision;
                if (response.Mods is not null)
                    Volatile.Write(ref _serverMods, response.Mods.ToArray());
                if (response.Players is not null)
                    Volatile.Write(ref _serverPlayers, response.Players.ToArray());
                if (response.HandshakeClients is not null)
                    Volatile.Write(
                        ref _handshakeClients, response.HandshakeClients.ToArray());
                if (!response.Success)
                {
                    Volatile.Write(ref _protocolStatus, response.Error ?? response.Status);
                    _context.Warning(
                        $"Server administration protocol rejected {request.Operation}: " +
                        $"{response.Error ?? response.Status}");
                    return;
                }

                if (publishProfile && response.Profile is not null)
                {
                    var profile = response.Profile;
                    var parsed = CommunityBalanceParser.ParseJson(
                        profile.Json, profile.GameProfileHash, profile.CompressedBytes);
                    CommunityBalanceState.Publish(parsed);
                    Interlocked.Exchange(ref _dirty, 0);
                    _context.Info(
                        $"Balance editor prepared {parsed.Editor.Categories.Count} categories " +
                        $"and {FormatCount(parsed.OptionCount)} settings from the Briefcase server copy.");
                }
                if (publishConfiguration && response.ServerConfiguration is not null)
                {
                    Volatile.Write(ref _serverConfiguration, response.ServerConfiguration);
                    Interlocked.Exchange(ref _serverConfigurationDirty, 0);
                }
                if (requestGameProfile)
                {
                    Interlocked.Exchange(ref _dirty, 0);
                    Interlocked.Exchange(ref _requestProfileAfterStage, 1);
                }
                Volatile.Write(ref _protocolStatus, response.Status);
                _context.Info(
                    $"Server administration protocol {request.Operation} completed at " +
                    $"revision {response.Revision}: {response.Status}");
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                Volatile.Write(ref _protocolStatus, "The server protocol request timed out.");
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Volatile.Write(ref _protocolStatus,
                    $"Server protocol unavailable: {exception.Message}");
                _context.Warning(
                    $"Server administration protocol {request.Operation} failed: " +
                    exception.Message);
            }
            finally { Interlocked.Exchange(ref _networkBusy, 0); }
        }, lifetime.Token);
    }

    private bool CategoryMatchesFilter(BalanceCategory category)
    {
        if (string.IsNullOrWhiteSpace(_filter)) return true;
        return category.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
               category.Sections.Any(section =>
                   section.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                   section.Settings.Any(SettingMatchesFilter));
    }

    private bool SettingMatchesFilter(BalanceSetting setting) =>
        string.IsNullOrWhiteSpace(_filter) ||
        setting.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        setting.Section.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        setting.Draft.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        (setting.Description?.Contains(
            _filter, StringComparison.OrdinalIgnoreCase) ?? false);

    // The French .NET culture formats thousands with U+202F. That glyph may be
    // absent from a compact UI font atlas, so a renderer may show a question
    // mark. Invariant formatting keeps every separator inside ASCII.
    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private enum ServerPage
    {
        Server,
        Players,
        Balancing,
        Mods,
        Compatibility
    }

    /// <summary>
    /// Captures the localized reason while the RPC parameter buffer still owns
    /// its FText. Briefcase copies it to a managed string before this callback
    /// returns; no Unreal pointer is retained by the mod.
    /// </summary>
    [UnrealPrefixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.ClientReturnToMainMenuWithTextReason))]
    private static void CaptureReturnToMenuReason(UnrealText ReturnReason)
    {
        var mod = _active;
        if (mod is null) return;
        var reason = ReturnReason.Value.Trim();
        if (reason.Length == 0) return;
        Interlocked.Exchange(ref mod._pendingReturnReason, reason);
        mod._nextReturnReasonDisplayAttempt = Environment.TickCount64 + 750;
        mod._context.Info($"Captured return-to-menu reason: {reason}");
    }

    /// <summary>
    /// Waits for a live main-menu widget and an active menu stack. The game's
    /// return RPC does not call DIMenuSubsystem.DisplayMainMenu on this path, so
    /// a DisplayMainMenu postfix would never run after a kick.
    /// </summary>
    private void TryDisplayPendingReturnToMenuReason()
    {
        var reason = Volatile.Read(ref _pendingReturnReason);
        if (string.IsNullOrWhiteSpace(reason) ||
            Environment.TickCount64 < _nextReturnReasonDisplayAttempt) return;
        _nextReturnReasonDisplayAttempt = Environment.TickCount64 + 500;

        var mainMenu = _context.Unreal.FindObjects(DIMainMenuUserWidget.StaticClass)
            .FirstOrDefault(candidate =>
                !candidate.Name.Contains("Default__", StringComparison.Ordinal));
        if (mainMenu is null) return;
        var menu = _context.Unreal.FindObjects(DIMenuSubsystem.StaticClass)
            .FirstOrDefault(candidate =>
                !candidate.Name.Contains("Default__", StringComparison.Ordinal));
        if (menu is null || !menu.HasActiveMenus()) return;

        try
        {
            // EPopupType.OkPopup = 2 in this build's generated metadata dump.
            menu.DisplayPopup(
                reason, "Briefcase.ServerReturnReason", 2);
            Interlocked.CompareExchange(ref _pendingReturnReason, null, reason);
            _context.Info("Displayed the return-to-menu reason popup.");
        }
        catch (Exception exception)
        {
            // Keep the reason and retry after the menu hierarchy advances.
            _context.Warning(
                $"Could not display the return-to-menu reason yet: {exception.Message}");
        }
    }

    [UnrealPrefixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.Client_ReceiveCommunityBalanceProfile))]
    private static void CaptureProfile(
        byte[] CompressedProfilePayload,
        int UncompressedProfileBytes,
        string ExpectedProfileHash)
    {
        var mod = _active;
        if (mod is null) return;
        Interlocked.Exchange(ref mod._requestInProgress, 0);
        try
        {
            var snapshot = CommunityBalanceParser.Parse(
                CompressedProfilePayload,
                UncompressedProfileBytes,
                ExpectedProfileHash);
            CommunityBalanceState.Publish(snapshot);
            Interlocked.Exchange(ref mod._receivedGameProfile, 1);
            Interlocked.Exchange(ref mod._dirty, 0);
            mod._context.Info(
                $"Community profile captured: {snapshot.Editor.Categories.Count} categories, " +
                $"{snapshot.OptionCount} settings, hash={snapshot.Hash}, " +
                $"algorithm={snapshot.HashAlgorithm}.");
        }
        catch (Exception exception)
        {
            CommunityBalanceState.Fail(exception.Message);
            mod._context.Warning(
                $"Community balance profile could not be decoded: {exception.Message}");
        }
    }

    [UnrealPostfixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.ReceiveTick))]
    private static void RequestProfileWhenNeeded(DeceiveIncPlayerController __instance)
    {
        var mod = _active;
        if (mod is null) return;
        mod.TryDisplayPendingReturnToMenuReason();
        if (Interlocked.Exchange(ref mod._refreshWorkspaceAfterLoad, 0) != 0)
            mod.RefreshFromProtocol();
        var forced = Interlocked.Exchange(ref mod._requestProfileAfterStage, 0) != 0;
        // A profile read through the local TCP endpoint is enough for the UI,
        // but it does not prove that this game client applied it. Keep retrying
        // the Unreal RPC until Client_ReceiveCommunityBalanceProfile answers.
        if (!forced && Volatile.Read(ref mod._receivedGameProfile) != 0) return;
        var now = Environment.TickCount64;
        if (!forced && now < mod._nextProfileRequest) return;
        if (Interlocked.Exchange(ref mod._requestInProgress, 1) != 0) return;

        mod._nextProfileRequest = now + 10_000;
        try
        {
            __instance.Server_RequestCommunityBalanceProfile();
            CommunityBalanceState.WaitingForServer();
        }
        catch (Exception exception)
        {
            mod._context.Warning($"Community balance profile request failed: {exception.Message}");
        }
        finally { Interlocked.Exchange(ref mod._requestInProgress, 0); }
    }
}
