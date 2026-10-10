using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;

namespace Briefcase.ServerApp;

public sealed record ModFormAsset(string Url, string Sha256);

public sealed record MarketplaceMod(string Id, string Name, string Description, string Version,
    Uri DownloadUrl, string Sha256, string Catalog, string Status = "Not installed",
    string? LogoUrl = null, Bitmap? Logo = null, string Author = "", string? LogoSha256 = null,
    string? DocumentationUrl = null, string? DiscordUrl = null, string? HomepageUrl = null,
    int CatalogSchema = 1, string? FormSchemaOverrideUrl = null, string? FormSchemaOverrideSha256 = null,
    IReadOnlyDictionary<string, ModFormAsset>? FormTranslations = null)
{
    public bool HasLogo => Logo is not null;
    public bool MissingLogo => Logo is null;
    public bool HasDocumentation => DocumentationUrl is not null;
    public bool HasDiscord => DiscordUrl is not null;
    public bool HasHomepage => HomepageUrl is not null;
    public bool HasAuthor => Author.Length > 0;
    public string VersionSummary => "v" + Version;
}

public sealed record InstalledModMetadata(string Id, string Version, string Name, string Author,
    string Description, string Catalog, string? DocumentationUrl, string? DiscordUrl, string? HomepageUrl,
    int? LegacyMaxPlayersMaximum = null);

public sealed class ModMarketplace
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const long MaximumArchive = 256L * 1024 * 1024;
    private const string LegacyIdPattern = "^[A-Za-z0-9_-]{1,64}$";
    private const string ModIdPattern = "^[a-z0-9]+(?:[._-][a-z0-9]+)*$";
    private readonly ModCatalogCache _cache;

    public ModMarketplace(string? cacheDirectory = null) => _cache = new ModCatalogCache(cacheDirectory);

    public async Task<IReadOnlyList<MarketplaceMod>> ReadCatalogAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await _cache.ReadAsync(url, data => ParseCatalog(data, url), cancellationToken);
        return ParseCatalog(bytes, url);
    }

    public static IReadOnlyList<MarketplaceMod> ParseCatalog(byte[] bytes, string url)
    {
        if (bytes.Length is <= 0 or > 1_048_576) throw new InvalidDataException("Catalog is too large or empty.");
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        var schema = root.GetProperty("schemaVersion").GetInt32();
        if (schema is not (1 or 2)) throw new InvalidDataException("Unsupported catalog schema.");
        var mods = new List<MarketplaceMod>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.GetProperty("mods").EnumerateArray())
        {
            if (mods.Count >= 256) throw new InvalidDataException("Catalog contains too many mods.");
            var id = item.GetProperty("id").GetString() ?? "";
            var name = item.GetProperty("name").GetString() ?? "";
            var description = item.GetProperty("description").GetString() ?? "";
            var version = item.GetProperty("version").GetString() ?? "";
            var download = item.GetProperty("downloadUrl").GetString() ?? "";
            var hash = item.GetProperty(schema == 2 ? "archiveSha256" : "sha256").GetString() ?? "";
            var logoUrl = item.TryGetProperty("logoUrl", out var logoProperty) ? logoProperty.GetString() : null;
            var logoHash = schema == 2 ? RequiredOptionalString(item, "logoSha256") : null;
            var author = schema == 2 ? item.GetProperty("author").GetString() ?? "" : "";
            var documentation = OptionalUrl(item, "documentationUrl");
            var discord = OptionalUrl(item, "discordUrl");
            var homepage = OptionalUrl(item, "homepageUrl");
            var formUrl = OptionalUrl(item, "formSchemaOverrideUrl");
            var formHash = schema == 2 ? RequiredOptionalString(item, "formSchemaOverrideSha256") : null;
            var formTranslations = ReadFormTranslations(item);
            if (!ids.Add(id) || !Regex.IsMatch(id, schema == 2 ? ModIdPattern : LegacyIdPattern) ||
                id.Length > 64 || name.Length is < 1 or > 128 ||
                description.Length > 1024 || !Regex.IsMatch(version, "^\\d+\\.\\d+\\.\\d+$") ||
                !Uri.TryCreate(download, UriKind.Absolute, out var asset) || asset.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(asset.UserInfo) || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") ||
                schema == 2 && (author.Length is < 1 or > 100 ||
                    (logoUrl is null) != (logoHash is null) ||
                    logoHash is not null && !Regex.IsMatch(logoHash, "^[a-fA-F0-9]{64}$") ||
                    (formUrl is null) != (formHash is null) ||
                    formHash is not null && !Regex.IsMatch(formHash, "^[a-fA-F0-9]{64}$")) ||
                logoUrl is not null && (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var logo) ||
                    logo.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(logo.UserInfo)))
                throw new InvalidDataException("Catalog contains an invalid mod entry.");
            mods.Add(new MarketplaceMod(id, name, description, version, asset, hash.ToLowerInvariant(), url,
                LogoUrl: logoUrl, Author: author, LogoSha256: logoHash?.ToLowerInvariant(),
                DocumentationUrl: documentation, DiscordUrl: discord, HomepageUrl: homepage,
                CatalogSchema: schema, FormSchemaOverrideUrl: formUrl,
                FormSchemaOverrideSha256: formHash?.ToLowerInvariant(), FormTranslations: formTranslations));
        }
        return mods;
    }

    private static string? RequiredOptionalString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    private static string? OptionalUrl(JsonElement item, string key)
    {
        var value = RequiredOptionalString(item, key);
        if (value is null) return null;
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Catalog contains an invalid " + key + ".");
        return uri.AbsoluteUri;
    }

    private static IReadOnlyDictionary<string, ModFormAsset>? ReadFormTranslations(JsonElement item)
    {
        if (!item.TryGetProperty("formTranslations", out var entries)) return null;
        if (entries.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid form translations.");
        var result = new Dictionary<string, ModFormAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateObject())
        {
            if (result.Count >= 16 || !Regex.IsMatch(entry.Name, "^[a-z]{2}(?:-[A-Za-z0-9]{2,8})?$"))
                throw new InvalidDataException("Invalid form translation language.");
            if (entry.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid form translation.");
            var url = OptionalUrl(entry.Value, "url") ?? throw new InvalidDataException("Missing form translation URL.");
            var hash = RequiredOptionalString(entry.Value, "sha256") ?? "";
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") ||
                !result.TryAdd(entry.Name, new ModFormAsset(url, hash.ToLowerInvariant())))
                throw new InvalidDataException("Invalid form translation checksum.");
        }
        return result;
    }

    public async Task<string> DownloadAsync(MarketplaceMod mod, string folder, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "mod-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var response = await Http.GetAsync(mod.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
                throw new InvalidDataException("Mod archive redirected outside HTTPS.");
            if (response.Content.Headers.ContentLength is > MaximumArchive)
                throw new InvalidDataException("Mod archive is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(file);
            using var sha = SHA256.Create();
            var bytes = new byte[65536];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(bytes, cancellationToken)) > 0)
            {
                copied += read;
                if (copied > MaximumArchive) throw new InvalidDataException("Mod archive is too large.");
                await output.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
                sha.TransformBlock(bytes, 0, read, null, 0);
                if (response.Content.Headers.ContentLength is > 0 and var size)
                    progress.Report($"Downloading {mod.Name}: {copied * 100 / size}%");
            }
            sha.TransformFinalBlock([], 0, 0);
            if (!Convert.ToHexString(sha.Hash!).Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Mod archive checksum does not match the catalog.");
            return file;
        }
        catch { if (File.Exists(file)) File.Delete(file); throw; }
    }

    public static async Task<byte[]?> DownloadFormOverrideAsync(MarketplaceMod mod, CancellationToken cancellationToken)
    {
        if (mod.FormSchemaOverrideUrl is null) return null;
        var bytes = await DownloadFormAssetAsync(mod.FormSchemaOverrideUrl, mod.FormSchemaOverrideSha256!, cancellationToken);
        FormSchema.LoadBase().ApplyOverride(bytes);
        return bytes;
    }

    public static async Task<IReadOnlyDictionary<string, byte[]>> DownloadFormTranslationsAsync(MarketplaceMod mod,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (mod.FormTranslations is null) return result;
        foreach (var (language, asset) in mod.FormTranslations)
        {
            var bytes = await DownloadFormAssetAsync(asset.Url, asset.Sha256, cancellationToken);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                json.RootElement.EnumerateObject().Any(entry => entry.Name.Length is < 1 or > 128 ||
                    entry.Value.ValueKind != JsonValueKind.String ||
                    entry.Value.GetString() is not { Length: > 0 and <= 256 }))
                throw new InvalidDataException("Invalid mod form translation: " + language);
            result.Add(language, bytes);
        }
        return result;
    }

    private static async Task<byte[]> DownloadFormAssetAsync(string url, string sha256, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" } ||
            response.Content.Headers.ContentLength is > 65536)
            throw new InvalidDataException("Mod form override must be a small HTTPS JSON file.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 65536) throw new InvalidDataException("Mod form override is too large.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Mod form override checksum does not match the catalog.");
        return bytes;
    }

    public static InstalledModMetadata? InstalledMetadata(ServerLocation location, string modId)
    {
        var file = Path.Combine(location.ModsRoot, modId, ".serverapp-install.json");
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 16384) return null;
            using var stream = File.OpenRead(file);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var identity) &&
                !string.Equals(identity.GetString(), modId, StringComparison.OrdinalIgnoreCase))
                return null;
            string Read(string key) => root.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
            int? legacyMaximum = null;
            if (root.TryGetProperty("serverConfiguration", out var previous) &&
                previous.ValueKind == JsonValueKind.Object &&
                previous.TryGetProperty("maxPlayersMaximum", out var limit) &&
                limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var oldMaximum) &&
                oldMaximum is > 16 and <= 32)
                legacyMaximum = oldMaximum;
            return new InstalledModMetadata(modId, Read("version"), Read("name"), Read("author"),
                Read("description"), Read("catalog"), Read("documentationUrl"), Read("discordUrl"),
                Read("homepageUrl"), legacyMaximum);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   KeyNotFoundException or InvalidOperationException)
        { return null; }
    }

    public static string InstalledVersion(ServerLocation location, string modId) =>
        InstalledMetadata(location, modId)?.Version ?? "";

    public static void Install(ServerLocation location, MarketplaceMod mod, string archiveFile, byte[]? logoBytes = null,
        byte[]? formOverrideBytes = null, IReadOnlyDictionary<string, byte[]>? formTranslations = null)
    {
        using (var running = ServerProcess.Find(location))
            if (running is not null) throw new InvalidOperationException("Stop the server before installing or updating a mod.");
        if (!File.Exists(Path.Combine(location.BriefcaseRoot, "Core", "Briefcase.NativeHost.dll")))
            throw new InvalidOperationException("Install Briefcase before installing mods.");
        var modsRoot = location.ModsRoot;
        var target = Path.Combine(modsRoot, mod.Id);
        var dataRoot = location.IsInstance ? location.InstanceRoot : location.BriefcaseRoot;
        var updates = Path.Combine(dataRoot, "Updates");
        foreach (var path in new[] { dataRoot, location.Ue4ssRoot,
                     modsRoot, updates, target })
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The installation path contains a directory link.");
        var stage = Path.Combine(updates, "ServerAppMod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var newFolder = Path.Combine(stage, "new");
        var oldFolder = Path.Combine(stage, "previous");
        var movedOld = false;
        var installedNew = false;
        var keepBackup = false;
        try
        {
            using var archive = ZipFile.OpenRead(archiveFile);
            var prefix = $"ue4ss/Mods/{mod.Id}/";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var bytes = 0L;
            if (archive.Entries.Count is < 1 or > 1024) throw new InvalidDataException("Mod package file count is invalid.");
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!seen.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Mod archive contains a duplicate or linked path.");
                if (name.EndsWith('/'))
                {
                    if (entry.Length != 0 ||
                        name is not ("ue4ss/" or "ue4ss/Mods/") &&
                        name != prefix && !name.StartsWith(prefix, StringComparison.Ordinal))
                        throw new InvalidDataException("Mod archive contains an unsafe directory.");
                    continue;
                }
                if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                    entry.Length > 100_000_000 ||
                    name[prefix.Length..].Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                                                        part.Contains(':') || part.Contains('\\')))
                    throw new InvalidDataException("Mod archive contains an unsafe path.");
                bytes += entry.Length;
                if (bytes > MaximumArchive) throw new InvalidDataException("Mod archive is too large.");
                var relative = name[prefix.Length..];
                var output = Path.GetFullPath(Path.Combine(newFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(Path.GetFullPath(newFolder) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mod archive path escapes its directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                entry.ExtractToFile(output);
            }
            if (!File.Exists(Path.Combine(newFolder, "dlls", "main.dll")))
                throw new InvalidDataException("The package is missing its UE4SS main.dll.");
            if (logoBytes is not null) File.WriteAllBytes(Path.Combine(newFolder, "logo.png"), logoBytes);
            if (formOverrideBytes is not null)
            {
                FormSchema.LoadBase().ApplyOverride(formOverrideBytes);
                var formDirectory = Path.Combine(newFolder, "Data");
                Directory.CreateDirectory(formDirectory);
                File.WriteAllBytes(Path.Combine(formDirectory, "server-configuration.override.json"), formOverrideBytes);
            }
            if (formTranslations is not null && formTranslations.Count > 0)
            {
                var languageDirectory = Path.Combine(newFolder, "Data", "FormTranslations");
                Directory.CreateDirectory(languageDirectory);
                foreach (var (language, translationBytes) in formTranslations)
                {
                    if (!Regex.IsMatch(language, "^[a-z]{2}(?:-[A-Za-z0-9]{2,8})?$"))
                        throw new InvalidDataException("Invalid form translation language.");
                    File.WriteAllBytes(Path.Combine(languageDirectory, language + ".json"), translationBytes);
                }
            }
            using (var stream = File.Create(Path.Combine(newFolder, ".serverapp-install.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("id", mod.Id);
                writer.WriteString("version", mod.Version);
                writer.WriteString("name", mod.Name);
                writer.WriteString("author", mod.Author);
                writer.WriteString("description", mod.Description);
                writer.WriteString("catalog", mod.Catalog);
                writer.WriteString("downloadUrl", mod.DownloadUrl.AbsoluteUri);
                writer.WriteString("sha256", mod.Sha256);
                if (mod.LogoUrl is not null) writer.WriteString("logoUrl", mod.LogoUrl);
                if (mod.LogoSha256 is not null) writer.WriteString("logoSha256", mod.LogoSha256);
                if (mod.DocumentationUrl is not null) writer.WriteString("documentationUrl", mod.DocumentationUrl);
                if (mod.DiscordUrl is not null) writer.WriteString("discordUrl", mod.DiscordUrl);
                if (mod.HomepageUrl is not null) writer.WriteString("homepageUrl", mod.HomepageUrl);
                if (mod.FormSchemaOverrideUrl is not null) writer.WriteString("formSchemaOverrideUrl", mod.FormSchemaOverrideUrl);
                if (mod.FormSchemaOverrideSha256 is not null) writer.WriteString("formSchemaOverrideSha256", mod.FormSchemaOverrideSha256);
                writer.WriteEndObject();
            }
            var previousConfig = Path.Combine(target, "Data", "config.json");
            if (File.Exists(previousConfig))
            {
                var dataFolder = Path.GetDirectoryName(previousConfig)!;
                if ((File.GetAttributes(dataFolder) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(previousConfig) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Existing mod configuration is a link.");
                var newConfig = Path.Combine(newFolder, "Data", "config.json");
                Directory.CreateDirectory(Path.GetDirectoryName(newConfig)!);
                File.Copy(previousConfig, newConfig, true);
            }
            Directory.CreateDirectory(modsRoot);
            if (Directory.Exists(target)) { Directory.Move(target, oldFolder); movedOld = true; }
            Directory.Move(newFolder, target);
            installedNew = true;
            ModCatalog.SetEnabled(location, mod.Id, true);
        }
        catch (Exception failure)
        {
            try
            {
                if (installedNew && Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (movedOld && Directory.Exists(oldFolder)) Directory.Move(oldFolder, target);
            }
            catch (Exception rollback)
            {
                keepBackup = true;
                throw new IOException($"Mod installation and rollback failed. Previous files remain at {oldFolder}.",
                    new AggregateException(failure, rollback));
            }
            throw;
        }
        finally { if (!keepBackup && Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

}
