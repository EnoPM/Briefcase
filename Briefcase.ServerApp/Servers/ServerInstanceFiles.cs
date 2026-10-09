using System.Text.Json;

namespace Briefcase.ServerApp;

/// <summary>Creates the small writable runtime beside an instance, never inside the shared game.</summary>
public static class ServerInstanceFiles
{
    public static string Provision(string dataDirectory, string id, ServerLocation installation, string? name = null)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid instance ID.");
        var root = Path.Combine(Path.GetFullPath(dataDirectory), "servers", id);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("The instance directory already contains files.");
        Directory.CreateDirectory(root);
        try
        {
            CopyIfPresent(installation.ConfigFile, Path.Combine(root, "TripwireServer.ini"));
            CopyIfPresent(installation.BalanceProfileFile, Path.Combine(root, "CommunityBalanceProfile.json"));
            CopyIfMissing(installation.BalanceTemplateFile, Path.Combine(root, "CommunityBalanceProfile.json"));
            var ini = Path.Combine(root, "TripwireServer.ini");
            if (!File.Exists(ini)) File.WriteAllText(ini, "[/Script/DeceiveInc.TripwireServerSettings]\n");
            CopyTree(Path.Combine(installation.Win64, "ue4ss"), Path.Combine(root, "ue4ss"));
            CopyTree(Path.Combine(installation.BriefcaseRoot, "Mods"), Path.Combine(root, "Briefcase", "Mods"));
            EnsureLegacyNativeModSelection(root);
            if (name is not null) ServerIniEditor.SaveServerName(Path.Combine(root, "TripwireServer.ini"), name);
            return root;
        }
        catch
        {
            // Only the newly created ID directory can be removed on provisioning failure.
            Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public static void RefreshRuntime(ServerLocation instance)
    {
        if (!instance.IsInstance) return;
        if (!Directory.Exists(instance.InstanceRoot))
            throw new DirectoryNotFoundException("The server instance directory is missing.");
        var shared = Path.Combine(instance.Win64, "ue4ss");
        var target = instance.Ue4ssRoot;
        var bridge = "BriefcaseServerBridge";
        if (!File.Exists(Path.Combine(shared, "UE4SS.dll")) ||
            !File.Exists(Path.Combine(shared, "Mods", bridge, "dlls", "main.dll")))
            throw new InvalidOperationException(
                "Briefcase is incomplete: install or update its UE4SS runtime and server bridge before starting this instance.");
        CopyIfPresent(Path.Combine(shared, "UE4SS.dll"), Path.Combine(target, "UE4SS.dll"));
        CopyIfMissing(Path.Combine(shared, "UE4SS-settings.ini"), Path.Combine(target, "UE4SS-settings.ini"));
        var settings = Path.Combine(target, "UE4SS-settings.ini");
        if (File.Exists(settings))
        {
            var lines = File.ReadAllLines(settings);
            for (var index = 0; index < lines.Length; index++)
                if (lines[index].TrimStart().StartsWith("ModsFolderPath", StringComparison.OrdinalIgnoreCase))
                    lines[index] = "ModsFolderPath = " + instance.ModsRoot;
            File.WriteAllLines(settings, lines);
        }
        CopyTree(Path.Combine(shared, "Mods", bridge), Path.Combine(instance.ModsRoot, bridge), overwrite: true);
        var sourceList = Path.Combine(shared, "Mods", "mods.txt");
        var targetList = Path.Combine(instance.ModsRoot, "mods.txt");
        CopyIfMissing(sourceList, targetList);
        if (Directory.Exists(Path.Combine(instance.ModsRoot, bridge)))
        {
            Directory.CreateDirectory(instance.ModsRoot);
            var selection = File.Exists(targetList) ? File.ReadAllLines(targetList).ToList() : [];
            selection.RemoveAll(line => line.TrimStart().StartsWith(bridge + " ", StringComparison.OrdinalIgnoreCase) ||
                                        line.TrimStart().StartsWith(bridge + ":", StringComparison.OrdinalIgnoreCase));
            selection.Insert(0, bridge + " : 1");
            File.WriteAllLines(targetList, selection);
        }
        EnsureLegacyNativeModSelection(instance.InstanceRoot);
    }

    private static void EnsureLegacyNativeModSelection(string instanceRoot)
    {
        var settings = Path.Combine(instanceRoot, "Briefcase", "settings.json");
        if (File.Exists(settings)) return; // Keep an explicit user selection intact.
        var list = Path.Combine(instanceRoot, "ue4ss", "Mods", "mods.txt");
        var nativeMods = Path.Combine(instanceRoot, "Briefcase", "Mods");
        if (!File.Exists(list) || !Directory.Exists(nativeMods)) return;

        var enabledUe4ss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(list))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;
            var separator = trimmed.IndexOf(':');
            if (separator > 0 && trimmed[(separator + 1)..].Trim() == "1")
                enabledUe4ss.Add(trimmed[..separator].Trim());
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["briefcase.player-cap"] = "BriefcasePlayerCap",
            ["briefcase.pregame-timer"] = "BriefcasePregameTimer",
            ["briefcase.suspicion-control"] = "BriefcaseSuspicionControl"
        };
        var nativeIds = Directory.EnumerateDirectories(nativeMods)
            .Where(path => File.Exists(Path.Combine(path, "briefcase.mod.json")))
            .Select(Path.GetFileName)
            .Where(id => id is not null)
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!nativeIds.Any(id => replacements.TryGetValue(id, out var ue4ss) && enabledUe4ss.Contains(ue4ss)))
            return;

        var enabledNative = nativeIds
            .Where(id => !replacements.TryGetValue(id, out var ue4ss) || !enabledUe4ss.Contains(ue4ss))
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var temporary = settings + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("enabledMods");
                writer.WriteStartArray();
                foreach (var id in enabledNative) writer.WriteStringValue(id);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(temporary, settings, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CopyIfPresent(string source, string target)
    {
        if (!File.Exists(source)) return;
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A linked runtime file cannot be copied safely.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static void CopyIfMissing(string source, string target)
    {
        if (!File.Exists(target)) CopyIfPresent(source, target);
    }

    private static void CopyTree(string source, string target, bool overwrite = false)
    {
        if (!Directory.Exists(source)) return;
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A linked runtime folder cannot be copied safely.");
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("A linked runtime entry cannot be copied safely.");
            var destination = Path.Combine(target, name);
            if (attributes.HasFlag(FileAttributes.Directory)) CopyTree(entry, destination, overwrite);
            else if (overwrite || !File.Exists(destination)) File.Copy(entry, destination, overwrite);
        }
    }
}
