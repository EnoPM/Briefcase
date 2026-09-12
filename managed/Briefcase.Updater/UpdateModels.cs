namespace Briefcase.Updater;

/// <summary>The release package matching the running Deceive Inc. process.</summary>
public enum BriefcasePackageKind
{
    Client,
    Server
}

/// <summary>A user-facing update operation snapshot.</summary>
public readonly record struct BriefcaseUpdateProgress(
    double Progress,
    string Stage,
    string Detail);

/// <summary>A downloaded and verified release ready for the external installer.</summary>
public sealed record PreparedBriefcaseUpdate(
    Version Version,
    string DisplayVersion,
    string ArchivePath,
    string StagingDirectory,
    BriefcasePackageKind PackageKind);

/// <summary>Settings needed to discover and download an official release.</summary>
public sealed record BriefcaseUpdateOptions(
    string InstalledVersion,
    BriefcasePackageKind PackageKind,
    string StagingBaseDirectory);

/// <summary>
/// The request is written beside a temporary copy of the update installer.
/// It contains no secrets and is consumed only after the game process exits.
/// </summary>
public sealed class UpdateInstallRequest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public int ParentProcessId { get; init; }
    public required string InstallRoot { get; init; }
    public required string ArchivePath { get; init; }
    public required string Version { get; init; }
    public required string PackageKind { get; init; }
    public required string RestartMode { get; init; }
    public required string RestartExecutable { get; init; }
    public string[] RestartArguments { get; init; } = [];
    public string? SteamAppId { get; init; }
    public bool Restart { get; init; } = true;
}

internal sealed record GitHubReleaseAsset(
    string Name,
    Uri DownloadUri,
    long Size,
    string Sha256);

internal sealed record GitHubRelease(
    Version Version,
    string DisplayVersion,
    GitHubReleaseAsset Asset);
