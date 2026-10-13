using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Briefcase.ServerApp;

/// <summary>Creates the small writable runtime beside an instance, never inside the shared game.</summary>
public static class ServerInstanceFiles
{
    private const string FreshInstanceMarker = ".briefcase-fresh-instance";

    public static string Provision(string dataDirectory, string id, ServerLocation installation, string? name = null)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid instance ID.");
        var root = Path.Combine(Path.GetFullPath(dataDirectory), "servers", id);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("The instance directory already contains files.");
        Directory.CreateDirectory(root);
        try
        {
            if (name is null) CopyIfPresent(installation.ConfigFile, Path.Combine(root, "TripwireServer.ini"));
            CopyIfPresent(installation.BalanceProfileFile, Path.Combine(root, "CommunityBalanceProfile.json"));
            CopyIfMissing(installation.BalanceTemplateFile, Path.Combine(root, "CommunityBalanceProfile.json"));
            var ini = Path.Combine(root, "TripwireServer.ini");
            if (!File.Exists(ini))
            {
                if (name is null) File.WriteAllText(ini, "[/Script/DeceiveInc.TripwireServerSettings]\n");
                else ServerIniEditor.WriteDefault(ini, name, installation);
            }
            if (name is null)
            {
                CopyTree(Path.Combine(installation.BriefcaseRoot, "Core"), Path.Combine(root, "Briefcase", "Core"));
                CopyIfPresent(Path.Combine(installation.Win64, "Package.json"), Path.Combine(root, "Package.json"));
                CopyTree(Path.Combine(installation.Win64, "ue4ss"), Path.Combine(root, "ue4ss"));
                CopyTree(Path.Combine(installation.BriefcaseRoot, "Mods"), Path.Combine(root, "Briefcase", "Mods"));
                EnsureLegacyNativeModSelection(root);
            }
            else File.WriteAllText(Path.Combine(root, FreshInstanceMarker), "");
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
        MigrateLegacyRuntime(instance);
        var target = instance.Ue4ssRoot;
        var bridge = "BriefcaseServerBridge";
        if (!File.Exists(Path.Combine(target, "UE4SS.dll")) ||
            !File.Exists(Path.Combine(target, "Mods", bridge, "dlls", "main.dll")))
            throw new InvalidOperationException(
                "Briefcase is incomplete in this instance: install or update its UE4SS runtime and server bridge before starting it.");
        var settings = Path.Combine(target, "UE4SS-settings.ini");
        if (File.Exists(settings))
        {
            var lines = File.ReadAllLines(settings);
            for (var index = 0; index < lines.Length; index++)
                if (lines[index].TrimStart().StartsWith("ModsFolderPath", StringComparison.OrdinalIgnoreCase))
                    lines[index] = "ModsFolderPath = " + instance.ModsRoot;
            File.WriteAllLines(settings, lines);
        }
        var targetList = Path.Combine(instance.ModsRoot, "mods.txt");
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

    /// <summary>Copies an old shared runtime once, without changing or removing files in the Steam build.</summary>
    public static void MigrateLegacyRuntime(ServerLocation instance)
    {
        if (!instance.IsInstance || !Directory.Exists(instance.InstanceRoot) ||
            File.Exists(Path.Combine(instance.InstanceRoot, FreshInstanceMarker))) return;
        var installedHost = Path.Combine(instance.BriefcaseRoot, "Core", "Briefcase.NativeHost.dll");
        if (File.Exists(installedHost)) return;
        var oldRoot = Path.Combine(instance.Win64, "Briefcase");
        if (!File.Exists(Path.Combine(oldRoot, "Core", "Briefcase.NativeHost.dll"))) return;
        CopyTree(Path.Combine(oldRoot, "Core"), Path.Combine(instance.BriefcaseRoot, "Core"));
        CopyIfMissing(Path.Combine(instance.Win64, "Package.json"),
            Path.Combine(instance.InstanceRoot, "Package.json"));
        CopyTree(Path.Combine(instance.Win64, "ue4ss"), instance.Ue4ssRoot);
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

    private static void CopyRuntimeFile(string source, string target)
    {
        if (!File.Exists(source)) return;
        if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("A linked runtime file cannot be copied safely.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("A linked runtime target cannot be replaced safely.");
            using var original = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var originalHash = SHA256.HashData(original);
            var waitForRead = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using var installed = File.Open(target, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    if (original.Length == installed.Length &&
                        originalHash.AsSpan().SequenceEqual(SHA256.HashData(installed))) return;
                    break;
                }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 &&
                                             waitForRead.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (waitForRead.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(100);
                }
            }
        }

        // Keep the previous DLL intact if Windows still has it mapped during shutdown.
        var temporary = target + ".serverapp-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            var wait = Stopwatch.StartNew();
            while (true)
            {
                try { File.Move(temporary, target, overwrite: true); return; }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 &&
                                             wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (wait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    // Windows also reports an in-use mapped DLL as access denied.
                    Thread.Sleep(100);
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
            else if (overwrite) CopyRuntimeFile(entry, destination);
            else if (!File.Exists(destination)) File.Copy(entry, destination);
        }
    }
}
