using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp.Launcher;

public sealed record AppRelease(string Version, Uri BrowserUrl, Uri ApiUrl, long Size, string Sha256);

public sealed class LauncherUpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const long MaximumArchiveSize = 512L * 1024 * 1024;

    public static string? ReadRepository(string root)
    {
        var file = Path.Combine(root, "UpdateSource.json");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length is <= 0 or > 4096)
            throw new InvalidDataException("Invalid application update source.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        if (doc.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
            !doc.RootElement.GetProperty("enabled").GetBoolean()) return null;
        var repository = doc.RootElement.GetProperty("repository").GetString() ?? "";
        if (!Regex.IsMatch(repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("Invalid application release repository.");
        return repository;
    }

    public static bool IsNewer(string remote, string installed) =>
        Version.TryParse(remote, out var latest) && Version.TryParse(installed, out var current) && latest > current;

    public async Task<AppRelease> LatestAsync(string repository, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{repository}/releases/latest");
        Configure(request);
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The latest application release is not stable.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$"))
            throw new InvalidDataException("Invalid application release version.");
        var version = tag[1..];
        var name = $"Briefcase.ServerApp-win-x64-{version}.zip";
        var matches = root.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The latest release has no unique Windows application archive.");
        var asset = matches[0];
        var browser = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var expected = $"https://github.com/{repository}/releases/download/{tag}/{name}";
        if (browser.AbsoluteUri != expected) throw new InvalidDataException("Unexpected application download address.");
        var api = new Uri(asset.GetProperty("url").GetString()!);
        if (!api.AbsoluteUri.StartsWith($"https://api.github.com/repos/{repository}/releases/assets/", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected application asset API address.");
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (size is <= 0 or > MaximumArchiveSize || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The application release is missing its SHA-256 digest.");
        return new AppRelease(version, browser, api, size, digest[7..].ToLowerInvariant());
    }

    public async Task<string> DownloadAsync(AppRelease release, string root, IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Briefcase", "ServerApp", "LauncherDownloads");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            var token = Environment.GetEnvironmentVariable("GH_TOKEN");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                string.IsNullOrWhiteSpace(token) ? release.BrowserUrl : release.ApiUrl);
            Configure(request, binary: !string.IsNullOrWhiteSpace(token));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var host = response.RequestMessage?.RequestUri?.Host;
            if (host is not ("github.com" or "api.github.com" or "release-assets.githubusercontent.com" or
                             "objects.githubusercontent.com"))
                throw new InvalidDataException("Application download redirected outside GitHub.");
            if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength != release.Size)
                throw new InvalidDataException("Unexpected application release size.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(file);
            using var sha = SHA256.Create();
            var buffer = new byte[65536];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > release.Size) throw new InvalidDataException("Application download exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.TransformBlock(buffer, 0, read, null, 0);
                progress.Report((double)copied / release.Size);
            }
            sha.TransformFinalBlock([], 0, 0);
            if (copied != release.Size ||
                !Convert.ToHexString(sha.Hash!).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Application archive checksum does not match its release.");
            return file;
        }
        catch { if (File.Exists(file)) File.Delete(file); throw; }
    }

    private static void Configure(HttpRequestMessage request, bool binary = false)
    {
        request.Headers.UserAgent.ParseAdd("Briefcase.Launcher/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" :
            "application/vnd.github+json"));
        var token = Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
