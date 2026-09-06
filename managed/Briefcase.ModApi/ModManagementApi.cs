namespace Briefcase.ModApi;

using System.Text.Json;

/// <summary>
/// Serializable view of one setting declared by a loaded managed mod. Values
/// retain their JSON scalar type so an administration client does not have to
/// parse culture-dependent text.
/// </summary>
public sealed record ManagedModConfigurationEntry(
    string Section,
    string Key,
    string Description,
    string ValueType,
    JsonElement Value,
    JsonElement? Minimum,
    JsonElement? Maximum,
    IReadOnlyList<string> Choices);

/// <summary>
/// Stable, path-free description of a managed mod installed in Briefcase/Mods.
/// FileName is the identifier accepted by every lifecycle method.
/// </summary>
public sealed record ManagedModStatus(
    string FileName,
    string DisplayName,
    string Version,
    string Description,
    bool Enabled,
    bool Loaded,
    string? LastError)
{
    /// <summary>
    /// Stable identifier declared by ModInfo. An empty value means that the
    /// assembly could not be inspected and only its file name is known.
    /// </summary>
    public string Id { get; init; } = "";
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<string> ActiveDependents { get; init; } = [];
    public bool CanStop => ActiveDependents.Count == 0;
    public string? StopBlockReason => CanStop
        ? null
        : $"Disable these dependent mods first: {string.Join(", ", ActiveDependents)}.";
}

/// <summary>
/// Managed facade over Briefcase's mod loader. Operations are synchronous and
/// affect only DLLs already present in the framework's Mods directory.
/// </summary>
public readonly struct ModManagementApi
{
    private readonly IModManagementBackend? _backend;

    internal ModManagementApi(IModManagementBackend backend) => _backend = backend;

    public bool IsAvailable => _backend is not null;

    public IReadOnlyList<ManagedModStatus> GetInstalled() =>
        Backend.SnapshotInstalledMods();

    public void SetEnabled(string fileName, bool enabled) =>
        Backend.SetEnabled(fileName, enabled);

    public void Load(string fileName) => Backend.Load(fileName);
    public void Reload(string fileName) => Backend.Reload(fileName);
    public void Unload(string fileName) => Backend.Unload(fileName);
    public void Refresh() => Backend.Refresh();

    public IReadOnlyList<ManagedModConfigurationEntry> GetConfiguration(string fileName) =>
        Backend.GetConfiguration(fileName);

    public void SetConfiguration(
        string fileName,
        string section,
        string key,
        JsonElement value) =>
        Backend.SetConfiguration(fileName, section, key, value);

    private IModManagementBackend Backend => _backend ??
        throw new InvalidOperationException(
            "Managed mod control is not available in this Briefcase host.");
}

// The implementation lives in Briefcase.ManagedHost. Keeping it internal
// prevents mods from supplying their own loader while preserving a public,
// strongly typed facade for callers.
internal interface IModManagementBackend
{
    ManagedModStatus[] SnapshotInstalledMods();
    void SetEnabled(string fileName, bool enabled);
    void Load(string fileName);
    void Reload(string fileName);
    void Unload(string fileName);
    void Refresh();
    ManagedModConfigurationEntry[] GetConfiguration(string fileName);
    void SetConfiguration(string fileName, string section, string key, JsonElement value);
}
