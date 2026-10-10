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
        var latest = await _releases.LatestBriefcaseAsync(cancellationToken);
        return new(installed, latest.Version);
    }

    public async Task<string> InstallOrUpdateAsync(ServerLocation location, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        using (var running = ServerProcess.FindAnyInstallation(location))
            if (running is not null) throw new InvalidOperationException("Stop the server before changing Briefcase.");
        var installed = GitHubReleaseService.InstalledBriefcaseVersion(location);
        var latest = await _releases.LatestBriefcaseAsync(cancellationToken);
        if (installed is not null && !CanReplace(installed, latest.Version))
            return $"Briefcase {installed} is already current.";
        var downloads = Path.Combine(location.Win64, "Briefcase", "Updates", "ServerAppDownloads");
        var archive = await _releases.DownloadAsync(latest, downloads, progress, cancellationToken);
        try
        {
            await Task.Run(() => BriefcasePackageInstaller.InstallOrUpdate(location, archive, latest.Version, progress), cancellationToken);
            await Task.Run(() => ServerPreparation.Prepare(location), cancellationToken);
            return $"Briefcase {latest.Version} installed. The local version.dll is disabled for app-managed injection; server remains stopped.";
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
    }
}
