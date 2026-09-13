namespace Briefcase.ModPackages;

public sealed record ModUpdateResult(
    string Id,
    string InstalledVersion,
    string? AvailableVersion,
    bool Updated,
    string? Error);

/// <summary>
/// Checks every update-enabled manifest before the mod loader opens any
/// assembly. Package replacement therefore never races a loaded DLL.
/// </summary>
public sealed class ModUpdateService : IDisposable
{
    private readonly GitHubModReleaseClient _releases;
    private readonly bool _ownsClient;

    public ModUpdateService()
        : this(new GitHubModReleaseClient(), ownsClient: true)
    {
    }

    public ModUpdateService(GitHubModReleaseClient releases, bool ownsClient = false)
    {
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _ownsClient = ownsClient;
    }

    public async Task<IReadOnlyList<ModUpdateResult>> UpdateInstalledAsync(
        IReadOnlyCollection<ModPackageDescriptor> packages,
        string modsDirectory,
        string stagingDirectory,
        Action<ModDownloadProgress>? progress = null,
        Action<string>? information = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = packages
            .Where(package => package.Manifest.Updates is { Automatic: true } &&
                              package.Manifest.Id is not null &&
                              package.Manifest.Version is not null)
            .ToArray();
        if (candidates.Length == 0) return [];

        var results = new ModUpdateResult[candidates.Length];
        var candidateProgress = new double[candidates.Length];
        var progressGate = new object();
        using var parallelism = new SemaphoreSlim(4);
        var tasks = candidates.Select((package, index) => Task.Run(async () =>
        {
            await parallelism.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await UpdateOneAsync(
                    package, modsDirectory, stagingDirectory,
                    update => ReportProgress(index, update),
                    information,
                    cancellationToken).ConfigureAwait(false);
                ReportProgress(index, new ModDownloadProgress(
                    1, "Mod update checked", package.Manifest.Id!));
            }
            catch (Exception exception)
            {
                var id = package.Manifest.Id!;
                results[index] = new ModUpdateResult(
                    id, package.Manifest.Version!, null, false, exception.Message);
                information?.Invoke($"Mod update for {id} was skipped: {exception.Message}");
            }
            finally
            {
                parallelism.Release();
            }
        }, cancellationToken)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;

        void ReportProgress(int index, ModDownloadProgress update)
        {
            if (progress is null) return;
            lock (progressGate)
            {
                candidateProgress[index] = Math.Max(
                    candidateProgress[index], Math.Clamp(update.Progress, 0, 1));
                progress(new ModDownloadProgress(
                    candidateProgress.Average(), update.Stage, update.Detail));
            }
        }
    }

    private async Task<ModUpdateResult> UpdateOneAsync(
        ModPackageDescriptor package,
        string modsDirectory,
        string stagingDirectory,
        Action<ModDownloadProgress>? progress,
        Action<string>? information,
        CancellationToken cancellationToken)
    {
        var id = package.Manifest.Id!;
        var installedText = package.Manifest.Version!;
        if (!PackageVersion.TryParse(installedText, out var installed))
            throw new InvalidDataException($"Installed mod version '{installedText}' is invalid.");
        var source = package.Manifest.Updates!;
        progress?.Invoke(new ModDownloadProgress(
            0.01, "Checking mod updates", $"Checking {id}..."));
        var release = await _releases.GetLatestAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (release.Version <= installed)
            return new ModUpdateResult(id, installed.Display, release.DisplayVersion, false, null);
        var prepared = await _releases.DownloadAsync(
            id, source, release, stagingDirectory, progress, cancellationToken,
            package.Manifest.Dependencies)
            .ConfigureAwait(false);
        ModPackageInstaller.Install(prepared, modsDirectory, package.DirectoryPath);
        information?.Invoke(
            $"Updated mod {id} from {installed.Display} to {release.DisplayVersion}.");
        return new ModUpdateResult(
            id, installed.Display, release.DisplayVersion, true, null);
    }

    public void Dispose()
    {
        if (_ownsClient) _releases.Dispose();
    }
}
