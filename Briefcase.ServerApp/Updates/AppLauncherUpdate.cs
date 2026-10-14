using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

/// <summary>Replaces the launcher only after the process that started this app has exited.</summary>
public static class AppLauncherUpdate
{
    private static readonly string[] Files = OperatingSystem.IsLinux()
        ? ["Briefcase", "libSkiaSharp.so", "libHarfBuzzSharp.so"]
        : ["Briefcase.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"];

    public static async Task ApplyAsync(string version, int launcherPid, CancellationToken cancellationToken)
    {
        await WaitForLauncherAsync(launcherPid, cancellationToken);
        try { ApplyPrepared(InstallRoot(), version); }
        catch (Exception error) when (AccessDenied(error))
        {
            if (OperatingSystem.IsLinux())
                throw new UnauthorizedAccessException(
                    "Briefcase cannot update its launcher in this folder. Move the installation to a writable folder.", error);
            var executable = Path.Combine(AppContext.BaseDirectory, "Briefcase.ServerApp.exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add("--launcher-maintenance");
            start.ArgumentList.Add(version);
            start.ArgumentList.Add(launcherPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var elevated = Process.Start(start) ??
                throw new Win32Exception("Administrator approval was not granted for the launcher update.");
            await elevated.WaitForExitAsync(cancellationToken);
            if (elevated.ExitCode != 0)
                throw new IOException("The administrator-approved launcher update failed.");
        }
    }

    public static async Task<int?> RunMaintenanceAsync(string[] arguments)
    {
        if (arguments is not ["--launcher-maintenance", var version, var pidText]) return null;
        try
        {
            if (!int.TryParse(pidText, out var pid) || pid <= 0) return 1;
            await WaitForLauncherAsync(pid, CancellationToken.None);
            ApplyPrepared(InstallRoot(), version);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                                      ArgumentException or JsonException or Win32Exception)
        { return 1; }
    }

    private static string InstallRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    private static async Task WaitForLauncherAsync(int pid, CancellationToken cancellationToken)
    {
        if (pid <= 0) throw new InvalidDataException("Invalid launcher process ID.");
        try
        {
            using var launcher = Process.GetProcessById(pid);
            if (!launcher.ProcessName.Equals("Briefcase", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update target is not the Briefcase launcher.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            await launcher.WaitForExitAsync(timeout.Token);
        }
        catch (ArgumentException) { /* The launcher already exited. */ }
    }

    public static void ApplyPrepared(string root, string version)
    {
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
            throw new InvalidDataException("Invalid launcher update version.");
        root = Path.GetFullPath(root);
        var stage = Path.Combine(root, "App", "launcher-updates", version);
        if (!Directory.Exists(stage) || File.GetAttributes(stage).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The staged launcher is missing or redirected.");
        var manifestPath = Path.Combine(stage, "manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length is <= 0 or > 4096)
            throw new InvalidDataException("Invalid staged launcher manifest.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.GetProperty("version").GetString() != version)
            throw new InvalidDataException("Unexpected staged launcher version.");
        var records = manifest.GetProperty("files").EnumerateArray().ToArray();
        if (records.Length != Files.Length ||
            !records.Select(record => record.GetProperty("path").GetString()).Order(StringComparer.Ordinal)
                .SequenceEqual(Files.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid staged launcher inventory.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var name = record.GetProperty("path").GetString()!;
            var hash = record.GetProperty("sha256").GetString() ?? "";
            var size = record.GetProperty("bytes").GetInt64();
            var path = Path.Combine(stage, name);
            if (size is <= 0 or > 512L * 1024 * 1024 || !Regex.IsMatch(hash, "^[a-f0-9]{64}$") ||
                !File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ||
                new FileInfo(path).Length != size || !Hash(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged launcher file failed verification: " + name);
            hashes.Add(name, hash);
        }
        if (Files.All(name => File.Exists(Path.Combine(root, name)) &&
                              Hash(Path.Combine(root, name)).Equals(hashes[name], StringComparison.OrdinalIgnoreCase)))
        {
            Directory.Delete(stage, true);
            return;
        }

        // Test write access before touching any installed file so an elevation request leaves the old launcher intact.
        var probe = Path.Combine(root, ".launcher-write-probe-" + Guid.NewGuid().ToString("N"));
        try { using var stream = File.Create(probe); }
        finally { if (File.Exists(probe)) File.Delete(probe); }

        var mutexName = (OperatingSystem.IsLinux() ? "Briefcase.Launcher.Update." : "Local\\Briefcase.Launcher.Update.") +
                        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root))).Substring(0, 20);
        using var gate = new Mutex(false, mutexName);
        if (!gate.WaitOne(TimeSpan.FromSeconds(30))) throw new IOException("Another launcher update is running.");
        try
        {
            var backup = Path.Combine(stage, "backup");
            Directory.CreateDirectory(backup);
            foreach (var name in Files)
            {
                var installed = Path.Combine(root, name);
                if (File.Exists(installed)) File.Copy(installed, Path.Combine(backup, name), true);
            }
            var replaced = new List<string>();
            try
            {
                // Replace the executable last. Each individual move is atomic on the installation volume.
                var launcherName = OperatingSystem.IsLinux() ? "Briefcase" : "Briefcase.exe";
                foreach (var name in Files.Where(name => name != launcherName)
                             .Append(launcherName))
                {
                    var temporary = Path.Combine(root, ".launcher-new-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        File.Copy(Path.Combine(stage, name), temporary);
                        if (OperatingSystem.IsLinux() && name == launcherName)
                            File.SetUnixFileMode(temporary,
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        File.Move(temporary, Path.Combine(root, name), true);
                        replaced.Add(name);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                if (Files.Any(name => !Hash(Path.Combine(root, name)).Equals(hashes[name], StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Installed launcher verification failed.");
            }
            catch
            {
                foreach (var name in replaced.AsEnumerable().Reverse())
                {
                    var saved = Path.Combine(backup, name);
                    if (File.Exists(saved)) File.Copy(saved, Path.Combine(root, name), true);
                    else File.Delete(Path.Combine(root, name));
                }
                throw;
            }
        }
        finally { gate.ReleaseMutex(); }
        try { Directory.Delete(stage, true); }
        catch (IOException) { /* A verified launcher is installed; stale staging is safe to retry later. */ }
    }

    private static bool AccessDenied(Exception error) => error is UnauthorizedAccessException or
        Win32Exception { NativeErrorCode: 5 } || error is IOException io && (io.HResult & 0xffff) == 5;

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
