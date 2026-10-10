using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public static class BriefcasePackageInstaller
{
    private sealed record Item(string Path, long Size, string Hash, bool Preserve);
    private sealed record TransactionItem(string Path, bool Existed);

    public static void RecoverPending(ServerLocation location)
    {
        var updates = Path.Combine(location.BriefcaseRoot, "Updates");
        if (!Directory.Exists(updates)) return;
        EnsurePlainAncestors(location.Win64, updates);
        foreach (var stage in Directory.EnumerateDirectories(updates, "ServerApp-*").Take(32))
        {
            EnsurePlainAncestors(location.Win64, stage);
            var journal = Path.Combine(stage, "transaction.json");
            if (!File.Exists(journal)) continue;
            EnsurePlainAncestors(stage, journal);
            if (new FileInfo(journal).Length is <= 0 or > 512_000)
                throw new InvalidDataException("Pending Briefcase transaction journal is invalid.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(journal));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Pending Briefcase transaction journal is not a list.");
            var records = document.RootElement.EnumerateArray().Select(value =>
                new TransactionItem(value.GetProperty("path").GetString() ?? "", value.GetProperty("existed").GetBoolean())).ToArray();
            if (records.Length > 4096 || records.Any(item => item.Path != "Package.json" && !ValidPath(item.Path)) ||
                records.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Length)
                throw new InvalidDataException("Pending Briefcase transaction contains an unsafe path.");
            foreach (var item in records.Reverse())
            {
                var target = SafeChild(location.Win64, item.Path);
                var backup = SafeChild(stage, "previous/" + item.Path);
                EnsurePlainAncestors(location.Win64, target);
                EnsurePlainAncestors(stage, backup);
                if (File.Exists(backup))
                {
                    if (File.Exists(target)) File.Delete(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(backup, target);
                }
                else if (!item.Existed && File.Exists(target)) File.Delete(target);
            }
            Directory.Delete(stage, recursive: true);
        }
    }

    public static void InstallOrUpdate(ServerLocation location, string archiveFile, string expectedVersion,
        IProgress<string> progress)
    {
        using (var process = ServerProcess.FindAnyInstallation(location))
            if (process is not null) throw new InvalidOperationException("Stop the server before installing Briefcase.");
        RecoverPending(location);
        using var archive = ZipFile.OpenRead(archiveFile);
        var manifestEntry = archive.GetEntry("Package.json") ?? throw new InvalidDataException("Missing package manifest.");
        if (manifestEntry.Length is <= 0 or > 1_048_576) throw new InvalidDataException("Invalid package manifest size.");
        using var manifestStream = manifestEntry.Open();
        using var doc = JsonDocument.Parse(manifestStream);
        var manifest = doc.RootElement;
        if (manifest.GetProperty("updateSchema").GetInt32() != 1 ||
            manifest.GetProperty("platform").GetString() != "windows-x64" ||
            manifest.GetProperty("environment").GetString() != "server" ||
            manifest.GetProperty("frameworkVersion").GetString() != expectedVersion)
            throw new InvalidDataException("The downloaded package is not this Windows server release.");
        using var sha = SHA256.Create();
        using (var game = File.OpenRead(location.Shipping))
        {
            var gameHash = Convert.ToHexString(sha.ComputeHash(game));
            if (!gameHash.Equals(manifest.GetProperty("gameSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This Briefcase release does not support the selected game build.");
        }
        var items = new List<Item>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Package.json" };
        foreach (var value in manifest.GetProperty("files").EnumerateArray())
        {
            var name = value.GetProperty("path").GetString() ?? "";
            if (!ValidPath(name) || !paths.Add(name)) throw new InvalidDataException("Unsafe or duplicate package path.");
            var length = value.GetProperty("bytes").GetInt64();
            var hash = value.GetProperty("sha256").GetString() ?? "";
            if (length is < 0 or > 512L * 1024 * 1024 || !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("Invalid package inventory.");
            items.Add(new Item(name, length, hash, value.TryGetProperty("preserve", out var flag) && flag.GetBoolean()));
        }
        if (paths.Contains("Briefcase/Core/Briefcase.ServerBootstrap.dll") ||
            paths.Contains("Briefcase/Core/Tools/Briefcase.ServerInjector.exe"))
            throw new InvalidDataException("App-private injection components must not be included in a Briefcase release.");
        if (!paths.Contains("version.dll") || !paths.Contains("ue4ss/UE4SS.dll") ||
            !paths.Contains("Briefcase/Core/Briefcase.NativeHost.dll") ||
            items.Count > 4095 || archive.Entries.Count != paths.Count ||
            archive.Entries.Any(entry => !paths.Contains(entry.FullName) || entry.FullName.EndsWith('/')))
            throw new InvalidDataException("Incomplete or unexpected Briefcase package files.");
        var stage = Path.Combine(location.Win64, "Briefcase", "Updates", "ServerApp-" + Guid.NewGuid().ToString("N"));
        EnsurePlainAncestors(location.Win64, stage);
        Directory.CreateDirectory(stage);
        var changed = new List<(string Target, string? Backup)>();
        var keepStage = false;
        try
        {
            foreach (var item in items)
            {
                var entry = archive.GetEntry(item.Path) ?? throw new InvalidDataException("Missing packaged file.");
                if (entry.Length != item.Size) throw new InvalidDataException("Package file size mismatch.");
                var extracted = SafeChild(stage, item.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(extracted)!);
                using (var input = entry.Open())
                using (var output = File.Create(extracted)) input.CopyTo(output);
                using var verify = File.OpenRead(extracted);
                if (!Convert.ToHexString(sha.ComputeHash(verify)).Equals(item.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Package file checksum mismatch: " + item.Path);
            }
            progress.Report(AppTranslations.Get("status.installing_briefcase", "Installing Briefcase…"));
            var transaction = items.Where(i => !i.Preserve || !File.Exists(SafeChild(location.Win64, i.Path)))
                .Select(i => new TransactionItem(i.Path, File.Exists(SafeChild(location.Win64, i.Path))))
                .Append(new TransactionItem("Package.json", File.Exists(SafeChild(location.Win64, "Package.json"))))
                .ToArray();
            var journalTemp = Path.Combine(stage, "transaction.tmp");
            using (var journalStream = File.Create(journalTemp))
            using (var writer = new Utf8JsonWriter(journalStream))
            {
                writer.WriteStartArray();
                foreach (var record in transaction)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", record.Path);
                    writer.WriteBoolean("existed", record.Existed);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            File.Move(journalTemp, Path.Combine(stage, "transaction.json"));
            foreach (var item in items.Where(i => i.Path != "version.dll"))
                Copy(item);
            var packageTarget = SafeChild(location.Win64, "Package.json");
            ReplaceExisting(packageTarget, "Package.json");
            using (var input = manifestEntry.Open())
            using (var output = File.Create(packageTarget)) input.CopyTo(output);
            Copy(items.Single(i => i.Path == "version.dll"));
            progress.Report(AppTranslations.Get("status.briefcase_ready", "Briefcase is ready."));
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var (target, backup) in changed.AsEnumerable().Reverse())
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (backup is not null) File.Move(backup, target);
                }
            }
            catch (Exception rollback)
            {
                keepStage = true;
                throw new IOException($"Briefcase installation and rollback failed. Backups remain in {stage}.",
                    new AggregateException(failure, rollback));
            }
            throw;
        }
        finally
        {
            if (!keepStage && Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }

        void Copy(Item item)
        {
            var target = SafeChild(location.Win64, item.Path);
            EnsurePlainAncestors(location.Win64, target);
            if (File.Exists(target))
            {
                if (item.Preserve) return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ReplaceExisting(target, item.Path);
            File.Copy(SafeChild(stage, item.Path), target);
        }

        void ReplaceExisting(string target, string name)
        {
            string? backup = null;
            if (File.Exists(target))
            {
                backup = SafeChild(stage, "previous/" + name);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(target, backup);
            }
            changed.Add((target, backup));
        }
    }

    private static bool ValidPath(string name) => name.Length is > 0 and <= 240 &&
        !name.StartsWith('/') && !name.Contains('\\') && !name.Contains(':') &&
        name.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..") &&
        (name == "version.dll" || name == "ue4ss/UE4SS.dll" ||
         name == "ue4ss/UE4SS-settings.ini" || name == "ue4ss/Mods/mods.txt" ||
         name is "ue4ss/Mods/BriefcaseServerBridge/dlls/main.dll" or
             "ue4ss/Mods/BriefcaseServerBridge/enabled.txt" ||
         name.StartsWith("ue4ss/Licenses/", StringComparison.Ordinal) ||
         name.StartsWith("Briefcase/Core/", StringComparison.Ordinal));

    private static string SafeChild(string root, string name)
    {
        var result = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escapes Win64.");
        return result;
    }

    private static void EnsurePlainAncestors(string root, string target)
    {
        var current = Path.GetDirectoryName(target);
        var normalizedRoot = Path.GetFullPath(root);
        while (current is not null && current.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A package path passes through a directory link.");
            if (string.Equals(current, normalizedRoot, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A package target is a file link.");
    }
}
