using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;

namespace Briefcase.ServerApp;

public sealed class ServerRow(string id, string win64, bool managed, string instanceRoot = "") : INotifyPropertyChanged
{
    private static readonly IBrush OfflineForeground = new SolidColorBrush(Color.Parse("#ADA7B7"));
    private static readonly IBrush OfflineBackground = new SolidColorBrush(Color.Parse("#302C37"));
    private static readonly IBrush LobbyForeground = new SolidColorBrush(Color.Parse("#8EC4FF"));
    private static readonly IBrush LobbyBackground = new SolidColorBrush(Color.Parse("#21354B"));
    private static readonly IBrush ActiveForeground = new SolidColorBrush(Color.Parse("#8EE0B3"));
    private static readonly IBrush ActiveBackground = new SolidColorBrush(Color.Parse("#214335"));
    private static readonly IBrush PendingForeground = new SolidColorBrush(Color.Parse("#F0C57F"));
    private static readonly IBrush PendingBackground = new SolidColorBrush(Color.Parse("#493821"));
    private static readonly IBrush FailureForeground = new SolidColorBrush(Color.Parse("#EDAFBA"));
    private static readonly IBrush FailureBackground = new SolidColorBrush(Color.Parse("#49303B"));
    private string _state = "Offline";
    private string _name = new DirectoryInfo(Path.GetFullPath(Path.Combine(win64, "../../.."))).Name;
    private string _players = "—";
    private bool _needsRestart;
    private bool _autoUpdateBeforeStart;
    public string Id { get; } = id;
    public string Win64 { get; private set; } = win64;
    public string InstanceRoot { get; private set; } = instanceRoot;
    public bool Managed { get; private set; } = managed;
    public void Rebind(ServerInstallation installation, string instanceRoot)
    {
        Win64 = installation.Win64;
        InstanceRoot = instanceRoot;
        Managed = true;
        PropertyChanged?.Invoke(this, new(nameof(Win64)));
        PropertyChanged?.Invoke(this, new(nameof(InstanceRoot)));
    }
    internal void RestoreBinding(string win64, string instanceRoot, bool managed)
    {
        Win64 = win64;
        InstanceRoot = instanceRoot;
        Managed = managed;
        PropertyChanged?.Invoke(this, new(nameof(Win64)));
        PropertyChanged?.Invoke(this, new(nameof(InstanceRoot)));
    }
    public string Name => _name;
    public bool UpdateName(string? configuredName) => Set(ref _name,
        string.IsNullOrWhiteSpace(configuredName)
            ? new DirectoryInfo(Path.GetFullPath(Path.Combine(Win64, "../../.."))).Name
            : configuredName.Trim(), nameof(Name));
    public string State { get => _state; set { if (Set(ref _state, value)) { PropertyChanged?.Invoke(this, new(nameof(DisplayState))); PropertyChanged?.Invoke(this, new(nameof(StateForeground))); PropertyChanged?.Invoke(this, new(nameof(StateBackground))); } } }
    public string DisplayState => AppTranslations.Get("state." + State, State);
    public IBrush StateForeground => State switch
    {
        "Lobby" => LobbyForeground,
        "In match" or "Running" => ActiveForeground,
        "Starting" or "Connecting" or "Waiting for data" => PendingForeground,
        "Connection unavailable" or "Incompatible data" or "Data interrupted" or "Missing files" => FailureForeground,
        _ => OfflineForeground
    };
    public IBrush StateBackground => State switch
    {
        "Lobby" => LobbyBackground,
        "In match" or "Running" => ActiveBackground,
        "Starting" or "Connecting" or "Waiting for data" => PendingBackground,
        "Connection unavailable" or "Incompatible data" or "Data interrupted" or "Missing files" => FailureBackground,
        _ => OfflineBackground
    };
    public string Players { get => _players; set { if (Set(ref _players, value)) PropertyChanged?.Invoke(this, new(nameof(PlayerSummary))); } }
    public string PlayerSummary => Players == "—" ? AppTranslations.Get("server.players_unavailable", "No live player data")
        : AppTranslations.Format("server.players_summary", "Players {0}", Players);
    public bool NeedsRestart { get => _needsRestart; set { if (Set(ref _needsRestart, value)) PropertyChanged?.Invoke(this, new(nameof(RestartWarning))); } }
    public bool AutoUpdateBeforeStart { get => _autoUpdateBeforeStart; set => Set(ref _autoUpdateBeforeStart, value); }
    public string RestartWarning => NeedsRestart ? AppTranslations.Get("status.restart_required") : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }
}

public sealed class ServerRegistry
{
    private readonly bool _inMemory;
    public const int SchemaVersion = 3;
    public string FilePath { get; private set; }
    public string DataDirectory => Path.GetDirectoryName(FilePath)!;
    public string LoadError { get; private set; } = "";
    public ObservableCollection<ServerRow> Servers { get; } = [];
    public ObservableCollection<ServerInstallation> Installations { get; } = [];
    public ObservableCollection<string> CatalogUrls { get; } = [];
    public string Language { get; set; } = "en";

    public static string ReadSavedLanguage(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath();
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return "en";
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            return json.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? AppTranslations.NormalizeLanguage(value.GetString()) : "en";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return "en"; }
    }

    public static string DefaultFilePath() => Path.Combine(AppDataStore.Root, "settings.json");

    public ServerRegistry(string? filePath = null, bool inMemory = false)
    {
        _inMemory = inMemory;
        FilePath = filePath ?? DefaultFilePath();
        if (!_inMemory) Load();
    }

    public ServerRow Add(string executable, bool managed = false, string? serverName = null)
    {
        if (!ServerLocation.TryCreateFromExecutable(executable, out var location, out var error))
            throw new InvalidDataException(error);
        if (Servers.Any(row => row.InstanceRoot.Length == 0 &&
                string.Equals(row.Win64, location!.Win64, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Migrate the existing server entry before sharing its installation.");
        var id = Guid.NewGuid().ToString("N");
        var instanceRoot = ServerInstanceFiles.Provision(DataDirectory, id, location!, serverName);
        var row = new ServerRow(id, location!.Win64, managed, instanceRoot);
        row.UpdateName(ServerIniEditor.ReadConfiguredServerName(Path.Combine(instanceRoot, "TripwireServer.ini")));
        Servers.Add(row);
        Save();
        return row;
    }

    public void Remove(ServerRow row)
    {
        Servers.Remove(row);
        Save();
    }

    public ServerInstallation AddInstallation(string branch, string installRoot)
    {
        branch = ServerInstallation.ValidateBranch(branch);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        if (Installations.Any(item => item.Branch.Equals(branch, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This Steam branch already has an installation. Update it instead.");
        if (Installations.Any(item => item.InstallRoot.Equals(root, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("This installation folder is already registered.");
        if (!ServerLocation.TryCreate(Path.Combine(root, "DeceiveInc", "Binaries", "Win64"),
                out _, out var error)) throw new InvalidDataException(error);
        var installation = new ServerInstallation(Guid.NewGuid().ToString("N"), branch, root);
        Installations.Add(installation);
        Save();
        return installation;
    }

    public void RefreshInstallation(ServerInstallation installation)
    {
        var index = Installations.IndexOf(installation);
        if (index >= 0) Installations[index] = installation with { };
    }

    public void Migrate(ServerRow row, ServerInstallation installation)
    {
        if (!Servers.Contains(row) || !Installations.Contains(installation))
            throw new InvalidOperationException("The server or installation is no longer registered.");
        if (!ServerLocation.TryCreate(installation.Win64, out _, out var error))
            throw new InvalidDataException(error);
        if (row.Win64.Equals(installation.Win64, StringComparison.OrdinalIgnoreCase)) return;
        if (!ServerLocation.TryCreate(row, out var source, out error))
            throw new InvalidDataException(error);
        using (var running = ServerProcess.Find(source!))
            if (running is not null) throw new InvalidOperationException("Stop this server before changing its installation.");
        var instanceRoot = row.InstanceRoot.Length > 0 ? row.InstanceRoot :
            ServerInstanceFiles.Provision(DataDirectory, row.Id, source!);
        var ini = Path.Combine(instanceRoot, "TripwireServer.ini");
        if (string.IsNullOrWhiteSpace(ServerIniEditor.ReadConfiguredServerName(ini)))
            ServerIniEditor.SaveServerName(ini, row.Name.Length > 48 ? row.Name[..48].TrimEnd() : row.Name);
        var previousRoot = row.InstanceRoot;
        var previousManaged = row.Managed;
        row.Rebind(installation, instanceRoot);
        try { Save(); }
        catch
        {
            row.RestoreBinding(source!.Win64, previousRoot, previousManaged);
            throw;
        }
    }

    public void AddCatalog(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Catalog URLs must be HTTPS without credentials.");
        if (CatalogUrls.Any(value => string.Equals(value, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))) return;
        CatalogUrls.Add(uri.AbsoluteUri);
        Save();
    }

    public void RemoveCatalog(string url)
    {
        CatalogUrls.Remove(url);
        Save();
    }

    public void ReloadFromDataDirectory(string directory)
    {
        FilePath = Path.Combine(Path.GetFullPath(directory), "settings.json");
        Servers.Clear();
        Installations.Clear();
        CatalogUrls.Clear();
        LoadError = "";
        if (!_inMemory) Load();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            using var stream = File.OpenRead(FilePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var schema = root.GetProperty("schemaVersion").GetInt32();
            if (schema is not (1 or 2 or SchemaVersion))
                throw new InvalidDataException("Unsupported ServerApp settings version.");
            Language = root.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                ? AppTranslations.NormalizeLanguage(language.GetString()) : "en";
            foreach (var value in root.GetProperty("servers").EnumerateArray())
            {
                var win64 = value.GetProperty("win64").GetString();
                if (ServerLocation.TryCreate(win64, out var location, out _))
                {
                    var id = value.GetProperty("id").GetString()!;
                    if (!Guid.TryParseExact(id, "N", out _) || Servers.Any(s => s.Id == id)) continue;
                    string instanceRoot;
                    if (schema == 1)
                    {
                        try { instanceRoot = ServerInstanceFiles.Provision(DataDirectory, id, location!); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                        { instanceRoot = ""; }
                    }
                    else instanceRoot = value.TryGetProperty("instanceRoot", out var instancePath) &&
                        instancePath.ValueKind == JsonValueKind.String ? instancePath.GetString() ?? "" : "";
                    var row = new ServerRow(id, location!.Win64,
                        value.TryGetProperty("managed", out var flag) && flag.GetBoolean(), instanceRoot)
                    {
                        AutoUpdateBeforeStart = value.TryGetProperty("autoUpdateBeforeStart", out var autoUpdate) &&
                                                autoUpdate.ValueKind == JsonValueKind.True
                    };
                    row.UpdateName(ServerIniEditor.ReadConfiguredServerName(
                        instanceRoot.Length > 0 ? Path.Combine(instanceRoot, "TripwireServer.ini") : location.ConfigFile));
                    Servers.Add(row);
                }
            }
            if (schema >= 3 && root.TryGetProperty("installations", out var installations))
                foreach (var value in installations.EnumerateArray())
                {
                    var id = value.GetProperty("id").GetString();
                    var branch = value.GetProperty("branch").GetString();
                    var path = value.GetProperty("installRoot").GetString();
                    if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(path) ||
                        !Path.IsPathFullyQualified(path)) continue;
                    try
                    {
                        var normalized = ServerInstallation.ValidateBranch(branch);
                        if (Installations.Any(item => item.Branch == normalized ||
                            item.InstallRoot.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                        Installations.Add(new ServerInstallation(id!, normalized, Path.GetFullPath(path)));
                    }
                    catch (InvalidDataException) { }
                }
            if (schema < 3)
            {
                var legacyRoot = Path.Combine(DataDirectory, "game", "dedicated");
                if (ServerLocation.TryCreate(Path.Combine(legacyRoot, "DeceiveInc", "Binaries", "Win64"),
                        out _, out _))
                    Installations.Add(new ServerInstallation(Guid.NewGuid().ToString("N"), "public", legacyRoot));
            }
            if (root.TryGetProperty("catalogUrls", out var urls))
                foreach (var value in urls.EnumerateArray())
                    if (value.GetString() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo))
                        CatalogUrls.Add(uri.AbsoluteUri);
            if (schema < SchemaVersion && !_inMemory)
            {
                var backup = FilePath + $".v{schema}-backup";
                if (!File.Exists(backup)) File.Copy(FilePath, backup);
                doc.Dispose();
                stream.Dispose();
                Save();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or
            InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Servers.Clear();
            Installations.Clear();
            CatalogUrls.Clear();
            LoadError = "ServerApp settings could not be read. The original file will be backed up before new settings are saved.";
        }
    }

    public void Save()
    {
        if (_inMemory) return;
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "servers"));
        if (LoadError.Length > 0 && File.Exists(FilePath))
        {
            File.Copy(FilePath, FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false);
            LoadError = "";
        }
        var temporary = FilePath + ".tmp";
        using (var stream = File.Create(temporary))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("language", AppTranslations.NormalizeLanguage(Language));
            writer.WriteStartArray("installations");
            foreach (var installation in Installations)
            {
                writer.WriteStartObject();
                writer.WriteString("id", installation.Id);
                writer.WriteString("branch", installation.Branch);
                writer.WriteString("installRoot", installation.InstallRoot);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("servers");
            foreach (var server in Servers)
            {
                writer.WriteStartObject();
                writer.WriteString("id", server.Id);
                writer.WriteString("win64", server.Win64);
                writer.WriteString("instanceRoot", server.InstanceRoot);
                writer.WriteBoolean("managed", server.Managed);
                writer.WriteBoolean("autoUpdateBeforeStart", server.AutoUpdateBeforeStart);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("catalogUrls");
            foreach (var url in CatalogUrls) writer.WriteStringValue(url);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.Move(temporary, FilePath, true);
    }
}
