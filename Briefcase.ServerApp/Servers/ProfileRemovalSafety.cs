namespace Briefcase.ServerApp;

/// <summary>Server-process checks used by the app before deleting an inactive profile's files.</summary>
public static class ProfileRemovalSafety
{
    public static void EnsureNoRunningServers(AppProfile profile)
    {
        var settings = Path.Combine(profile.DataDirectory, "settings.json");
        if (!File.Exists(settings)) return;
        var registry = new ServerRegistry(settings);
        if (registry.LoadError.Length > 0)
            throw new InvalidDataException(AppTranslations.Get("settings.profile_delete_invalid_settings"));
        foreach (var server in registry.Servers)
        {
            if (!ServerLocation.TryCreate(server, out var location, out _)) continue;
            using var running = ServerProcess.Find(location!);
            if (running is not null)
                throw new InvalidOperationException(AppTranslations.Get("settings.profile_delete_running_servers"));
        }
    }
}
