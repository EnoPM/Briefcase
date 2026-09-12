using Briefcase.ModApi;
using System.Text.Json;

namespace Briefcase.ManagedHost;

/// <summary>
/// Keeps the Avalonia configuration host independent from the mod loader's
/// implementation while still allowing the framework tab to issue lifecycle
/// commands.
/// </summary>
internal interface IFrameworkModControl : IModManagementBackend
{
    string ModsDirectory { get; }
}

/// <summary>
/// Breaks the construction cycle between ModContext and ManagedModManager.
/// Mods receive this stable facade; the real manager is attached before any
/// mod assembly is loaded.
/// </summary>
internal sealed class DeferredModManagementBackend : IModManagementBackend
{
    private IModManagementBackend? _target;

    public void Attach(IModManagementBackend target) =>
        _target = target ?? throw new ArgumentNullException(nameof(target));

    public ManagedModStatus[] SnapshotInstalledMods() => Target.SnapshotInstalledMods();
    public void SetEnabled(string fileName, bool enabled) => Target.SetEnabled(fileName, enabled);
    public void Load(string fileName) => Target.Load(fileName);
    public void Reload(string fileName) => Target.Reload(fileName);
    public void Unload(string fileName) => Target.Unload(fileName);
    public void Refresh() => Target.Refresh();
    public ManagedModConfigurationEntry[] GetConfiguration(string fileName) =>
        Target.GetConfiguration(fileName);
    public void SetConfiguration(
        string fileName, string section, string key, JsonElement value) =>
        Target.SetConfiguration(fileName, section, key, value);

    private IModManagementBackend Target => _target ??
        throw new InvalidOperationException("The managed mod controller is not ready.");
}
