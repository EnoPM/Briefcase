using System.Text.Json;

namespace Briefcase.ModPackages;

/// <summary>Downloads the curated Briefcase marketplace catalogue with a local fallback.</summary>
public sealed class MarketplaceCatalogClient : IDisposable
{
    public static readonly Uri DefaultCatalogUri = new(
        "https://raw.githubusercontent.com/EnoPM/Briefcase/main/marketplace/catalog.json");
    private const int MaximumCatalogBytes = 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _catalogUri;

    public MarketplaceCatalogClient(Uri? catalogUri = null)
        : this(CreateHttpClient(), catalogUri ?? DefaultCatalogUri, ownsHttpClient: true)
    {
    }

    public MarketplaceCatalogClient(
        HttpClient httpClient,
        Uri catalogUri,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _catalogUri = catalogUri ?? throw new ArgumentNullException(nameof(catalogUri));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<MarketplaceCatalog> GetAsync(
        string cachePath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cachePath);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await _httpClient.GetAsync(
                _catalogUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumCatalogBytes)
                throw new InvalidDataException("The marketplace catalogue is too large.");
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token)
                .ConfigureAwait(false);
            if (bytes.Length is <= 0 or > MaximumCatalogBytes)
                throw new InvalidDataException("The marketplace catalogue size is invalid.");
            var catalogue = Parse(bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken)
                .ConfigureAwait(false);
            return catalogue;
        }
        catch when (File.Exists(cachePath))
        {
            return Parse(await File.ReadAllBytesAsync(cachePath, cancellationToken)
                .ConfigureAwait(false));
        }
    }

    public static MarketplaceCatalog Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is <= 0 or > MaximumCatalogBytes)
            throw new InvalidDataException("The marketplace catalogue size is invalid.");
        var catalogue = JsonSerializer.Deserialize<MarketplaceCatalog>(utf8, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }) ?? throw new InvalidDataException("The marketplace catalogue is empty.");
        Validate(catalogue);
        return catalogue;
    }

    public static void Validate(MarketplaceCatalog catalogue)
    {
        if (catalogue.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Unsupported marketplace schema {catalogue.SchemaVersion}.");
        if (catalogue.Mods is null || catalogue.Mods.Count > 2048)
            throw new InvalidDataException("The marketplace catalogue contains too many mods.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in catalogue.Mods)
        {
            if (mod is null)
                throw new InvalidDataException("The marketplace contains a null mod entry.");
            if (!ids.Add(mod.Id))
                throw new InvalidDataException($"Duplicate marketplace mod id '{mod.Id}'.");
            if (string.IsNullOrWhiteSpace(mod.Name) || mod.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(mod.Author) || mod.Author.Length > 100 ||
                mod.Description is null || mod.Description.Length > 1000 ||
                mod.Target is not ("client" or "server" or "both") ||
                mod.Dependencies is null || mod.Dependencies.Count > 64 ||
                mod.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                mod.Dependencies.Count)
                throw new InvalidDataException($"Marketplace metadata for '{mod.Id}' is invalid.");
            GitHubModReleaseClient.ValidateSource(mod.ToSource());
            ModPackageManifestFile.Validate(new ModPackageManifest
            {
                EntryAssembly = "placeholder.dll",
                Id = mod.Id,
                Version = "0.0.0",
                Dependencies = mod.Dependencies
            });
            if (mod.Homepage is not null &&
                (!Uri.TryCreate(mod.Homepage, UriKind.Absolute, out var homepage) ||
                 homepage.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException(
                    $"Marketplace homepage for '{mod.Id}' must use HTTPS.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Briefcase-Marketplace/1.0");
        return client;
    }
}
