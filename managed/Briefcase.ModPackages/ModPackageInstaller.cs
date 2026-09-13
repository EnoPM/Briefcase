using System.IO.Compression;

namespace Briefcase.ModPackages;

/// <summary>Validates and atomically installs a downloaded mod package.</summary>
public static class ModPackageInstaller
{
    private const int MaximumEntries = 512;
    private const long MaximumExpandedBytes = 256L * 1024 * 1024;

    public static ModPackageDescriptor Install(
        PreparedModPackage prepared,
        string modsDirectory,
        string? existingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Directory.CreateDirectory(modsDirectory);
        var extraction = Path.Combine(prepared.StagingDirectory, "extracted");
        Directory.CreateDirectory(extraction);
        try
        {
            ExtractValidated(prepared.ArchivePath, extraction);
            var manifestPath = Path.Combine(extraction, ModPackageManifest.FileName);
            var manifest = ModPackageManifestFile.Read(manifestPath);
            ValidateIdentity(prepared, manifest);
            var entryAssembly = Path.Combine(extraction, manifest.EntryAssembly);
            if (!File.Exists(entryAssembly))
                throw new InvalidDataException(
                    $"The mod package entry assembly '{manifest.EntryAssembly}' is missing.");

            var destination = existingDirectory is null
                ? Path.Combine(modsDirectory, prepared.ExpectedId)
                : Path.GetFullPath(existingDirectory);
            EnsureDirectChild(modsDirectory, destination);
            if (existingDirectory is null && Directory.Exists(destination))
                throw new InvalidOperationException(
                    $"A mod directory named '{prepared.ExpectedId}' already exists.");
            PreserveDataDirectory(destination, extraction);
            ReplaceDirectory(extraction, destination);
            return new ModPackageDescriptor(
                destination,
                Path.Combine(destination, ModPackageManifest.FileName),
                Path.Combine(destination, manifest.EntryAssembly),
                manifest);
        }
        finally
        {
            GitHubModReleaseClient.TryDeleteDirectory(prepared.StagingDirectory);
        }
    }

    private static void ExtractValidated(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is <= 0 or > MaximumEntries)
            throw new InvalidDataException("The mod archive entry count is invalid.");
        long expanded = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') ||
                name.Contains(':') || name.Split('/').Any(segment => segment == ".."))
                throw new InvalidDataException("The mod archive contains an unsafe path.");
            if (!names.Add(name))
                throw new InvalidDataException("The mod archive contains duplicate paths.");
            if (name.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The Data directory is reserved for persistent mod data.");
            expanded = checked(expanded + entry.Length);
            if (expanded > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded mod archive is too large.");
            var output = Path.GetFullPath(Path.Combine(destination, name));
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) +
                       Path.DirectorySeparatorChar;
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The mod archive escapes its destination.");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(output);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var source = entry.Open();
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            source.CopyTo(target);
        }
        if (!File.Exists(Path.Combine(destination, ModPackageManifest.FileName)))
            throw new InvalidDataException(
                $"The mod archive must contain {ModPackageManifest.FileName} at its root.");
    }

    private static void ValidateIdentity(
        PreparedModPackage prepared,
        ModPackageManifest manifest)
    {
        if (!string.Equals(manifest.Id, prepared.ExpectedId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The package declares mod '{manifest.Id}' instead of '{prepared.ExpectedId}'.");
        if (!PackageVersion.TryParse(manifest.Version, out var packageVersion) ||
            packageVersion.CompareTo(prepared.Version) != 0)
            throw new InvalidDataException(
                "The package manifest version does not match its GitHub release.");
        if (manifest.Updates is null ||
            !string.Equals(manifest.Updates.Repository, prepared.Source.Repository,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.Updates.Asset, prepared.Source.Asset,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The package update source does not match the selected marketplace release.");
        if (prepared.ExpectedDependencies is not null &&
            !manifest.Dependencies.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(prepared.ExpectedDependencies))
            throw new InvalidDataException(
                "The package dependencies do not match the selected marketplace entry.");
    }

    private static void PreserveDataDirectory(string existing, string replacement)
    {
        var source = Path.Combine(existing, "Data");
        if (!Directory.Exists(source)) return;
        var destination = Path.Combine(replacement, "Data");
        CopyDirectory(source, destination);
    }

    private static void ReplaceDirectory(string replacement, string destination)
    {
        var backup = destination + $".briefcase-backup-{Guid.NewGuid():N}";
        var hadExisting = Directory.Exists(destination);
        if (hadExisting) Directory.Move(destination, backup);
        try
        {
            Directory.Move(replacement, destination);
            if (hadExisting) Directory.Delete(backup, recursive: true);
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            if (hadExisting && Directory.Exists(backup)) Directory.Move(backup, destination);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(
                destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var output = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output, overwrite: true);
        }
    }

    private static void EnsureDirectChild(string rootDirectory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) +
                   Path.DirectorySeparatorChar;
        var normalized = Path.GetFullPath(path);
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(normalized),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "A mod package must be installed directly inside Briefcase/Mods.");
    }
}
