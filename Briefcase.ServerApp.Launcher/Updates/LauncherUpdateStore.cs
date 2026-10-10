using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp.Launcher;

/// <summary>Stages the launcher from the verified release archive without touching the running executable.</summary>
public static class LauncherUpdateStore
{
    private static readonly string[] Files =
        ["Briefcase.exe", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "av_libglesv2.dll"];

    public static bool IsPending(string root, string version)
    {
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) return false;
        var stage = Path.Combine(root, "App", "launcher-updates", version);
        if (!Directory.Exists(stage)) return false;
        var manifestPath = Path.Combine(stage, "manifest.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length is <= 0 or > 4096)
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var manifest = document.RootElement;
            if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
                manifest.GetProperty("version").GetString() != version)
                return false;
            var records = manifest.GetProperty("files").EnumerateArray().ToArray();
            if (records.Length != Files.Length ||
                !records.Select(record => record.GetProperty("path").GetString()).Order(StringComparer.Ordinal)
                    .SequenceEqual(Files.Order(StringComparer.Ordinal)))
                return false;
            VerifyFiles(stage, records);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      InvalidDataException or JsonException or ArgumentException)
        { return false; }
    }

    public static string Stage(string root, string archivePath, string version)
    {
        if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
            throw new InvalidDataException("Invalid launcher version.");
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = UniqueEntry(archive, "Launcher/manifest.json");
        if (manifestEntry.Length is <= 0 or > 4096)
            throw new InvalidDataException("Invalid launcher manifest size.");
        using var input = manifestEntry.Open();
        using var document = JsonDocument.Parse(input);
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 1 ||
            manifest.GetProperty("version").GetString() != version)
            throw new InvalidDataException("Unexpected launcher manifest version.");
        var records = manifest.GetProperty("files").EnumerateArray().ToArray();
        if (records.Length != Files.Length ||
            !records.Select(record => record.GetProperty("path").GetString()).Order(StringComparer.Ordinal)
                .SequenceEqual(Files.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Unexpected launcher file inventory.");

        var stagingRoot = Path.Combine(root, "App", "launcher-updates");
        Directory.CreateDirectory(stagingRoot);
        var destination = Path.Combine(stagingRoot, version);
        if (Directory.Exists(destination))
        {
            VerifyFiles(destination, records);
            return destination;
        }
        var temporary = Path.Combine(stagingRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var record in records)
            {
                var name = record.GetProperty("path").GetString()!;
                var entry = UniqueEntry(archive, name);
                var size = record.GetProperty("bytes").GetInt64();
                var hash = record.GetProperty("sha256").GetString() ?? "";
                if (size is <= 0 or > 512L * 1024 * 1024 || entry.Length != size ||
                    !Regex.IsMatch(hash, "^[a-f0-9]{64}$"))
                    throw new InvalidDataException("Invalid launcher file record: " + name);
                using var source = entry.Open();
                using var target = File.Create(Path.Combine(temporary, name));
                source.CopyTo(target);
            }
            File.WriteAllBytes(Path.Combine(temporary, "manifest.json"), ReadEntry(manifestEntry));
            VerifyFiles(temporary, records);
            Directory.Move(temporary, destination);
            return destination;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static ZipArchiveEntry UniqueEntry(ZipArchive archive, string name)
    {
        var entries = archive.Entries.Where(entry => entry.FullName == name).Take(2).ToArray();
        if (entries.Length != 1) throw new InvalidDataException("Missing or duplicated launcher file: " + name);
        return entries[0];
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static void VerifyFiles(string directory, JsonElement[] records)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Launcher staging directory is a link.");
        foreach (var record in records)
        {
            var name = record.GetProperty("path").GetString()!;
            var file = Path.Combine(directory, name);
            if (!File.Exists(file) || File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) ||
                new FileInfo(file).Length != record.GetProperty("bytes").GetInt64())
                throw new InvalidDataException("Incomplete staged launcher: " + name);
            using var stream = File.OpenRead(file);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(
                    record.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Staged launcher checksum mismatch: " + name);
        }
    }
}
