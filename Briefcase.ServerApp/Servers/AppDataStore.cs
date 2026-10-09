using System.Text.Json;
using System.Text.Json.Nodes;

namespace Briefcase.ServerApp;

/// <summary>Locates the application's data independently of its executable.</summary>
public static class AppDataStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Briefcase", "ServerApp");

    // This small locator must remain at a stable path when the data itself moves.
    public static string LocatorFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Briefcase", "ServerApp.location.json");

    public static string Root => ResolveRoot(DefaultRoot, LocatorFile);
    public static string ServersRoot => Path.Combine(Root, "servers");
    public static string SteamCmdRoot => Path.Combine(Root, "SteamCMD");
    public static string DownloadsRoot => Path.Combine(Root, "Downloads");
    public static string CatalogsRoot => Path.Combine(Root, "Catalogs");

    public static string ResolveRoot(string fallback, string locator)
    {
        if (!File.Exists(locator)) return Path.GetFullPath(fallback);
        using var doc = JsonDocument.Parse(File.ReadAllText(locator));
        var path = doc.RootElement.GetProperty("dataDirectory").GetString();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The Briefcase data directory locator is invalid.");
        return Path.GetFullPath(path);
    }

    public static string ServerInstallRoot(string dataRoot, string name)
    {
        ValidateServerName(name);
        return Path.Combine(Path.GetFullPath(dataRoot), "game", "dedicated");
    }

    public static string VersionInstallRoot(string dataRoot, string branch) =>
        Path.Combine(Path.GetFullPath(dataRoot), "game", "versions", ServerInstallation.ValidateBranch(branch));

    public static string ValidateServerName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 48 || trimmed is "." or ".." ||
            trimmed.EndsWith('.') || trimmed.EndsWith(' ') ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            IsReservedName(trimmed))
            throw new InvalidDataException("Enter a server name of 1–48 characters without characters forbidden in folder names.");
        return trimmed;
    }

    private static bool IsReservedName(string name)
    {
        var stem = name.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    public static bool IsInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative == "." || relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathFullyQualified(relative);
    }

    /// <returns>True if the old directory could not be removed after the new one was activated.</returns>
    public static bool Move(string source, string destination, string locator)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)) return false;
        ValidateMoveDestination(source, destination);

        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException("Invalid data directory.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".briefcase-data-" + Guid.NewGuid().ToString("N"));
        var activated = false;
        try
        {
            Directory.CreateDirectory(stage);
            if (Directory.Exists(source)) CopyDirectory(source, stage);
            Directory.CreateDirectory(Path.Combine(stage, "servers"));
            RewriteRegisteredServerPaths(Path.Combine(stage, "settings.json"), source, destination);
            if (Directory.Exists(destination)) Directory.Delete(destination);
            Directory.Move(stage, destination);
            try { WriteLocator(locator, destination); }
            catch
            {
                Directory.Move(destination, stage);
                throw;
            }
            activated = true;
        }
        finally
        {
            if (!activated && Directory.Exists(stage)) Directory.Delete(stage, true);
        }

        if (!Directory.Exists(source)) return false;
        try { Directory.Delete(source, true); return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public static void ValidateMoveDestination(string source, string destination)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (IsInside(destination, source) || IsInside(source, destination))
            throw new InvalidDataException("Choose a folder outside the current Briefcase data directory.");
        if ((Directory.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) ||
            (Directory.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Linked data folders cannot be moved safely.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidDataException("Choose an empty folder for Briefcase data.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The data directory contains a linked file or folder that cannot be moved safely.");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target);
            }
            else File.Copy(entry, target);
        }
    }

    private static void RewriteRegisteredServerPaths(string settingsFile, string source, string destination)
    {
        if (!File.Exists(settingsFile)) return;
        var root = JsonNode.Parse(File.ReadAllText(settingsFile))?.AsObject()
            ?? throw new InvalidDataException("The Briefcase settings file is invalid.");
        if (root["servers"] is JsonArray servers)
            foreach (var item in servers)
            {
                if (item is not JsonObject server) continue;
                foreach (var key in new[] { "win64", "instanceRoot" })
                    if (server[key]?.GetValue<string>() is { Length: > 0 } path && IsInside(path, source))
                        server[key] = Path.Combine(destination, Path.GetRelativePath(source, path));
            }
        if (root["installations"] is JsonArray installations)
            foreach (var item in installations)
                if (item is JsonObject installation &&
                    installation["installRoot"]?.GetValue<string>() is { Length: > 0 } path &&
                    IsInside(path, source))
                    installation["installRoot"] = Path.Combine(destination, Path.GetRelativePath(source, path));
        File.WriteAllText(settingsFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteLocator(string locator, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(locator)!);
        var temporary = locator + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("dataDirectory", destination);
                writer.WriteEndObject();
            }
            File.Move(temporary, locator, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
