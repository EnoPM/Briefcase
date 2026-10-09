using System.Buffers.Binary;
using System.Net.Http;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Briefcase.ServerApp;

/// <summary>Loads an optional square logo.png from a UE4SS mod folder or logoUrl from a catalog.</summary>
public static class ModLogo
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Dictionary<string, (long Length, DateTime Modified, Bitmap? Image)> Local =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (byte[] Bytes, Bitmap Image)> Remote = new(StringComparer.Ordinal);

    public static Bitmap? FromDirectory(string directory)
    {
        var file = Path.Combine(directory, "logo.png");
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                info.Length is < 24 or > MaximumBytes) return null;
            if (Local.TryGetValue(file, out var cached) &&
                cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
                return cached.Image;
            using var input = File.OpenRead(file);
            var image = Decode(input);
            Local[file] = (info.Length, info.LastWriteTimeUtc, image);
            return image;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return null; }
    }

    public static async Task<Bitmap?> FromUrlAsync(string? url, string? sha256,
        CancellationToken cancellationToken) => (await LoadRemoteAsync(url, sha256, cancellationToken))?.Image;

    public static async Task<byte[]?> VerifiedBytesAsync(string? url, string? sha256,
        CancellationToken cancellationToken) => sha256 is null
            ? null : (await LoadRemoteAsync(url, sha256, cancellationToken))?.Bytes;

    private static async Task<(byte[] Bytes, Bitmap Image)?> LoadRemoteAsync(string? url, string? sha256,
        CancellationToken cancellationToken)
    {
        if (url is null) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(source.UserInfo)) return null;
        var key = url + "\n" + sha256;
        if (Remote.TryGetValue(key, out var cached)) return cached;
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" }) return null;
            if (response.Content.Headers.ContentLength is > MaximumBytes) return null;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(bytes, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaximumBytes) return null;
                buffer.Write(bytes, 0, read);
            }
            var payload = buffer.ToArray();
            if (sha256 is not null && !Convert.ToHexString(SHA256.HashData(payload))
                    .Equals(sha256, StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = new MemoryStream(payload);
            var image = Decode(stream);
            if (image is null) return null;
            var result = (payload, image);
            Remote[key] = result;
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return null; }
    }

    private static Bitmap? Decode(Stream input)
    {
        Span<byte> header = stackalloc byte[24];
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !header.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(20, 4));
        if (width is < 32 or > 1024 || height != width) return null;
        input.Position = 0;
        return Bitmap.DecodeToWidth(input, 96, BitmapInterpolationMode.HighQuality);
    }
}
