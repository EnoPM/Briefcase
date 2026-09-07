using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using ServerAdminControl.Protocol;
using ImGuiNET;
using System.Globalization;
using System.Text.Json;

namespace ServerAdminControl.Client;

/// <summary>
/// Displays and edits the server-admin-control profile. The game RPC remains
/// the source of truth for what the client applies; the local TCP protocol only
/// stages managed JSON on the dedicated server.
/// </summary>
public sealed class ServerAdminControlClientMod : BriefcaseMod
{
    private static readonly uint Accent = ImGuiColor.Rgba(72, 219, 184);
    private static readonly uint Muted = ImGuiColor.Rgba(145, 154, 170);
    private static readonly uint Warning = ImGuiColor.Rgba(255, 190, 92);
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
            "Briefcase server", "Endpoint", "127.0.0.1:50000",
            "Unified Briefcase TCP endpoint in HOST:PORT format. The game may use the same numeric port over UDP.");
        _administrationSecret = context.Configuration.Bind(
            "Briefcase server", "Administration password", "",
            "Must match AdminPassword in the dedicated server's TripwireServer.ini.",
            secret: true);
        _handshake = new ModHandshakeClient(
            context, Info,
            () => _endpoint?.Value ?? "127.0.0.1:50000",
            context.Info, context.Warning);
        CommunityBalanceState.Reset();
        _panel = context.Configuration.RegisterServerPanel("Administration", DrawPanel);
        // Network work starts from the first patched tick, after Load has
        // returned and every hook is attached. This keeps initialization and
        // hot reload deterministic even when the profile is large.
        Interlocked.Exchange(ref _refreshWorkspaceAfterLoad, 1);
        RequestGameProfile();
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

    private void DrawPanel(ConfigurationPanelContext panel)
    {
        ImGui.TextColored(ToVector4(Accent), "BRIEFCASE SERVER");
        ImGui.TextWrapped(Volatile.Read(ref _protocolStatus));
        ImGui.Separator();

        var available = ImGui.GetContentRegionAvail();
        var navigationWidth = Math.Clamp(available.X * 0.27f, 190, 260);
        var navigationVisible = ImGui.BeginChild(
            "server-navigation",
            new System.Numerics.Vector2(navigationWidth, 0),
            ImGuiNET.ImGuiChildFlags.Borders);
        try
        {
            if (navigationVisible)
            {
                DrawServerNavigationItem("Server", ServerPage.Server);
                DrawServerNavigationItem("Players", ServerPage.Players);
                DrawServerNavigationItem("Balancing", ServerPage.Balancing);
                DrawServerNavigationItem("Mods", ServerPage.Mods);
                DrawServerNavigationItem("Compatibility", ServerPage.Compatibility);
            }
        }
        finally { ImGui.EndChild(); }

        ImGui.SameLine();
        var contentVisible = ImGui.BeginChild(
            "server-content", System.Numerics.Vector2.Zero,
            ImGuiNET.ImGuiChildFlags.Borders);
        try
        {
            if (!contentVisible) return;
            switch (_selectedServerPage)
            {
                case ServerPage.Server:
                    DrawServerConfigurationPanel();
                    break;
                case ServerPage.Players:
                    DrawPlayersPanel();
                    break;
                case ServerPage.Balancing:
                    DrawBalancingPanel();
                    break;
                case ServerPage.Mods:
                    DrawServerModsPanel();
                    break;
                case ServerPage.Compatibility:
                    DrawCompatibilityPanel();
                    break;
            }
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawServerNavigationItem(string label, ServerPage page)
    {
        if (ImGui.Selectable(
                $"{label}##server-{page}",
                _selectedServerPage == page,
                ImGuiNET.ImGuiSelectableFlags.None,
                new System.Numerics.Vector2(0, 46)))
            _selectedServerPage = page;
    }

    private void DrawServerConfigurationPanel()
    {
        ImGui.TextColored(ToVector4(Accent), "SERVER CONFIGURATION");
        ImGui.TextWrapped(
            "These values are stored in TripwireServer.ini. Save writes them atomically; " +
            "restart applies settings that the running server has already read.");
        ImGui.Separator();

        var endpoint = _endpoint?.Value ?? "";
        ImGui.SetNextItemWidth(300);
        if (ImGui.InputText("Briefcase endpoint", ref endpoint, 256) &&
            _endpoint is not null)
        {
            _endpoint.Value = endpoint;
            _handshake?.Refresh();
        }
        var administrationSecret = _administrationSecret?.Value ?? "";
        ImGui.SetNextItemWidth(300);
        var authenticationFlags = _showAuthenticationSecret
            ? ImGuiNET.ImGuiInputTextFlags.None
            : ImGuiNET.ImGuiInputTextFlags.Password;
        if (ImGui.InputText(
                "Administration password", ref administrationSecret, 129,
                authenticationFlags) && _administrationSecret is not null)
            _administrationSecret.Value = administrationSecret;
        ImGui.SameLine();
        ImGui.Checkbox("Show##authentication-password", ref _showAuthenticationSecret);
        ImGui.TextDisabled(
            "Used only to authenticate Briefcase administration requests; it is never sent to the server.");
        var busy = Volatile.Read(ref _networkBusy) != 0;
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh configuration")) RequestServerConfiguration();
        if (busy) ImGui.EndDisabled();

        var configuration = Volatile.Read(ref _serverConfiguration);
        if (configuration is null)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No server configuration has been received yet.");
            return;
        }

        ImGui.Spacing();
        ImGui.SeparatorText("Identity and access");
        var serverName = configuration.ServerName;
        ImGui.SetNextItemWidth(340);
        if (ImGui.InputText("Server name", ref serverName, 65))
            configuration = UpdateServerConfiguration(configuration with { ServerName = serverName });

        var region = configuration.Region;
        if (DrawMappedStringCombo("Region", ref region, Regions))
            configuration = UpdateServerConfiguration(configuration with { Region = region });

        var gameMode = configuration.GameMode;
        if (DrawStringCombo("Game mode", ref gameMode, ["Solo", "Duo", "Trio"]))
        {
            var playerLimit = GetMaximumPlayers(gameMode);
            configuration = UpdateServerConfiguration(configuration with
            {
                GameMode = gameMode,
                MaxPlayers = Math.Min(configuration.MaxPlayers, playerLimit)
            });
        }

        var maximumPlayers = GetMaximumPlayers(configuration.GameMode);
        var players = Math.Clamp(configuration.MaxPlayers, 1, maximumPlayers);
        if (players != configuration.MaxPlayers)
        {
            configuration = UpdateServerConfiguration(
                configuration with { MaxPlayers = players });
            ImGui.TextColored(ToVector4(Warning),
                $"Maximum players was adjusted to the {configuration.GameMode} limit ({maximumPlayers}).");
        }
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt(
                $"Maximum players (1-{maximumPlayers})", ref players, 1, maximumPlayers))
            configuration = UpdateServerConfiguration(configuration with { MaxPlayers = players });

        var password = configuration.Password;
        ImGui.SetNextItemWidth(300);
        var passwordFlags = _showServerPassword
            ? ImGuiNET.ImGuiInputTextFlags.None
            : ImGuiNET.ImGuiInputTextFlags.Password;
        if (ImGui.InputText("Password", ref password, 129, passwordFlags))
            configuration = UpdateServerConfiguration(configuration with { Password = password });
        ImGui.SameLine();
        ImGui.Checkbox("Show##server-password", ref _showServerPassword);

        ImGui.TextDisabled(
            "Rotate AdminPassword locally with scripts/provision_admin_authentication.bat.");

        var isPublic = configuration.IsPublic;
        if (ImGui.Checkbox("Public server", ref isPublic))
            configuration = UpdateServerConfiguration(configuration with { IsPublic = isPublic });
        var crossplay = configuration.Crossplay;
        if (ImGui.Checkbox("Crossplay", ref crossplay))
            configuration = UpdateServerConfiguration(configuration with { Crossplay = crossplay });

        ImGui.SeparatorText("Network");
        var gamePort = configuration.GamePort;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Game port", ref gamePort))
            configuration = UpdateServerConfiguration(configuration with { GamePort = gamePort });
        var queryPort = configuration.QueryPort;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Query port", ref queryPort))
            configuration = UpdateServerConfiguration(configuration with { QueryPort = queryPort });
        var enableUpnp = configuration.EnableUpnp;
        if (ImGui.Checkbox("UPnP port mapping", ref enableUpnp))
            configuration = UpdateServerConfiguration(configuration with { EnableUpnp = enableUpnp });

        ImGui.SeparatorText("Lobby");
        var autoShutdown = configuration.AutoShutdownEmptyMinutes;
        ImGui.SetNextItemWidth(260);
        if (ImGui.SliderFloat(
                "Auto-shutdown when empty (minutes)", ref autoShutdown, 0, 1440, "%.1f"))
            configuration = UpdateServerConfiguration(
                configuration with { AutoShutdownEmptyMinutes = autoShutdown });

        ImGui.SeparatorText("Gameplay");
        var sandbox = configuration.SandboxMode;
        if (ImGui.Checkbox("Sandbox mode (unlock all)", ref sandbox))
            configuration = UpdateServerConfiguration(configuration with { SandboxMode = sandbox });
        var randomizeMap = configuration.RandomizeMap;
        if (ImGui.Checkbox("Randomize map order", ref randomizeMap))
            configuration = UpdateServerConfiguration(configuration with { RandomizeMap = randomizeMap });

        ImGui.TextUnformatted("Map rotation");
        var selectedMaps = new HashSet<string>(
            configuration.MapRotation, StringComparer.OrdinalIgnoreCase);
        foreach (var map in Maps)
        {
            var selected = selectedMaps.Contains(map.Code);
            var lastSelectedMap = selected && selectedMaps.Count == 1;
            if (lastSelectedMap) ImGui.BeginDisabled();
            if (ImGui.Checkbox($"{map.Label}##map-{map.Code}", ref selected))
            {
                if (selected) selectedMaps.Add(map.Code);
                else selectedMaps.Remove(map.Code);
                var orderedRotation = Maps
                    .Where(item => selectedMaps.Contains(item.Code))
                    .Select(item => item.Code)
                    .ToArray();
                configuration = UpdateServerConfiguration(
                    configuration with { MapRotation = orderedRotation });
            }
            if (lastSelectedMap) ImGui.EndDisabled();
            DrawBlockedTooltip(lastSelectedMap,
                "At least one map must remain in the rotation.");
        }

        ImGui.SeparatorText("Bots");
        var fillWithBots = configuration.FillWithBots;
        if (ImGui.Checkbox("Fill with bots", ref fillWithBots))
            configuration = UpdateServerConfiguration(configuration with { FillWithBots = fillWithBots });
        var difficulty = configuration.BotsDifficulty;
        if (DrawStringCombo("Bot difficulty", ref difficulty, ["Easy", "Normal", "Difficult"]))
            configuration = UpdateServerConfiguration(configuration with { BotsDifficulty = difficulty });
        var bots = configuration.BotsAmount;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Number of bots (0 = auto)", ref bots, 0, 8))
            configuration = UpdateServerConfiguration(configuration with { BotsAmount = bots });

        ImGui.SeparatorText("Heat");
        configuration = DrawHeatConfiguration(configuration);

        ImGui.Spacing();
        var dirty = Volatile.Read(ref _serverConfigurationDirty) != 0;
        if (busy || !dirty) ImGui.BeginDisabled();
        if (ImGui.Button("Save configuration")) SaveServerConfiguration(configuration);
        if (busy || !dirty) ImGui.EndDisabled();
        ImGui.SameLine();
        if (busy || dirty) ImGui.BeginDisabled();
        if (ImGui.Button("Restart server")) _confirmRestart = true;
        if (busy || dirty) ImGui.EndDisabled();
        DrawBlockedTooltip(dirty, "Save the pending configuration changes before restarting.");

        if (dirty)
            ImGui.TextColored(ToVector4(Warning), "The configuration has unsaved changes.");

        if (_confirmRestart)
        {
            ImGui.Separator();
            ImGui.TextColored(ToVector4(Warning),
                "The dedicated server will disconnect every player and restart.");
            if (ImGui.Button("Confirm restart"))
            {
                _confirmRestart = false;
                RestartServer();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) _confirmRestart = false;
        }

        ImGui.SeparatorText("Runtime");
        ImGui.TextDisabled($"PID: {configuration.ProcessId}");
        ImGui.TextWrapped($"Executable: {configuration.ExecutablePath}");
        ImGui.TextWrapped($"Configuration: {configuration.ConfigurationPath}");
    }

    private ServerConfigurationEnvelope UpdateServerConfiguration(
        ServerConfigurationEnvelope configuration)
    {
        Volatile.Write(ref _serverConfiguration, configuration);
        Interlocked.Exchange(ref _serverConfigurationDirty, 1);
        _confirmRestart = false;
        return configuration;
    }

    private static bool DrawStringCombo(
        string label, ref string current, IReadOnlyList<string> values)
    {
        var changed = false;
        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo(label, current)) return false;
        try
        {
            foreach (var value in values)
            {
                var selected = value.Equals(current, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(value, selected))
                {
                    current = value;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
        }
        finally { ImGui.EndCombo(); }
        return changed;
    }

    private static bool DrawMappedStringCombo(
        string label,
        ref string current,
        IReadOnlyList<(string Value, string Label)> values)
    {
        var preview = current;
        foreach (var item in values)
        {
            if (!item.Value.Equals(current, StringComparison.OrdinalIgnoreCase)) continue;
            preview = item.Label;
            break;
        }
        var changed = false;
        ImGui.SetNextItemWidth(220);
        if (!ImGui.BeginCombo(label, preview)) return false;
        try
        {
            foreach (var item in values)
            {
                var selected = item.Value.Equals(current, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(item.Label, selected))
                {
                    current = item.Value;
                    changed = true;
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
        }
        finally { ImGui.EndCombo(); }
        return changed;
    }

    private ServerConfigurationEnvelope DrawHeatConfiguration(
        ServerConfigurationEnvelope configuration)
    {
        var civilian = configuration.CivilianHeatPercent;
        if (DrawIntSlider("Civilian heat %", ref civilian, -1, 100))
            configuration = UpdateServerConfiguration(
                configuration with { CivilianHeatPercent = civilian });

        var staff = configuration.StaffHeatPercent;
        if (DrawIntSlider("Staff heat %", ref staff, -1, 100))
            configuration = UpdateServerConfiguration(
                configuration with { StaffHeatPercent = staff });

        var guard = configuration.GuardHeatPercent;
        if (DrawIntSlider("Guard heat %", ref guard, -1, 100))
            configuration = UpdateServerConfiguration(
                configuration with { GuardHeatPercent = guard });

        var technician = configuration.TechnicianHeatPercent;
        if (DrawIntSlider("Technician heat %", ref technician, -1, 100))
            configuration = UpdateServerConfiguration(
                configuration with { TechnicianHeatPercent = technician });

        var vip = configuration.VipHeatPercent;
        if (DrawIntSlider("VIP heat %", ref vip, -1, 100))
            configuration = UpdateServerConfiguration(
                configuration with { VipHeatPercent = vip });

        var scold = configuration.ScoldHeatPerSecond;
        if (DrawFloatSlider("Scold heat per second", ref scold, 0, 10))
            configuration = UpdateServerConfiguration(
                configuration with { ScoldHeatPerSecond = scold });

        var spyHitDelay = configuration.SpyHitHeatDelaySeconds;
        if (DrawFloatSlider("Delay: spy hit (seconds)", ref spyHitDelay, 0, 30))
            configuration = UpdateServerConfiguration(
                configuration with { SpyHitHeatDelaySeconds = spyHitDelay });

        var passiveDelay = configuration.PassiveHeatGainDelaySeconds;
        if (DrawFloatSlider("Delay: passive gain (seconds)", ref passiveDelay, 0, 30))
            configuration = UpdateServerConfiguration(
                configuration with { PassiveHeatGainDelaySeconds = passiveDelay });

        var aggroDelay = configuration.AggroAfterCoverHeatDelaySeconds;
        if (DrawFloatSlider("Delay: aggro after cover (seconds)", ref aggroDelay, 0, 30))
            configuration = UpdateServerConfiguration(
                configuration with { AggroAfterCoverHeatDelaySeconds = aggroDelay });

        var decayDelay = configuration.HeatDecayDelaySeconds;
        if (DrawFloatSlider("Delay: to decay (seconds)", ref decayDelay, 0, 30))
            configuration = UpdateServerConfiguration(
                configuration with { HeatDecayDelaySeconds = decayDelay });

        var decayRate = configuration.HeatDecayRate;
        if (DrawFloatSlider("Decay rate", ref decayRate, 0, 10))
            configuration = UpdateServerConfiguration(
                configuration with { HeatDecayRate = decayRate });

        return configuration;
    }

    private static bool DrawIntSlider(string label, ref int value, int minimum, int maximum)
    {
        ImGui.SetNextItemWidth(300);
        return ImGui.SliderInt(label, ref value, minimum, maximum);
    }

    private static bool DrawFloatSlider(
        string label, ref float value, float minimum, float maximum)
    {
        ImGui.SetNextItemWidth(300);
        return ImGui.SliderFloat(label, ref value, minimum, maximum, "%.2f");
    }

    private static int GetMaximumPlayers(string gameMode) =>
        gameMode.Equals("Solo", StringComparison.OrdinalIgnoreCase) ? 8 :
        gameMode.Equals("Duo", StringComparison.OrdinalIgnoreCase) ? 10 : 12;

    private void DrawPlayersPanel()
    {
        ImGui.TextColored(ToVector4(Accent), "CONNECTED PLAYERS");
        ImGui.TextWrapped(
            "Players are observed by the authoritative server. Kick requests are " +
            "validated here and executed on Unreal's game thread.");
        ImGui.Separator();

        var busy = Volatile.Read(ref _networkBusy) != 0;
        var now = Environment.TickCount64;
        if (!busy && now >= _nextPlayerRefresh)
        {
            _nextPlayerRefresh = now + 2_000;
            RequestPlayers();
        }
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh players"))
        {
            _nextPlayerRefresh = now + 2_000;
            RequestPlayers();
        }
        if (busy) ImGui.EndDisabled();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("Kick message", ref _kickReason, 257);
        ImGui.TextDisabled("The message is sent to the player before returning them to the menu.");

        var players = Volatile.Read(ref _serverPlayers);
        ImGui.TextDisabled($"{FormatCount(players.Length)} connected player(s)");
        if (players.Length == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("No connected player has been observed yet.");
            return;
        }

        if (ImGui.BeginTable(
                "connected-players", 5,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthStretch, 0.40f);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Platform", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Faction", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableHeadersRow();
            foreach (var player in players)
            {
                ImGui.PushID(player.PlayerToken);
                try
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(player.DisplayName);
                    if (player.IsBot)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled("[BOT]");
                    }
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(player.PlayerId.ToString(CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(FormatPlatform(player.PlatformType));
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(player.FactionId.ToString(CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(4);
                    var kickBlocked = busy || player.IsBot;
                    if (kickBlocked) ImGui.BeginDisabled();
                    if (ImGui.SmallButton("Kick"))
                        _pendingKickToken = player.PlayerToken;
                    if (kickBlocked) ImGui.EndDisabled();
                    DrawBlockedTooltip(kickBlocked, player.IsBot
                        ? "Bots are managed by the match configuration."
                        : "A server request is already in progress.");
                }
                finally { ImGui.PopID(); }
            }
            ImGui.EndTable();
        }

        if (_pendingKickToken is null) return;
        var pending = players.FirstOrDefault(player =>
            player.PlayerToken.Equals(_pendingKickToken, StringComparison.Ordinal));
        if (pending is null)
        {
            _pendingKickToken = null;
            return;
        }
        ImGui.Separator();
        ImGui.TextColored(ToVector4(Warning), $"Kick {pending.DisplayName}?");
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(_kickReason)
            ? "The default administrator message will be used."
            : _kickReason);
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Confirm kick"))
        {
            SendKickPlayer(pending.PlayerToken, _kickReason);
            _pendingKickToken = null;
            _nextPlayerRefresh = Environment.TickCount64 + 750;
        }
        if (busy) ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Cancel##kick")) _pendingKickToken = null;
    }

    private static string FormatPlatform(byte platformType) =>
        platformType == 0 ? "Unknown" : $"Type {platformType}";

    private void DrawBalancingPanel()
    {
        var snapshot = CommunityBalanceState.Snapshot;
        ImGui.TextColored(ToVector4(Accent), "COMMUNITY BALANCING");
        ImGui.TextColored(ToVector4(Muted), CommunityBalanceState.Status);
        ImGui.Separator();

        var busy = Volatile.Read(ref _networkBusy) != 0;
        if (ImGui.Button("Request game values")) RequestGameProfile();
        ImGui.SameLine();
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Reload server workspace")) RefreshFromProtocol();
        if (busy) ImGui.EndDisabled();

        if (snapshot is not null)
        {
            ImGui.SameLine();
            var cannotApply = busy || snapshot.Editor.ModifiedCount == 0 ||
                              !snapshot.Editor.IsValid;
            if (cannotApply) ImGui.BeginDisabled();
            if (ImGui.Button("Save changes")) StageChanges(snapshot);
            if (cannotApply) ImGui.EndDisabled();

            if (snapshot.Editor.ModifiedCount > 0)
            {
                ImGui.SameLine();
                if (ImGui.Button("Reset all"))
                {
                    snapshot.Editor.ResetAll();
                    Interlocked.Exchange(ref _dirty, 0);
                }
            }
        }

        ImGui.TextColored(
            ToVector4(Muted),
            "Saved changes are reloaded by the server and synchronized through the vanilla RPC.");

        if (snapshot is null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "Connect to the community server. Briefcase requests the game profile " +
                "periodically and can also retrieve the managed copy captured server-side.");
            return;
        }

        if (snapshot.Editor.InvalidCount > 0)
            ImGui.TextColored(ToVector4(Warning),
                $"{snapshot.Editor.InvalidCount} setting(s) must be corrected before saving.");
        else if (snapshot.Editor.ModifiedCount > 0)
            ImGui.TextColored(ToVector4(Warning),
                $"{snapshot.Editor.ModifiedCount} setting(s) have unsaved changes.");
        ImGui.Text(
            $"{snapshot.Editor.Categories.Count} categories | " +
            $"{FormatCount(snapshot.OptionCount)} editable settings");
        ImGui.TextColored(
            ToVector4(Muted), $"Received locally at {snapshot.ReceivedAtLocal:HH:mm:ss}");
        if (ImGui.TreeNode("Technical details"))
        {
            ImGui.TextWrapped($"Game profile hash: {snapshot.Hash}");
            ImGui.Text($"Hash algorithm: {snapshot.HashAlgorithm}");
            ImGui.Text(
                $"Transfer: {FormatCount(snapshot.CompressedBytes)} compressed bytes, " +
                $"{FormatCount(snapshot.UncompressedBytes)} uncompressed bytes");
            ImGui.TextDisabled(_serverRevision < 0
                ? "Briefcase server revision unknown"
                : $"Briefcase server revision {_serverRevision}");
            ImGui.TreePop();
        }
        ImGui.Spacing();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint(
            "##balance-filter", "Search categories, settings or descriptions...",
            ref _filter, 256);

        var visibleCategories = snapshot.Editor.Categories
            .Where(CategoryMatchesFilter)
            .ToArray();
        if (visibleCategories.Length == 0)
        {
            ImGui.TextDisabled("No balancing setting matches this search.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedCategory) ||
            visibleCategories.All(category =>
                !category.Name.Equals(_selectedCategory, StringComparison.OrdinalIgnoreCase)))
            _selectedCategory = visibleCategories[0].Name;
        ImGui.BeginChild(
            "balance-categories", new System.Numerics.Vector2(235, -1),
            ImGuiNET.ImGuiChildFlags.Borders);
        try
        {
            foreach (var category in visibleCategories)
            {
                if (ImGui.Selectable(
                        $"{category.Name} ({category.SettingCount})##{category.Name}",
                        category.Name.Equals(
                            _selectedCategory, StringComparison.OrdinalIgnoreCase)))
                    _selectedCategory = category.Name;
            }
        }
        finally { ImGui.EndChild(); }

        ImGui.SameLine();
        ImGui.BeginChild(
            "balance-values", new System.Numerics.Vector2(0, -1),
            ImGuiNET.ImGuiChildFlags.Borders);
        try
        {
            var selected = visibleCategories.First(category =>
                category.Name.Equals(_selectedCategory, StringComparison.OrdinalIgnoreCase));
            DrawCategory(selected);
        }
        finally { ImGui.EndChild(); }
    }

    private void DrawServerModsPanel()
    {
        var busy = Volatile.Read(ref _networkBusy) != 0;
        var mods = Volatile.Read(ref _serverMods);

        ImGui.TextColored(ToVector4(Accent), "SERVER MODS");
        ImGui.TextWrapped(
            "Manage DLLs already installed in the server's Briefcase/Mods directory.");
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh server mods")) RequestServerMods(refreshDirectory: true);
        if (busy) ImGui.EndDisabled();
        ImGui.Separator();

        if (mods.Length == 0)
        {
            ImGui.TextDisabled(
                busy ? "Requesting the server mod list..." : "No server mods were returned.");
            return;
        }

        foreach (var mod in mods)
        {
            ImGui.PushID(mod.FileName);
            try
            {
                var enabled = mod.Enabled;
                var disableBlocked = busy || !mod.CanManage || (enabled && !mod.CanStop);
                if (disableBlocked) ImGui.BeginDisabled();
                if (ImGui.Checkbox("##enabled", ref enabled))
                    SendModCommand(
                        ServerAdminOperations.SetModEnabled, mod.FileName, enabled);
                if (disableBlocked) ImGui.EndDisabled();
                DrawBlockedTooltip(disableBlocked, busy
                    ? "A server request is already in progress."
                    : mod.StopBlockReason);
                ImGui.SameLine();
                ImGui.TextUnformatted(mod.DisplayName);
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(mod.Version)
                    ? mod.FileName
                    : $"{mod.FileName}  |  {mod.Version}");

                if (mod.LastError is not null)
                    ImGui.TextColored(
                        ToVector4(Warning), $"Error: {mod.LastError}");
                else
                    ImGui.TextColored(
                        mod.Loaded ? ToVector4(Accent) : ToVector4(Muted),
                        mod.Loaded ? "Loaded" : "Unloaded");

                if (mod.Loaded)
                {
                    var stopBlocked = busy || !mod.CanStop;
                    if (stopBlocked) ImGui.BeginDisabled();
                    if (ImGui.Button("Reload"))
                        SendModCommand(ServerAdminOperations.ReloadMod, mod.FileName);
                    if (stopBlocked) ImGui.EndDisabled();
                    DrawBlockedTooltip(stopBlocked, busy
                        ? "A server request is already in progress."
                        : mod.StopBlockReason);
                    ImGui.SameLine();
                    if (stopBlocked) ImGui.BeginDisabled();
                    if (ImGui.Button("Unload"))
                        SendModCommand(ServerAdminOperations.UnloadMod, mod.FileName);
                    if (stopBlocked) ImGui.EndDisabled();
                    DrawBlockedTooltip(stopBlocked, busy
                        ? "A server request is already in progress."
                        : mod.StopBlockReason);
                }
                else
                {
                    var loadBlocked = busy || !mod.CanManage;
                    if (loadBlocked) ImGui.BeginDisabled();
                    if (ImGui.Button("Load"))
                        SendModCommand(ServerAdminOperations.LoadMod, mod.FileName);
                    if (loadBlocked) ImGui.EndDisabled();
                }

                if (!string.IsNullOrWhiteSpace(mod.ManagementNote))
                    ImGui.TextDisabled(mod.ManagementNote);
                if (mod.Dependencies.Count > 0)
                    ImGui.TextDisabled($"Requires: {string.Join(", ", mod.Dependencies)}");
                if (!string.IsNullOrWhiteSpace(mod.Description))
                    ImGui.TextWrapped(mod.Description);
                DrawServerModConfiguration(mod, busy);
                ImGui.Separator();
            }
            finally { ImGui.PopID(); }
        }
    }

    private void DrawServerModConfiguration(ServerModEnvelope mod, bool busy)
    {
        if (mod.Configuration.Count == 0) return;
        ImGui.SeparatorText("Configuration");
        foreach (var section in mod.Configuration.GroupBy(
                     entry => entry.Section, StringComparer.OrdinalIgnoreCase))
        {
            ImGui.TextDisabled(section.Key);
            foreach (var entry in section)
            {
                ImGui.PushID($"{entry.Section}/{entry.Key}");
                try
                {
                    if (busy) ImGui.BeginDisabled();
                    var changed = false;
                    JsonElement value = entry.Value;
                    switch (entry.ValueType)
                    {
                        case "bool":
                        {
                            var current = entry.Value.GetBoolean();
                            changed = ImGui.Checkbox(entry.Key, ref current);
                            if (changed) value = JsonSerializer.SerializeToElement(current);
                            break;
                        }
                        case "int":
                        {
                            var identity = $"{mod.FileName}\n{entry.Section}\n{entry.Key}";
                            changed = DrawCommittedInteger(
                                entry.Key, identity, entry.Value.GetInt32(), out var current);
                            if (changed) value = JsonSerializer.SerializeToElement(current);
                            break;
                        }
                        case "float":
                        {
                            var current = entry.Value.GetSingle();
                            changed = entry.Minimum is { } minimum &&
                                      entry.Maximum is { } maximum
                                ? ImGui.SliderFloat(
                                    entry.Key, ref current,
                                    minimum.GetSingle(), maximum.GetSingle())
                                : ImGui.InputFloat(entry.Key, ref current);
                            if (changed) value = JsonSerializer.SerializeToElement(current);
                            break;
                        }
                        case "double":
                        {
                            var current = entry.Value.GetDouble();
                            changed = ImGui.InputDouble(entry.Key, ref current);
                            if (changed) value = JsonSerializer.SerializeToElement(current);
                            break;
                        }
                        case "string":
                        {
                            var current = entry.Value.GetString() ?? "";
                            changed = ImGui.InputText(
                                entry.Key, ref current, 4096,
                                ImGuiNET.ImGuiInputTextFlags.EnterReturnsTrue);
                            if (changed) value = JsonSerializer.SerializeToElement(current);
                            break;
                        }
                        case "enum":
                        {
                            var current = entry.Value.GetString() ?? "";
                            if (ImGui.BeginCombo(entry.Key, current))
                            {
                                try
                                {
                                    foreach (var choice in entry.Choices)
                                    {
                                        var selected = choice.Equals(
                                            current, StringComparison.OrdinalIgnoreCase);
                                        if (ImGui.Selectable(choice, selected))
                                        {
                                            value = JsonSerializer.SerializeToElement(choice);
                                            changed = true;
                                        }
                                    }
                                }
                                finally { ImGui.EndCombo(); }
                            }
                            break;
                        }
                        default:
                            ImGui.TextDisabled($"{entry.Key}: unsupported type");
                            break;
                    }
                    if (busy) ImGui.EndDisabled();
                    if (changed)
                        SendModSetting(mod.FileName, entry.Section, entry.Key, value);
                    if (!string.IsNullOrWhiteSpace(entry.Description))
                    {
                        ImGui.Indent();
                        ImGui.TextDisabled(entry.Description);
                        ImGui.Unindent();
                    }
                }
                finally { ImGui.PopID(); }
            }
        }
    }

    private bool DrawCommittedInteger(
        string label, string identity, int serverValue, out int submittedValue)
    {
        // Dear ImGui deliberately rejects EnterReturnsTrue on InputInt because
        // InputInt is an InputScalar widget. Keep the in-progress value locally
        // and submit it only when the edit is committed, preventing a TCP
        // administration request for every digit or key repeat.
        var current = _serverModIntegerDrafts.TryGetValue(identity, out var draft)
            ? draft
            : serverValue;
        if (ImGui.InputInt(label, ref current, 1, 10))
            _serverModIntegerDrafts[identity] = current;

        var enterPressed = ImGui.IsItemActive() &&
                           (ImGui.IsKeyPressed(ImGuiKey.Enter) ||
                            ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));
        var committed = ImGui.IsItemDeactivatedAfterEdit() || enterPressed;
        if (committed && _serverModIntegerDrafts.Remove(identity, out submittedValue))
            return submittedValue != serverValue;

        submittedValue = serverValue;
        return false;
    }

    private void DrawCompatibilityPanel()
    {
        ImGui.TextColored(ToVector4(Accent), "MOD COMPATIBILITY");
        ImGui.TextWrapped(
            "The handshake channel exchanges manifests only. Administration uses a separate " +
            "channel on the same TCP endpoint. This version never downloads or executes a package.");
        ImGui.Separator();

        ImGui.TextDisabled($"Endpoint: {_endpoint?.Value ?? ""} (shared with administration)");
        if (ImGui.Button("Handshake now")) _handshake?.Refresh();
        ImGui.TextColored(ToVector4(Muted),
            _handshake?.Status ?? "The local handshake client is unavailable.");

        var hello = _handshake?.Latest;
        if (hello is not null)
        {
            var color = hello.Accepted ? Accent : Warning;
            ImGui.TextColored(ToVector4(color), hello.Accepted ? "Accepted" : "Rejected");
            ImGui.SameLine();
            ImGui.TextDisabled(
                $"Protocol {hello.ProtocolVersion} | Briefcase {hello.FrameworkVersion} | " +
                $"Build {hello.GameBuild}");
        }

        ImGui.SeparatorText("This client");
        DrawHandshakeManifest("client-manifest", _handshake?.LocalMods ?? []);
        ImGui.SeparatorText("Server components");
        DrawHandshakeManifest("server-manifest", hello?.Mods ?? []);

        var busy = Volatile.Read(ref _networkBusy) != 0;
        var now = Environment.TickCount64;
        if (!busy && now >= _nextHandshakeRefresh)
        {
            _nextHandshakeRefresh = now + 2_000;
            RequestModHandshakes();
        }
        if (busy) ImGui.BeginDisabled();
        if (ImGui.Button("Refresh observed clients")) RequestModHandshakes();
        if (busy) ImGui.EndDisabled();

        var clients = Volatile.Read(ref _handshakeClients);
        ImGui.TextDisabled($"{FormatCount(clients.Length)} active Briefcase client(s)");
        foreach (var client in clients)
        {
            var label = $"{client.RemoteAddress} | Briefcase {client.FrameworkVersion} | " +
                        $"Build {client.GameBuild}##{client.ClientInstanceId}";
            if (!ImGui.TreeNode(label)) continue;
            DrawHandshakeManifest($"observed-{client.ClientInstanceId}", client.Mods);
            ImGui.TreePop();
        }
    }

    private static void DrawHandshakeManifest(
        string tableId, IReadOnlyList<ModHandshakeMod> mods)
    {
        if (mods.Count == 0)
        {
            ImGui.TextDisabled("No manifest has been received yet.");
            return;
        }
        if (!ImGui.BeginTable(
                tableId, 4,
                ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg |
                ImGuiTableFlags.SizingStretchProp)) return;
        try
        {
            ImGui.TableSetupColumn("Component", ImGuiTableColumnFlags.WidthStretch, 0.46f);
            ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 85);
            ImGui.TableSetupColumn("SHA-256", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var mod in mods)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                ImGui.TextUnformatted(mod.DisplayName);
                if (!string.Equals(mod.DisplayName, mod.Id, StringComparison.OrdinalIgnoreCase))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"[{mod.Id}]");
                }
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(mod.Version) ? "-" : mod.Version);
                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(mod.Loaded ? "Loaded" : mod.Enabled ? "Enabled" : "Disabled");
                ImGui.TableSetColumnIndex(3);
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(mod.Sha256)
                    ? "core"
                    : mod.Sha256[..Math.Min(12, mod.Sha256.Length)]);
            }
        }
        finally { ImGui.EndTable(); }
    }

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

    private void DrawCategory(BalanceCategory category)
    {
        ImGui.TextColored(ToVector4(Accent), category.Name);
        ImGui.TextDisabled($"{FormatCount(category.SettingCount)} settings");
        ImGui.Separator();

        foreach (var section in category.Sections)
        {
            var showWholeSection = string.IsNullOrWhiteSpace(_filter) ||
                                   category.Name.Contains(
                                       _filter, StringComparison.OrdinalIgnoreCase) ||
                                   section.Name.Contains(
                                       _filter, StringComparison.OrdinalIgnoreCase);
            var visibleSettings = showWholeSection
                ? section.Settings.ToArray()
                : section.Settings.Where(SettingMatchesFilter).ToArray();
            if (visibleSettings.Length == 0) continue;
            var flags = string.IsNullOrWhiteSpace(_filter)
                ? ImGuiTreeNodeFlags.None
                : ImGuiTreeNodeFlags.DefaultOpen;
            if (!ImGui.TreeNodeEx(
                    $"{section.Name} ({visibleSettings.Length})##{category.Name}/{section.Name}",
                    flags)) continue;

            if (ImGui.BeginTable(
                    $"settings##{category.Name}/{section.Name}", 2,
                    ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg |
                    ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn(
                    "Setting", ImGuiTableColumnFlags.WidthStretch, 0.62f);
                ImGui.TableSetupColumn(
                    "Value", ImGuiTableColumnFlags.WidthStretch, 0.38f);
                ImGui.TableHeadersRow();
                foreach (var setting in visibleSettings)
                {
                    ImGui.PushID(setting.Id);
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(setting.Name);
                    if (!string.IsNullOrWhiteSpace(setting.Description))
                    {
                        ImGui.TextWrapped(setting.Description);
                        if (setting.AllowedRange is not null)
                            ImGui.TextColored(
                                ToVector4(Muted), $"Allowed range: {setting.AllowedRange}");
                    }
                    else if (setting.AllowedRange is not null)
                        ImGui.TextColored(
                            ToVector4(Muted), $"Allowed range: {setting.AllowedRange}");
                    ImGui.TableSetColumnIndex(1);
                    DrawSetting(setting);
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            ImGui.TreePop();
        }
    }

    private void DrawSetting(BalanceSetting setting)
    {
        if (!setting.IsEditable) ImGui.BeginDisabled();
        if (setting.Kind == BalanceSettingKind.Boolean)
        {
            var value = setting.BooleanValue;
            if (ImGui.Checkbox("##value", ref value))
            {
                setting.SetBoolean(value);
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
        else
        {
            var value = setting.Draft;
            var flags = setting.Kind is BalanceSettingKind.Integer or BalanceSettingKind.Number
                ? ImGuiNET.ImGuiInputTextFlags.CharsScientific
                : ImGuiNET.ImGuiInputTextFlags.None;
            ImGui.SetNextItemWidth(setting.IsModified ? -58 : -1);
            if (ImGui.InputText("##value", ref value, 16_384, flags))
            {
                setting.SetDraft(value);
                Interlocked.Exchange(ref _dirty, 1);
            }
        }
        if (!setting.IsEditable) ImGui.EndDisabled();

        if (setting.IsModified)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset")) setting.Reset();
        }
        if (!setting.IsValid)
            ImGui.TextColored(ToVector4(Warning), setting.ValidationError ?? "Invalid value.");
    }

    private bool SettingMatchesFilter(BalanceSetting setting) =>
        string.IsNullOrWhiteSpace(_filter) ||
        setting.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        setting.Section.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        setting.Draft.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        (setting.Description?.Contains(
            _filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private static System.Numerics.Vector4 ToVector4(uint rgba) => new(
        (rgba & 0xff) / 255.0f,
        ((rgba >> 8) & 0xff) / 255.0f,
        ((rgba >> 16) & 0xff) / 255.0f,
        ((rgba >> 24) & 0xff) / 255.0f);

    private static void DrawBlockedTooltip(bool blocked, string? reason)
    {
        if (!blocked || string.IsNullOrWhiteSpace(reason) ||
            !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420);
        ImGui.TextUnformatted(reason);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    // The French .NET culture formats thousands with U+202F. That glyph is
    // absent from our compact Inter atlas, so ImGui renders it as a question
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
