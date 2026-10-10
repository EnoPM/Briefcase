using System.Text.Json;

namespace Briefcase.ServerApp;

public sealed record AppProfile(string Id, string Name, string DataDirectory);

/// <summary>Keeps profile selection outside every profile's own data directory.</summary>
public sealed class AppProfileStore
{
    private const int SchemaVersion = 1;
    private readonly string _filePath;
    private readonly string _profilesRoot;
    private readonly List<AppProfile> _profiles = [];
    private string _activeId = "default";
    private string _nextActiveId = "default";

    public static AppProfileStore Current { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Briefcase", "ServerApp.profiles.json"),
        AppDataStore.DefaultRoot, AppDataStore.LocatorFile,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Briefcase", "Profiles"));

    public IReadOnlyList<AppProfile> Profiles => _profiles.AsReadOnly();
    public AppProfile Active => _profiles.Single(profile => profile.Id == _activeId);
    public string NextActiveId => _nextActiveId;
    public bool HasPendingActivation => _nextActiveId != _activeId;
    public string LoadError { get; private set; } = "";

    public AppProfileStore(string filePath, string fallbackDirectory, string legacyLocator, string profilesRoot)
    {
        _filePath = Path.GetFullPath(filePath);
        _profilesRoot = Path.GetFullPath(profilesRoot);
        var legacyDirectory = Path.GetFullPath(fallbackDirectory);
        try { legacyDirectory = AppDataStore.ResolveRoot(fallbackDirectory, legacyLocator); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidDataException or ArgumentException or KeyNotFoundException or
                                      InvalidOperationException or NotSupportedException)
        { /* A valid profile list remains usable even if its legacy locator is damaged. */ }
        _profiles.Add(new AppProfile("default", "Default", legacyDirectory));
        if (!File.Exists(_filePath)) return;
        try { Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or
                                      InvalidDataException or ArgumentException or KeyNotFoundException or
                                      InvalidOperationException or NotSupportedException)
        {
            LoadError = "The profile list could not be read. The existing data directory remains available; " +
                        "the invalid profile list will be backed up before it is replaced.";
        }
    }

    public AppProfile CreateAndActivate(string name)
        => Create(name, activateNow: true);

    public AppProfile CreateAndScheduleActivation(string name)
        => Create(name, activateNow: false);

    private AppProfile Create(string name, bool activateNow)
    {
        name = ValidateName(name);
        if (_profiles.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A profile with this name already exists.");
        if (_profiles.Count >= 32) throw new InvalidDataException("The profile limit has been reached.");
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_profilesRoot, id);
        ValidateSeparateDirectory(directory, null);
        Directory.CreateDirectory(Path.Combine(directory, "servers"));
        var profile = new AppProfile(id, name, directory);
        var next = _profiles.Append(profile).ToArray();
        Save(next, id);
        _profiles.Add(profile);
        _nextActiveId = id;
        if (activateNow) _activeId = id;
        return profile;
    }

    public void ScheduleActivation(string id)
    {
        if (!_profiles.Any(profile => profile.Id == id))
            throw new InvalidDataException("The selected profile no longer exists.");
        if (_nextActiveId == id) return;
        Save(_profiles, id);
        _nextActiveId = id;
    }

    public void Activate(string id)
    {
        if (!_profiles.Any(profile => profile.Id == id))
            throw new InvalidDataException("The selected profile no longer exists.");
        if (_activeId == id && _nextActiveId == id) return;
        Save(_profiles, id);
        _activeId = id;
        _nextActiveId = id;
    }

    public void UpdateActiveDataDirectory(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Active.DataDirectory.Equals(directory, StringComparison.OrdinalIgnoreCase)) return;
        ValidateSeparateDirectory(directory, _activeId);
        var next = _profiles.Select(profile => profile.Id == _activeId
            ? profile with { DataDirectory = directory } : profile).ToArray();
        Save(next, _nextActiveId);
        _profiles.Clear();
        _profiles.AddRange(next);
    }

    private void ValidateSeparateDirectory(string directory, string? excludeId)
    {
        if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A profile cannot use a linked data directory.");
        foreach (var profile in _profiles.Where(profile => profile.Id != excludeId))
            if (AppDataStore.IsInside(directory, profile.DataDirectory) ||
                AppDataStore.IsInside(profile.DataDirectory, directory))
                throw new InvalidDataException("Choose a data directory separate from the other profiles.");
    }

    private static string ValidateName(string? name)
    {
        var value = name?.Trim() ?? "";
        if (value.Length is < 1 or > 48 || value.Any(char.IsControl))
            throw new InvalidDataException("Enter a profile name of 1–48 characters.");
        return value;
    }

    private void Load()
    {
        if (new FileInfo(_filePath).Length is <= 0 or > 65_536)
            throw new InvalidDataException("Invalid profile list size.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(_filePath));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            throw new InvalidDataException("Unsupported profile list version.");
        var activeId = root.GetProperty("activeProfileId").GetString();
        var profiles = new List<AppProfile>();
        foreach (var item in root.GetProperty("profiles").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var name = ValidateName(item.GetProperty("name").GetString());
            var directory = item.GetProperty("dataDirectory").GetString() ?? "";
            if (id != "default" && !Guid.TryParseExact(id, "N", out _) ||
                !Path.IsPathFullyQualified(directory) || profiles.Count >= 32 ||
                profiles.Any(profile => profile.Id == id ||
                    profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    AppDataStore.IsInside(directory, profile.DataDirectory) ||
                    AppDataStore.IsInside(profile.DataDirectory, directory)))
                throw new InvalidDataException("The profile list contains invalid or overlapping entries.");
            profiles.Add(new AppProfile(id, name, Path.GetFullPath(directory)));
        }
        if (profiles.Count == 0 || profiles.Count(profile => profile.Id == "default") != 1 ||
            !profiles.Any(profile => profile.Id == activeId))
            throw new InvalidDataException("The profile list has no valid active profile.");
        _profiles.Clear();
        _profiles.AddRange(profiles);
        _activeId = activeId!;
        _nextActiveId = activeId!;
    }

    private void Save(IEnumerable<AppProfile> profiles, string activeId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        if (LoadError.Length > 0 && File.Exists(_filePath))
        {
            File.Copy(_filePath, _filePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
            LoadError = "";
        }
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", SchemaVersion);
                writer.WriteString("activeProfileId", activeId);
                writer.WriteStartArray("profiles");
                foreach (var profile in profiles)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", profile.Id);
                    writer.WriteString("name", profile.Name);
                    writer.WriteString("dataDirectory", profile.DataDirectory);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(temporary, _filePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
