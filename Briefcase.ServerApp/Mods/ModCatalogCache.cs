using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Briefcase.ServerApp;

/// <summary>Stores validated HTTPS catalogs and revalidates them with ETag/Last-Modified.</summary>
public sealed class ModCatalogCache
{
    private const int MaximumBytes = 1_048_576;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _directory;

    public ModCatalogCache(string? directory = null) => _directory = directory ?? AppDataStore.CatalogsRoot;

    public async Task<byte[]> ReadAsync(string url, Action<byte[]> validate, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(source.UserInfo))
            throw new InvalidDataException("Catalogs must use HTTPS without credentials.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri))).ToLowerInvariant();
        var file = Path.Combine(_directory, key + ".json");
        var headers = Path.Combine(_directory, key + ".headers.json");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.UserAgent.ParseAdd("Briefcase.ServerApp/1.0");
            if (File.Exists(file) && File.Exists(headers))
            {
                try
                {
                    using var headerDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(headers, cancellationToken));
                    if (headerDoc.RootElement.TryGetProperty("etag", out var etag) &&
                        etag.GetString() is { } tag && EntityTagHeaderValue.TryParse(tag, out var parsed))
                        request.Headers.IfNoneMatch.Add(parsed);
                    if (headerDoc.RootElement.TryGetProperty("lastModified", out var modified) &&
                        modified.GetString() is { } stamp && DateTimeOffset.TryParse(stamp, out var date))
                        request.Headers.IfModifiedSince = date;
                }
                catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
                {
                    // A damaged validator must not prevent an unconditional refresh.
                }
            }
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
                throw new InvalidDataException("Catalog redirected outside HTTPS.");
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var cached = await ReadCacheAsync(file, cancellationToken);
                validate(cached);
                return cached;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumBytes)
                throw new InvalidDataException("Catalog is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumBytes) throw new InvalidDataException("Catalog is too large.");
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            validate(bytes);
            Directory.CreateDirectory(_directory);
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(temp, bytes, cancellationToken); File.Move(temp, file, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            var headersTemp = headers + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(headersTemp))
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("etag", response.Headers.ETag?.ToString());
                    writer.WriteString("lastModified", response.Content.Headers.LastModified?.ToString("R"));
                    writer.WriteEndObject();
                }
                File.Move(headersTemp, headers, true);
            }
            finally { if (File.Exists(headersTemp)) File.Delete(headersTemp); }
            return bytes;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!File.Exists(file)) throw;
            var cached = await ReadCacheAsync(file, cancellationToken);
            validate(cached);
            return cached;
        }
    }

    private static async Task<byte[]> ReadCacheAsync(string file, CancellationToken cancellationToken)
    {
        if (new FileInfo(file).Length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("Cached catalog is too large or empty.");
        return await File.ReadAllBytesAsync(file, cancellationToken);
    }
}
