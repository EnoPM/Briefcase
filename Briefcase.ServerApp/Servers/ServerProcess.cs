using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Briefcase.ServerApp;

public sealed class ServerProcess
{
    private const uint EventModifyState = 0x0002;
    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenEvent(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    public static Process? Find(ServerLocation location)
    {
        foreach (var process in Process.GetProcessesByName("DeceiveIncServer-Win64-Shipping"))
        {
            try
            {
                if (!process.HasExited && string.Equals(
                        Path.GetFullPath(process.MainModule!.FileName), location.Shipping,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!location.IsInstance) return process;
                    var marker = OpenEvent(Synchronize, false,
                        $"Local\\BriefcaseNative.Instance.{location.InstanceId}.{process.Id}");
                    if (marker != 0)
                    {
                        CloseHandle(marker);
                        return process;
                    }
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // The process may have exited or access may be denied.
            }
            process.Dispose();
        }
        return null;
    }

    public static Process Start(ServerLocation location)
    {
        if (!location.IsInstance)
            throw new InvalidOperationException("Migrate this server to a managed instance before starting it from Briefcase.");
        using var existing = Find(location);
        if (existing is not null)
            throw new InvalidOperationException("This server is already running.");
        var injector = Path.Combine(AppContext.BaseDirectory, "Native", "Briefcase.ServerInjector.exe");
        var bootstrap = Path.Combine(AppContext.BaseDirectory, "Native", "Briefcase.ServerBootstrap.dll");
        if (!File.Exists(injector) || !File.Exists(bootstrap))
            throw new InvalidOperationException("The ServerApp installation is incomplete: its private injection components are missing.");
        ServerInstanceFiles.MigrateLegacyRuntime(location);
        if (!File.Exists(Path.Combine(location.BriefcaseRoot, "Core", "Briefcase.NativeHost.dll")))
            throw new InvalidOperationException("Install Briefcase before starting this server from ServerApp.");
        using (var other = FindAnyInstallation(location))
            if (other is null) ServerPreparation.Prepare(location);
        ServerInstanceFiles.RefreshRuntime(location);
        var info = CreateStartInfo(location);
        info.FileName = injector;
        info.ArgumentList.Insert(0, location.Shipping);
        info.ArgumentList.Insert(0, "--server");
        if (location.IsInstance)
        {
            info.ArgumentList.Insert(2, "--instance-root");
            info.ArgumentList.Insert(3, location.InstanceRoot);
            info.ArgumentList.Insert(4, "--instance-id");
            info.ArgumentList.Insert(5, location.InstanceId);
        }
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var helper = Process.Start(info) ?? throw new InvalidOperationException("The Briefcase injector could not be started.");
        if (!helper.WaitForExit(100_000))
        {
            helper.Kill(entireProcessTree: true);
            throw new TimeoutException("The Briefcase injector timed out.");
        }
        var output = helper.StandardOutput.ReadToEnd().Trim();
        var error = helper.StandardError.ReadToEnd().Trim();
        if (helper.ExitCode != 0 || !int.TryParse(output, out var pid) || pid <= 0)
            throw new InvalidOperationException("Briefcase injection failed: " + (error.Length > 0 ? error : output));
        return Process.GetProcessById(pid);
    }

    public static ProcessStartInfo CreateStartInfo(ServerLocation location)
    {
        var (gamePort, queryPort) = ReadPorts(location.ConfigFile);
        var start = new ProcessStartInfo(location.Shipping)
        {
            WorkingDirectory = location.Win64,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var flag in new[] { "-unattended", "-NoSplash", "-NOCONSOLE", "-nullrhi", "-nosound" })
            start.ArgumentList.Add(flag);
        start.ArgumentList.Add($"-Port={gamePort}");
        start.ArgumentList.Add($"-QueryPort={queryPort}");
        if (location.IsInstance)
        {
            if (!File.Exists(location.ConfigFile))
                throw new FileNotFoundException("The instance server configuration is missing.", location.ConfigFile);
            if (!File.Exists(location.BalanceProfileFile))
                throw new FileNotFoundException("The instance balance profile is missing.", location.BalanceProfileFile);
            start.ArgumentList.Add($"-TripwireServerIni={location.ConfigFile}");
            start.ArgumentList.Add($"-CommunityBalanceProfile={location.BalanceProfileFile}");
        }
        return start;
    }

    public static Process? FindAnyInstallation(ServerLocation location)
    {
        if (!ServerLocation.TryCreate(location.Win64, out var shared, out _)) return null;
        return Find(shared!);
    }

    public static async Task StopAsync(ServerLocation location, CancellationToken cancellationToken)
    {
        using var process = Find(location);
        if (process is null)
            return;
        var suffix = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = OpenEvent(EventModifyState, false, "Local\\BriefcaseNative.Stop." + suffix);
        var stopped = OpenEvent(Synchronize, false, "Local\\BriefcaseNative.Stopped." + suffix);
        if (request == 0 || stopped == 0)
        {
            if (request != 0) CloseHandle(request);
            if (stopped != 0) CloseHandle(stopped);
            throw new InvalidOperationException("Briefcase shutdown control is not ready. The server was left running.");
        }
        try
        {
            if (!SetEvent(request))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not request server shutdown.");
            // The same acknowledgment used by Briefcase.ServerRestart confirms mod cleanup.
            if (await Task.Run(() => WaitForSingleObject(stopped, 5000), cancellationToken) != 0)
                throw new TimeoutException("The server did not acknowledge shutdown. It was left running.");
            if (!process.HasExited)
            {
                using var gracefulWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulWait.CancelAfter(TimeSpan.FromSeconds(5));
                try { await process.WaitForExitAsync(gracefulWait.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Briefcase has acknowledged mod cleanup, but Unreal did not exit by itself.
                    if (!process.HasExited) process.Kill();
                }
            }
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            CloseHandle(request);
            CloseHandle(stopped);
        }
    }

    internal static (int Game, int Query) ReadPorts(string configFile)
    {
        var game = 50000;
        var query = 50001;
        if (File.Exists(configFile))
        {
            foreach (var raw in File.ReadLines(configFile))
            {
                var line = raw.Trim();
                if (line.StartsWith("GamePort=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[9..], out var value)) game = value;
                if (line.StartsWith("QueryPort=", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(line[10..], out value)) query = value;
            }
        }
        if (game is < 1 or > 65535 || query is < 1 or > 65535 || game == query)
            throw new InvalidDataException("The configured game and query ports must be different valid UDP ports.");
        return (game, query);
    }
}
