using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Briefcase.ModPackages;

/// <summary>Reads stable releases and downloads one validated GitHub asset.</summary>
public sealed class GitHubModReleaseClient : IDisposable
{
    private const long MaximumArchiveBytes = 128L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubModReleaseClient()
        : this(CreateHttpClient(), ownsHttpClient: true)
    {
    }

    public GitHubModReleaseClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<GitHubModRelease> GetLatestAsync(
        GitHubModSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        var uri = new Uri(
            $"https://api.github.com/repos/{source.Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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
            throw new InvalidDataException("GitHub returned a non-stable mod release.");
        var tag = root.GetProperty("tag_name").GetString();
        if (!PackageVersion.TryParse(tag, out var version))
            throw new InvalidDataException($"GitHub release tag '{tag}' is invalid.");
        var expectedName = source.ResolveAssetName(version.Display);
        GitHubModReleaseAsset? selected = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (!string.Equals(name, expectedName, StringComparison.Ordinal)) continue;
            if (selected is not null)
                throw new InvalidDataException(
                    $"GitHub release contains more than one '{expectedName}' asset.");
            var size = asset.GetProperty("size").GetInt64();
            var digest = asset.TryGetProperty("digest", out var digestValue)
                ? digestValue.GetString()
                : null;
            var urlText = asset.GetProperty("browser_download_url").GetString();
            if (size is <= 0 or > MaximumArchiveBytes)
                throw new InvalidDataException("The mod archive size is invalid.");
            if (!TryReadSha256(digest, out var sha256))
                throw new InvalidDataException(
                    "The mod archive does not expose a valid GitHub SHA-256 digest.");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(downloadUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !downloadUri.AbsolutePath.StartsWith(
                    $"/{source.Repository}/releases/download/",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The mod archive URL is not trusted.");
            selected = new GitHubModReleaseAsset(name!, downloadUri, size, sha256);
        }
        return selected is null
            ? throw new InvalidDataException(
                $"GitHub release {version.Display} does not contain '{expectedName}'.")
            : new GitHubModRelease(version, version.Display, selected);
    }

    public async Task<PreparedModPackage> DownloadAsync(
        string expectedId,
        GitHubModSource source,
        GitHubModRelease release,
        string stagingBaseDirectory,
        Action<ModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? expectedDependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedId);
        var staging = Path.GetFullPath(Path.Combine(
            stagingBaseDirectory,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}",
            expectedId,
            release.DisplayVersion));
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, release.Asset.Name);
        try
        {
            progress?.Invoke(new ModDownloadProgress(
                0.02, "Downloading mod", $"Preparing {expectedId} {release.DisplayVersion}..."));
            using var request = new HttpRequestMessage(HttpMethod.Get, release.Asset.DownloadUri);
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } contentLength &&
                contentLength != release.Asset.Size)
                throw new InvalidDataException(
                    "The downloaded mod size differs from the GitHub release metadata.");
            await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var target = new FileStream(
                archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long received = 0;
            var lastPercent = -1;
            try
            {
                while (true)
                {
                    var read = await sourceStream.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    received = checked(received + read);
                    if (received > release.Asset.Size)
                        throw new InvalidDataException("The downloaded mod is larger than expected.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    var percent = (int)(received * 100 / release.Asset.Size);
                    if (percent == lastPercent) continue;
                    lastPercent = percent;
                    progress?.Invoke(new ModDownloadProgress(
                        0.05 + (0.85 * received / release.Asset.Size),
                        $"Downloading {expectedId}",
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
                    $"The downloaded mod is incomplete ({received} of {release.Asset.Size} bytes).");
            target.Position = 0;
            var actualHash = Convert.ToHexString(
                await SHA256.HashDataAsync(target, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(release.Asset.Sha256)))
                throw new InvalidDataException("The downloaded mod failed SHA-256 verification.");
            progress?.Invoke(new ModDownloadProgress(
                0.92, "Verifying mod", "The GitHub release asset passed its digest check."));
            return new PreparedModPackage(
                expectedId, source, release.Version, archivePath, staging,
                expectedDependencies);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    public static void ValidateSource(GitHubModSource source) =>
        ModPackageManifestFile.Validate(new ModPackageManifest
        {
            EntryAssembly = "placeholder.dll",
            Id = "placeholder.mod",
            Version = "0.0.0",
            Updates = source
        });

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Briefcase-Mods/1.0");
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
        if (candidate.Length != 64 || candidate.Any(character => !Uri.IsHexDigit(character)))
            return false;
        sha256 = candidate.ToLowerInvariant();
        return true;
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024d):0.0} MiB";

    internal static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
