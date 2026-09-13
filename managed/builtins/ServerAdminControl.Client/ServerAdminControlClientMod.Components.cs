using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Briefcase.ClientModApi;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Client;

/// <summary>
/// Toolkit-neutral projection of the administration state. Network and Unreal
/// work remain in ServerAdminControlClientMod; this file only binds that state
/// to controls that the Avalonia renderer understands.
/// </summary>
public sealed partial class ServerAdminControlClientMod
{
    private long _componentUiRevision;

    private UiComponent BuildComponentPanel() => Ui.Column(
        Ui.Text("BRIEFCASE SERVER", UiTextTone.Accent, wrap: false),
        Ui.Section("Server API unavailable",
                Ui.Text(() => Volatile.Read(ref _protocolStatus)),
                Ui.Text(
                    "Check the administration address and password, then refresh the connection.",
                    UiTextTone.Muted))
            .VisibleWhen(() => !ApiConnected()),
        Ui.Tabs(
            () => _selectedServerPage.ToString(),
            selected =>
            {
                if (!Enum.TryParse<ServerPage>(selected, out var page)) return;
                _selectedServerPage = page;
                Interlocked.Increment(ref _componentUiRevision);
            },
            Ui.Tab(nameof(ServerPage.Server), "Server",
                BuildServerConfigurationComponents()),
            Ui.Tab(nameof(ServerPage.Players), "Players",
                BuildPlayersComponents()),
            Ui.Tab(nameof(ServerPage.Balancing), "Balancing",
                BuildBalancingComponents()),
            Ui.Tab(nameof(ServerPage.Mods), "Mods",
                BuildServerModsComponents()),
            Ui.Tab(nameof(ServerPage.Compatibility), "Compatibility",
                BuildCompatibilityComponents()))
            .VisibleWhen(ApiConnected));

    /// <summary>
    /// Keeps server edits local until the administrator explicitly applies
    /// them. The footer lives outside the scrolling page so it remains visible
    /// while editing long server, balancing, or mod forms.
    /// </summary>
    private UiComponent BuildServerChangesFooter() =>
        Ui.Card(
                Ui.Row(
                    Ui.Text(PendingServerChangesLabel),
                    Ui.Button("Cancel", CancelPendingServerChanges),
                    Ui.Button("Apply changes", UiIcon.Save, ApplyPendingServerChanges)
                        .WithClass(UiClasses.Primary)
                        .EnabledWhen(CanApplyPendingServerChanges)))
            .WithClass(UiClasses.ActionBar)
            .VisibleWhen(HasPendingServerChanges);

    private bool HasPendingServerChanges() => _selectedServerPage switch
    {
        ServerPage.Server => Volatile.Read(ref _serverConfigurationDirty) != 0,
        ServerPage.Balancing => CommunityBalanceState.Snapshot?.Editor.ModifiedCount > 0,
        ServerPage.Mods => !string.IsNullOrWhiteSpace(_selectedServerModFileName) &&
                           Volatile.Read(ref _serverModDraftDirty) != 0,
        _ => false
    };

    private string PendingServerChangesLabel() => _selectedServerPage switch
    {
        ServerPage.Server => "The server configuration has unapplied changes.",
        ServerPage.Balancing =>
            $"{CommunityBalanceState.Snapshot?.Editor.ModifiedCount ?? 0} balancing setting(s) have unapplied changes.",
        ServerPage.Mods => "This server mod has unapplied configuration changes.",
        _ => "There are unapplied server changes."
    };

    private bool CanApplyPendingServerChanges() => !NetworkBusy() &&
        HasPendingServerChanges() &&
        (_selectedServerPage != ServerPage.Balancing ||
         CommunityBalanceState.Snapshot?.Editor.IsValid == true);

    private void ApplyPendingServerChanges()
    {
        switch (_selectedServerPage)
        {
            case ServerPage.Server when CurrentConfiguration() is { } configuration:
                SaveServerConfiguration(configuration);
                break;
            case ServerPage.Balancing when CommunityBalanceState.Snapshot is { } snapshot:
                StageChanges(snapshot);
                break;
            case ServerPage.Mods:
                ApplyServerModDraft();
                break;
        }
    }

    private void CancelPendingServerChanges()
    {
        switch (_selectedServerPage)
        {
            case ServerPage.Server:
                CancelServerConfigurationDraft();
                break;
            case ServerPage.Balancing:
                CommunityBalanceState.Snapshot?.Editor.ResetAll();
                Interlocked.Exchange(ref _dirty, 0);
                break;
            case ServerPage.Mods:
                CancelServerModDraft();
                break;
        }
    }

    private UiComponent BuildConnectionToolbar() => Ui.Row(
        Ui.Dynamic(
            () =>
            [
                Ui.Status(ConnectionLabel(), ConnectionTone())
                    .WithTooltip(() => Volatile.Read(ref _protocolStatus))
            ],
            () => Volatile.Read(ref _apiConnectionRevision)),
        Ui.Button("Refresh connection", UiIcon.Refresh, RefreshFromProtocol)
            .EnabledWhen(() => !NetworkBusy()));

    private string ConnectionLabel() =>
        (ServerApiConnectionState)Volatile.Read(ref _apiConnectionState) switch
        {
            ServerApiConnectionState.Connected => "API connected",
            ServerApiConnectionState.Connecting => "Connecting to API...",
            _ => "API disconnected"
        };

    private UiStatusTone ConnectionTone() =>
        (ServerApiConnectionState)Volatile.Read(ref _apiConnectionState) switch
        {
            ServerApiConnectionState.Connected => UiStatusTone.Success,
            ServerApiConnectionState.Connecting => UiStatusTone.Information,
            _ => UiStatusTone.Warning
        };

    private UiComponent BuildServerConfigurationComponents()
    {
        var mapControls = Maps.Select(map =>
            (UiComponent)Ui.Toggle(
                    map.Label,
                    () => CurrentConfiguration()?.MapRotation.Contains(
                        map.Code, StringComparer.OrdinalIgnoreCase) == true,
                    selected => SetMapSelected(map.Code, selected))
                .EnabledWhen(() => !IsOnlySelectedMap(map.Code))
                .WithTooltip(() => IsOnlySelectedMap(map.Code)
                    ? "At least one map must remain in the rotation."
                    : null)).ToArray();

        return Ui.Column(
            Ui.Section("Server connection",
                Ui.Text(
                    "These values are stored in TripwireServer.ini. Save writes them atomically; " +
                    "restart applies settings that the running server has already read.",
                    UiTextTone.Muted),
                Ui.TextField("Briefcase endpoint",
                    () => _endpoint?.Value ?? "",
                    value =>
                    {
                        if (_endpoint is not null) _endpoint.Value = value;
                        SetApiConnectionState(ServerApiConnectionState.Disconnected);
                        _handshake?.Refresh();
                    }, maximumLength: 256,
                    commitMode: UiCommitMode.OnCommit),
                Ui.TextField("Administration password",
                        () => _administrationSecret?.Value ?? "",
                        value =>
                        {
                            if (_administrationSecret is not null)
                                _administrationSecret.Value = value;
                            SetApiConnectionState(ServerApiConnectionState.Disconnected);
                        },
                        secret: true, maximumLength: 128,
                        commitMode: UiCommitMode.OnCommit)
                    .VisibleWhen(() => !_showAuthenticationSecret),
                Ui.TextField("Administration password",
                        () => _administrationSecret?.Value ?? "",
                        value =>
                        {
                            if (_administrationSecret is not null)
                                _administrationSecret.Value = value;
                            SetApiConnectionState(ServerApiConnectionState.Disconnected);
                        },
                        maximumLength: 128,
                        commitMode: UiCommitMode.OnCommit)
                    .VisibleWhen(() => _showAuthenticationSecret),
                Ui.Toggle("Show administration password",
                    () => _showAuthenticationSecret,
                    value => _showAuthenticationSecret = value),
                Ui.Text(
                    "Used only to authenticate Briefcase administration requests; it is never sent to the game server.",
                    UiTextTone.Muted),
                Ui.Button("Refresh configuration", RequestServerConfiguration)
                    .EnabledWhen(() => !NetworkBusy())),
            Ui.Text("No server configuration has been received yet.", UiTextTone.Muted)
                .VisibleWhen(() => CurrentConfiguration() is null),
            Ui.Column(
                Ui.Section("Identity and access",
                    Ui.TextField("Server name",
                        () => CurrentConfiguration()?.ServerName ?? "",
                        value => EditConfiguration(configuration =>
                            configuration with { ServerName = value }),
                        maximumLength: 64),
                    Ui.Choice("Region",
                        () => CurrentConfiguration()?.Region ?? "",
                        value => EditConfiguration(configuration =>
                            configuration with { Region = value }),
                        Regions.Select(region => region.Value),
                        value => Regions.First(region => region.Value == value).Label),
                    Ui.Choice("Game mode",
                        () => CurrentConfiguration()?.GameMode ?? "Solo",
                        value => EditConfiguration(configuration => configuration with
                        {
                            GameMode = value,
                            MaxPlayers = Math.Min(
                                configuration.MaxPlayers,
                                ServerAdminProtocol.MaximumPlayerCount)
                        }),
                        new[] { "Solo", "Duo", "Trio" }),
                    Ui.Number($"Maximum players (1-{ServerAdminProtocol.MaximumPlayerCount})",
                        () => CurrentConfiguration()?.MaxPlayers ?? 1,
                        value => EditConfiguration(configuration =>
                            configuration with { MaxPlayers = value }),
                        1, ServerAdminProtocol.MaximumPlayerCount),
                    Ui.TextField("Password",
                            () => CurrentConfiguration()?.Password ?? "",
                            value => EditConfiguration(configuration =>
                                configuration with { Password = value }),
                            secret: true, maximumLength: 128)
                        .VisibleWhen(() => !_showServerPassword),
                    Ui.TextField("Password",
                            () => CurrentConfiguration()?.Password ?? "",
                            value => EditConfiguration(configuration =>
                                configuration with { Password = value }),
                            maximumLength: 128)
                        .VisibleWhen(() => _showServerPassword),
                    Ui.Toggle("Show server password",
                        () => _showServerPassword,
                        value => _showServerPassword = value),
                    Ui.Toggle("Public server",
                        () => CurrentConfiguration()?.IsPublic ?? false,
                        value => EditConfiguration(configuration =>
                            configuration with { IsPublic = value })),
                    Ui.Toggle("Crossplay",
                        () => CurrentConfiguration()?.Crossplay ?? false,
                        value => EditConfiguration(configuration =>
                            configuration with { Crossplay = value }))),
                Ui.Section("Network",
                    Ui.Number("Game port",
                        () => CurrentConfiguration()?.GamePort ?? 0,
                        value => EditConfiguration(configuration =>
                            configuration with { GamePort = value }),
                        1, 65535),
                    Ui.Number("Query port",
                        () => CurrentConfiguration()?.QueryPort ?? 0,
                        value => EditConfiguration(configuration =>
                            configuration with { QueryPort = value }),
                        1, 65535),
                    Ui.Toggle("UPnP port mapping",
                        () => CurrentConfiguration()?.EnableUpnp ?? false,
                        value => EditConfiguration(configuration =>
                            configuration with { EnableUpnp = value }))),
                Ui.Section("Lobby",
                    Ui.Number("Auto-shutdown when empty (minutes)",
                        () => CurrentConfiguration()?.AutoShutdownEmptyMinutes ?? 0,
                        value => EditConfiguration(configuration =>
                            configuration with { AutoShutdownEmptyMinutes = value }),
                        0, 1440, 0.1f, "0.0")),
                Ui.Section("Gameplay",
                    Ui.Toggle("Sandbox mode (unlock all)",
                        () => CurrentConfiguration()?.SandboxMode ?? false,
                        value => EditConfiguration(configuration =>
                            configuration with { SandboxMode = value })),
                    Ui.Toggle("Randomize map order",
                        () => CurrentConfiguration()?.RandomizeMap ?? false,
                        value => EditConfiguration(configuration =>
                            configuration with { RandomizeMap = value })),
                    Ui.Text("Map rotation", UiTextTone.Accent),
                    Ui.Column(mapControls)),
                Ui.Section("Bots",
                    Ui.Toggle("Fill with bots",
                        () => CurrentConfiguration()?.FillWithBots ?? false,
                        value => EditConfiguration(configuration =>
                            configuration with { FillWithBots = value })),
                    Ui.Choice("Bot difficulty",
                        () => CurrentConfiguration()?.BotsDifficulty ?? "Normal",
                        value => EditConfiguration(configuration =>
                            configuration with { BotsDifficulty = value }),
                        new[] { "Easy", "Normal", "Difficult" }),
                    Ui.Number("Number of bots (0 = auto)",
                        () => CurrentConfiguration()?.BotsAmount ?? 0,
                        value => EditConfiguration(configuration =>
                            configuration with { BotsAmount = value }),
                        0, 8)),
                BuildHeatComponents(),
                BuildServerProcessActions(),
                Ui.Section("Runtime",
                    Ui.Text(() => $"PID: {CurrentConfiguration()?.ProcessId}"),
                    Ui.Text(() => $"Executable: {CurrentConfiguration()?.ExecutablePath}"),
                    Ui.Text(() => $"Configuration: {CurrentConfiguration()?.ConfigurationPath}")))
                .EnabledWhen(() => CurrentConfiguration() is not null));
    }

    private UiComponent BuildHeatComponents() => Ui.Section("Heat",
        IntConfiguration("Civilian heat %", configuration => configuration.CivilianHeatPercent,
            (configuration, value) => configuration with { CivilianHeatPercent = value }, -1, 100),
        IntConfiguration("Staff heat %", configuration => configuration.StaffHeatPercent,
            (configuration, value) => configuration with { StaffHeatPercent = value }, -1, 100),
        IntConfiguration("Guard heat %", configuration => configuration.GuardHeatPercent,
            (configuration, value) => configuration with { GuardHeatPercent = value }, -1, 100),
        IntConfiguration("Technician heat %", configuration => configuration.TechnicianHeatPercent,
            (configuration, value) => configuration with { TechnicianHeatPercent = value }, -1, 100),
        IntConfiguration("VIP heat %", configuration => configuration.VipHeatPercent,
            (configuration, value) => configuration with { VipHeatPercent = value }, -1, 100),
        FloatConfiguration("Scold heat per second", configuration => configuration.ScoldHeatPerSecond,
            (configuration, value) => configuration with { ScoldHeatPerSecond = value }, 0, 10),
        FloatConfiguration("Delay: spy hit (seconds)", configuration => configuration.SpyHitHeatDelaySeconds,
            (configuration, value) => configuration with { SpyHitHeatDelaySeconds = value }, 0, 30),
        FloatConfiguration("Delay: passive gain (seconds)", configuration => configuration.PassiveHeatGainDelaySeconds,
            (configuration, value) => configuration with { PassiveHeatGainDelaySeconds = value }, 0, 30),
        FloatConfiguration("Delay: aggro after cover (seconds)", configuration => configuration.AggroAfterCoverHeatDelaySeconds,
            (configuration, value) => configuration with { AggroAfterCoverHeatDelaySeconds = value }, 0, 30),
        FloatConfiguration("Delay: to decay (seconds)", configuration => configuration.HeatDecayDelaySeconds,
            (configuration, value) => configuration with { HeatDecayDelaySeconds = value }, 0, 30),
        FloatConfiguration("Decay rate", configuration => configuration.HeatDecayRate,
            (configuration, value) => configuration with { HeatDecayRate = value }, 0, 10));

    private UiComponent BuildServerProcessActions() => Ui.Section("Server process",
        Ui.Row(
            Ui.Button("Restart server", () => _confirmRestart = true)
                .EnabledWhen(() =>
                    !NetworkBusy() && Volatile.Read(ref _serverConfigurationDirty) == 0)),
        Ui.Column(
            Ui.Text(
                "The dedicated server will disconnect every player and restart.",
                UiTextTone.Warning),
            Ui.Row(
                Ui.Button("Confirm restart", () =>
                {
                    _confirmRestart = false;
                    RestartServer();
                }),
                Ui.Button("Cancel", () => _confirmRestart = false)))
            .VisibleWhen(() => _confirmRestart));

    private UiComponent BuildPlayersComponents() => Ui.Column(
        Ui.Section("Connected players",
            Ui.Text(
                "Players are observed by the authoritative server. Kick requests are validated " +
                "here and executed on Unreal's game thread.", UiTextTone.Muted),
            Ui.Button("Refresh players", () =>
            {
                _nextPlayerRefresh = Environment.TickCount64 + 2_000;
                RequestPlayers();
            }).EnabledWhen(() => !NetworkBusy()),
            Ui.TextField("Kick message", () => _kickReason, value => _kickReason = value,
                maximumLength: 256),
            Ui.Text("The message is sent to the player before returning them to the menu.",
                UiTextTone.Muted),
            Ui.Dynamic(BuildPlayerRows, PlayerUiRevision)));

    private IReadOnlyList<UiComponent> BuildPlayerRows()
    {
        var players = Volatile.Read(ref _serverPlayers);
        var result = new List<UiComponent>
        {
            Ui.Text($"{FormatCount(players.Length)} connected player(s)", UiTextTone.Muted)
        };
        if (players.Length == 0)
        {
            result.Add(Ui.Text("No connected player has been observed yet.", UiTextTone.Muted));
            return result;
        }
        foreach (var player in players)
        {
            var current = player;
            result.Add(Ui.Row(
                Ui.Text(
                    $"{current.DisplayName}{(current.IsBot ? " [BOT]" : "")} | " +
                    $"ID {current.PlayerId} | {FormatPlatform(current.PlatformType)} | " +
                    $"faction {current.FactionId}"),
                Ui.Button("Kick", () =>
                {
                    _pendingKickToken = current.PlayerToken;
                    Interlocked.Increment(ref _componentUiRevision);
                }).EnabledWhen(() => !NetworkBusy() && !current.IsBot)
                  .WithTooltip(current.IsBot
                      ? "Bots are managed by the match configuration."
                      : "A server request is already in progress.")));
        }

        var pending = players.FirstOrDefault(player =>
            player.PlayerToken.Equals(_pendingKickToken, StringComparison.Ordinal));
        if (pending is not null)
        {
            result.Add(Ui.Text($"Kick {pending.DisplayName}?", UiTextTone.Warning));
            result.Add(Ui.Text(string.IsNullOrWhiteSpace(_kickReason)
                ? "The default administrator message will be used."
                : _kickReason));
            result.Add(Ui.Row(
                Ui.Button("Confirm kick", () =>
                {
                    SendKickPlayer(pending.PlayerToken, _kickReason);
                    _pendingKickToken = null;
                    _nextPlayerRefresh = Environment.TickCount64 + 750;
                    Interlocked.Increment(ref _componentUiRevision);
                }).EnabledWhen(() => !NetworkBusy()),
                Ui.Button("Cancel", () =>
                {
                    _pendingKickToken = null;
                    Interlocked.Increment(ref _componentUiRevision);
                })));
        }
        return result;
    }

    private long PlayerUiRevision()
    {
        if (!NetworkBusy() && Environment.TickCount64 >= _nextPlayerRefresh)
        {
            _nextPlayerRefresh = Environment.TickCount64 + 2_000;
            RequestPlayers();
        }
        return RuntimeHelpers.GetHashCode(Volatile.Read(ref _serverPlayers)) * 397L +
               Volatile.Read(ref _componentUiRevision);
    }

    private UiComponent BuildBalancingComponents() => Ui.Column(
        Ui.Section("Community balancing",
            Ui.Text(() => CommunityBalanceState.Status, UiTextTone.Muted),
            Ui.Row(
                Ui.Button("Request game values", RequestGameProfile),
                Ui.Button("Reload server profile", RefreshFromProtocol)
                    .EnabledWhen(() => !NetworkBusy())),
            Ui.Text(
                "Changes are saved to CommunityBalanceProfile.json. Restart the server before players join to apply them.",
                UiTextTone.Muted),
            Ui.TextField("Search",
                () => _filter,
                value =>
                {
                    _filter = value;
                    _balancePage = 0;
                    Interlocked.Increment(ref _componentUiRevision);
                }, maximumLength: 256,
                hint: "Search categories, settings or descriptions...",
                commitMode: UiCommitMode.OnCommit),
            Ui.Dynamic(BuildBalanceEditorComponents, BalanceUiRevision)));

    private IReadOnlyList<UiComponent> BuildBalanceEditorComponents()
    {
        var snapshot = CommunityBalanceState.Snapshot;
        if (snapshot is null)
            return
            [
                Ui.Text(
                    "Connect to the Briefcase administration endpoint and reload the server profile. " +
                    "Joining the gameplay server is not required.", UiTextTone.Muted)
            ];

        var result = new List<UiComponent>();
        if (snapshot.Editor.InvalidCount > 0)
            result.Add(Ui.Text(
                $"{snapshot.Editor.InvalidCount} setting(s) must be corrected before saving.",
                UiTextTone.Warning));
        else if (snapshot.Editor.ModifiedCount > 0)
            result.Add(Ui.Text(
                $"{snapshot.Editor.ModifiedCount} setting(s) have unsaved changes.",
                UiTextTone.Warning));
        result.Add(Ui.Text(
            $"{snapshot.Editor.Categories.Count} categories | " +
            $"{FormatCount(snapshot.OptionCount)} editable settings"));
        result.Add(Ui.Text($"Received locally at {snapshot.ReceivedAtLocal:HH:mm:ss}", UiTextTone.Muted));
        result.Add(Ui.Text(
            $"Profile: {snapshot.HashAlgorithm} {snapshot.Hash} | " +
            $"{FormatCount(snapshot.CompressedBytes)} compressed bytes | " +
            (_serverRevision < 0 ? "server revision unknown" : $"server revision {_serverRevision}"),
            UiTextTone.Muted));

        var categories = snapshot.Editor.Categories.Where(CategoryMatchesFilter).ToArray();
        if (categories.Length == 0)
        {
            result.Add(Ui.Text("No balancing setting matches this search.", UiTextTone.Muted));
            return result;
        }
        if (string.IsNullOrWhiteSpace(_selectedCategory) || categories.All(category =>
                !category.Name.Equals(_selectedCategory, StringComparison.OrdinalIgnoreCase)))
            _selectedCategory = categories[0].Name;

        result.Add(Ui.Choice("Category",
            () => _selectedCategory,
            value =>
            {
                _selectedCategory = value;
                _balancePage = 0;
                Interlocked.Increment(ref _componentUiRevision);
            },
            categories.Select(category => category.Name),
            value =>
            {
                var category = categories.First(item => item.Name == value);
                return $"{category.Name} ({category.SettingCount})";
            }));
        var selected = categories.First(category =>
            category.Name.Equals(_selectedCategory, StringComparison.OrdinalIgnoreCase));
        result.Add(BuildBalanceCategory(selected));
        return result;
    }

    private UiComponent BuildBalanceCategory(BalanceCategory category)
    {
        var visible = new List<(string Section, BalanceSetting Setting)>();
        foreach (var section in category.Sections)
        {
            var showWholeSection = string.IsNullOrWhiteSpace(_filter) ||
                                   category.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                                   section.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase);
            var settings = showWholeSection
                ? section.Settings.ToArray()
                : section.Settings.Where(SettingMatchesFilter).ToArray();
            visible.AddRange(settings.Select(setting => (section.Name, setting)));
        }
        if (visible.Count == 0)
            return Ui.Text("No setting matches this search.", UiTextTone.Muted);

        var pageCount = (visible.Count + BalancePageSize - 1) / BalancePageSize;
        _balancePage = Math.Clamp(_balancePage, 0, pageCount - 1);
        var first = _balancePage * BalancePageSize;
        var page = visible.Skip(first).Take(BalancePageSize).ToArray();
        var content = new List<UiComponent>
        {
            Ui.Text(
                $"Showing {first + 1}-{first + page.Length} of {visible.Count} settings",
                UiTextTone.Muted)
        };
        if (pageCount > 1)
        {
            content.Add(Ui.Row(
                Ui.Button("Previous", () => ChangeBalancePage(-1))
                    .EnabledWhen(() => _balancePage > 0),
                Ui.Text($"Page {_balancePage + 1} of {pageCount}", UiTextTone.Muted),
                Ui.Button("Next", () => ChangeBalancePage(1))
                    .EnabledWhen(() => _balancePage + 1 < pageCount)));
        }
        var sections = page.GroupBy(entry => entry.Section).ToArray();
        for (var index = 0; index < sections.Length; index++)
        {
            var section = sections[index];
            content.Add(Ui.Text(
                $"{section.Key} ({section.Count()} on this page)",
                UiTextTone.Accent));
            content.AddRange(section.Select(entry =>
                BuildBalanceSetting(entry.Setting)));
            if (index + 1 < sections.Length) content.Add(Ui.Separator());
        }
        return Ui.Column(content.ToArray());
    }

    private void ChangeBalancePage(int offset)
    {
        _balancePage = Math.Max(0, _balancePage + offset);
        Interlocked.Increment(ref _componentUiRevision);
    }

    private UiComponent BuildBalanceSetting(BalanceSetting setting)
    {
        UiComponent editor = setting.Kind switch
        {
            BalanceSettingKind.Boolean => Ui.Toggle(
                setting.Name, () => setting.BooleanValue, value =>
                {
                    setting.SetBoolean(value);
                    Interlocked.Exchange(ref _dirty, 1);
                }),
            BalanceSettingKind.Integer => Ui.Number(
                setting.Name,
                () => double.TryParse(setting.Draft, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var value) ? value : 0,
                value =>
                {
                    setting.SetDraft(Math.Round(value).ToString(CultureInfo.InvariantCulture));
                    Interlocked.Exchange(ref _dirty, 1);
                },
                int.MinValue, int.MaxValue, 1, "0"),
            BalanceSettingKind.Number => Ui.Number(
                setting.Name,
                () => double.TryParse(setting.Draft, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value) ? value : 0,
                value =>
                {
                    setting.SetDraft(value.ToString("R", CultureInfo.InvariantCulture));
                    Interlocked.Exchange(ref _dirty, 1);
                },
                -1e12, 1e12, 0.1, "0.######"),
            _ => Ui.TextField(
                setting.Name, () => setting.Draft, value =>
                {
                    setting.SetDraft(value);
                    Interlocked.Exchange(ref _dirty, 1);
                }, maximumLength: 16_384,
                commitMode: UiCommitMode.OnCommit)
        };
        editor.EnabledWhen(() => setting.IsEditable);
        return Ui.Column(
            editor,
            Ui.Text(setting.Description ?? "", UiTextTone.Muted)
                .VisibleWhen(() => !string.IsNullOrWhiteSpace(setting.Description)),
            Ui.Text(
                    () => $"Allowed range: {setting.AllowedRange}", UiTextTone.Muted)
                .VisibleWhen(() => setting.AllowedRange is not null),
            Ui.Button("Reset", () => setting.Reset())
                .VisibleWhen(() => setting.IsModified),
            Ui.Text(() => setting.ValidationError ?? "Invalid value.", UiTextTone.Warning)
                .VisibleWhen(() => !setting.IsValid));
    }

    private long BalanceUiRevision()
    {
        var snapshot = CommunityBalanceState.Snapshot;
        return (snapshot is null ? 0 : RuntimeHelpers.GetHashCode(snapshot)) * 397L +
               Volatile.Read(ref _componentUiRevision);
    }

    private UiComponent BuildServerModsComponents() => Ui.Dynamic(
        BuildServerModsPage,
        ServerModsUiRevision);

    private IReadOnlyList<UiComponent> BuildServerModsPage()
    {
        var mods = Volatile.Read(ref _serverMods)
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(_selectedServerModFileName))
        {
            var selected = mods.FirstOrDefault(mod => mod.FileName.Equals(
                _selectedServerModFileName,
                StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
                return BuildServerModConfigurationPage(selected);
            _selectedServerModFileName = null;
        }

        var result = new List<UiComponent>
        {
            Ui.Section("Server mod library",
                Ui.Text(
                    "Enable, load, reload, and unload the managed mods installed on this server.",
                    UiTextTone.Muted),
                Ui.Button("Refresh server mods", UiIcon.Refresh,
                        () => RequestServerMods(refreshDirectory: true))
                    .EnabledWhen(() => !NetworkBusy()))
        };
        if (mods.Length == 0)
        {
            result.Add(Ui.Text(NetworkBusy()
                ? "Requesting the server mod list..."
                : "No server mods were returned.", UiTextTone.Muted));
            return result;
        }

        result.AddRange(mods.Select(BuildServerModCard));
        return result;
    }

    private UiComponent BuildServerModCard(ServerModEnvelope mod)
    {
        var children = new List<UiComponent>
        {
            BuildServerModStatus(mod),
            Ui.Text(string.IsNullOrWhiteSpace(mod.Version)
                ? mod.FileName
                : $"{mod.FileName}  ·  {mod.Version}", UiTextTone.Muted),
            BuildServerModEnabledToggle(mod),
            BuildServerModActions(mod, includeConfigure: true)
        };
        if (HasPendingServerModDraft(mod))
            children.Add(Ui.Status("Unsaved settings", UiStatusTone.Warning));
        AddServerModDetails(children, mod);
        return Ui.Card(mod.DisplayName, children.ToArray());
    }

    private IReadOnlyList<UiComponent> BuildServerModConfigurationPage(
        ServerModEnvelope mod)
    {
        EnsureServerModDraft(mod);
        var result = new List<UiComponent>
        {
            Ui.Button("Back to server mods", UiIcon.ChevronLeft, () =>
            {
                _selectedServerModFileName = null;
                Interlocked.Increment(ref _componentUiRevision);
            }),
            Ui.Text(mod.DisplayName, UiTextTone.Accent),
            Ui.Text(string.IsNullOrWhiteSpace(mod.Version)
                ? mod.FileName
                : $"{mod.FileName}  ·  {mod.Version}", UiTextTone.Muted),
            Ui.Section("Mod status",
                BuildServerModStatus(mod),
                BuildServerModEnabledToggle(mod),
                BuildServerModActions(mod, includeConfigure: false))
        };
        if (!string.IsNullOrWhiteSpace(mod.Description))
            result.Add(Ui.Text(mod.Description, UiTextTone.Muted));
        if (mod.Configuration.Count == 0)
        {
            result.Add(Ui.Section("Configuration",
                Ui.Text("This server mod does not expose configurable settings.",
                    UiTextTone.Muted)));
            return result;
        }

        foreach (var section in mod.Configuration.GroupBy(
                     entry => entry.Section,
                     StringComparer.OrdinalIgnoreCase))
        {
            result.Add(Ui.Section(section.Key,
                section.Select(entry => BuildServerModSetting(mod, entry)).ToArray()));
        }
        return result;
    }

    private UiComponent BuildServerModStatus(ServerModEnvelope mod) =>
        mod.LastError is not null
            ? Ui.Status("Error", UiStatusTone.Error)
                .WithTooltip(mod.LastError)
            : mod.Loaded
                ? Ui.Status("Loaded", UiStatusTone.Success)
                : Ui.Status("Unloaded", UiStatusTone.Neutral);

    private UiComponent BuildServerModEnabledToggle(ServerModEnvelope mod) =>
        Ui.Toggle("Enabled", () => mod.Enabled, enabled =>
                SendModCommand(
                    ServerAdminOperations.SetModEnabled,
                    mod.FileName,
                    enabled))
            .EnabledWhen(() =>
                !NetworkBusy() &&
                !HasPendingServerModDraft(mod) &&
                mod.CanManage &&
                (!mod.Enabled || mod.CanStop))
            .WithTooltip(() => ServerModActionBlockReason(mod));

    private UiComponent BuildServerModActions(
        ServerModEnvelope mod,
        bool includeConfigure)
    {
        return Ui.Row(
            Ui.Button("Configure", UiIcon.Settings, () =>
                {
                    EnsureServerModDraft(mod);
                    _selectedServerModFileName = mod.FileName;
                    Interlocked.Increment(ref _componentUiRevision);
                })
                .VisibleWhen(() => includeConfigure && mod.Configuration.Count > 0)
                .EnabledWhen(() => CanOpenServerModConfiguration(mod))
                .WithTooltip(() => CanOpenServerModConfiguration(mod)
                    ? null
                    : "Apply or cancel the pending changes for the other server mod first."),
            Ui.Button("Reload", UiIcon.Reload, () =>
                    SendModCommand(ServerAdminOperations.ReloadMod, mod.FileName))
                .VisibleWhen(() => mod.Loaded)
                .EnabledWhen(() =>
                    !NetworkBusy() && !HasPendingServerModDraft(mod) && mod.CanStop)
                .WithTooltip(() => ServerModActionBlockReason(mod)),
            Ui.Button("Unload", UiIcon.Power, () =>
                    SendModCommand(ServerAdminOperations.UnloadMod, mod.FileName))
                .VisibleWhen(() => mod.Loaded)
                .EnabledWhen(() =>
                    !NetworkBusy() && !HasPendingServerModDraft(mod) && mod.CanStop)
                .WithTooltip(() => ServerModActionBlockReason(mod)),
            Ui.Button("Load", UiIcon.Play, () =>
                    SendModCommand(ServerAdminOperations.LoadMod, mod.FileName))
                .VisibleWhen(() => !mod.Loaded)
                .EnabledWhen(() =>
                    !NetworkBusy() && !HasPendingServerModDraft(mod) && mod.CanManage)
                .WithTooltip(() => ServerModActionBlockReason(mod)));
    }

    private static void AddServerModDetails(
        ICollection<UiComponent> children,
        ServerModEnvelope mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.LastError))
            children.Add(Ui.Text(mod.LastError, UiTextTone.Error));
        if (!string.IsNullOrWhiteSpace(mod.ManagementNote))
            children.Add(Ui.Text(mod.ManagementNote, UiTextTone.Muted));
        if (mod.Dependencies.Count > 0)
            children.Add(Ui.Text(
                $"Requires: {string.Join(", ", mod.Dependencies)}",
                UiTextTone.Muted));
        if (!string.IsNullOrWhiteSpace(mod.Description))
            children.Add(Ui.Text(mod.Description, UiTextTone.Muted));
    }

    private long ServerModsUiRevision() =>
        RuntimeHelpers.GetHashCode(Volatile.Read(ref _serverMods)) * 397L +
        Volatile.Read(ref _componentUiRevision);
    private UiComponent BuildServerModSetting(
        ServerModEnvelope mod,
        ServerModConfigurationEntry entry)
    {
        JsonElement Draft() => GetServerModDraftValue(entry);
        void Stage(JsonElement value) => SetServerModDraftValue(entry, value);

        UiComponent editor = entry.ValueType switch
        {
            "bool" => Ui.Toggle(entry.Key,
                () => Draft().GetBoolean(),
                value => Stage(JsonSerializer.SerializeToElement(value))),
            "int" => Ui.Number(entry.Key,
                () => Draft().GetInt32(),
                value => Stage(JsonSerializer.SerializeToElement(value)),
                entry.Minimum?.GetInt32() ?? int.MinValue,
                entry.Maximum?.GetInt32() ?? int.MaxValue,
                // A server-mod callback updates only the local draft. Recording
                // each spinner step immediately prevents the visual refresh from
                // restoring the server value before the control loses focus.
                1, "0", UiCommitMode.Immediate),
            "float" => Ui.Number(entry.Key,
                () => Draft().GetSingle(),
                value => Stage(JsonSerializer.SerializeToElement(value)),
                entry.Minimum?.GetSingle() ?? -1e12f,
                entry.Maximum?.GetSingle() ?? 1e12f,
                0.1f, "0.###", UiCommitMode.Immediate),
            "double" => Ui.Number(entry.Key,
                () => Draft().GetDouble(),
                value => Stage(JsonSerializer.SerializeToElement(value)),
                entry.Minimum?.GetDouble() ?? -1e12,
                entry.Maximum?.GetDouble() ?? 1e12,
                0.1, "0.######", UiCommitMode.Immediate),
            "string" => Ui.TextField(entry.Key,
                () => Draft().GetString() ?? "",
                value => Stage(JsonSerializer.SerializeToElement(value)),
                maximumLength: 4096,
                commitMode: UiCommitMode.OnCommit),
            "enum" => Ui.Choice(entry.Key,
                () => Draft().GetString() ?? "",
                value => Stage(JsonSerializer.SerializeToElement(value)),
                entry.Choices),
            _ => Ui.Text($"{entry.Key}: unsupported type", UiTextTone.Warning)
        };
        editor.EnabledWhen(() => !NetworkBusy());
        return Ui.Column(
            editor,
            Ui.Text(entry.Description, UiTextTone.Muted)
                .VisibleWhen(() => !string.IsNullOrWhiteSpace(entry.Description)));
    }

    private void EnsureServerModDraft(ServerModEnvelope mod)
    {
        if (string.Equals(
                _serverModDraftFileName,
                mod.FileName,
                StringComparison.OrdinalIgnoreCase))
            return;
        if (Volatile.Read(ref _serverModDraftDirty) != 0) return;
        ResetServerModDraft(mod);
    }

    private void ResetServerModDraft(ServerModEnvelope mod)
    {
        var values = mod.Configuration.ToDictionary(
            entry => ServerModSettingId(entry.Section, entry.Key),
            entry => entry.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
        _serverModDraftFileName = mod.FileName;
        Volatile.Write(ref _serverModOriginalValues, values);
        Volatile.Write(
            ref _serverModDraftValues,
            new Dictionary<string, JsonElement>(values, StringComparer.OrdinalIgnoreCase));
        Interlocked.Exchange(ref _serverModDraftDirty, 0);
    }

    private void ClearServerModDraft()
    {
        _selectedServerModFileName = null;
        _serverModDraftFileName = null;
        Volatile.Write(
            ref _serverModOriginalValues,
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));
        Volatile.Write(
            ref _serverModDraftValues,
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase));
        Interlocked.Exchange(ref _serverModDraftDirty, 0);
    }

    private void CancelServerModDraft()
    {
        var original = Volatile.Read(ref _serverModOriginalValues);
        Volatile.Write(
            ref _serverModDraftValues,
            new Dictionary<string, JsonElement>(original, StringComparer.OrdinalIgnoreCase));
        Interlocked.Exchange(ref _serverModDraftDirty, 0);
    }

    private JsonElement GetServerModDraftValue(ServerModConfigurationEntry entry)
    {
        var draft = Volatile.Read(ref _serverModDraftValues);
        return draft.TryGetValue(ServerModSettingId(entry.Section, entry.Key), out var value)
            ? value
            : entry.Value;
    }

    private void SetServerModDraftValue(
        ServerModConfigurationEntry entry,
        JsonElement value)
    {
        var updated = new Dictionary<string, JsonElement>(
            Volatile.Read(ref _serverModDraftValues),
            StringComparer.OrdinalIgnoreCase)
        {
            [ServerModSettingId(entry.Section, entry.Key)] = value.Clone()
        };
        Volatile.Write(ref _serverModDraftValues, updated);
        var original = Volatile.Read(ref _serverModOriginalValues);
        var dirty = updated.Any(item =>
            !original.TryGetValue(item.Key, out var previous) ||
            !JsonElement.DeepEquals(previous, item.Value));
        Interlocked.Exchange(ref _serverModDraftDirty, dirty ? 1 : 0);
    }

    private bool HasPendingServerModDraft(ServerModEnvelope mod) =>
        Volatile.Read(ref _serverModDraftDirty) != 0 &&
        string.Equals(
            _serverModDraftFileName,
            mod.FileName,
            StringComparison.OrdinalIgnoreCase);

    private bool CanOpenServerModConfiguration(ServerModEnvelope mod) =>
        Volatile.Read(ref _serverModDraftDirty) == 0 ||
        string.Equals(
            _serverModDraftFileName,
            mod.FileName,
            StringComparison.OrdinalIgnoreCase);

    private string? ServerModActionBlockReason(ServerModEnvelope mod) =>
        HasPendingServerModDraft(mod)
            ? "Apply or cancel this mod's pending configuration changes first."
            : mod.StopBlockReason;

    private static string ServerModSettingId(string section, string key) =>
        section + "\n" + key;
    private UiComponent BuildCompatibilityComponents() => Ui.Section(
        "Mod compatibility",
        Ui.Text(
            "The handshake channel exchanges manifests only. Administration uses a separate channel " +
            "on the same TCP endpoint. This version never downloads or executes a package.",
            UiTextTone.Muted),
        Ui.Text(() => $"Endpoint: {_endpoint?.Value ?? ""}"),
        Ui.Button("Handshake now", () => _handshake?.Refresh()),
        Ui.Text(() => _handshake?.Status ?? "The local handshake client is unavailable.",
            UiTextTone.Muted),
        Ui.Button("Refresh observed clients", RequestModHandshakes)
            .EnabledWhen(() => !NetworkBusy()),
        Ui.Dynamic(BuildCompatibilityRows, CompatibilityUiRevision));

    private IReadOnlyList<UiComponent> BuildCompatibilityRows()
    {
        var result = new List<UiComponent>();
        var hello = _handshake?.Latest;
        if (hello is not null)
        {
            result.Add(Ui.Text(hello.Accepted ? "Accepted" : "Rejected",
                hello.Accepted ? UiTextTone.Accent : UiTextTone.Warning));
            result.Add(Ui.Text(
                $"Protocol {hello.ProtocolVersion} | Briefcase {hello.FrameworkVersion} | " +
                $"Build {hello.GameBuild}", UiTextTone.Muted));
        }
        result.Add(Ui.Text("This client", UiTextTone.Accent));
        result.AddRange(BuildManifestRows(_handshake?.LocalMods ?? []));
        result.Add(Ui.Text("Server components", UiTextTone.Accent));
        result.AddRange(BuildManifestRows(hello?.Mods ?? []));

        var clients = Volatile.Read(ref _handshakeClients);
        result.Add(Ui.Text($"{FormatCount(clients.Length)} active Briefcase client(s)",
            UiTextTone.Muted));
        foreach (var client in clients)
        {
            result.Add(Ui.Text(
                $"{client.RemoteAddress} | Briefcase {client.FrameworkVersion} | Build {client.GameBuild}",
                UiTextTone.Accent));
            result.AddRange(BuildManifestRows(client.Mods));
        }
        return result;
    }

    private static IEnumerable<UiComponent> BuildManifestRows(
        IReadOnlyList<ModHandshakeMod> mods)
    {
        if (mods.Count == 0)
            return [Ui.Text("No manifest has been received yet.", UiTextTone.Muted)];
        return mods.Select(mod => (UiComponent)Ui.Text(
            $"{mod.DisplayName} | {(string.IsNullOrWhiteSpace(mod.Version) ? "-" : mod.Version)} | " +
            $"{(mod.Loaded ? "Loaded" : mod.Enabled ? "Enabled" : "Disabled")} | " +
            $"{(string.IsNullOrWhiteSpace(mod.Sha256) ? "no hash" : mod.Sha256[..Math.Min(12, mod.Sha256.Length)])}"));
    }

    private long CompatibilityUiRevision()
    {
        if (!NetworkBusy() && Environment.TickCount64 >= _nextHandshakeRefresh)
        {
            _nextHandshakeRefresh = Environment.TickCount64 + 2_000;
            RequestModHandshakes();
        }
        var hello = _handshake?.Latest;
        return (hello is null ? 0 : RuntimeHelpers.GetHashCode(hello)) * 397L +
               RuntimeHelpers.GetHashCode(Volatile.Read(ref _handshakeClients));
    }

    private bool NetworkBusy() => Volatile.Read(ref _networkBusy) != 0;

    private ServerConfigurationEnvelope? CurrentConfiguration() =>
        Volatile.Read(ref _serverConfiguration);

    private void CancelServerConfigurationDraft()
    {
        var saved = Volatile.Read(ref _savedServerConfiguration);
        if (saved is not null) Volatile.Write(ref _serverConfiguration, saved);
        Interlocked.Exchange(ref _serverConfigurationDirty, 0);
        _confirmRestart = false;
        Interlocked.Increment(ref _componentUiRevision);
    }

    private void ClearServerConfigurationDraft()
    {
        Volatile.Write(ref _serverConfiguration, null);
        Volatile.Write(ref _savedServerConfiguration, null);
        Interlocked.Exchange(ref _serverConfigurationDirty, 0);
        _confirmRestart = false;
    }

    private void EditConfiguration(
        Func<ServerConfigurationEnvelope, ServerConfigurationEnvelope> edit)
    {
        if (CurrentConfiguration() is { } configuration)
            UpdateServerConfiguration(edit(configuration));
    }

    private UiComponent IntConfiguration(
        string label,
        Func<ServerConfigurationEnvelope, int> get,
        Func<ServerConfigurationEnvelope, int, ServerConfigurationEnvelope> set,
        int minimum,
        int maximum) => Ui.Number(label,
        () => CurrentConfiguration() is { } configuration ? get(configuration) : minimum,
        value => EditConfiguration(configuration => set(configuration, value)),
        minimum, maximum);

    private UiComponent FloatConfiguration(
        string label,
        Func<ServerConfigurationEnvelope, float> get,
        Func<ServerConfigurationEnvelope, float, ServerConfigurationEnvelope> set,
        float minimum,
        float maximum) => Ui.Number(label,
        () => CurrentConfiguration() is { } configuration ? get(configuration) : minimum,
        value => EditConfiguration(configuration => set(configuration, value)),
        minimum, maximum, 0.1f, "0.00");

    private bool IsOnlySelectedMap(string code)
    {
        var rotation = CurrentConfiguration()?.MapRotation;
        return rotation is { Count: 1 } &&
               rotation.Contains(code, StringComparer.OrdinalIgnoreCase);
    }

    private void SetMapSelected(string code, bool selected)
    {
        if (!selected && IsOnlySelectedMap(code)) return;
        EditConfiguration(configuration =>
        {
            var selectedMaps = new HashSet<string>(
                configuration.MapRotation, StringComparer.OrdinalIgnoreCase);
            if (selected) selectedMaps.Add(code);
            else selectedMaps.Remove(code);
            return configuration with
            {
                MapRotation = Maps
                    .Where(map => selectedMaps.Contains(map.Code))
                    .Select(map => map.Code)
                    .ToArray()
            };
        });
    }
}
