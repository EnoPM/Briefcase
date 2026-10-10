using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Threading;

namespace Briefcase.ServerApp;

public sealed record UpdatePanelAppearance(IBrush Background, IBrush Border)
{
    private static UpdatePanelAppearance Create(string background, string border) =>
        new(new SolidColorBrush(Color.Parse(background)), new SolidColorBrush(Color.Parse(border)));

    public static readonly UpdatePanelAppearance Neutral = Create("#211D29", "#3D3446");
    public static readonly UpdatePanelAppearance Missing = Create("#302027", "#985060");
    public static readonly UpdatePanelAppearance Checking = Create("#33291F", "#A87A3E");
    public static readonly UpdatePanelAppearance Available = Create("#202D3C", "#4D83B7");
    public static readonly UpdatePanelAppearance Current = Create("#203128", "#4D996F");
    public static readonly UpdatePanelAppearance Development = Create("#30263D", "#A176D5");
}

public sealed record PlayerTeamGroup(int Index, int Capacity, IReadOnlyList<PlayerRow> Members)
{
    public string Label => AppTranslations.Format("player.team", "Team {0}", Index + 1);
    public string Occupancy => $"{Members.Count} / {Capacity}";
    public bool HasSpace => Members.Count < Capacity;
    public IReadOnlyList<PlayerTeamSlot> Slots => Enumerable.Range(0, Math.Max(Capacity, Members.Count))
        .Select(position => new PlayerTeamSlot(Index, position,
            position < Members.Count ? Members[position] : null)).ToArray();
}

public sealed record PlayerTeamSlot(int Team, int Position, PlayerRow? Player)
{
    public bool HasPlayer => Player is not null;
    public bool IsEmpty => Player is null;
}

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IReadOnlyList<DemoServerProfile> _demoProfiles = [];
    public bool IsDemo { get; }
    private readonly Dictionary<string, ServerLiveClient> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _connectionGate = new();
    private readonly IServerTransport _serverTransport = new WindowsNamedPipeTransport();
    public event Action? LiveStateChanged;

    private void OnLiveStateChanged()
    {
        if (!_disposed) LiveStateChanged?.Invoke();
    }

    private static TimeSpan ProcessUptime(Process? process)
    {
        if (process is null) return TimeSpan.Zero;
        try
        {
            var uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime();
            return uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return TimeSpan.FromSeconds(15);
        }
    }

    private ServerSnapshot SnapshotFor(ServerLocation location, int? pid, TimeSpan processUptime)
    {
        lock (_connectionGate)
        {
            if (_connections.TryGetValue(location.LiveKey, out var existing) &&
                (pid is null || existing.ProcessId != pid))
            {
                existing.StateChanged -= OnLiveStateChanged;
                existing.Dispose();
                _connections.Remove(location.LiveKey);
            }
            if (pid is { } processId && !_connections.ContainsKey(location.LiveKey))
                _connections.Add(location.LiveKey,
                    new ServerLiveClient(processId, _serverTransport, OnLiveStateChanged,
                        location.IsInstance ? location.InstanceId : null));
            var live = _connections.TryGetValue(location.LiveKey, out var connection)
                ? connection.Latest : null;
            return ServerSnapshotReader.Read(location, pid, live,
                connection?.Status ?? LiveChannelStatus.Disconnected, processUptime,
                connection?.ConnectionAge);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _messageClearTimer.Stop();
        _updateRevision++;
        lock (_connectionGate)
        {
            foreach (var connection in _connections.Values)
            {
                connection.StateChanged -= OnLiveStateChanged;
                connection.Dispose();
            }
            _connections.Clear();
        }
    }
    private string _win64Path = "";
    private string _message = "";
    private string _serverName = "—";
    private string _serverState = "Offline";
    private string _mapName = "—";
    private string _gameMode = "—";
    private string _playerCountText = "—";
    private ServerNetworkStatus _networkStatus = ServerNetworkStatus.Unknown;
    private string _dataSource = "No server selected";
    private bool _isRunning;
    private bool _isBusy;
    private bool _hasRunningDataServers;
    private bool _isSelectionLoading;
    private int _selectionRevision;
    private bool _hasPlayerRoster;
    private double _progressPercent;
    private bool _progressIndeterminate = true;
    private ServerRow? _selectedServer;
    private ServerInstallation? _selectedInstallationTarget;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _teamGate = new(1, 1);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly DispatcherTimer _messageClearTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly Dictionary<string, UpdateCacheEntry> _updateCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _runtimeChecked;
    private sealed record UpdateCacheEntry(string GameStatus, string BriefcaseStatus,
        string GameVersion, string BriefcaseVersion, bool GameAvailable, bool BriefcaseAction,
        string BriefcaseActionLabel,
        UpdatePanelAppearance GameAppearance, UpdatePanelAppearance BriefcaseAppearance);
    private enum UpdateComponent { Game, Briefcase, Runtime }
    private int _updateRevision;
    private bool _disposed;
    private bool _isCheckingUpdates;
    private bool _gameUpdateAvailable;
    private bool _briefcaseActionVisible;
    private bool _runtimeInstallVisible;
    private string _gameUpdateStatus = AppTranslations.Get("updates.waiting");
    private string _briefcaseUpdateStatus = AppTranslations.Get("updates.waiting");
    private string _runtimeStatus = AppTranslations.Get("updates.waiting");
    private string _briefcaseActionLabel = AppTranslations.Get("updates.install_briefcase");
    private string _gameInstalledVersion = "—";
    private string _briefcaseInstalledVersion = "—";
    private string _runtimeInstalledVersion = "—";
    private UpdatePanelAppearance _gameAppearance = UpdatePanelAppearance.Neutral;
    private UpdatePanelAppearance _briefcaseAppearance = UpdatePanelAppearance.Neutral;
    private UpdatePanelAppearance _runtimeAppearance = UpdatePanelAppearance.Neutral;
    public ServerRegistry Registry { get; }
    public ObservableCollection<ServerRow> Servers => Registry.Servers;
    public ObservableCollection<ServerInstallation> Installations => Registry.Installations;
    public ObservableCollection<string> CatalogUrls => Registry.CatalogUrls;
    public ObservableCollection<MarketplaceMod> MarketplaceMods { get; } = [];
    public int FormAssetsRevision { get; private set; }
    public bool HasMarketplaceMods => MarketplaceMods.Count > 0;
    public bool NoMarketplaceMods => MarketplaceMods.Count == 0;
    private string _catalogUrlDraft = "";
    public string CatalogUrlDraft { get => _catalogUrlDraft; set => Set(ref _catalogUrlDraft, value); }

    public MainViewModel(bool demo = false, ServerRegistry? registry = null)
    {
        MarketplaceMods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasMarketplaceMods));
            OnPropertyChanged(nameof(NoMarketplaceMods));
        };
        Mods.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstalledMods));
            OnPropertyChanged(nameof(NoInstalledMods));
        };
        IsDemo = demo;
        _messageClearTimer.Tick += (_, _) =>
        {
            if (!IsBusy) Message = IsDemo ? AppTranslations.Get("status.demo_data") : "";
        };
        Registry = registry ?? new ServerRegistry(inMemory: demo);
        if (demo)
        {
            _demoProfiles = DemoData.CreateProfiles();
            foreach (var profile in _demoProfiles) Servers.Add(profile.Row);
            foreach (var mod in DemoData.Marketplace()) MarketplaceMods.Add(mod);
            CatalogUrls.Add("https://example.invalid/briefcase/catalog.json");
        }
        SelectedServer = Servers.FirstOrDefault();
        if (Registry.LoadError.Length > 0) Message = Registry.LoadError;
        if (demo) Message = AppTranslations.Get("status.demo_data");
    }

    public ServerRow? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (Set(ref _selectedServer, value))
            {
                Message = IsDemo ? AppTranslations.Get("status.demo_data") : "";
                _selectionRevision++;
                _updateRevision++;
                IsCheckingUpdates = false;
                GameUpdateAvailable = false;
                BriefcaseActionVisible = false;
                GameUpdateStatus = AppTranslations.Get("updates.waiting");
                BriefcaseUpdateStatus = AppTranslations.Get("updates.waiting");
                GameInstalledVersion = "—";
                BriefcaseInstalledVersion = "—";
                GameAppearance = UpdatePanelAppearance.Neutral;
                BriefcaseAppearance = UpdatePanelAppearance.Neutral;
                IsSelectionLoading = value is not null && !IsDemo;
                IsRunning = false;
                HasPlayerRoster = false;
                Players.Clear();
                PlayerTeams.Clear();
                ShowPlayerTeams = false;
                ShowTeamUnavailable = false;
                Mods.Clear();
                ServerName = "—";
                ServerState = "Offline";
                MapName = "—";
                GameMode = "—";
                PlayerCountText = "—";
                NetworkStatus = ServerNetworkStatus.Unknown;
                DataSource = "No server selected";
                OnPropertyChanged(nameof(RosterMessage));
                Win64Path = value?.Win64 ?? "";
                SelectedInstallationTarget = Installations.FirstOrDefault(item =>
                    value is not null && item.Win64.Equals(value.Win64, StringComparison.OrdinalIgnoreCase));
                OnPropertyChanged(nameof(HasSelectedServer));
                OnPropertyChanged(nameof(SelectedServerName));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanDelete));
                OnPropertyChanged(nameof(CanManageMods));
                if (IsDemo) ApplyDemoProfile();
            }
        }
    }
    public bool HasSelectedServer => SelectedServer is not null;
    public ServerInstallation? SelectedInstallationTarget
    {
        get => _selectedInstallationTarget;
        set { if (Set(ref _selectedInstallationTarget, value)) OnPropertyChanged(nameof(CanMigrateServer)); }
    }
    public bool CanMigrateServer => SelectedServer is not null && SelectedInstallationTarget is not null &&
        !IsRunning && !IsBusy && !IsDemo &&
        !SelectedServer.Win64.Equals(SelectedInstallationTarget.Win64, StringComparison.OrdinalIgnoreCase);
    public string SelectedServerName => SelectedServer?.Name ?? AppTranslations.Get("status.select_server");
    public bool CanDelete => HasSelectedServer && !IsBusy;
    public bool CanInstall => !IsBusy && !IsDemo;
    public bool IsCheckingUpdates { get => _isCheckingUpdates; private set { if (Set(ref _isCheckingUpdates, value)) UpdateActions(); } }
    public bool CanCheckUpdates => !IsCheckingUpdates && !IsBusy && !IsDemo;
    public bool GameUpdateAvailable { get => _gameUpdateAvailable; private set { if (Set(ref _gameUpdateAvailable, value)) UpdateActions(); } }
    public bool BriefcaseActionVisible { get => _briefcaseActionVisible; private set { if (Set(ref _briefcaseActionVisible, value)) UpdateActions(); } }
    public bool RuntimeInstallVisible { get => _runtimeInstallVisible; private set { if (Set(ref _runtimeInstallVisible, value)) UpdateActions(); } }
    public bool CanUpdateGame => GameUpdateAvailable && !IsRunning && !IsBusy;
    public bool CanInstallBriefcase => BriefcaseActionVisible && !IsRunning && !IsBusy &&
        (Registry.BriefcaseUpdatesEnabled || BriefcaseInstalledVersion == "—");
    public bool CanInstallRuntime => RuntimeInstallVisible && !IsBusy;
    public string GameUpdateStatus { get => _gameUpdateStatus; private set => Set(ref _gameUpdateStatus, value); }
    public string BriefcaseUpdateStatus { get => _briefcaseUpdateStatus; private set => Set(ref _briefcaseUpdateStatus, value); }
    public string RuntimeStatus { get => _runtimeStatus; private set => Set(ref _runtimeStatus, value); }
    public string BriefcaseActionLabel { get => _briefcaseActionLabel; private set => Set(ref _briefcaseActionLabel, value); }
    public string GameInstalledVersion { get => _gameInstalledVersion; private set => Set(ref _gameInstalledVersion, value); }
    public string BriefcaseInstalledVersion { get => _briefcaseInstalledVersion; private set => Set(ref _briefcaseInstalledVersion, value); }
    public string RuntimeInstalledVersion { get => _runtimeInstalledVersion; private set => Set(ref _runtimeInstalledVersion, value); }
    public UpdatePanelAppearance GameAppearance { get => _gameAppearance; private set => Set(ref _gameAppearance, value); }
    public UpdatePanelAppearance BriefcaseAppearance { get => _briefcaseAppearance; private set => Set(ref _briefcaseAppearance, value); }
    public UpdatePanelAppearance RuntimeAppearance { get => _runtimeAppearance; private set => Set(ref _runtimeAppearance, value); }
    public bool IsSelectionLoading { get => _isSelectionLoading; private set => Set(ref _isSelectionLoading, value); }
    public void CompleteSelectionLoading(ServerRow selected)
    {
        if (SelectedServer == selected) IsSelectionLoading = false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<PlayerRow> Players { get; } = [];
    public ObservableCollection<PlayerTeamGroup> PlayerTeams { get; } = [];
    private bool _showPlayerTeams;
    public bool ShowPlayerTeams { get => _showPlayerTeams; private set { if (Set(ref _showPlayerTeams, value)) OnPropertyChanged(nameof(CanArrangeTeams)); } }
    private bool _showTeamUnavailable;
    public bool ShowTeamUnavailable { get => _showTeamUnavailable; private set => Set(ref _showTeamUnavailable, value); }
    public bool CanArrangeTeams => ShowPlayerTeams && ServerState == "Lobby" && !IsDemo;
    public int TeamSize => GameMode.Equals("Trio", StringComparison.OrdinalIgnoreCase) ? 3 : 2;
    public ObservableCollection<ModRow> Mods { get; } = [];
    public bool HasInstalledMods => Mods.Count > 0;
    public bool NoInstalledMods => Mods.Count == 0;

    public string Win64Path
    {
        get => _win64Path;
        set
        {
            if (Set(ref _win64Path, value))
                OnPropertyChanged(nameof(ServerPathHint));
        }
    }
    public string ServerPathHint => string.IsNullOrWhiteSpace(Win64Path)
        ? AppTranslations.Get("status.server_path_hint")
        : Win64Path;
    public string Message
    {
        get => _message;
        private set
        {
            _messageClearTimer.Stop();
            if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public double ProgressPercent { get => _progressPercent; private set => Set(ref _progressPercent, value); }
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => Set(ref _progressIndeterminate, value); }
    public string ServerName { get => _serverName; private set => Set(ref _serverName, value); }
    public string ServerState { get => _serverState; private set { if (Set(ref _serverState, value)) { OnPropertyChanged(nameof(ServerStateDisplay)); OnPropertyChanged(nameof(CanArrangeTeams)); } } }
    public string ServerStateDisplay => AppTranslations.Get("state." + ServerState, ServerState);
    public string MapName { get => _mapName; private set => Set(ref _mapName, value); }
    public string GameMode { get => _gameMode; private set { if (Set(ref _gameMode, value)) OnPropertyChanged(nameof(TeamSize)); } }
    public string PlayerCountText { get => _playerCountText; private set => Set(ref _playerCountText, value); }
    public ServerNetworkStatus NetworkStatus
    {
        get => _networkStatus;
        private set
        {
            if (!Set(ref _networkStatus, value)) return;
            OnPropertyChanged(nameof(NetworkAddress));
            OnPropertyChanged(nameof(CanCopyNetworkAddress));
            OnPropertyChanged(nameof(NetworkFirewall));
            OnPropertyChanged(nameof(NetworkUpnp));
            OnPropertyChanged(nameof(NetworkEos));
            OnPropertyChanged(nameof(NetworkFirewallColor));
            OnPropertyChanged(nameof(NetworkUpnpColor));
            OnPropertyChanged(nameof(NetworkEosColor));
        }
    }
    public string NetworkAddress => CanCopyNetworkAddress ? NetworkStatus.Address : "—";
    public bool CanCopyNetworkAddress => NetworkStatus.Address.Length > 0;
    public string NetworkFirewall => NetworkLabel(NetworkStatus.Firewall, "network.allowed", "network.blocked");
    public string NetworkUpnp => NetworkLabel(NetworkStatus.Upnp, "network.mapped", "network.failed");
    public string NetworkEos => NetworkLabel(NetworkStatus.Eos, "network.connected", "network.failed");
    public IBrush NetworkFirewallColor => NetworkColor(NetworkStatus.Firewall);
    public IBrush NetworkUpnpColor => NetworkColor(NetworkStatus.Upnp);
    public IBrush NetworkEosColor => NetworkColor(NetworkStatus.Eos);

    private static string NetworkLabel(NetworkCheck check, string readyKey, string failedKey) => check switch
    {
        NetworkCheck.Ready => AppTranslations.Get(readyKey),
        NetworkCheck.Failed => AppTranslations.Get(failedKey),
        NetworkCheck.Disabled => AppTranslations.Get("network.disabled"),
        _ => AppTranslations.Get("network.unknown")
    };

    private static IBrush NetworkColor(NetworkCheck check) => check switch
    {
        NetworkCheck.Ready => new SolidColorBrush(Color.Parse("#67C99B")),
        NetworkCheck.Failed => new SolidColorBrush(Color.Parse("#E78391")),
        NetworkCheck.Disabled => new SolidColorBrush(Color.Parse("#BAA5D0")),
        _ => new SolidColorBrush(Color.Parse("#8F859B"))
    };
    public string DataSource { get => _dataSource; private set { if (Set(ref _dataSource, value)) OnPropertyChanged(nameof(DataSourceDisplay)); } }
    public string DataSourceDisplay => AppTranslations.Get("source." + DataSource, DataSource);
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value)) UpdateActions();
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) UpdateActions();
        }
    }
    public bool HasRunningDataServers
    {
        get => _hasRunningDataServers;
        private set => Set(ref _hasRunningDataServers, value);
    }
    public bool HasPlayerRoster { get => _hasPlayerRoster; private set => Set(ref _hasPlayerRoster, value); }
    public bool CanStart => HasSelectedServer && !IsRunning && !IsBusy;
    public bool CanStop => IsRunning && !IsBusy;
    public bool CanRestart => IsRunning && !IsBusy;
    public bool CanManageMods => HasSelectedServer && !IsRunning && !IsBusy && !IsDemo;
    public string RosterMessage => !IsRunning ? AppTranslations.Get("status.roster_start")
        : HasPlayerRoster ? Players.Count == 0 ? AppTranslations.Get("status.roster_empty") : ""
        : AppTranslations.Get("status.roster_waiting");

    public ServerRow AddServer(string executable, bool managed = false, string? serverName = null)
    {
        if (IsDemo) throw new InvalidOperationException("The screenshot preview cannot add servers.");
        var row = Registry.Add(executable, managed, serverName);
        SelectedServer = row;
        ReportTransient(AppTranslations.Get("status.server_added"));
        return row;
    }

    public ServerRow CreateServerFromInstallation(ServerInstallation installation, string serverName)
    {
        if (!Installations.Contains(installation))
            throw new InvalidOperationException("The selected server version is no longer installed.");
        return AddServer(installation.Shipping, managed: true,
            serverName: AppDataStore.ValidateServerName(serverName));
    }

    public async Task<bool> InstallSteamVersionAsync(string branch, string? betaPassword, string? installRoot = null)
    {
        if (IsDemo) { ReportPreviewOnly(); return false; }
        if (IsBusy) return false;
        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressPercent = 0;
        try
        {
            branch = ServerInstallation.ValidateBranch(branch);
            var existing = Installations.FirstOrDefault(item => item.Branch == branch);
            var root = existing?.InstallRoot ?? Path.GetFullPath(
                installRoot ?? AppDataStore.VersionInstallRoot(Registry.DataDirectory, branch));
            if (existing is not null && ServerLocation.TryCreate(existing.Win64, out var location, out _))
            {
                using var running = ServerProcess.FindAnyInstallation(location!);
                if (running is not null)
                    throw new InvalidOperationException("Stop all servers using this version before updating it.");
            }
            using var progress = new OperationProgress(ReportProgress);
            var updating = existing is not null && File.Exists(existing.Shipping);
            var tools = AppDataStore.SteamCmdRoot;
            if (ElevatedUpdateService.NeedsElevation(root) || ElevatedUpdateService.NeedsElevation(tools))
                await ElevatedUpdateService.InstallSteamVersionAsync(root, updating, branch, betaPassword,
                    progress, CancellationToken.None);
            else
            {
                try
                {
                    await new SteamCmdService(tools).InstallOrUpdateAsync(root, updating, progress,
                        CancellationToken.None, branch, betaPassword);
                }
                catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex))
                {
                    await ElevatedUpdateService.InstallSteamVersionAsync(root, updating, branch, betaPassword,
                        progress, CancellationToken.None);
                }
            }
            if (existing is null) Registry.AddInstallation(branch, root);
            else Registry.RefreshInstallation(existing);
            ReportTransient(AppTranslations.Format("versions.installed", "Steam branch {0} is ready.", branch));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or ArgumentException or System.Net.Http.HttpRequestException or
            Win32Exception or OperationCanceledException)
        { Message = ex.Message; return false; }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    public async Task MigrateSelectedServerAsync()
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (!CanMigrateServer || SelectedServer is not { } row || SelectedInstallationTarget is not { } target)
            return;
        IsBusy = true;
        try
        {
            Registry.Migrate(row, target);
            Win64Path = row.Win64;
            _updateCache.Clear();
            ReportTransient(AppTranslations.Format("versions.migrated",
                "Server configuration moved to the {0} installation; original game files were kept.", target.Branch));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ArgumentException)
        { Message = ex.Message; }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanMigrateServer));
            await RefreshAsync();
        }
    }

    public void ForgetServer(ServerRow row)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        Registry.Remove(row);
        if (SelectedServer == row) SelectedServer = Servers.FirstOrDefault();
        ReportTransient(AppTranslations.Get("status.server_removed"));
    }

    public async Task RemoveServerAsync(ServerRow row, bool deleteFiles)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (deleteFiles)
            {
                if (!ServerLocation.TryCreate(row, out var location, out var error))
                    throw new InvalidOperationException(error);
                Message = AppTranslations.Get("status.server_deleting");
                await ServerRemoval.DeleteFilesAsync(location!);
            }
            ForgetServer(row);
            if (deleteFiles) ReportTransient(AppTranslations.Get("status.server_deleted"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = ex.Message; }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    public void Report(string text) => Message = text;

    public void ReportTransient(string text)
    {
        Message = text;
        if (HasMessage) _messageClearTimer.Start();
    }

    public IReadOnlyList<ServerRow> RunningDataServers()
    {
        if (IsDemo) return [];
        var source = Registry.DataDirectory;
        var running = new List<ServerRow>();
        foreach (var row in Servers)
        {
            if (!ServerLocation.TryCreate(row, out var location, out _)) continue;
            if (!(row.InstanceRoot.Length > 0 && AppDataStore.IsInside(row.InstanceRoot, source)) &&
                !AppDataStore.IsInside(location!.InstallRoot, source)) continue;
            using var process = ServerProcess.Find(location!);
            if (process is not null) running.Add(row);
        }
        HasRunningDataServers = running.Count > 0;
        return running;
    }

    public async Task<bool> ChangeDataDirectoryAsync(string directory, bool stopRunningServers = false)
    {
        if (IsDemo) { ReportPreviewOnly(); return false; }
        if (IsBusy) return false;
        IsBusy = true;
        await _refreshGate.WaitAsync();
        await _updateGate.WaitAsync();
        try
        {
            _updateRevision++;
            _updateCache.Clear();
            IsCheckingUpdates = false;
            var source = Registry.DataDirectory;
            var target = Path.GetFullPath(directory);
            if (source.Equals(target, StringComparison.OrdinalIgnoreCase)) return true;
            AppDataStore.ValidateMoveDestination(source, target);
            Registry.Save();
            var running = RunningDataServers();
            if (running.Count > 0 && !stopRunningServers)
                throw new InvalidOperationException(AppTranslations.Get("settings.stop_servers_before_move"));
            if (running.Count > 0)
            {
                Message = AppTranslations.Get("settings.stopping_servers");
                foreach (var row in running)
                {
                    if (!ServerLocation.TryCreate(row, out var location, out var error))
                        throw new InvalidDataException(error);
                    await ServerProcess.StopAsync(location!, CancellationToken.None);
                }
            }
            if (RunningDataServers().Count > 0)
                throw new InvalidOperationException(AppTranslations.Get("settings.stop_servers_before_move"));
            Message = AppTranslations.Get("settings.moving_data");
            var selectedId = SelectedServer?.Id;
            var oldDataRetained = await Task.Run(() => AppDataStore.Move(source, target, AppDataStore.LocatorFile));
            Registry.ReloadFromDataDirectory(target);
            SelectedServer = Servers.FirstOrDefault(row => row.Id == selectedId) ?? Servers.FirstOrDefault();
            ReportTransient(AppTranslations.Get(oldDataRetained
                ? "settings.data_moved_old_retained" : "settings.data_moved"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ArgumentException or System.Text.Json.JsonException or
            TimeoutException or System.ComponentModel.Win32Exception)
        {
            Message = ex.Message;
            return false;
        }
        finally
        {
            _updateGate.Release();
            _refreshGate.Release();
            IsBusy = false;
            await RefreshAsync();
        }
    }

    public async Task KickPlayerAsync(PlayerRow player)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (SelectedServer is not { } selected || !IsRunning) return;
        ServerLiveClient? connection;
        lock (_connectionGate)
            _connections.TryGetValue(selected.Id, out connection);
        if (connection?.Latest?.Players.All(item => item.Token != player.Token) != false)
        {
            Message = AppTranslations.Get("status.player_gone");
            return;
        }
        var result = await connection.KickAsync(player.Token, CancellationToken.None);
        if (result.Ok)
            ReportTransient(AppTranslations.Format("status.player_kicked", "{0} was removed from the server.", player.Name));
        else
            Message = AppTranslations.Format("status.kick_failed", "Kick failed: {0}", result.Message);
        await RefreshAsync();
    }

    private void RebuildPlayerTeams(ServerSnapshot snapshot)
    {
        var teamMode = snapshot.GameMode is "Duo" or "Trio";
        var teamDataAvailable = snapshot.Players.Any(player => player.Team is not null);
        ShowTeamUnavailable = IsRunning && snapshot.HasPlayerRoster && teamMode &&
                              snapshot.Players.Count > 0 && !teamDataAvailable;
        ShowPlayerTeams = IsRunning && snapshot.HasPlayerRoster && teamMode && teamDataAvailable;
        if (!ShowPlayerTeams)
        {
            if (PlayerTeams.Count > 0) PlayerTeams.Clear();
            return;
        }
        var capacity = snapshot.GameMode == "Trio" ? 3 : 2;
        var maximum = Math.Clamp((snapshot.MaxPlayers.GetValueOrDefault(12) + capacity - 1) / capacity,
            1, 16);
        var occupied = snapshot.Players.Where(player => player.Team is >= 0 and < 32)
            .GroupBy(player => player.Team!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<PlayerRow>)group.ToArray());
        var indexes = occupied.Keys.Order().ToList();
        for (var index = 0; index < maximum; index++)
        {
            if (occupied.ContainsKey(index)) continue;
            indexes.Add(index);
            break;
        }
        var next = indexes.Order().Select(index => new PlayerTeamGroup(index, capacity,
            occupied.GetValueOrDefault(index) ?? [])).ToArray();
        if (PlayerTeams.Count == next.Length && PlayerTeams.Zip(next).All(pair =>
                pair.First.Index == pair.Second.Index &&
                pair.First.Capacity == pair.Second.Capacity &&
                pair.First.Members.SequenceEqual(pair.Second.Members))) return;
        PlayerTeams.Clear();
        foreach (var team in next) PlayerTeams.Add(team);
    }

    public async Task ChangePlayerTeamAsync(PlayerRow player, int targetTeam, PlayerRow? swapWith)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (!CanArrangeTeams || player.Team is null ||
            targetTeam == player.Team || targetTeam < 0 || targetTeam >= 32) return;
        if (SelectedServer is not { } selected) return;
        if (!await _teamGate.WaitAsync(0)) return;
        try
        {
            ServerLiveClient? connection;
            lock (_connectionGate) _connections.TryGetValue(selected.Id, out connection);
            var live = connection?.Latest;
            var currentPlayer = live?.Players.FirstOrDefault(item => item.Token == player.Token);
            var currentTarget = swapWith is null ? null :
                live?.Players.FirstOrDefault(item => item.Token == swapWith.Token);
            if (live is null || live.State != "Lobby" ||
                currentPlayer?.Team != player.Team ||
                swapWith is not null && (currentTarget?.Team != swapWith.Team ||
                    currentTarget?.Team != targetTeam))
            {
                Message = AppTranslations.Get("status.player_gone");
                return;
            }
            var size = TeamSize;
            if (swapWith is null && live.Players.Count(item => item.Team == targetTeam) >= size)
            {
                Message = AppTranslations.Get("player.team_full");
                return;
            }
            var result = swapWith is null
                ? await connection!.MoveTeamAsync(player.Token, targetTeam, size, CancellationToken.None)
                : await connection!.SwapTeamAsync(player.Token, swapWith.Token, size, CancellationToken.None);
            if (result.Ok)
                ReportTransient(AppTranslations.Get(swapWith is null ? "player.move_success" : "player.swap_success"));
            else
                Message = AppTranslations.Format("player.team_failed", "Team change failed: {0}", result.Message);
            if (result.Ok) await RefreshAsync();
        }
        finally { _teamGate.Release(); }
    }

    public async Task InstallServerAsync(string folder, string? serverName = null)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy) return;
        IsBusy = true;
        ProgressIndeterminate = true;
        try
        {
            if (serverName is not null) serverName = AppDataStore.ValidateServerName(serverName);
            var existingShipping = Path.Combine(Path.GetFullPath(folder), "DeceiveInc", "Binaries", "Win64",
                "DeceiveIncServer-Win64-Shipping.exe");
            if (File.Exists(existingShipping) && File.Exists(Path.Combine(folder, "steamapps", "appmanifest_5007710.acf")))
            {
                AddServer(existingShipping, managed: true, serverName: serverName);
                ReportTransient(AppTranslations.Get("status.existing_server_added"));
                return;
            }
            var sharedInstall = serverName is not null && string.Equals(Path.GetFullPath(folder),
                AppDataStore.ServerInstallRoot(Registry.DataDirectory, serverName), StringComparison.OrdinalIgnoreCase);
            if (serverName is not null && !sharedInstall && Directory.Exists(folder) &&
                Directory.EnumerateFileSystemEntries(folder).Any())
                throw new InvalidDataException("Choose an empty folder for the new server.");
            using var progress = new OperationProgress(ReportProgress);
            var tools = AppDataStore.SteamCmdRoot;
            string shipping;
            if (ElevatedUpdateService.NeedsElevation(folder) || ElevatedUpdateService.NeedsElevation(tools))
            {
                await ElevatedUpdateService.InstallSteamVersionAsync(folder, false, "public", null,
                    progress, CancellationToken.None);
                shipping = existingShipping;
            }
            else
            {
                try
                {
                    shipping = await new SteamCmdService(tools).InstallOrUpdateAsync(folder, false, progress,
                        CancellationToken.None);
                }
                catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex))
                {
                    await ElevatedUpdateService.InstallSteamVersionAsync(folder, false, "public", null,
                        progress, CancellationToken.None);
                    shipping = existingShipping;
                }
            }
            if (!File.Exists(shipping)) throw new IOException("The dedicated server installation is incomplete.");
            AddServer(shipping, managed: true, serverName: serverName);
            ReportTransient(AppTranslations.Get("status.server_installed"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or System.Net.Http.HttpRequestException or Win32Exception)
        {
            Message = ex.Message;
        }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    public async Task UpdateGameAsync()
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy || SelectedServer is null) return;
        IsBusy = true;
        ProgressIndeterminate = true;
        try
        {
            var root = ServerLocation.TryCreate(SelectedServer.Win64, out var location, out var error)
                ? location!.InstallRoot : throw new InvalidDataException(error);
            if (Installations.Any(item => item.Win64.Equals(location!.Win64, StringComparison.OrdinalIgnoreCase) &&
                item.Branch != "public"))
                throw new InvalidOperationException("Update beta server versions from application settings.");
            using var progress = new OperationProgress(ReportProgress);
            var tools = AppDataStore.SteamCmdRoot;
            if (ElevatedUpdateService.NeedsElevation(root) || ElevatedUpdateService.NeedsElevation(tools))
                await ElevatedUpdateService.RunAsync("game", location!.Win64, progress, CancellationToken.None);
            else
            {
                try { await new SteamCmdService(tools).InstallOrUpdateAsync(root, true, progress, CancellationToken.None); }
                catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex))
                { await ElevatedUpdateService.RunAsync("game", location!.Win64, progress, CancellationToken.None); }
            }
            var installation = Installations.FirstOrDefault(item =>
                item.Win64.Equals(location!.Win64, StringComparison.OrdinalIgnoreCase));
            if (installation is not null) Registry.RefreshInstallation(installation);
            ReportTransient(AppTranslations.Get("status.server_current"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or System.Net.Http.HttpRequestException or Win32Exception)
        {
            Message = ex.Message;
        }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    public void AddCatalog()
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        try { Registry.AddCatalog(CatalogUrlDraft.Trim()); CatalogUrlDraft = ""; ReportTransient(AppTranslations.Get("status.catalog_saved")); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Message = ex.Message; }
    }

    public void RemoveCatalog(string url)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        Registry.RemoveCatalog(url);
        ReportTransient(AppTranslations.Get("status.catalog_removed"));
    }

    public async Task RefreshMarketplaceAsync(bool announce = false) => await RunBusyAsync(async _ =>
    {
        var marketplace = new ModMarketplace();
        var list = new List<MarketplaceMod>();
        var sources = CatalogUrls.ToList();
        if (ServerLocation.TryCreate(SelectedServer, out var selected, out var sourceError))
            sources.AddRange(ModCatalog.InstalledCatalogSources(selected!));
        sources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var url in sources)
            list.AddRange(await marketplace.ReadCatalogAsync(url, CancellationToken.None));
        string? formError = null;
        if (ServerLocation.TryCreate(SelectedServer, out var location, out var ignoredError))
        {
            if (SelectedServer is { } current &&
                (!location!.IsInstance || current.Id == location.InstanceId))
            {
                foreach (var mod in list)
                {
                    try
                    {
                        if (await ModMarketplace.RestoreFormAssetsAsync(location, mod, CancellationToken.None))
                            FormAssetsRevision++;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or HttpRequestException)
                    { formError ??= error.Message; }
                }
            }
            list = list.Select(mod => WithMarketplaceStatus(location!, mod)).ToList();
        }
        using var logoGate = new SemaphoreSlim(4);
        var withLogos = await Task.WhenAll(list.Select(async mod =>
        {
            await logoGate.WaitAsync();
            try { return mod with { Logo = ModLogo.ForMarketplaceMod(mod) ??
                await ModLogo.FromUrlAsync(mod.LogoUrl, mod.LogoSha256, CancellationToken.None) }; }
            finally { logoGate.Release(); }
        }));
        MarketplaceMods.Clear();
        foreach (var mod in withLogos.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)) MarketplaceMods.Add(mod);
        var updatedRows = ModCatalog.WithAvailableUpdates(Mods.ToArray(), MarketplaceMods);
        for (var index = 0; index < updatedRows.Count; index++)
            if (!Equals(Mods[index], updatedRows[index])) Mods[index] = updatedRows[index];
        if (formError is not null)
            throw new InvalidDataException(AppTranslations.Format("status.mod_form_update_failed",
                "A mod configuration form could not be updated: {0}", formError));
        return AppTranslations.Get("status.catalog_refreshed", "Mod catalog refreshed.");
    }, announce);

    private static MarketplaceMod WithMarketplaceStatus(ServerLocation location, MarketplaceMod mod)
    {
        var version = ModMarketplace.InstalledVersion(location, mod.Id);
        var exists = Directory.Exists(Path.Combine(location.ModsRoot, mod.Id));
        var updateAvailable = exists && (version.Length == 0 || GitHubReleaseService.IsNewer(mod.Version, version));
        var status = !exists ? AppTranslations.Get("market.not_installed") : version.Length == 0
            ? AppTranslations.Get("market.installed_unknown")
            : updateAvailable
                ? AppTranslations.Format("market.update_from", "Update available from {0}", version)
                : AppTranslations.Get("market.current");
        return mod with { Status = status, IsInstalled = exists, CanApply = !exists || updateAvailable };
    }

    private void RefreshMarketplaceStatuses(ServerLocation location)
    {
        for (var index = 0; index < MarketplaceMods.Count; index++)
        {
            var mod = MarketplaceMods[index];
            var updated = WithMarketplaceStatus(location, mod);
            if (updated.Status != mod.Status || updated.IsInstalled != mod.IsInstalled || updated.CanApply != mod.CanApply)
                MarketplaceMods[index] = updated;
        }
    }

    public async Task InstallMarketplaceModAsync(MarketplaceMod mod) => await RunBusyAsync(async progress =>
    {
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error)) throw new InvalidDataException(error);
        using (var running = ServerProcess.Find(location!))
            if (running is not null) throw new InvalidOperationException("Stop the server before installing or updating a mod.");
        if (!ModMarketplace.BriefcaseRuntimeInstalled(location!))
            throw new InvalidOperationException("Install Briefcase before installing mods.");
        async Task InstallNormallyAsync()
        {
            var market = new ModMarketplace();
            var downloads = AppDataStore.DownloadsRoot;
            var archive = await market.DownloadAsync(mod, downloads, progress, CancellationToken.None);
            try
            {
                var logo = await ModLogo.VerifiedBytesAsync(mod.LogoUrl, mod.LogoSha256, CancellationToken.None);
                if (mod.LogoSha256 is not null && logo is null)
                    throw new InvalidDataException("The mod logo could not be downloaded or verified.");
                var formOverride = await ModMarketplace.DownloadFormOverrideAsync(mod, CancellationToken.None);
                var formTranslations = await ModMarketplace.DownloadFormTranslationsAsync(mod, CancellationToken.None);
                var configSchema = await ModMarketplace.DownloadConfigSchemaAsync(mod, CancellationToken.None);
                await Task.Run(() => ModMarketplace.Install(location!, mod, archive, logo, formOverride,
                    formTranslations, configSchema));
            }
            finally { if (File.Exists(archive)) File.Delete(archive); }
        }
        async Task InstallElevatedAsync() => await ElevatedUpdateService.RunAsync("mod", location!.Win64,
            progress, CancellationToken.None, mod.Catalog, mod.Id, mod.Sha256,
            location.InstanceRoot, location.InstanceId, Registry.DataDirectory);
        if (ElevatedUpdateService.NeedsElevation(location!.Win64) ||
            ElevatedUpdateService.NeedsElevation(location.ModsRoot))
            await InstallElevatedAsync();
        else
        {
            try { await InstallNormallyAsync(); }
            catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex)) { await InstallElevatedAsync(); }
        }
        return AppTranslations.Format("status.mod_installed", "{0} installed or updated. It will load after the next server start.", mod.Name);
    });

    public async Task SetModEnabledAsync(ModRow mod, bool enabled) => await RunBusyAsync(async _ =>
    {
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error)) throw new InvalidDataException(error);
        await Task.Run(() => ModCatalog.SetEnabled(location!, mod.Name, enabled));
        return AppTranslations.Format(enabled ? "status.mod_enabled" : "status.mod_disabled",
            enabled ? "{0} enabled." : "{0} disabled.", mod.Name);
    });

    public async Task UninstallModAsync(ModRow mod) => await RunBusyAsync(async _ =>
    {
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error)) throw new InvalidDataException(error);
        await Task.Run(() => ModCatalog.Uninstall(location!, mod.Name));
        return AppTranslations.Format("status.mod_uninstalled", "{0} uninstalled.", mod.Name);
    });

    public async Task CheckUpdatesAsync(bool force = false)
    {
        var key = Win64Path;
        var revision = ++_updateRevision;
        if (!force && _updateCache.TryGetValue(key, out var remembered))
        {
            ApplyUpdateCache(remembered);
            return;
        }
        if (!IsDemo) IsCheckingUpdates = true;
        RuntimeStatus = AppTranslations.Get("updates.checking");
        GameUpdateStatus = AppTranslations.Get("updates.checking");
        BriefcaseUpdateStatus = AppTranslations.Get("updates.checking");
        GameUpdateAvailable = false;
        BriefcaseActionVisible = false;
        GameAppearance = UpdatePanelAppearance.Checking;
        BriefcaseAppearance = UpdatePanelAppearance.Checking;
        RuntimeAppearance = UpdatePanelAppearance.Checking;
        if (IsDemo)
        {
            GameAppearance = UpdatePanelAppearance.Neutral;
            BriefcaseAppearance = UpdatePanelAppearance.Neutral;
            RuntimeAppearance = UpdatePanelAppearance.Neutral;
            return;
        }
        await _updateGate.WaitAsync();
        try
        {
            if (_disposed || revision != _updateRevision) return;
            if (!force && _updateCache.TryGetValue(key, out remembered))
            {
                ApplyUpdateCache(remembered);
                return;
            }
            if (force || !_runtimeChecked)
            {
                var runtime = await Task.Run(VisualCppInstaller.InstalledVersion);
                if (_disposed || revision != _updateRevision) return;
                _runtimeChecked = true;
                RuntimeInstallVisible = runtime is null;
                RuntimeInstalledVersion = runtime ?? "—";
                RuntimeAppearance = runtime is null ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Current;
                RuntimeStatus = runtime is null ? AppTranslations.Get("updates.runtime_missing") :
                    AppTranslations.Get("updates.runtime_ready", "Installed and ready.");
            }
            else
            {
                RuntimeAppearance = RuntimeInstallVisible ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Current;
                RuntimeStatus = RuntimeInstallVisible ? AppTranslations.Get("updates.runtime_missing") :
                    AppTranslations.Get("updates.runtime_ready", "Installed and ready.");
            }
            if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error))
            {
                GameUpdateStatus = AppTranslations.Get("updates.select_server");
                BriefcaseUpdateStatus = AppTranslations.Get("updates.select_server");
                GameAppearance = SelectedServer is null ? UpdatePanelAppearance.Neutral : UpdatePanelAppearance.Missing;
                BriefcaseAppearance = UpdatePanelAppearance.Neutral;
                return;
            }
            var localBuild = SteamCmdService.InstalledBuildId(location!);
            if (localBuild is not null)
                GameInstalledVersion = AppTranslations.Format("updates.build_version", "Build {0}", localBuild);
            try { BriefcaseInstalledVersion = GitHubReleaseService.InstalledBriefcaseVersion(location!) ?? "—"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { BriefcaseInstalledVersion = "—"; }
            BriefcaseAppearance = BriefcaseInstalledVersion == "—"
                ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Checking;
            var tools = AppDataStore.SteamCmdRoot;
            var installedBranch = Installations.FirstOrDefault(item =>
                item.Win64.Equals(location!.Win64, StringComparison.OrdinalIgnoreCase))?.Branch;
            var gameTask = installedBranch is not null and not "public"
                ? Task.FromResult<(SteamCmdService.BuildCheck? Check, string? Error)>((null,
                    AppTranslations.Get("versions.beta_updates_in_settings",
                        "Update this beta branch from application settings.")))
                : CheckGameAsync(location!, tools);
            var skipBriefcaseRelease = !Registry.BriefcaseUpdatesEnabled && BriefcaseInstalledVersion != "—";
            var briefcaseTask = skipBriefcaseRelease
                ? Task.FromResult<(BriefcaseInstallService.VersionCheck? Check, string? Error)>((null, null))
                : CheckBriefcaseAsync(location!);
            await Task.WhenAll(gameTask, briefcaseTask);
            if (_disposed || revision != _updateRevision) return;
            var game = gameTask.Result;
            var briefcase = briefcaseTask.Result;
            GameInstalledVersion = (game.Check?.Installed ?? localBuild) is { } build
                ? AppTranslations.Format("updates.build_version", "Build {0}", build) : "—";
            BriefcaseInstalledVersion = briefcase.Check?.Installed ?? BriefcaseInstalledVersion;
            GameUpdateAvailable = game.Check?.UpdateAvailable == true;
            GameAppearance = game.Check is { Public: not null, Installed: not null }
                ? GameUpdateAvailable ? UpdatePanelAppearance.Available : UpdatePanelAppearance.Current
                : UpdatePanelAppearance.Neutral;
            GameUpdateStatus = game.Error ?? game.Check?.Error ??
                (game.Check?.UpdateAvailable == true
                    ? AppTranslations.Format("updates.game_available", "Installed build {0}; public build {1}.", game.Check.Installed!, game.Check.Public!)
                    : AppTranslations.Format("updates.game_current", "Up to date (build {0}).", game.Check?.Installed ?? "?"));
            if (skipBriefcaseRelease)
            {
                ShowBriefcaseUpdatesDisabled();
                RememberCurrentUpdates(key);
                return;
            }
            var developmentBuild = BriefcaseInstallService.IsDevelopmentBuild(BriefcaseInstalledVersion);
            BriefcaseActionVisible = !developmentBuild &&
                (briefcase.Check is { InstallNeeded: true } or { UpdateAvailable: true });
            BriefcaseAppearance = developmentBuild ? UpdatePanelAppearance.Development : briefcase.Check?.InstallNeeded == true
                ? UpdatePanelAppearance.Missing : briefcase.Check?.UpdateAvailable == true
                    ? UpdatePanelAppearance.Available : briefcase.Check is not null
                        ? UpdatePanelAppearance.Current : BriefcaseInstalledVersion == "—"
                            ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Neutral;
            BriefcaseActionLabel = briefcase.Check?.InstallNeeded == true
                ? AppTranslations.Get("updates.install_briefcase") : AppTranslations.Get("updates.update_briefcase");
            BriefcaseUpdateStatus = developmentBuild ? AppTranslations.Get("updates.briefcase_development") :
                briefcase.Error ?? (briefcase.Check is null ? AppTranslations.Get("updates.unknown") :
                briefcase.Check.InstallNeeded ? AppTranslations.Format("updates.briefcase_missing", "Not installed; latest release {0}.", briefcase.Check.Latest) :
                briefcase.Check.UpdateAvailable ? AppTranslations.Format("updates.briefcase_available", "Installed {0}; latest {1}.", briefcase.Check.Installed!, briefcase.Check.Latest) :
                AppTranslations.Format("updates.briefcase_current", "Up to date ({0}).", briefcase.Check.Installed!));
            RememberCurrentUpdates(key);
        }
        finally
        {
            if (!_disposed && revision == _updateRevision) IsCheckingUpdates = false;
            _updateGate.Release();
        }
    }

    private void RememberCurrentUpdates(string key)
    {
        if (key.Length == 0) return;
        _updateCache[key] = new UpdateCacheEntry(GameUpdateStatus, BriefcaseUpdateStatus,
            GameInstalledVersion, BriefcaseInstalledVersion, GameUpdateAvailable, BriefcaseActionVisible,
            BriefcaseActionLabel, GameAppearance, BriefcaseAppearance);
    }

    public Task RefreshGameUpdateAsync() => RefreshComponentAsync(UpdateComponent.Game);
    public Task RefreshBriefcaseUpdateAsync() => RefreshComponentAsync(UpdateComponent.Briefcase);
    public Task RefreshRuntimeUpdateAsync() => RefreshComponentAsync(UpdateComponent.Runtime);

    public async Task BriefcaseUpdatePreferenceChangedAsync()
    {
        _updateCache.Clear();
        UpdateActions();
        if (SelectedServer is not null) await RefreshBriefcaseUpdateAsync();
    }

    private void ShowBriefcaseUpdatesDisabled()
    {
        BriefcaseActionVisible = false;
        BriefcaseAppearance = BriefcaseInstallService.IsDevelopmentBuild(BriefcaseInstalledVersion)
            ? UpdatePanelAppearance.Development : UpdatePanelAppearance.Neutral;
        BriefcaseUpdateStatus = AppTranslations.Get("updates.briefcase_disabled");
    }

    private async Task RefreshComponentAsync(UpdateComponent component)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        var key = Win64Path;
        var revision = ++_updateRevision;
        IsCheckingUpdates = true;
        if (component == UpdateComponent.Game)
        {
            GameAppearance = UpdatePanelAppearance.Checking;
            GameUpdateStatus = AppTranslations.Get("updates.checking");
            GameUpdateAvailable = false;
        }
        else if (component == UpdateComponent.Briefcase)
        {
            BriefcaseAppearance = UpdatePanelAppearance.Checking;
            BriefcaseUpdateStatus = AppTranslations.Get("updates.checking");
            BriefcaseActionVisible = false;
        }
        else
        {
            RuntimeAppearance = UpdatePanelAppearance.Checking;
            RuntimeStatus = AppTranslations.Get("updates.checking");
        }
        await _updateGate.WaitAsync();
        try
        {
            if (_disposed || revision != _updateRevision) return;
            if (component == UpdateComponent.Runtime)
            {
                var runtime = await Task.Run(VisualCppInstaller.InstalledVersion);
                if (_disposed || revision != _updateRevision) return;
                _runtimeChecked = true;
                RuntimeInstallVisible = runtime is null;
                RuntimeInstalledVersion = runtime ?? "—";
                RuntimeAppearance = runtime is null ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Current;
                RuntimeStatus = runtime is null ? AppTranslations.Get("updates.runtime_missing") :
                    AppTranslations.Get("updates.runtime_ready", "Installed and ready.");
                return;
            }
            if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error))
            {
                if (component == UpdateComponent.Game)
                {
                    GameUpdateStatus = error;
                    GameAppearance = SelectedServer is null ? UpdatePanelAppearance.Neutral : UpdatePanelAppearance.Missing;
                }
                else
                {
                    BriefcaseUpdateStatus = error;
                    BriefcaseAppearance = UpdatePanelAppearance.Neutral;
                }
                return;
            }
            if (component == UpdateComponent.Game)
            {
                var local = SteamCmdService.InstalledBuildId(location!);
                var tools = AppDataStore.SteamCmdRoot;
                var result = await CheckGameAsync(location!, tools);
                if (_disposed || revision != _updateRevision) return;
                GameInstalledVersion = (result.Check?.Installed ?? local) is { } build
                    ? AppTranslations.Format("updates.build_version", "Build {0}", build) : "—";
                GameUpdateAvailable = result.Check?.UpdateAvailable == true;
                GameAppearance = result.Check is { Public: not null, Installed: not null }
                    ? GameUpdateAvailable ? UpdatePanelAppearance.Available : UpdatePanelAppearance.Current
                    : UpdatePanelAppearance.Neutral;
                GameUpdateStatus = result.Error ?? result.Check?.Error ??
                    (GameUpdateAvailable
                        ? AppTranslations.Format("updates.game_available", "Public build {1} is available.", result.Check!.Installed!, result.Check.Public!)
                        : AppTranslations.Get("updates.game_current", "Up to date."));
            }
            else
            {
                try { BriefcaseInstalledVersion = GitHubReleaseService.InstalledBriefcaseVersion(location!) ?? "—"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { BriefcaseInstalledVersion = "—"; }
                if (!Registry.BriefcaseUpdatesEnabled && BriefcaseInstalledVersion != "—")
                {
                    ShowBriefcaseUpdatesDisabled();
                    RememberCurrentUpdates(key);
                    return;
                }
                var result = await CheckBriefcaseAsync(location!);
                if (_disposed || revision != _updateRevision) return;
                BriefcaseInstalledVersion = result.Check?.Installed ?? BriefcaseInstalledVersion;
                var developmentBuild = BriefcaseInstallService.IsDevelopmentBuild(BriefcaseInstalledVersion);
                BriefcaseActionVisible = !developmentBuild &&
                    (result.Check is { InstallNeeded: true } or { UpdateAvailable: true });
                BriefcaseAppearance = developmentBuild ? UpdatePanelAppearance.Development : result.Check?.InstallNeeded == true
                    ? UpdatePanelAppearance.Missing : result.Check?.UpdateAvailable == true
                        ? UpdatePanelAppearance.Available : result.Check is not null
                            ? UpdatePanelAppearance.Current : BriefcaseInstalledVersion == "—"
                                ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Neutral;
                BriefcaseActionLabel = result.Check?.InstallNeeded == true
                    ? AppTranslations.Get("updates.install_briefcase") : AppTranslations.Get("updates.update_briefcase");
                BriefcaseUpdateStatus = developmentBuild ? AppTranslations.Get("updates.briefcase_development") :
                    result.Error ?? (result.Check is null ? AppTranslations.Get("updates.unknown") :
                    result.Check.InstallNeeded ? AppTranslations.Format("updates.briefcase_missing", "Latest release: {0}.", result.Check.Latest) :
                    result.Check.UpdateAvailable ? AppTranslations.Format("updates.briefcase_available", "Latest release: {1}.", result.Check.Installed!, result.Check.Latest) :
                    AppTranslations.Get("updates.briefcase_current", "Up to date."));
            }
            RememberCurrentUpdates(key);
        }
        finally
        {
            if (!_disposed && revision == _updateRevision) IsCheckingUpdates = false;
            _updateGate.Release();
        }
    }

    private void ApplyUpdateCache(UpdateCacheEntry entry)
    {
        GameUpdateStatus = entry.GameStatus;
        BriefcaseUpdateStatus = entry.BriefcaseStatus;
        GameInstalledVersion = entry.GameVersion;
        BriefcaseInstalledVersion = entry.BriefcaseVersion;
        GameUpdateAvailable = entry.GameAvailable;
        BriefcaseActionVisible = entry.BriefcaseAction;
        BriefcaseActionLabel = entry.BriefcaseActionLabel;
        GameAppearance = entry.GameAppearance;
        BriefcaseAppearance = entry.BriefcaseAppearance;
        if (_runtimeChecked)
        {
            RuntimeAppearance = RuntimeInstallVisible ? UpdatePanelAppearance.Missing : UpdatePanelAppearance.Current;
            RuntimeStatus = RuntimeInstallVisible ? AppTranslations.Get("updates.runtime_missing") :
                AppTranslations.Get("updates.runtime_ready", "Installed and ready.");
        }
    }

    private static async Task<(SteamCmdService.BuildCheck? Check, string? Error)> CheckGameAsync(ServerLocation location, string tools)
    {
        try { return (await new SteamCmdService(tools).CheckBuildAsync(location, new Progress<string>(_ => { }), CancellationToken.None), null); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex.Message); }
    }

    private static async Task<(BriefcaseInstallService.VersionCheck? Check, string? Error)> CheckBriefcaseAsync(ServerLocation location)
    {
        try { return (await new BriefcaseInstallService().CheckVersionAsync(location, CancellationToken.None), null); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex.Message); }
    }

    public async Task InstallBriefcaseAsync() => await RunBusyAsync(async progress =>
    {
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error)) throw new InvalidDataException(error);
        if (!Registry.BriefcaseUpdatesEnabled &&
            GitHubReleaseService.InstalledBriefcaseVersion(location!) is not null)
            throw new InvalidOperationException(AppTranslations.Get("updates.briefcase_disabled"));
        if (ElevatedUpdateService.NeedsElevation(location!.Win64) ||
            ElevatedUpdateService.NeedsElevation(location.BriefcaseRoot))
        {
            await ElevatedUpdateService.RunAsync("briefcase", location.Win64, progress, CancellationToken.None);
            return AppTranslations.Get("status.briefcase_installed", "Briefcase installed or updated.");
        }
        try { return await new BriefcaseInstallService().InstallOrUpdateAsync(location, progress, CancellationToken.None); }
        catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex))
        {
            await ElevatedUpdateService.RunAsync("briefcase", location.Win64, progress, CancellationToken.None);
            return AppTranslations.Get("status.briefcase_installed", "Briefcase installed or updated.");
        }
    });

    public async Task InstallCppRuntimeAsync() => await RunBusyAsync(async progress =>
    {
        var folder = Path.Combine(Path.GetTempPath(), "Briefcase", "InstallerDownloads");
        return await new VisualCppInstaller().InstallAsync(folder, progress, CancellationToken.None);
    });

    private async Task RunBusyAsync(Func<IProgress<string>, Task<string>> action, bool reportSuccess = true)
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy) return;
        IsBusy = true;
        ProgressIndeterminate = true;
        try
        {
            using var progress = new OperationProgress(ReportProgress);
            var result = await action(progress);
            if (reportSuccess) ReportTransient(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Message = ex.Message; }
        finally { IsBusy = false; await RefreshAsync(); }
    }

    public async Task RefreshAsync(bool waitForSelection = false)
    {
        if (IsDemo || _disposed) return;
        if (IsBusy) return;
        var revision = _selectionRevision;
        if (waitForSelection) await _refreshGate.WaitAsync();
        else if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            if (revision != _selectionRevision) return;
            var registered = Servers.ToArray();
            lock (_connectionGate)
            {
                var paths = registered.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in _connections.Keys.Where(path => !paths.Contains(path)).ToArray())
                {
                    _connections[path].StateChanged -= OnLiveStateChanged;
                    _connections[path].Dispose();
                    _connections.Remove(path);
                }
            }
            var rows = await Task.Run(() => registered.Select(row =>
            {
                if (!ServerLocation.TryCreate(row, out var item, out _))
                    return (row, "Missing files", "—", false, (string?)null, false);
                using var active = ServerProcess.Find(item!);
                var snapshot = SnapshotFor(item!, active?.Id, ProcessUptime(active));
                var count = snapshot.PlayerCount is { } players
                    ? $"{players} / {snapshot.MaxPlayers?.ToString() ?? "?"}" : "—";
                return (row, active is null ? "Offline" : snapshot.State == "—" ? "Running" : snapshot.State,
                    count, ServerChangeTracker.NeedsRestart(item!, active),
                    ServerIniEditor.ReadConfiguredServerName(item!.ConfigFile), active is not null);
            }).ToArray());
            var selectedNameChanged = false;
            foreach (var (row, state, players, needsRestart, configuredName, _) in rows)
            {
                if (row.UpdateName(configuredName) && row == SelectedServer) selectedNameChanged = true;
                row.State = state;
                row.Players = players;
                row.NeedsRestart = needsRestart;
            }
            HasRunningDataServers = rows.Any(item => item.Item6 &&
                AppDataStore.IsInside(item.row.InstanceRoot, Registry.DataDirectory));
            if (selectedNameChanged) OnPropertyChanged(nameof(SelectedServerName));
            if (revision != _selectionRevision) return;
            if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error))
            {
                IsRunning = false;
                ServerState = "Offline";
                NetworkStatus = ServerNetworkStatus.Unknown;
                DataSource = "No server selected";
                if (SelectedServer is not null) Message = error;
                Players.Clear();
                PlayerTeams.Clear();
                ShowPlayerTeams = false;
                ShowTeamUnavailable = false;
                Mods.Clear();
                HasPlayerRoster = false;
                OnPropertyChanged(nameof(RosterMessage));
                return;
            }
            var (pid, snapshot, mods, network) = await Task.Run(() =>
            {
                using var process = ServerProcess.Find(location!);
                var status = ServerNetworkStatus.Unknown;
                if (process is not null)
                {
                    try { status = ServerNetworkStatusReader.Read(location!, process.StartTime.ToUniversalTime()); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                }
                return (process?.Id, SnapshotFor(location!, process?.Id, ProcessUptime(process)),
                    ModCatalog.Read(location!), status);
            });
            if (revision != _selectionRevision) return;
            IsRunning = pid is not null;
            ServerName = snapshot.Name;
            ServerState = snapshot.State;
            MapName = GameMapNames.Display(snapshot.Map);
            GameMode = snapshot.GameMode;
            NetworkStatus = network;
            PlayerCountText = snapshot.PlayerCount is { } count
                ? $"{count} / {snapshot.MaxPlayers?.ToString() ?? "?"}"
                : "—";
            DataSource = snapshot.Source;
            HasPlayerRoster = snapshot.HasPlayerRoster;
            if (!Players.SequenceEqual(snapshot.Players))
            {
                Players.Clear();
                foreach (var player in snapshot.Players) Players.Add(player);
            }
            RebuildPlayerTeams(snapshot);
            var displayedMods = ModCatalog.WithAvailableUpdates(mods, MarketplaceMods);
            if (!Mods.SequenceEqual(displayedMods))
            {
                Mods.Clear();
                foreach (var mod in displayedMods) Mods.Add(mod);
            }
            RefreshMarketplaceStatuses(location!);
            OnPropertyChanged(nameof(RosterMessage));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Message = ex.Message;
        }
        finally
        {
            if (!waitForSelection && revision == _selectionRevision) IsSelectionLoading = false;
            _refreshGate.Release();
        }
    }

    public async Task StartAsync()
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy) return;
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error))
        {
            Message = error;
            return;
        }
        var autoUpdateBeforeStart = SelectedServer?.AutoUpdateBeforeStart == true;
        IsBusy = true;
        try
        {
            if (autoUpdateBeforeStart)
                await InstallKnownUpdatesBeforeStartAsync(location!);
            EnsurePortsAvailable(location!);
            using var process = await Task.Run(() => ServerProcess.Start(location!));
            ReportTransient(AppTranslations.Get("status.started", "Server started."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or ArgumentException or HttpRequestException or TimeoutException or
            TaskCanceledException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    public async Task StopAsync()
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy) return;
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error))
        {
            Message = error;
            return;
        }
        IsBusy = true;
        try
        {
            await ServerProcess.StopAsync(location!, CancellationToken.None);
            ReportTransient(AppTranslations.Get("status.stopped"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    public async Task RestartAsync()
    {
        if (IsDemo) { ReportPreviewOnly(); return; }
        if (IsBusy) return;
        if (!ServerLocation.TryCreate(SelectedServer, out var location, out var error))
        {
            Message = error;
            return;
        }
        var autoUpdateBeforeStart = SelectedServer?.AutoUpdateBeforeStart == true;
        IsBusy = true;
        try
        {
            EnsurePortsAvailable(location!);
            await ServerProcess.StopAsync(location!, CancellationToken.None);
            if (autoUpdateBeforeStart)
                await InstallKnownUpdatesBeforeStartAsync(location!);
            using var process = await Task.Run(() => ServerProcess.Start(location!));
            ReportTransient(AppTranslations.Get("status.restarted", "Server restarted."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception or TimeoutException or ArgumentException or HttpRequestException or
            TaskCanceledException)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task InstallKnownUpdatesBeforeStartAsync(ServerLocation location)
    {
        using var progress = new OperationProgress(ReportProgress);
        if (Registry.BriefcaseUpdatesEnabled && BriefcaseActionVisible &&
            GitHubReleaseService.InstalledBriefcaseVersion(location) is not null)
        {
            Message = AppTranslations.Get("status.updating_briefcase_before_start", "Updating Briefcase before server startup…");
            if (ElevatedUpdateService.NeedsElevation(location.Win64) ||
                ElevatedUpdateService.NeedsElevation(location.BriefcaseRoot))
                await ElevatedUpdateService.RunAsync("briefcase", location.Win64, progress, CancellationToken.None);
            else
            {
                try { await new BriefcaseInstallService().InstallOrUpdateAsync(location, progress, CancellationToken.None); }
                catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex))
                { await ElevatedUpdateService.RunAsync("briefcase", location.Win64, progress, CancellationToken.None); }
            }
        }
        var marketplace = new ModMarketplace();
        foreach (var mod in MarketplaceMods.ToArray())
        {
            var installed = ModMarketplace.InstalledMetadata(location, mod.Id);
            if (installed is null || !string.Equals(installed.Catalog, mod.Catalog, StringComparison.OrdinalIgnoreCase) ||
                !GitHubReleaseService.IsNewer(mod.Version, installed.Version)) continue;
            Message = AppTranslations.Format("status.updating_mod_before_start", "Updating {0} before server startup…", mod.Name);
            async Task InstallNormallyAsync()
            {
                var downloads = AppDataStore.DownloadsRoot;
                var archive = await marketplace.DownloadAsync(mod, downloads, progress, CancellationToken.None);
                try
                {
                    var logo = await ModLogo.VerifiedBytesAsync(mod.LogoUrl, mod.LogoSha256, CancellationToken.None);
                    if (mod.LogoSha256 is not null && logo is null)
                        throw new InvalidDataException("The mod logo could not be downloaded or verified.");
                    var formOverride = await ModMarketplace.DownloadFormOverrideAsync(mod, CancellationToken.None);
                    var formTranslations = await ModMarketplace.DownloadFormTranslationsAsync(mod, CancellationToken.None);
                    var configSchema = await ModMarketplace.DownloadConfigSchemaAsync(mod, CancellationToken.None);
                    await Task.Run(() => ModMarketplace.Install(location, mod, archive, logo, formOverride,
                        formTranslations, configSchema));
                }
                finally { if (File.Exists(archive)) File.Delete(archive); }
            }
            async Task InstallElevatedAsync() => await ElevatedUpdateService.RunAsync("mod", location.Win64,
                progress, CancellationToken.None, mod.Catalog, mod.Id, mod.Sha256,
                location.InstanceRoot, location.InstanceId);
            if (ElevatedUpdateService.NeedsElevation(location.Win64) ||
                ElevatedUpdateService.NeedsElevation(location.ModsRoot))
                await InstallElevatedAsync();
            else
            {
                try { await InstallNormallyAsync(); }
                catch (Exception ex) when (ElevatedUpdateService.AccessDenied(ex)) { await InstallElevatedAsync(); }
            }
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
    private void OnPropertyChanged(string? property) => PropertyChanged?.Invoke(this, new(property));

    private sealed class OperationProgress : IProgress<string>, IDisposable
    {
        private readonly IProgress<string> _dispatcher;
        private int _completed;

        public OperationProgress(Action<string> report) => _dispatcher = new Progress<string>(line =>
        {
            if (Volatile.Read(ref _completed) == 0) report(line);
        });

        public void Report(string line) => _dispatcher.Report(line);
        public void Dispose() => Volatile.Write(ref _completed, 1);
    }

    private void ReportProgress(string line)
    {
        var match = Regex.Match(line, @"progress:\s*(?<percent>\d{1,3}(?:\.\d+)?)",
            RegexOptions.IgnoreCase);
        var steamProgress = match.Success;
        if (!match.Success)
            match = Regex.Match(line, @"(?<percent>\d{1,3}(?:\.\d+)?)\s*%");
        Message = steamProgress
            ? AppTranslations.Format("status.server_download_progress", "Installing server: {0}%",
                match.Groups["percent"].Value)
            : line;
        if (!line.Contains('%') && !line.Contains("progress:", StringComparison.OrdinalIgnoreCase))
        {
            ProgressIndeterminate = true;
            return;
        }
        if (match.Success && double.TryParse(match.Groups["percent"].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) &&
            value is >= 0 and <= 100)
        {
            ProgressPercent = value;
            ProgressIndeterminate = false;
        }
        else ProgressIndeterminate = true;
    }
    private void UpdateActions()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanManageMods));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(CanUpdateGame));
        OnPropertyChanged(nameof(CanInstallBriefcase));
        OnPropertyChanged(nameof(CanInstallRuntime));
        OnPropertyChanged(nameof(CanMigrateServer));
        OnPropertyChanged(nameof(RosterMessage));
    }

    private void ReportPreviewOnly() => Message = AppTranslations.Get("status.demo_preview_only");

    private void ApplyDemoProfile()
    {
        var profile = _demoProfiles.FirstOrDefault(item => item.Row == SelectedServer);
        if (profile is null) return;
        var snapshot = profile.Snapshot;
        IsRunning = snapshot.State != "Offline";
        ServerName = snapshot.Name;
        ServerState = snapshot.State;
        MapName = GameMapNames.Display(snapshot.Map);
        GameMode = snapshot.GameMode;
        NetworkStatus = ServerNetworkStatus.Unknown;
        PlayerCountText = snapshot.PlayerCount is { } count ? $"{count} / {snapshot.MaxPlayers}" : "—";
        DataSource = snapshot.Source;
        HasPlayerRoster = snapshot.HasPlayerRoster;
        Players.Clear();
        foreach (var player in snapshot.Players) Players.Add(player);
        RebuildPlayerTeams(snapshot);
        Mods.Clear();
        foreach (var mod in profile.Mods) Mods.Add(mod);
        OnPropertyChanged(nameof(RosterMessage));
    }

    private void EnsurePortsAvailable(ServerLocation location)
    {
        var (game, query) = ServerProcess.ReadPorts(location.ConfigFile);
        foreach (var row in Servers)
        {
            if (row.Id == location.InstanceId ||
                !ServerLocation.TryCreate(row, out var other, out _)) continue;
            using var running = ServerProcess.Find(other!);
            if (running is null) continue;
            var (otherGame, otherQuery) = ServerProcess.ReadPorts(other!.ConfigFile);
            if (game == otherGame || game == otherQuery || query == otherGame || query == otherQuery)
                throw new InvalidOperationException($"A running server already uses UDP port {game} or {query}. Give each server separate game and query ports in Configuration.");
        }
    }
}
