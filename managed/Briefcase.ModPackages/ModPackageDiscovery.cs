namespace Briefcase.ModPackages;

public static class ModPackageDiscovery
{
    private static readonly string[] LegacySidecarSuffixes =
        [".pdb", ".deps.json", ".runtimeconfig.json"];

    /// <summary>
    /// Moves old flat DLL installations into one directory per mod. A minimal
    /// manifest is created because ModInfo remains available from the assembly.
    /// </summary>
    public static int MigrateLegacyMods(string modsDirectory, Action<string>? log = null)
    {
        Directory.CreateDirectory(modsDirectory);
        var migrated = 0;
        foreach (var assembly in Directory.EnumerateFiles(
                     modsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var stem = Path.GetFileNameWithoutExtension(assembly);
            var folderName = SanitizeFolderName(stem);
            var destinationDirectory = ResolveLegacyDestination(
                modsDirectory, folderName, Path.GetFileName(assembly));
            Directory.CreateDirectory(destinationDirectory);
            var destinationAssembly = Path.Combine(
                destinationDirectory, Path.GetFileName(assembly));
            File.Move(assembly, destinationAssembly, overwrite: true);
            foreach (var suffix in LegacySidecarSuffixes)
            {
                var sidecar = Path.Combine(modsDirectory, stem + suffix);
                if (File.Exists(sidecar))
                    File.Move(sidecar, Path.Combine(destinationDirectory, stem + suffix),
                        overwrite: true);
            }
            var manifestPath = Path.Combine(
                destinationDirectory, ModPackageManifest.FileName);
            if (!File.Exists(manifestPath))
                ModPackageManifestFile.WriteMinimal(
                    manifestPath, Path.GetFileName(destinationAssembly));
            log?.Invoke($"Migrated legacy mod {Path.GetFileName(assembly)} to {folderName}.");
            migrated++;
        }
        return migrated;
    }

    public static IReadOnlyList<ModPackageDescriptor> Discover(
        string modsDirectory,
        Action<string>? warning = null)
    {
        Directory.CreateDirectory(modsDirectory);
        var result = new List<ModPackageDescriptor>();
        foreach (var directory in Directory.EnumerateDirectories(modsDirectory)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var manifestPath = Path.Combine(directory, ModPackageManifest.FileName);
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = ModPackageManifestFile.Read(manifestPath);
                var entryAssembly = Path.GetFullPath(Path.Combine(
                    directory, manifest.EntryAssembly));
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) +
                           Path.DirectorySeparatorChar;
                if (!entryAssembly.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(entryAssembly))
                    throw new InvalidDataException(
                        $"The entry assembly '{manifest.EntryAssembly}' is missing.");
                result.Add(new ModPackageDescriptor(
                    Path.GetFullPath(directory),
                    Path.GetFullPath(manifestPath),
                    entryAssembly,
                    manifest));
            }
            catch (Exception exception)
            {
                warning?.Invoke(
                    $"Could not read mod package '{Path.GetFileName(directory)}': {exception.Message}");
            }
        }
        return result;
    }

    private static string ResolveLegacyDestination(
        string modsDirectory,
        string folderName,
        string assemblyFileName)
    {
        var preferred = Path.Combine(modsDirectory, folderName);
        if (!Directory.Exists(preferred)) return preferred;

        var manifestPath = Path.Combine(preferred, ModPackageManifest.FileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = ModPackageManifestFile.Read(manifestPath);
                if (string.Equals(manifest.EntryAssembly, assemblyFileName,
                        StringComparison.OrdinalIgnoreCase))
                    return preferred;
            }
            catch
            {
                // Preserve an unreadable package directory instead of placing
                // more files into it. Discovery will report its own warning.
            }
        }
        else if (!Directory.EnumerateFileSystemEntries(preferred).Any())
        {
            return preferred;
        }

        for (var suffix = 1; suffix <= 1000; suffix++)
        {
            var candidate = Path.Combine(modsDirectory, $"{folderName}-legacy-{suffix}");
            if (!Directory.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not allocate a package directory for '{assemblyFileName}'.");
    }

    private static string SanitizeFolderName(string value)
    {
        var characters = value.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_'
                    ? character
                    : '_')
            .ToArray();
        var result = new string(characters).Trim('.', ' ');
        return string.IsNullOrWhiteSpace(result) ? "mod" : result;
    }
}
