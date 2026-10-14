using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

internal static class LinuxModPackageInstaller
{
    private const long MaximumPackageBytes = 256L * 1024 * 1024;

    internal static void Install(ServerLocation location, MarketplaceMod mod, string archiveFile,
        byte[]? logoBytes, byte[]? formOverrideBytes,
        IReadOnlyDictionary<string, byte[]>? formTranslations, byte[]? configSchemaBytes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (!mod.PlatformAvailable || mod.CatalogSchema != 3)
            throw new InvalidOperationException("The catalog does not provide a Linux package for this mod.");
        using (var running = ServerProcess.Find(location))
            if (running is not null) throw new InvalidOperationException("Stop the server before changing a mod.");
        if (!ModMarketplace.BriefcaseRuntimeInstalled(location))
            throw new InvalidOperationException("Install Briefcase before installing mods.");

        var root = location.ModsRoot;
        var target = Path.Combine(root, mod.InstallId);
        var updates = Path.Combine(location.InstanceRoot, "Updates");
        foreach (var path in new[] { location.InstanceRoot, location.BriefcaseRoot, root, updates, target })
            if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The mod installation path contains a directory link.");
        var stage = Path.Combine(updates, "ServerAppMod-" + Guid.NewGuid().ToString("N"));
        var fresh = Path.Combine(stage, "new");
        var previous = Path.Combine(stage, "previous");
        Directory.CreateDirectory(fresh);
        var movedOld = false;
        var movedNew = false;
        var keepBackup = false;
        try
        {
            ExtractVerified(archiveFile, mod, fresh);
            if (logoBytes is not null)
            {
                if (mod.LogoSha256 is not { Length: 64 } logoHash || !HashMatches(logoBytes, logoHash))
                    throw new InvalidDataException("The mod logo checksum does not match the catalog.");
                File.WriteAllBytes(Path.Combine(fresh, "logo.png"), logoBytes);
            }
            if (formOverrideBytes is not null)
            {
                FormSchema.LoadBase().ApplyOverride(formOverrideBytes);
                if (mod.FormSchemaOverrideSha256 is not { Length: 64 } formHash ||
                    !HashMatches(formOverrideBytes, formHash))
                    throw new InvalidDataException("The mod form checksum does not match the catalog.");
                WriteDataFile(fresh, "server-configuration.override.json", formOverrideBytes);
            }
            if (configSchemaBytes is not null)
            {
                ModConfigEditor.ValidateSchema(configSchemaBytes);
                if (mod.ConfigSchemaSha256 is not { Length: 64 } schemaHash ||
                    !HashMatches(configSchemaBytes, schemaHash))
                    throw new InvalidDataException("The mod configuration schema checksum does not match the catalog.");
                WriteDataFile(fresh, "config.schema.json", configSchemaBytes);
            }
            if (formTranslations is not null)
                foreach (var (language, bytes) in formTranslations)
                {
                    if (!Regex.IsMatch(language, "^[a-z]{2}(?:-[A-Za-z0-9]{2,8})?$"))
                        throw new InvalidDataException("Invalid form translation language.");
                    using var json = JsonDocument.Parse(bytes);
                    if (json.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Invalid form translation.");
                    if (mod.FormTranslations is null ||
                        !mod.FormTranslations.TryGetValue(language, out var asset) ||
                        !HashMatches(bytes, asset.Sha256))
                        throw new InvalidDataException("The mod translation checksum does not match the catalog.");
                    WriteDataFile(fresh, Path.Combine("FormTranslations", language + ".json"), bytes);
                }
            var oldConfig = Path.Combine(target, "Data", "config.json");
            if (File.Exists(oldConfig))
            {
                if (File.GetAttributes(oldConfig).HasFlag(FileAttributes.ReparsePoint) ||
                    File.GetAttributes(Path.GetDirectoryName(oldConfig)!).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The existing mod configuration is a link.");
                var newConfig = Path.Combine(fresh, "Data", "config.json");
                Directory.CreateDirectory(Path.GetDirectoryName(newConfig)!);
                File.Copy(oldConfig, newConfig, true);
            }
            WriteInstallMetadata(fresh, mod);
            Directory.CreateDirectory(root);
            if (Directory.Exists(target)) { Directory.Move(target, previous); movedOld = true; }
            Directory.Move(fresh, target);
            movedNew = true;
            ModCatalog.SetEnabled(location, mod.InstallId, true);
        }
        catch (Exception failure)
        {
            try
            {
                if (movedNew && Directory.Exists(target)) Directory.Delete(target, recursive: true);
                if (movedOld && Directory.Exists(previous)) Directory.Move(previous, target);
            }
            catch (Exception rollback)
            {
                keepBackup = true;
                throw new IOException($"Mod installation and rollback failed. Previous files remain at {previous}.",
                    new AggregateException(failure, rollback));
            }
            throw;
        }
        finally { if (!keepBackup && Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    private static void WriteDataFile(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, "Data", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void ExtractVerified(string archiveFile, MarketplaceMod mod, string outputRoot)
    {
        using var archive = ZipFile.OpenRead(archiveFile);
        if (archive.Entries.Count is < 3 or > 1024)
            throw new InvalidDataException("The Linux mod package file count is invalid.");
        var manifestEntry = archive.GetEntry("ModPackage.json") ??
            throw new InvalidDataException("The Linux mod package manifest is missing.");
        if (manifestEntry.Length is <= 0 or > 262144)
            throw new InvalidDataException("The Linux mod package manifest is invalid.");
        using var manifestStream = manifestEntry.Open();
        using var document = JsonDocument.Parse(manifestStream);
        var manifest = document.RootElement;
        if (manifest.GetProperty("updateSchema").GetInt32() != 1 ||
            manifest.GetProperty("kind").GetString() != "briefcase-mod" ||
            manifest.GetProperty("platform").GetString() != "linux-x64" ||
            manifest.GetProperty("modId").GetString() != mod.InstallId ||
            manifest.GetProperty("version").GetString() != mod.Version)
            throw new InvalidDataException("The Linux mod package does not match the catalog.");
        var prefix = $"Briefcase/Mods/{mod.InstallId}/";
        var inventory = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
        long total = 0;
        foreach (var row in manifest.GetProperty("files").EnumerateArray())
        {
            var path = row.GetProperty("path").GetString() ?? "";
            var size = row.GetProperty("bytes").GetInt64();
            var hash = row.GetProperty("sha256").GetString() ?? "";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) ||
                !SafeRelative(path[prefix.Length..]) || size is <= 0 or > MaximumPackageBytes ||
                !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") ||
                !inventory.TryAdd(path, (size, hash)))
                throw new InvalidDataException("The Linux mod package inventory is invalid.");
            total += size;
            if (total > MaximumPackageBytes) throw new InvalidDataException("The Linux mod package is too large.");
        }
        if (inventory.Count is < 2 or > 1023 || archive.Entries.Count != inventory.Count + 1)
            throw new InvalidDataException("The Linux mod package inventory is incomplete.");
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == "ModPackage.json") continue;
            if (!inventory.Remove(entry.FullName, out var expected) ||
                entry.Length != expected.Size || entry.FullName.EndsWith('/') ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("The Linux mod package contains an unexpected file.");
            var relative = entry.FullName[prefix.Length..];
            var output = Path.Combine(outputRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var source = entry.Open();
            using var target = File.Create(output);
            using var sha = SHA256.Create();
            var buffer = new byte[65536];
            long copied = 0;
            int count;
            while ((count = source.Read(buffer)) > 0)
            {
                copied += count;
                if (copied > expected.Size) throw new InvalidDataException("The Linux mod package file is too large.");
                target.Write(buffer, 0, count);
                sha.TransformBlock(buffer, 0, count, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            if (copied != expected.Size ||
                !Convert.ToHexString(sha.Hash!).Equals(expected.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Linux mod package checksum does not match its inventory.");
        }
        if (inventory.Count != 0) throw new InvalidDataException("The Linux mod package is incomplete.");
        var modManifestFile = Path.Combine(outputRoot, "briefcase.mod.json");
        if (!File.Exists(modManifestFile)) throw new InvalidDataException("The Linux mod manifest is missing.");
        using var modDocument = JsonDocument.Parse(File.ReadAllBytes(modManifestFile));
        var modManifest = modDocument.RootElement;
        var entryName = modManifest.GetProperty("entry").GetString() ?? "";
        if (modManifest.GetProperty("id").GetString() != mod.InstallId ||
            modManifest.GetProperty("version").GetString() != mod.Version ||
            !SafeRelative(entryName) || !entryName.EndsWith(".so", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(outputRoot, entryName)))
            throw new InvalidDataException("The Linux mod binary does not match its manifest.");
    }

    private static bool SafeRelative(string path) => path.Length is > 0 and <= 256 &&
        !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part.Length is > 0 and <= 128 && part is not ("." or ".."));

    private static bool HashMatches(byte[] bytes, string expected) =>
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static void WriteInstallMetadata(string folder, MarketplaceMod mod)
    {
        using var stream = File.Create(Path.Combine(folder, ".serverapp-install.json"));
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
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
        if (mod.ConfigSchemaUrl is not null) writer.WriteString("configSchemaUrl", mod.ConfigSchemaUrl);
        if (mod.ConfigSchemaSha256 is not null) writer.WriteString("configSchemaSha256", mod.ConfigSchemaSha256);
        writer.WriteEndObject();
    }
}
