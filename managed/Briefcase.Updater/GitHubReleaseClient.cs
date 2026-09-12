using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Briefcase.Updater;

/// <summary>
/// Discovers stable releases from the official Briefcase GitHub repository and
/// downloads the package that matches the running client or dedicated server.
/// </summary>
public sealed class GitHubReleaseClient : IDisposable
{
    internal static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/EnoPM/Briefcase/releases/latest");
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubReleaseClient()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    internal GitHubReleaseClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<PreparedBriefcaseUpdate?> PrepareLatestAsync(
        BriefcaseUpdateOptions options,
        Action<BriefcaseUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ReleaseVersion.TryParse(
                options.InstalledVersion, out var installedVersion, out _))
            throw new InvalidDataException(
                $"Installed Briefcase version '{options.InstalledVersion}' is invalid.");

        CleanupOldStagingDirectories(options.StagingBaseDirectory);
        progress?.Invoke(new BriefcaseUpdateProgress(
            0.02, "Checking for updates", "Contacting the Briefcase release service..."));
        var release = await GetLatestReleaseAsync(options.PackageKind, cancellationToken)
            .ConfigureAwait(false);
        if (release.Version <= installedVersion)
        {
            progress?.Invoke(new BriefcaseUpdateProgress(
                0.06,
                "Briefcase is up to date",
                $"Version {options.InstalledVersion} is the latest stable release."));
            return null;
        }

        var stagingDirectory = Path.GetFullPath(Path.Combine(
            options.StagingBaseDirectory,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}",
            release.DisplayVersion));
        Directory.CreateDirectory(stagingDirectory);
        var archivePath = Path.Combine(stagingDirectory, release.Asset.Name);
        try
        {
            await DownloadAsync(release, archivePath, progress, cancellationToken)
                .ConfigureAwait(false);
            ReleaseArchiveValidator.Validate(
                archivePath, release.DisplayVersion, options.PackageKind);
            progress?.Invoke(new BriefcaseUpdateProgress(
                0.94,
                $"Briefcase {release.DisplayVersion} is ready",
                "The download was verified. Preparing the automatic restart..."));
            return new PreparedBriefcaseUpdate(
                release.Version,
                release.DisplayVersion,
                archivePath,
                stagingDirectory,
                options.PackageKind);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private async Task<GitHubRelease> GetLatestReleaseAsync(
        BriefcasePackageKind packageKind,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        var root = document.RootElement;
        if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
            throw new InvalidDataException("GitHub returned a non-stable release as latest.");

        var tag = root.GetProperty("tag_name").GetString();
        if (!ReleaseVersion.TryParse(tag, out var version, out var displayVersion))
            throw new InvalidDataException($"GitHub release tag '{tag}' is invalid.");

        var expectedName = packageKind == BriefcasePackageKind.Client
            ? $"Briefcase-Client-v{displayVersion}.zip"
            : $"Briefcase-Server-v{displayVersion}.zip";
        GitHubReleaseAsset? selected = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (!string.Equals(name, expectedName, StringComparison.Ordinal)) continue;
            if (selected is not null)
                throw new InvalidDataException(
                    $"GitHub release contains more than one '{expectedName}' asset.");

            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.GetProperty("digest").GetString();
            var urlText = asset.GetProperty("browser_download_url").GetString();
            if (size is <= 0 or > MaximumArchiveBytes)
                throw new InvalidDataException("The release archive size is invalid.");
            if (!TryReadSha256(digest, out var sha256))
                throw new InvalidDataException(
                    "The release archive does not expose a valid GitHub SHA-256 digest.");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !downloadUri.AbsolutePath.StartsWith(
                    "/EnoPM/Briefcase/releases/download/",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release archive URL is not trusted.");

            selected = new GitHubReleaseAsset(name!, downloadUri, size, sha256);
        }

        return selected is null
            ? throw new InvalidDataException(
                $"GitHub release {displayVersion} does not contain '{expectedName}'.")
            : new GitHubRelease(version, displayVersion, selected);
    }

    private async Task DownloadAsync(
        GitHubRelease release,
        string destination,
        Action<BriefcaseUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.Asset.DownloadUri);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength != release.Asset.Size)
            throw new InvalidDataException(
                "The downloaded archive size differs from the GitHub release metadata.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long received = 0;
        var lastReportedPercent = -1;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                received = checked(received + read);
                if (received > release.Asset.Size)
                    throw new InvalidDataException("The downloaded archive is larger than expected.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);

                var percent = (int)(received * 100 / release.Asset.Size);
                if (percent == lastReportedPercent) continue;
                lastReportedPercent = percent;
                progress?.Invoke(new BriefcaseUpdateProgress(
                    0.08 + (0.82 * received / release.Asset.Size),
                    $"Downloading Briefcase {release.DisplayVersion}",
                    $"{percent}%  -  {FormatBytes(received)} / {FormatBytes(release.Asset.Size)}"));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (received != release.Asset.Size)
            throw new InvalidDataException(
                $"The downloaded archive is incomplete ({received} of {release.Asset.Size} bytes).");

        target.Position = 0;
        var hash = await SHA256.HashDataAsync(target, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(release.Asset.Sha256)))
            throw new InvalidDataException("The downloaded archive failed SHA-256 verification.");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Briefcase-Updater/1.0");
        return client;
    }

    private static bool ReadBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static bool TryReadSha256(string? digest, out string sha256)
    {
        sha256 = "";
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = digest[prefix.Length..];
        if (candidate.Length != 64 ||
            candidate.Any(character => !Uri.IsHexDigit(character)))
            return false;
        sha256 = candidate.ToLowerInvariant();
        return true;
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024d):0.0} MiB";

    private static void CleanupOldStagingDirectories(string stagingBaseDirectory)
    {
        try
        {
            var root = Path.GetFullPath(stagingBaseDirectory);
            if (!Directory.Exists(root)) return;
            var cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (Directory.GetCreationTimeUtc(directory) >= cutoff) continue;
                TryDeleteDirectory(directory);
            }
        }
        catch
        {
            // Stale temporary files never block update discovery.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A failed update must never prevent Briefcase from loading.
        }
    }
}
