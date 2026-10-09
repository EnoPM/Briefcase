using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public sealed record ReleaseAsset(string Version, Uri Url, long Size, string Sha256);

public sealed class GitHubReleaseService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const long MaximumArchiveSize = 512L * 1024 * 1024;

    public static string? InstalledBriefcaseVersion(ServerLocation location)
    {
        var file = Path.Combine(location.BriefcaseRoot, "Core", "Updater", "build.json");
        if (!File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.TryGetProperty("frameworkVersion", out var value) ? value.GetString() : null;
    }

    public static bool IsNewer(string remote, string? installed) => installed is null ||
        Version.TryParse(remote, out var latest) && Version.TryParse(installed, out var current) && latest > current;

    public async Task<ReleaseAsset> LatestBriefcaseAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.github.com/repos/EnoPM/BriefcaseNative/releases/latest");
        request.Headers.UserAgent.ParseAdd("Briefcase.ServerApp/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("The latest Briefcase release is not stable.");
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")) throw new InvalidDataException("Invalid Briefcase release version.");
        var version = tag[1..];
        var name = $"BriefcaseNative-Server-windows-x64-{version}.zip";
        var matching = root.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matching.Length != 1) throw new InvalidDataException("The latest release has no unique Windows server archive.");
        var item = matching[0];
        var url = new Uri(item.GetProperty("browser_download_url").GetString()!);
        var expected = $"https://github.com/EnoPM/BriefcaseNative/releases/download/{tag}/{name}";
        if (url.AbsoluteUri != expected) throw new InvalidDataException("Unexpected Briefcase download address.");
        var size = item.GetProperty("size").GetInt64();
        var digest = item.GetProperty("digest").GetString() ?? "";
        if (size is <= 0 or > MaximumArchiveSize || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("The release is missing a valid SHA-256 digest.");
        return new ReleaseAsset(version, url, size, digest[7..].ToLowerInvariant());
    }

    public async Task<string> DownloadAsync(ReleaseAsset asset, string folder, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("The release redirected outside GitHub.");
            if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength != asset.Size)
                throw new InvalidDataException("Unexpected release size.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(file);
            var buffer = new byte[65536];
            long copied = 0;
            int read;
            using var sha = SHA256.Create();
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > asset.Size) throw new InvalidDataException("Release exceeds the advertised size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.TransformBlock(buffer, 0, read, null, 0);
                progress.Report($"Downloading Briefcase: {copied * 100 / asset.Size}%");
            }
            sha.TransformFinalBlock([], 0, 0);
            if (copied != asset.Size || !Convert.ToHexString(sha.Hash!).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Briefcase archive checksum does not match GitHub's digest.");
            return file;
        }
        catch { if (File.Exists(file)) File.Delete(file); throw; }
    }
}
