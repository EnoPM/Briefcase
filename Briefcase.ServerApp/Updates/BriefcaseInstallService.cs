using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace Briefcase.ServerApp;

public sealed class BriefcaseInstallService
{
    public sealed record VersionCheck(string? Installed, string Latest)
    {
        public bool InstallNeeded => Installed is null;
        public bool UpdateAvailable => Installed is not null && CanReplace(Installed, Latest);
    }
    public static bool IsDevelopmentBuild(string? version) =>
        version?.Contains("-dev", StringComparison.OrdinalIgnoreCase) == true;

    public static bool CanReplace(string installed, string latest)
    {
        if (GitHubReleaseService.IsNewer(latest, installed)) return true;
        if (!IsDevelopmentBuild(installed)) return false;
        var suffix = installed.IndexOf('-', StringComparison.Ordinal);
        return suffix > 0 && Version.TryParse(installed[..suffix], out var local) &&
            Version.TryParse(latest, out var release) && release.CompareTo(local) >= 0;
    }
    private readonly GitHubReleaseService _releases = new();

    public async Task<VersionCheck> CheckVersionAsync(ServerLocation location, CancellationToken cancellationToken)
    {
        var installed = GitHubReleaseService.InstalledBriefcaseVersion(location);
        if (OperatingSystem.IsLinux())
        {
            var (version, _, _) = await LatestLinuxPackageAsync(cancellationToken);
            return new(installed, version);
        }
        var latest = await _releases.LatestBriefcaseAsync(cancellationToken);
        return new(installed, latest.Version);
    }

    public async Task<string> InstallOrUpdateAsync(ServerLocation location, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (!location.IsInstance)
            throw new InvalidOperationException("Migrate this server to a managed instance before installing Briefcase.");
        using (var running = ServerProcess.Find(location))
            if (running is not null) throw new InvalidOperationException("Stop the server before changing Briefcase.");
        if (OperatingSystem.IsLinux())
            return await InstallLinuxAsync(location, progress, cancellationToken);
        var installed = GitHubReleaseService.InstalledBriefcaseVersion(location);
        var latest = await _releases.LatestBriefcaseAsync(cancellationToken);
        if (installed is not null && !CanReplace(installed, latest.Version))
            return $"Briefcase {installed} is already current.";
        var downloads = Path.Combine(location.BriefcaseRoot, "Updates", "ServerAppDownloads");
        var archive = await _releases.DownloadAsync(latest, downloads, progress, cancellationToken);
        try
        {
            await Task.Run(() => BriefcasePackageInstaller.InstallOrUpdate(location, archive, latest.Version, progress), cancellationToken);
            await Task.Run(() => ServerPreparation.Prepare(location), cancellationToken);
            return $"Briefcase {latest.Version} installed for this server instance; server remains stopped.";
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }

    private static string? BundledLinuxArchive()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Native");
        if (!Directory.Exists(folder)) return null;
        var candidates = Directory.GetFiles(folder, "BriefcaseNative-Server-linux-x64-*.zip");
        if (candidates.Length > 1)
            throw new InvalidDataException("The application contains multiple Linux Briefcase server packages.");
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string LinuxPackageVersion(string archiveFile, bool verifyFileName = true)
    {
        using var archive = ZipFile.OpenRead(archiveFile);
        var entry = archive.GetEntry("Package.json") ??
            throw new InvalidDataException("The Linux Briefcase package has no manifest.");
        if (entry.Length is <= 0 or > 65536)
            throw new InvalidDataException("The Linux Briefcase package manifest is invalid.");
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var version = document.RootElement.GetProperty("frameworkVersion").GetString();
        if (version is null || !Version.TryParse(version, out _))
            throw new InvalidDataException("The Linux Briefcase package has no valid version.");
        var expected = $"BriefcaseNative-Server-linux-x64-{version}.zip";
        if (verifyFileName && !Path.GetFileName(archiveFile).Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException("The Linux Briefcase package version does not match its filename.");
        return version;
    }

    private async Task<(string Version, ReleaseAsset? Remote, string? Local)> LatestLinuxPackageAsync(
        CancellationToken cancellationToken)
    {
        var local = BundledLinuxArchive();
        var localVersion = local is null ? null : LinuxPackageVersion(local);
        ReleaseAsset? remote;
        try { remote = await _releases.LatestBriefcaseAsync(cancellationToken); }
        catch (Exception ex) when (local is not null && !cancellationToken.IsCancellationRequested &&
                                   ex is HttpRequestException or InvalidDataException or TaskCanceledException)
        { remote = null; }
        if (remote is not null && (localVersion is null || GitHubReleaseService.IsNewer(remote.Version, localVersion)))
            return (remote.Version, remote, null);
        if (local is not null) return (localVersion!, null, local);
        throw new InvalidDataException("No Linux Briefcase server package is available.");
    }

    private async Task<string> InstallLinuxAsync(ServerLocation location, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var (latest, release, local) = await LatestLinuxPackageAsync(cancellationToken);
        var installed = GitHubReleaseService.InstalledBriefcaseVersion(location);
        if (installed is not null && !CanReplace(installed, latest))
            return $"Briefcase {installed} is already current.";
        var launcher = Path.Combine(AppContext.BaseDirectory, "Native", "Briefcase.ServerLauncher");
        if (!File.Exists(launcher))
            throw new FileNotFoundException("The Linux server launcher is missing from this test build.", launcher);
        var archive = local ?? await _releases.DownloadAsync(release!,
            Path.Combine(location.BriefcaseRoot, "Updates", "ServerAppDownloads"), progress, cancellationToken);
        try
        {
            if (LinuxPackageVersion(archive, verifyFileName: local is not null) != latest)
                throw new InvalidDataException("The Linux Briefcase package version changed unexpectedly.");
            progress.Report("Installing Briefcase for this server instance…");
            var start = new ProcessStartInfo(launcher)
            {
                WorkingDirectory = location.InstanceRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            var executable = LinuxGameView.Prepare(location);
            foreach (var argument in new[] { "--server", executable, "--instance-root", location.InstanceRoot,
                         "--instance-id", location.InstanceId, "--install-archive", archive })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The Linux Briefcase installer did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Briefcase installation failed: {(await error).Trim()}");
            ServerInstanceFiles.RefreshRuntime(location);
            return $"Briefcase {latest} installed for this server instance; server remains stopped.";
        }
        finally { if (local is null && File.Exists(archive)) File.Delete(archive); }
    }
}
