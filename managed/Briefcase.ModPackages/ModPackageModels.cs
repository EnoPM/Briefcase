namespace Briefcase.ModPackages;

public sealed record ModDownloadProgress(
    double Progress,
    string Stage,
    string Detail);

public sealed record GitHubModRelease(
    PackageVersion Version,
    string DisplayVersion,
    GitHubModReleaseAsset Asset);

public sealed record GitHubModReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Sha256);

public sealed record PreparedModPackage(
    string ExpectedId,
    GitHubModSource Source,
    PackageVersion Version,
    string ArchivePath,
    string StagingDirectory,
    IReadOnlyList<string>? ExpectedDependencies = null);

public sealed record MarketplaceCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MarketplaceMod> Mods { get; init; } = [];
}

public sealed record MarketplaceMod
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Repository { get; init; } = "";
    public string Asset { get; init; } = "";
    public string Target { get; init; } = "client";
    public string? Homepage { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public GitHubModSource ToSource() => new()
    {
        Repository = Repository,
        Asset = Asset,
        Automatic = true
    };
}
