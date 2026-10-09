using System.Diagnostics;

namespace Briefcase.ServerApp;

public static class ServerChangeTracker
{
    public static bool NeedsRestart(ServerLocation location, Process? process)
    {
        if (process is null) return false;
        DateTime started;
        try { started = process.StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        if (Changed(location.ConfigFile, started)) return true;
        if (Changed(location.BalanceProfileFile, started)) return true;
        var root = location.ModsRoot;
        if (Directory.Exists(root))
        {
            if (Changed(Path.Combine(root, "mods.txt"), started)) return true;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                if (Changed(Path.Combine(dir, "Data", "config.json"), started) ||
                    Changed(Path.Combine(dir, "BriefcasePreEntry.dll"), started) ||
                    Changed(Path.Combine(dir, "dlls", "main.dll"), started)) return true;
            }
        }
        return false;
    }

    private static bool Changed(string file, DateTime started) =>
        File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 &&
        File.GetLastWriteTimeUtc(file) > started;
}
