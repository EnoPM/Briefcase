using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp.Launcher;

public sealed record InstalledApp(string Version, string Directory)
{
    public string Executable => Path.Combine(Directory, "Briefcase.ServerApp.exe");
}

/// <summary>Installs immutable app versions; the active pointer changes only after startup succeeds.</summary>
public static class AppUpdateStore
{
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private const long MaximumFileSize = 512L * 1024 * 1024;
    private const long MaximumTotalSize = 1024L * 1024 * 1024;

    public static InstalledApp? Current(string root)
    {
        var pointer = Path.Combine(root, "App", "current.json");
        if (File.Exists(pointer))
        {
            if (new FileInfo(pointer).Length is <= 0 or > 4096)
                throw new InvalidDataException("Invalid application version pointer.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(pointer));
            var version = document.RootElement.GetProperty("version").GetString() ?? "";
            if (!VersionPattern.IsMatch(version))
                throw new InvalidDataException("Invalid installed application version.");
            var installed = new InstalledApp(version, Path.Combine(root, "App", "versions", version));
            if (File.Exists(installed.Executable)) return installed;
            throw new FileNotFoundException("The selected application version is missing.", installed.Executable);
        }
        var legacy = Path.Combine(root, "Briefcase.ServerApp.exe");
        return File.Exists(legacy) ? new InstalledApp("0.0.0", root) : null;
    }

    public static InstalledApp Prepare(string root, string archivePath, string version)
    {
        if (!VersionPattern.IsMatch(version)) throw new InvalidDataException("Invalid application release version.");
        using var archive = ZipFile.OpenRead(archivePath);
        var prefix = $"App/versions/{version}/";
        var manifestEntry = archive.GetEntry(prefix + "manifest.json") ??
                            throw new InvalidDataException("The release has no application manifest.");
        if (manifestEntry.Length is <= 0 or > 2_000_000)
            throw new InvalidDataException("Invalid application manifest size.");
        using var manifestStream = manifestEntry.Open();
        using var document = JsonDocument.Parse(manifestStream);
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.GetProperty("version").GetString() != version)
            throw new InvalidDataException("The application release manifest is incompatible.");
        var records = new Dictionary<string, (long Size, string Hash)>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.GetProperty("files").EnumerateArray())
        {
            if (records.Count >= 500) throw new InvalidDataException("Too many application release files.");
            var name = item.GetProperty("path").GetString() ?? "";
            var size = item.GetProperty("bytes").GetInt64();
            var hash = item.GetProperty("sha256").GetString() ?? "";
            if (!SafeName(name) || name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
                size is < 0 or > MaximumFileSize || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") ||
                !records.TryAdd(name, (size, hash)))
                throw new InvalidDataException("Unsafe application release inventory.");
            total += size;
            if (total > MaximumTotalSize) throw new InvalidDataException("Application release is too large.");
        }
        if (!records.ContainsKey("Briefcase.ServerApp.exe") ||
            !records.ContainsKey("Native/Briefcase.ServerInjector.exe") ||
            !records.ContainsKey("Native/Briefcase.ServerBootstrap.dll"))
            throw new InvalidDataException("The application release is incomplete.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count > 520) throw new InvalidDataException("Too many release archive entries.");
        foreach (var entry in archive.Entries)
        {
            if (!SafeName(entry.FullName) || !seen.Add(entry.FullName) ||
                (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000))
                throw new InvalidDataException("Unsafe release archive entry.");
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = entry.FullName[prefix.Length..];
            if (relative != "manifest.json" && !records.ContainsKey(relative))
                throw new InvalidDataException("Unexpected application release file.");
        }
        if (records.Keys.Any(name => !seen.Contains(prefix + name)) ||
            seen.Count(name => name.StartsWith(prefix, StringComparison.Ordinal)) != records.Count + 1)
            throw new InvalidDataException("Application release files do not match its manifest.");
        var versions = Path.Combine(root, "App", "versions");
        Directory.CreateDirectory(versions);
        var destination = Path.Combine(versions, version);
        var stage = Path.Combine(versions, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var (name, expected) in records)
            {
                var entry = archive.GetEntry(prefix + name)!;
                if (entry.Length != expected.Size) throw new InvalidDataException("Application file size mismatch.");
                var file = Child(stage, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                using var input = entry.Open();
                using var output = File.Create(file);
                using var sha = SHA256.Create();
                var buffer = new byte[65536];
                long copied = 0;
                int read;
                while ((read = input.Read(buffer)) > 0)
                {
                    copied += read;
                    if (copied > expected.Size) throw new InvalidDataException("Application file exceeds its declared size.");
                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                }
                sha.TransformFinalBlock([], 0, 0);
                if (copied != expected.Size ||
                    !Convert.ToHexString(sha.Hash!).Equals(expected.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Application file checksum mismatch: " + name);
            }
            if (!Directory.Exists(destination)) Directory.Move(stage, destination);
            else if (!ManifestMatches(destination, records))
                throw new InvalidDataException("An installed application version differs from its release.");
            return new InstalledApp(version, destination);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    public static void Activate(string root, InstalledApp app)
    {
        var versions = Path.GetFullPath(Path.Combine(root, "App", "versions")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(app.Directory).StartsWith(versions, StringComparison.OrdinalIgnoreCase) ||
            !VersionPattern.IsMatch(app.Version) || !File.Exists(app.Executable))
            throw new InvalidDataException("Cannot activate an application outside this installation.");
        var folder = Path.Combine(root, "App");
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, "{\"version\":\"" + app.Version + "\"}\n");
            File.Move(temporary, Path.Combine(folder, "current.json"), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool ManifestMatches(string directory, Dictionary<string, (long Size, string Hash)> records)
    {
        foreach (var (name, expected) in records)
        {
            var file = Child(directory, name);
            if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                new FileInfo(file).Length != expected.Size) return false;
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream))
                    .Equals(expected.Hash, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool SafeName(string name) => name.Length is > 0 and <= 240 &&
        !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':') &&
        name.Split('/').All(part => part.Length > 0 && part != "." && part != ".." &&
            part.All(ch => !char.IsControl(ch)));

    private static string Child(string root, string name)
    {
        var file = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!file.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Application file escapes its version directory.");
        return file;
    }
}
