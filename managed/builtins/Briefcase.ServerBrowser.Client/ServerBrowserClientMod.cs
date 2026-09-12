using Briefcase.ClientModApi;
using Briefcase.ClientSupport;
using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.ServerBrowser.Client;

/// <summary>
/// Core server directory. UI actions only edit managed state; pending gameplay
/// connections are consumed from ReceiveTick so Unreal's DirectConnect always
/// runs on the game thread. Selecting a server also supplies its administration
/// profile to Briefcase's built-in remote administration client.
/// </summary>
public sealed class ServerBrowserClientMod : BriefcaseMod
{
    private sealed record ConnectionRequest(string Name, string Endpoint, string Password);

    private static ServerBrowserClientMod? _active;
    private readonly object _directoryGate = new();
    private ModContext _context;
    private ServerDirectory _directory = new();
    private ServerDirectoryStore? _store;
    private IDisposable? _panel;
    private ConnectionRequest? _pendingConnection;
    private string _editingId = "";
    private string _name = "";
    private string _endpoint = "";
    private string _password = "";
    private string _administrationEndpoint = "";
    private string _administrationPassword = "";
    private string _status = "Select a saved server or add a new one.";
    private long _directoryUiRevision;
    private bool _showEditor;
    private bool _showPassword;
    private bool _showAdministrationPassword;
    private bool _confirmDelete;

    public override ModInfo Info { get; } = new(
        Id: "briefcase.server-browser.client",
        Name: "Servers",
        Author: "EnoPM",
        Version: "0.2.0-dev",
        Description: "Save, join and administer community servers from Briefcase.",
        RequiredCapabilities: BriefcaseAbi.CoreCapability |
                              BriefcaseAbi.UnrealReflectionCapability |
                              BriefcaseAbi.UnrealInvocationCapability |
                              BriefcaseAbi.RenderingCapability |
                              BriefcaseAbi.PatchingCapability)
    {
        ShowConfigurationTab = false
    };

    public override void Load(ModContext context)
    {
        _context = context;
        _active = this;
        var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var frameworkDirectory = Path.Combine(gameDirectory, "Briefcase");
        _store = new ServerDirectoryStore(
            Path.Combine(frameworkDirectory, "servers.json"),
            Path.Combine(frameworkDirectory, "settings.json"));
        _directory = _store.Load();
        Select(_directory.SelectedServerId);
        _showEditor = false;
        _panel = context.Ui().RegisterServerPanel("Server directory", BuildPanel());
        SaveDirectory();
        context.Info($"Server manager loaded with {_directory.Servers.Count} saved server(s).");
    }

    public override void Unload()
    {
        _active = null;
        Interlocked.Exchange(ref _pendingConnection, null);
        _panel?.Dispose();
        _panel = null;
        SaveDirectory();
        ServerWorkspace.ClearSelection();
    }

    [UnrealPostfixPatch(
        typeof(DeceiveIncPlayerController),
        nameof(DeceiveIncPlayerController.ReceiveTick),
        Priority = -200,
        Reentrancy = PatchReentrancy.SuppressTargetPatches,
        OnException = PatchExceptionPolicy.LogAndContinue)]
    private static void ProcessPendingConnection()
    {
        var mod = _active;
        if (mod is null) return;
        var request = Interlocked.Exchange(ref mod._pendingConnection, null);
        if (request is null) return;

        try
        {
            CommunityServerConnection.Connect(
                mod._context, request.Endpoint, request.Password);
            Volatile.Write(ref mod._status,
                $"Connection to {request.Name} ({request.Endpoint}) requested.");
            mod._context.Info(
                $"Server manager requested a connection to {request.Endpoint}.");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref mod._status, $"Connection failed: {exception.Message}");
            mod._context.Warning(
                $"Server manager connection failed: {exception.Message}");
        }
    }

    private UiComponent BuildPanel() => Ui.Column(
        Ui.Column(
                Ui.Section("Saved community servers",
                    Ui.Text(
                        "Keep every server you use in one place. Join opens the gameplay " +
                        "connection; Configure opens authenticated Briefcase administration.",
                        UiTextTone.Muted),
                    Ui.Dynamic(BuildSavedServerComponents, DirectoryUiRevision)),
                Ui.Button("Add server", UiIcon.Server, BeginAdd))
            .VisibleWhen(() => !_showEditor),
        Ui.Column(
                Ui.Button("Back to server list", UiIcon.ChevronRight, ReturnToList),
                Ui.Section("Server details",
                    Ui.Text(() => string.IsNullOrEmpty(_editingId)
                        ? "Add a server"
                        : $"Edit {_name}", UiTextTone.Accent),
                    Ui.TextField("Name", () => _name, value => _name = value,
                        maximumLength: 128),
                    Ui.TextField("Game IP:PORT", () => _endpoint,
                        value => _endpoint = value,
                        maximumLength: 256, hint: "127.0.0.1:50000"),
                    Ui.TextField("Game password", () => _password,
                            value => _password = value,
                            secret: true, maximumLength: 256)
                        .VisibleWhen(() => !_showPassword),
                    Ui.TextField("Game password", () => _password,
                            value => _password = value,
                            maximumLength: 256)
                        .VisibleWhen(() => _showPassword),
                    Ui.Toggle("Show game password", () => _showPassword,
                        value => _showPassword = value)),
                Ui.Section("Briefcase administration",
                    Ui.Text(
                        "Administration uses the dedicated Briefcase TCP endpoint. " +
                        "The password must match the server configuration.",
                        UiTextTone.Muted),
                    Ui.TextField("Administration IP:PORT",
                        () => _administrationEndpoint,
                        value => _administrationEndpoint = value,
                        maximumLength: 256, hint: "127.0.0.1:47000"),
                    Ui.TextField("Administration password",
                            () => _administrationPassword,
                            value => _administrationPassword = value,
                            secret: true, maximumLength: 256)
                        .VisibleWhen(() => !_showAdministrationPassword),
                    Ui.TextField("Administration password",
                            () => _administrationPassword,
                            value => _administrationPassword = value,
                            maximumLength: 256)
                        .VisibleWhen(() => _showAdministrationPassword),
                    Ui.Toggle("Show administration password",
                        () => _showAdministrationPassword,
                        value => _showAdministrationPassword = value)),
                Ui.Row(
                    Ui.Button("Save", UiIcon.Save, () => { SaveEditedServer(); }),
                    Ui.Button("Join", UiIcon.Play, ConnectEditedServer),
                    Ui.Button("Configure", UiIcon.Settings, ConfigureEditedServer),
                    Ui.Button("Delete", UiIcon.Delete, () =>
                        {
                            _confirmDelete = true;
                            MarkDirectoryChanged();
                        })
                        .VisibleWhen(() => !string.IsNullOrEmpty(_editingId) && !_confirmDelete)),
                Ui.Dynamic(BuildDeleteConfirmation, DirectoryUiRevision))
            .VisibleWhen(() => _showEditor),
        Ui.Section("Status",
            Ui.Text(() => Volatile.Read(ref _status)),
            Ui.Text("Saved in Briefcase/servers.json.", UiTextTone.Muted)));

    private IReadOnlyList<UiComponent> BuildSavedServerComponents()
    {
        SavedServer[] servers;
        lock (_directoryGate) servers = _directory.Servers.ToArray();
        if (servers.Length == 0)
            return [Ui.Text("No server is saved yet.", UiTextTone.Muted)];

        return servers.Select(server => (UiComponent)Ui.Card(
                server.Name,
                Ui.Text(server.Endpoint, UiTextTone.Muted, wrap: false),
                Ui.Row(
                    Ui.Button("Edit", UiIcon.Settings, () => BeginEdit(server.Id)),
                    Ui.Button("Join", UiIcon.Play, () => ConnectSavedServer(server.Id)),
                    Ui.Button("Configure", UiIcon.Server,
                        () => ConfigureSavedServer(server.Id)))))
            .ToArray();
    }

    private IReadOnlyList<UiComponent> BuildDeleteConfirmation()
    {
        if (!_confirmDelete) return [];
        return
        [
            Ui.Text("Delete this saved server?", UiTextTone.Warning),
            Ui.Row(
                Ui.Button("Confirm delete", UiIcon.Delete, DeleteSelected),
                Ui.Button("Cancel", () =>
                {
                    _confirmDelete = false;
                    MarkDirectoryChanged();
                }))
        ];
    }

    private long DirectoryUiRevision() => Volatile.Read(ref _directoryUiRevision);

    private void MarkDirectoryChanged() =>
        Interlocked.Increment(ref _directoryUiRevision);

    private void BeginEdit(string id)
    {
        Select(id);
        _showEditor = true;
        MarkDirectoryChanged();
    }

    private void Select(string id)
    {
        SavedServer? selected;
        lock (_directoryGate)
        {
            selected = _directory.Servers.FirstOrDefault(server =>
                string.Equals(server.Id, id, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) _directory.SelectedServerId = selected.Id;
        }
        if (selected is null)
        {
            BeginAdd();
            return;
        }
        _editingId = selected.Id;
        _name = selected.Name;
        _endpoint = selected.Endpoint;
        _password = selected.Password;
        _administrationEndpoint = selected.AdministrationEndpoint;
        _administrationPassword = selected.AdministrationPassword;
        _confirmDelete = false;
        ServerWorkspace.Select(ToWorkspaceProfile(selected));
    }

    private void BeginAdd()
    {
        _editingId = "";
        _name = "New server";
        _endpoint = "127.0.0.1:50000";
        _password = "";
        _administrationEndpoint = "127.0.0.1:47000";
        _administrationPassword = "";
        _confirmDelete = false;
        _showEditor = true;
        Volatile.Write(ref _status, "Enter the new server details, then press Save.");
        MarkDirectoryChanged();
    }

    private void ReturnToList()
    {
        _showEditor = false;
        _confirmDelete = false;
        Volatile.Write(ref _status, "Select a saved server, join it, or open its administration.");
        MarkDirectoryChanged();
    }

    private bool SaveEditedServer()
    {
        try
        {
            var name = _name.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("The server name is empty.");
            var endpoint = CommunityServerConnection.NormalizeEndpoint(_endpoint);
            var administrationEndpoint = CommunityServerConnection.NormalizeEndpoint(
                _administrationEndpoint);
            SavedServer server;
            lock (_directoryGate)
            {
                server = _directory.Servers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, _editingId,
                        StringComparison.OrdinalIgnoreCase)) ?? new SavedServer();
                if (!_directory.Servers.Contains(server)) _directory.Servers.Add(server);
                server.Name = name;
                server.Endpoint = endpoint;
                server.Password = _password;
                server.AdministrationEndpoint = administrationEndpoint;
                server.AdministrationPassword = _administrationPassword;
                _directory.SelectedServerId = server.Id;
                _editingId = server.Id;
            }
            _name = name;
            _endpoint = endpoint;
            _administrationEndpoint = administrationEndpoint;
            SaveDirectory();
            ServerWorkspace.Select(ToWorkspaceProfile(server));
            Volatile.Write(ref _status, $"Saved {name}.");
            MarkDirectoryChanged();
            return true;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status, $"Could not save server: {exception.Message}");
            return false;
        }
    }

    private void ConnectEditedServer()
    {
        if (!SaveEditedServer()) return;
        QueueConnection(_name, _endpoint, _password);
    }

    private void ConfigureEditedServer()
    {
        if (!SaveEditedServer()) return;
        if (SelectedWorkspaceProfile() is { } profile)
            ServerWorkspace.OpenAdministration(profile);
    }

    private void ConnectSavedServer(string id)
    {
        Select(id);
        QueueConnection(_name, _endpoint, _password);
    }

    private void ConfigureSavedServer(string id)
    {
        Select(id);
        if (SelectedWorkspaceProfile() is { } profile)
            ServerWorkspace.OpenAdministration(profile);
    }

    private void DeleteSelected()
    {
        lock (_directoryGate)
        {
            _directory.Servers.RemoveAll(server => string.Equals(
                server.Id, _editingId, StringComparison.OrdinalIgnoreCase));
            _directory.SelectedServerId = _directory.Servers.FirstOrDefault()?.Id ?? "";
        }
        SaveDirectory();
        _showEditor = false;
        _confirmDelete = false;
        if (string.IsNullOrEmpty(_directory.SelectedServerId))
            ServerWorkspace.ClearSelection();
        else
            Select(_directory.SelectedServerId);
        Volatile.Write(ref _status, "Saved server deleted.");
        MarkDirectoryChanged();
    }

    private ServerWorkspaceProfile? SelectedWorkspaceProfile()
    {
        lock (_directoryGate)
        {
            var server = _directory.Servers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, _directory.SelectedServerId,
                    StringComparison.OrdinalIgnoreCase));
            return server is null ? null : ToWorkspaceProfile(server);
        }
    }

    private static ServerWorkspaceProfile ToWorkspaceProfile(SavedServer server) => new(
        server.Id,
        server.Name,
        server.Endpoint,
        server.Password,
        server.AdministrationEndpoint,
        server.AdministrationPassword);

    private void QueueConnection(string name, string endpoint, string password)
    {
        try
        {
            name = string.IsNullOrWhiteSpace(name) ? endpoint.Trim() : name.Trim();
            endpoint = CommunityServerConnection.NormalizeEndpoint(endpoint);
            Interlocked.Exchange(
                ref _pendingConnection,
                new ConnectionRequest(name, endpoint, password));
            Volatile.Write(ref _status,
                $"Waiting for the Unreal game thread to connect to {name}...");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status, $"Could not connect: {exception.Message}");
        }
    }

    private void SaveDirectory()
    {
        try
        {
            lock (_directoryGate) _store?.Save(_directory);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status,
                $"Could not save Briefcase/servers.json: {exception.Message}");
            _context.Warning($"Server directory save failed: {exception.Message}");
        }
    }
}
