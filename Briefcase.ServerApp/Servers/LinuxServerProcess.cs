using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Briefcase.ServerApp;

internal static class LinuxServerProcess
{
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    private static IEnumerable<int> ProcessIds()
    {
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(directory), out var pid)) yield return pid;
    }

    private static string[] ReadNulDelimited(int pid, string name)
    {
        try
        {
            return Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{pid}/{name}"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsServer(int pid, ServerLocation location, bool checkInstance)
    {
        var arguments = ReadNulDelimited(pid, "cmdline");
        if (arguments.Length == 0 || !Path.IsPathFullyQualified(arguments[0])) return false;
        var executable = Path.GetFullPath(arguments[0]);
        var environment = ReadNulDelimited(pid, "environ");
        var instanceRoot = environment.FirstOrDefault(value => value.StartsWith("BRIEFCASE_INSTANCE_ROOT=", StringComparison.Ordinal))
            ?.Substring("BRIEFCASE_INSTANCE_ROOT=".Length);
        if (checkInstance && location.IsInstance)
            return (executable == LinuxGameView.Executable(location) || executable == location.Shipping) &&
                   instanceRoot == location.InstanceRoot &&
                   environment.Contains("BRIEFCASE_INSTANCE_ID=" + location.InstanceId);
        if (executable == location.Shipping) return true;
        return instanceRoot is not null &&
               executable == Path.Combine(instanceRoot, "GameView", "DeceiveInc", "Binaries", "Linux",
                   "DeceiveIncServer-Linux-Shipping") && LinuxGameView.BelongsTo(location, instanceRoot);
    }

    internal static Process? Find(ServerLocation location)
    {
        foreach (var pid in ProcessIds())
        {
            if (!IsServer(pid, location, checkInstance: true)) continue;
            try { return Process.GetProcessById(pid); }
            catch (ArgumentException) { /* Process exited during enumeration. */ }
        }
        return null;
    }

    internal static Process? FindAnyInstallation(ServerLocation location)
    {
        foreach (var pid in ProcessIds())
        {
            if (!IsServer(pid, location, checkInstance: false)) continue;
            try { return Process.GetProcessById(pid); }
            catch (ArgumentException) { /* Process exited during enumeration. */ }
        }
        return null;
    }

    private static int ParentId(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var end = stat.LastIndexOf(')');
            var fields = stat[(end + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return int.TryParse(fields[1], out var parent) ? parent : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static Process? FindLauncher(ServerLocation location, int gamePid)
    {
        var parent = ParentId(gamePid);
        if (parent < 1) return null;
        var args = ReadNulDelimited(parent, "cmdline");
        if (args.Length == 0 || Path.GetFileName(args[0]) != "Briefcase.ServerLauncher") return null;
        static bool HasPair(string[] args, string flag, string value)
        {
            for (var i = 1; i + 1 < args.Length; i++)
                if (args[i] == flag && args[i + 1] == value) return true;
            return false;
        }
        var executable = ReadNulDelimited(gamePid, "cmdline").FirstOrDefault();
        if (executable is null || !HasPair(args, "--server", executable) ||
            !HasPair(args, "--instance-id", location.InstanceId) ||
            !HasPair(args, "--instance-root", location.InstanceRoot)) return null;
        try { return Process.GetProcessById(parent); }
        catch (ArgumentException) { return null; }
    }

    internal static Process Start(ServerLocation location)
    {
        if (!location.IsInstance)
            throw new InvalidOperationException("Migrate this server to a managed instance before starting it from Briefcase.");
        using (var existing = Find(location))
            if (existing is not null) throw new InvalidOperationException("This server is already running.");
        ServerIniEditor.RepairServerWrittenMapRotation(location.ConfigFile);
        ServerInstanceFiles.RefreshRuntime(location);
        if (!File.Exists(location.ConfigFile) || !File.Exists(location.BalanceProfileFile))
            throw new InvalidOperationException("The instance configuration or balance profile is missing.");
        var viewExecutable = LinuxGameView.Prepare(location);
        var launcher = Path.Combine(AppContext.BaseDirectory, "Native", "Briefcase.ServerLauncher");
        if (!File.Exists(launcher))
            throw new InvalidOperationException("The Linux installation is missing its private server launcher.");
        var (gamePort, queryPort) = ServerProcess.ReadPorts(location.ConfigFile);
        var info = new ProcessStartInfo(launcher)
        {
            WorkingDirectory = location.InstanceRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "--server", viewExecutable, "--instance-root", location.InstanceRoot,
                     "--instance-id", location.InstanceId, "--", $"-Port={gamePort}", $"-QueryPort={queryPort}" })
            info.ArgumentList.Add(argument);
        using var supervisor = Process.Start(info) ?? throw new InvalidOperationException("The Linux server launcher did not start.");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            using var game = Find(location);
            if (game is not null && !game.HasExited) return Process.GetProcessById(game.Id);
            if (supervisor.HasExited)
                throw new InvalidOperationException($"The Linux server launcher exited with code {supervisor.ExitCode}. See the instance's Briefcase logs.");
            Thread.Sleep(250);
        }
        throw new TimeoutException("The Linux server did not start within 45 seconds. See the instance's Briefcase logs.");
    }

    internal static async Task StopAsync(ServerLocation location, CancellationToken cancellationToken)
    {
        using var game = Find(location);
        if (game is null) return;
        using var supervisor = FindLauncher(location, game.Id);
        if (supervisor is null)
            throw new InvalidOperationException("The matching Briefcase supervisor could not be found; the server was left running.");
        if (kill(supervisor.Id, 15) != 0)
            throw new InvalidOperationException("Could not request the Linux server to stop; it was left running.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await game.WaitForExitAsync(timeout.Token);
            ServerIniEditor.RepairServerWrittenMapRotation(location.ConfigFile);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Linux server did not stop within 45 seconds. See the instance's Briefcase logs.");
        }
    }
}
