using Briefcase.ModPackages;

namespace Briefcase.ManagedHost;

/// <summary>
/// Owns asynchronous network and package work outside the unsafe native-facing
/// mod manager. Each operation creates short-lived HTTP clients and shares only
/// immutable snapshots with the loader.
/// </summary>
internal sealed class ModMarketplaceController
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private readonly string _modsDirectory;
    private readonly string _stagingDirectory;
    private readonly string _cachePath;
    private readonly bool _isServer;
    private readonly Func<HashSet<string>> _installedIds;
    private readonly Action _refresh;
    private readonly Action<string> _information;

    public ModMarketplaceController(
        string modsDirectory,
        string stagingDirectory,
        string cachePath,
        bool isServer,
        Func<HashSet<string>> installedIds,
        Action refresh,
        Action<string> information)
    {
        _modsDirectory = modsDirectory;
        _stagingDirectory = stagingDirectory;
        _cachePath = cachePath;
        _isServer = isServer;
        _installedIds = installedIds;
        _refresh = refresh;
        _information = information;
    }

    public async Task<MarketplaceModStatus[]> GetMarketplaceAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        using var client = new MarketplaceCatalogClient();
        var catalogue = await client.GetAsync(
                _cachePath, forceRefresh, cancellationToken)
            .ConfigureAwait(false);
        var installed = _installedIds();
        return catalogue.Mods
            .Where(IsCompatibleTarget)
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
            .Select(mod => new MarketplaceModStatus(
                mod.Id,
                mod.Name,
                mod.Author,
                mod.Description,
                mod.Repository,
                mod.Homepage,
                mod.Dependencies,
                installed.Contains(mod.Id)))
            .ToArray();
    }

    public async Task InstallAsync(
        string id,
        Action<MarketplaceInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var catalogueClient = new MarketplaceCatalogClient();
            var catalogue = await catalogueClient.GetAsync(
                    _cachePath, forceRefresh: false, cancellationToken)
                .ConfigureAwait(false);
            var byId = catalogue.Mods
                .Where(IsCompatibleTarget)
                .ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
            if (!byId.ContainsKey(id))
                throw new InvalidOperationException(
                    $"Mod '{id}' is not present in the Briefcase marketplace.");
            var installed = _installedIds();
            using var releases = new GitHubModReleaseClient();
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task InstallRecursive(string modId)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (installed.Contains(modId) || completed.Contains(modId)) return;
                if (!visiting.Add(modId))
                    throw new InvalidDataException(
                        $"Marketplace dependency cycle detected at '{modId}'.");
                if (!byId.TryGetValue(modId, out var mod))
                    throw new InvalidOperationException(
                        $"Required marketplace mod '{modId}' is unavailable for this target.");
                foreach (var dependency in mod.Dependencies)
                    await InstallRecursive(dependency).ConfigureAwait(false);

                progress?.Invoke(new MarketplaceInstallProgress(
                    0.01, "Checking release", $"Resolving {mod.Name}..."));
                var source = mod.ToSource();
                var release = await releases.GetLatestAsync(source, cancellationToken)
                    .ConfigureAwait(false);
                var prepared = await releases.DownloadAsync(
                        mod.Id,
                        source,
                        release,
                        _stagingDirectory,
                        update => progress?.Invoke(new MarketplaceInstallProgress(
                            update.Progress, update.Stage, update.Detail)),
                        cancellationToken,
                        mod.Dependencies)
                    .ConfigureAwait(false);
                ModPackageInstaller.Install(prepared, _modsDirectory);
                installed.Add(modId);
                completed.Add(modId);
                visiting.Remove(modId);
                _information(
                    $"Installed marketplace mod {mod.Id} {release.DisplayVersion} from {mod.Repository}.");
            }

            await InstallRecursive(id).ConfigureAwait(false);
            _refresh();
            progress?.Invoke(new MarketplaceInstallProgress(
                1, "Mod installed", "The mod and its dependencies are ready."));
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private bool IsCompatibleTarget(MarketplaceMod mod) =>
        mod.Target == "both" || (_isServer ? mod.Target == "server" : mod.Target == "client");
}
