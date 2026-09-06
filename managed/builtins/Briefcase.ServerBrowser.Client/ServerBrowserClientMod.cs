using System.Numerics;
using Briefcase.ClientSupport;
using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using ImGuiNET;

namespace Briefcase.ServerBrowser.Client;

/// <summary>
/// Core server directory. ImGui only edits managed state; a pending connection
/// is consumed from ReceiveTick so Unreal's DirectConnect always runs on the
/// game thread.
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
    private string _status = "Select a saved server or add a new one.";
    private bool _showPassword;
    private bool _confirmDelete;

    public override ModInfo Info { get; } = new(
        Id: "briefcase.server-browser.client",
        Name: "Servers",
        Author: "EnoPM",
        Version: "0.1.0-dev",
        Description: "Save community servers and connect to them from the Briefcase menu.",
        RequiredCapabilities: BriefcaseAbi.CoreCapability |
                              BriefcaseAbi.UnrealReflectionCapability |
                              BriefcaseAbi.UnrealInvocationCapability |
                              BriefcaseAbi.RenderingCapability |
                              BriefcaseAbi.PatchingCapability);

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
        _panel = context.Configuration.RegisterPanel(DrawPanel);
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

    private void DrawPanel(ConfigurationPanelContext panel)
    {
        ImGui.SeparatorText("Saved community servers");
        ImGui.TextWrapped(
            "Select a server and press Connect. Double-clicking a saved entry also connects immediately.");

        var available = ImGui.GetContentRegionAvail();
        var listWidth = Math.Clamp(available.X * 0.36f, 180, 280);
        if (ImGui.BeginChild(
                "server-list", new Vector2(listWidth, 300), ImGuiNET.ImGuiChildFlags.Borders))
        {
            SavedServer[] servers;
            lock (_directoryGate) servers = _directory.Servers.ToArray();
            foreach (var server in servers)
            {
                ImGui.PushID(server.Id);
                var selected = string.Equals(
                    server.Id, _editingId, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(server.Name, selected)) Select(server.Id);
                if (ImGui.IsItemHovered() &&
                    ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    QueueConnection(server.Name, server.Endpoint, server.Password);
                ImGui.TextDisabled(server.Endpoint);
                ImGui.Spacing();
                ImGui.PopID();
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild(
                "server-editor", new Vector2(0, 300), ImGuiNET.ImGuiChildFlags.Borders))
        {
            ImGui.Text(string.IsNullOrEmpty(_editingId) ? "Add server" : "Edit server");
            ImGui.Separator();
            ImGui.InputText("Name", ref _name, 128);
            ImGui.InputText("IP:PORT", ref _endpoint, 256);
            var passwordFlags = _showPassword
                ? ImGuiNET.ImGuiInputTextFlags.None
                : ImGuiNET.ImGuiInputTextFlags.Password;
            ImGui.InputText("Password", ref _password, 256, passwordFlags);
            ImGui.Checkbox("Show password", ref _showPassword);
            ImGui.Spacing();

            if (ImGui.Button("Save")) SaveEditedServer();
            ImGui.SameLine();
            if (ImGui.Button("Connect"))
                QueueConnection(_name, _endpoint, _password);
            ImGui.SameLine();
            if (ImGui.Button("Add new")) BeginAdd();

            if (!string.IsNullOrEmpty(_editingId))
            {
                if (!_confirmDelete)
                {
                    if (ImGui.Button("Delete")) _confirmDelete = true;
                }
                else
                {
                    ImGui.TextColored(new Vector4(1, 0.55f, 0.3f, 1),
                        "Delete this saved server?");
                    if (ImGui.Button("Confirm delete")) DeleteSelected();
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel")) _confirmDelete = false;
                }
            }
        }
        ImGui.EndChild();

        ImGui.Spacing();
        ImGui.SeparatorText("Connection status");
        ImGui.TextWrapped(Volatile.Read(ref _status));
        ImGui.TextDisabled("Saved in Briefcase/servers.json.");
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
        _confirmDelete = false;
    }

    private void BeginAdd()
    {
        _editingId = "";
        _name = "New server";
        _endpoint = "127.0.0.1:50000";
        _password = "";
        _confirmDelete = false;
        Volatile.Write(ref _status, "Enter the new server details, then press Save.");
    }

    private void SaveEditedServer()
    {
        try
        {
            var name = _name.Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("The server name is empty.");
            var endpoint = CommunityServerConnection.NormalizeEndpoint(_endpoint);
            lock (_directoryGate)
            {
                var server = _directory.Servers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, _editingId,
                        StringComparison.OrdinalIgnoreCase));
                if (server is null)
                {
                    server = new SavedServer();
                    _directory.Servers.Add(server);
                }
                server.Name = name;
                server.Endpoint = endpoint;
                server.Password = _password;
                _directory.SelectedServerId = server.Id;
                _editingId = server.Id;
            }
            _name = name;
            _endpoint = endpoint;
            SaveDirectory();
            Volatile.Write(ref _status, $"Saved {name}.");
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _status, $"Could not save server: {exception.Message}");
        }
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
        Select(_directory.SelectedServerId);
        _confirmDelete = false;
        Volatile.Write(ref _status, "Saved server deleted.");
    }

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
