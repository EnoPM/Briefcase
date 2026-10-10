using System.ComponentModel;
using System.Diagnostics;

namespace Briefcase.ServerApp;

/// <summary>Restarts through the installed launcher, or its sibling build when running from Rider.</summary>
public static class AppRestartService
{
    public static void StartLauncher()
    {
        using var process = Process.Start(CreateStartInfo(AppContext.BaseDirectory, Environment.ProcessId)) ??
            throw new Win32Exception("Briefcase Launcher could not be started.");
    }

    public static ProcessStartInfo CreateStartInfo(string applicationDirectory, int processId)
    {
        var app = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
        var launcherRoot = app.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
                           app.Parent.Parent?.Name.Equals("App", StringComparison.OrdinalIgnoreCase) == true
            ? app.Parent.Parent.Parent?.FullName : app.FullName;
        var launcher = Path.Combine(launcherRoot ?? app.FullName, "Briefcase.Launcher.exe");
        var development = false;
        if (!File.Exists(launcher))
        {
            var solution = app;
            while (solution.Parent is not null &&
                   !File.Exists(Path.Combine(solution.FullName, "Briefcase.ServerApp.slnx")))
                solution = solution.Parent;
            if (!File.Exists(Path.Combine(solution.FullName, "Briefcase.ServerApp.slnx")))
                throw new FileNotFoundException("Briefcase Launcher was not found beside the application.", launcher);
            var configuration = app.FullName.Split(Path.DirectorySeparatorChar)
                .FirstOrDefault(part => part is "Debug" or "Release") ?? "Debug";
            launcher = Path.Combine(solution.FullName, "Briefcase.ServerApp.Launcher", "bin",
                configuration, "net10.0", "win-x64", "Briefcase.Launcher.exe");
            development = true;
        }
        if (!File.Exists(launcher))
            throw new FileNotFoundException("Briefcase Launcher must be built before restarting from Rider.", launcher);
        var start = new ProcessStartInfo(launcher)
        {
            WorkingDirectory = Path.GetDirectoryName(launcher)!,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--restart-wait-pid=" + processId);
        if (development) start.ArgumentList.Add("--restart-app-directory=" + app.FullName);
        return start;
    }
}
