using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp.Launcher;

internal static class LauncherElevation
{
    private const string MaintenanceArgument = "--launcher-maintenance";

    public static bool AccessDenied(Exception error) => error is UnauthorizedAccessException ||
        error is IOException io && (io.HResult & 0xffff) == 5;

    public static async Task PrepareAsync(string archive, string version, string sha256, string repository,
        CancellationToken cancellationToken) =>
        await RunAsync(["prepare", archive, version, sha256, repository], cancellationToken);

    public static async Task ActivateAsync(string version, CancellationToken cancellationToken) =>
        await RunAsync(["activate", version], cancellationToken);

    private static async Task RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Briefcase.Launcher.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("The launcher executable is unavailable.", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(MaintenanceArgument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Win32Exception("Administrator approval was not granted.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The administrator-approved application update failed.");
    }

    public static int? RunMaintenance(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != MaintenanceArgument) return null;
        try
        {
            var root = AppContext.BaseDirectory;
            if (arguments.Length == 6 && arguments[1] == "prepare")
            {
                var archive = Path.GetFullPath(arguments[2]);
                var downloads = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Briefcase", "ServerApp", "LauncherDownloads")) + Path.DirectorySeparatorChar;
                if (!Regex.IsMatch(arguments[5], "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
                    !Regex.IsMatch(arguments[4], "^[a-fA-F0-9]{64}$")) return 1;
                if (archive.StartsWith(downloads, StringComparison.OrdinalIgnoreCase))
                {
                    if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return 1;
                    using var input = File.OpenRead(archive);
                    if (!Convert.ToHexString(SHA256.HashData(input))
                            .Equals(arguments[4], StringComparison.OrdinalIgnoreCase)) return 1;
                    AppUpdateStore.Prepare(root, archive, arguments[3]);
                    LauncherUpdateStore.Stage(root, archive, arguments[3]);
                }
                else
                {
                    // With over-the-shoulder UAC, the administrator has a different LocalAppData.
                    // Fetch and verify the release again under that account instead of trusting a foreign path.
                    var updates = new LauncherUpdateService();
                    var release = updates.LatestAsync(arguments[5], CancellationToken.None).GetAwaiter().GetResult();
                    if (release.Version != arguments[3] ||
                        !release.Sha256.Equals(arguments[4], StringComparison.OrdinalIgnoreCase)) return 1;
                    var downloaded = updates.DownloadAsync(release, root, new Progress<double>(),
                        CancellationToken.None).GetAwaiter().GetResult();
                    try
                    {
                        AppUpdateStore.Prepare(root, downloaded, arguments[3]);
                        LauncherUpdateStore.Stage(root, downloaded, arguments[3]);
                    }
                    finally { if (File.Exists(downloaded)) File.Delete(downloaded); }
                }
                return 0;
            }
            if (arguments.Length == 3 && arguments[1] == "activate")
            {
                var app = new InstalledApp(arguments[2], Path.Combine(root, "App", "versions", arguments[2]));
                AppUpdateStore.Activate(root, app);
                return 0;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException) { }
        return 1;
    }
}
