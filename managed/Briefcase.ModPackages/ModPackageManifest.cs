using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Briefcase.ModPackages;

/// <summary>
/// The file stored at Briefcase/Mods/&lt;mod-id&gt;/briefcase.mod.json.
/// ModInfo remains the runtime authority; this manifest describes the package
/// before its assembly is loaded and provides its optional release source.
/// </summary>
public sealed record ModPackageManifest
{
    public const string FileName = "briefcase.mod.json";
    public const int CurrentSchemaVersion = 1;
    public const int MaximumBytes = 64 * 1024;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string EntryAssembly { get; init; } = "";
    public string? Id { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public GitHubModSource? Updates { get; init; }
}

public sealed record GitHubModSource
{
    /// <summary>GitHub owner and repository, for example Briefcase/ExampleMod.</summary>
    public string Repository { get; init; } = "";

    /// <summary>
    /// Release asset file name. The optional {version} token is replaced by the
    /// normalized release version without a leading v.
    /// </summary>
    public string Asset { get; init; } = "";

    public bool Automatic { get; init; } = true;

    public string ResolveAssetName(string version)
    {
        var name = Asset.Replace("{version}", version, StringComparison.Ordinal);
        ModPackageManifestFile.ValidateAssetName(name, nameof(Asset));
        return name;
    }
}

public sealed record ModPackageDescriptor(
    string DirectoryPath,
    string ManifestPath,
    string EntryAssemblyPath,
    ModPackageManifest Manifest);

public static partial class ModPackageManifestFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{1,126}[A-Za-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdPattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,98}[A-Za-z0-9])?/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    public static ModPackageManifest Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The mod manifest is missing.", path);
        if (info.Length is <= 0 or > ModPackageManifest.MaximumBytes)
            throw new InvalidDataException("The mod manifest size is invalid.");
        var manifest = JsonSerializer.Deserialize<ModPackageManifest>(
                           File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException("The mod manifest is empty.");
        Validate(manifest);
        return manifest;
    }

    public static void Write(string path, ModPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var text = JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine;
        if (text.Length > ModPackageManifest.MaximumBytes)
            throw new InvalidDataException("The mod manifest is too large.");
        File.WriteAllText(path, text);
    }

    public static void WriteMinimal(string path, string entryAssembly) =>
        Write(path, new ModPackageManifest { EntryAssembly = entryAssembly });

    public static void Validate(ModPackageManifest manifest)
    {
        if (manifest.SchemaVersion != ModPackageManifest.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported mod manifest schema {manifest.SchemaVersion}.");
        ValidateFileName(manifest.EntryAssembly, nameof(manifest.EntryAssembly), ".dll");
        if (manifest.Id is not null && !ModIdPattern().IsMatch(manifest.Id))
            throw new InvalidDataException("The mod manifest id is invalid.");
        if (manifest.Version is not null &&
            !PackageVersion.TryParse(manifest.Version, out _))
            throw new InvalidDataException("The mod manifest version is invalid.");
        if (manifest.Dependencies is null ||
            manifest.Dependencies.Count > 64 ||
            manifest.Dependencies.Any(id => !ModIdPattern().IsMatch(id)) ||
            manifest.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            manifest.Dependencies.Count)
            throw new InvalidDataException("The mod manifest dependencies are invalid.");
        if (manifest.Id is not null && manifest.Dependencies.Contains(
                manifest.Id, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("A mod package cannot depend on itself.");

        if (manifest.Updates is null) return;
        if (manifest.Id is null || manifest.Version is null)
            throw new InvalidDataException(
                "A mod with an update source must declare id and version.");
        if (!RepositoryPattern().IsMatch(manifest.Updates.Repository) ||
            manifest.Updates.Repository.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("The GitHub repository name is invalid.");
        ValidateAssetName(manifest.Updates.Asset, nameof(manifest.Updates.Asset), allowToken: true);
    }

    internal static void ValidateAssetName(
        string name,
        string parameterName,
        bool allowToken = false)
    {
        var resolved = allowToken
            ? name.Replace("{version}", "1.0.0", StringComparison.Ordinal)
            : name;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
            !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            !name.Equals(Path.GetFileName(name), StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            resolved.Contains('{', StringComparison.Ordinal) ||
            resolved.Contains('}', StringComparison.Ordinal))
            throw new InvalidDataException($"{parameterName} is not a valid ZIP asset name.");
    }

    private static void ValidateFileName(string name, string parameterName, string extension)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
            !name.Equals(Path.GetFileName(name), StringComparison.Ordinal) ||
            !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"{parameterName} is not a valid file name.");
    }
}
