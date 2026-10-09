namespace Briefcase.ServerApp;

public sealed class BriefcaseInstallService
{
    public sealed record VersionCheck(string? Installed, string Latest)
    {
        public bool InstallNeeded => Installed is null;
        public bool UpdateAvailable => Installed is not null && !IsDevelopmentBuild(Installed) &&
            GitHubReleaseService.IsNewer(Latest, Installed);
    }
    public static bool IsDevelopmentBuild(string? version) =>
        version?.Contains("-dev", StringComparison.OrdinalIgnoreCase) == true;
    private readonly GitHubReleaseService _releases = new();

    public async Task<VersionCheck> CheckVersionAsync(ServerLocation location, CancellationToken cancellationToken)
    {
        var installed = GitHubReleaseService.InstalledBriefcaseVersion(location);
        if (IsDevelopmentBuild(installed)) return new(installed, installed!);
        var latest = await _releases.LatestBriefcaseAsync(cancellationToken);
        return new(installed, latest.Version);
    }

    public async Task<string> InstallOrUpdateAsync(ServerLocation location, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        using (var running = ServerProcess.FindAnyInstallation(location))
            if (running is not null) throw new InvalidOperationException("Stop the server before changing Briefcase.");
        var installed = GitHubReleaseService.InstalledBriefcaseVersion(location);
        if (IsDevelopmentBuild(installed))
            throw new InvalidOperationException("A local development build is installed; use the local deployment instead of a GitHub release.");
        var latest = await _releases.LatestBriefcaseAsync(cancellationToken);
        if (installed is not null && !GitHubReleaseService.IsNewer(latest.Version, installed))
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
