using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Briefcase.ServerApp;

public sealed record ModRow(string Name, string State, string Configuration, string ConfigPath,
    string Version = "", bool HasNativeBinary = false, bool IsCatalogManaged = false, Bitmap? Logo = null,
    string DisplayName = "", string Author = "", string Description = "",
    string? DocumentationUrl = null, string? DiscordUrl = null, string? HomepageUrl = null)
{
    public string Title => DisplayName.Length > 0 ? DisplayName : Name;
    public bool HasDistinctId => DisplayName.Length > 0 && !DisplayName.Equals(Name, StringComparison.OrdinalIgnoreCase);
    public bool HasAuthor => Author.Length > 0;
    public bool HasDescription => Description.Length > 0;
    public bool HasDocumentation => !string.IsNullOrWhiteSpace(DocumentationUrl);
    public bool HasDiscord => !string.IsNullOrWhiteSpace(DiscordUrl);
    public bool HasHomepage => !string.IsNullOrWhiteSpace(HomepageUrl);
    public bool HasLogo => Logo is not null;
    public bool MissingLogo => Logo is null;
    public bool IsEnabled => State == "Enabled";
    public bool IsCore => Name.Equals("BriefcaseServerBridge", StringComparison.OrdinalIgnoreCase);
    public bool CanToggle => !IsCore;
    public bool CanEnable => CanToggle && !IsEnabled;
    public bool CanDisable => CanToggle && IsEnabled;
    public string DisplayState => AppTranslations.Get("mod." + State, State);
    public string DisplayConfiguration => AppTranslations.Get("mod." + Configuration, Configuration);
    public bool HasVersion => Version.Length > 0;
    public bool HasConfiguration => Configuration == "Has configuration";
    public string VersionSummary => HasVersion ? "v" + Version : AppTranslations.Get("mod.version_unknown", "Version unknown");
    public string NativeBinarySummary => AppTranslations.Get("mod.native_binary", "Native DLL");
    public string InstallSourceSummary => IsCatalogManaged
        ? AppTranslations.Get("mod.catalog_source", "Catalog")
        : AppTranslations.Get("mod.local_source", "Local");
    public IBrush StateForeground => State == "Enabled" ? Brushes.LightGreen :
        State == "Disabled" ? Brushes.LightGray : Brushes.Khaki;
    public IBrush StateBackground => State == "Enabled" ? new SolidColorBrush(Color.Parse("#234535")) :
        State == "Disabled" ? new SolidColorBrush(Color.Parse("#34313B")) :
        new SolidColorBrush(Color.Parse("#493A25"));
}

public static class ModCatalog
{
    public static IReadOnlyList<ModRow> Read(ServerLocation location)
    {
        var root = location.ModsRoot;
        if (!Directory.Exists(root)) return [];
        var selection = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(root, "mods.txt");
        if (File.Exists(file) && new FileInfo(file).Length <= 65536)
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Split('#', 2)[0].Trim();
                var separator = line.LastIndexOf(':');
                if (separator <= 0) continue;
                var name = line[..separator].Trim();
                var flag = line[(separator + 1)..].Trim();
                if (name.Length is > 0 and <= 128 && flag is "0" or "1")
                    selection[name] = flag == "1";
            }
        }
        return Directory.EnumerateDirectories(root)
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                           !Path.GetFileName(path).Equals("shared", StringComparison.OrdinalIgnoreCase))
            .Take(256)
            .Select(path =>
            {
                var name = Path.GetFileName(path);
                var state = selection.TryGetValue(name, out var enabled)
                    ? enabled ? "Enabled" : "Disabled"
                    : "Not selected";
                var config = File.Exists(Path.Combine(path, "Data", "config.json"))
                    ? "Has configuration" : "No configuration";
                var metadata = ModMarketplace.InstalledMetadata(location, name);
                return new ModRow(name, state, config, Path.Combine(path, "Data", "config.json"),
                    metadata?.Version ?? "",
                    File.Exists(Path.Combine(path, "dlls", "main.dll")),
                    File.Exists(Path.Combine(path, ".serverapp-install.json")),
                    ModLogo.FromDirectory(path), metadata?.Name ?? "", metadata?.Author ?? "",
                    metadata?.Description ?? "", metadata?.DocumentationUrl, metadata?.DiscordUrl,
                    metadata?.HomepageUrl);
            })
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> InstalledCatalogSources(ServerLocation location)
    {
        var root = location.ModsRoot;
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .Take(256)
            .Select(path => ModMarketplace.InstalledMetadata(location, Path.GetFileName(path))?.Catalog)
            .Where(url => url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                          uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
    }

    public static void SetEnabled(ServerLocation location, string name, bool enabled)
    {
        var root = ValidateTarget(location, name, out var target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        var file = Path.Combine(root, "mods.txt");
        var lines = ReadSelection(file);
        var selected = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (!IsSelectionFor(lines[index], name)) continue;
            lines[index] = name + (enabled ? " : 1" : " : 0");
            selected = true;
        }
        if (!selected) lines.Add(name + (enabled ? " : 1" : " : 0"));
        WriteSelection(file, lines);
    }

    public static void Uninstall(ServerLocation location, string name)
    {
        var root = ValidateTarget(location, name, out var target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException(target);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(target));
        while (stack.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The mod contains a directory link; uninstall was cancelled.");
                if (entry is DirectoryInfo child) stack.Push(child);
            }
        }
        var file = Path.Combine(root, "mods.txt");
        var lines = ReadSelection(file);
        var retained = lines.Where(line => !IsSelectionFor(line, name)).ToList();
        var dataRoot = location.IsInstance ? location.InstanceRoot : location.BriefcaseRoot;
        var updates = Path.Combine(dataRoot, "Updates");
        if (Directory.Exists(dataRoot) &&
            File.GetAttributes(dataRoot).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The Briefcase directory is a link.");
        if (Directory.Exists(updates) && File.GetAttributes(updates).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The update directory is a link.");
        Directory.CreateDirectory(updates);
        var pending = Path.Combine(updates, "ServerAppRemovedMod-" + Guid.NewGuid().ToString("N"));
        Directory.Move(target, pending);
        try { if (retained.Count != lines.Count) WriteSelection(file, retained); }
        catch
        {
            Directory.Move(pending, target);
            throw;
        }
        Directory.Delete(pending, recursive: true);
    }

    private static string ValidateTarget(ServerLocation location, string name, out string target)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || Path.GetFileName(name) != name ||
            name.Contains(':') || name.Contains('\\') || name.Contains('/') || name is "." or ".." ||
            name.Equals("shared", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("BriefcaseServerBridge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This mod cannot be managed from the application.");
        using (var running = ServerProcess.Find(location))
            if (running is not null) throw new InvalidOperationException("Stop the server before changing mods.");
        var root = location.ModsRoot;
        target = Path.Combine(root, name);
        foreach (var path in new[] { location.IsInstance ? location.InstanceRoot : location.Win64,
                     location.Ue4ssRoot, root, target })
            if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The mod path contains a directory link.");
        return root;
    }

    private static List<string> ReadSelection(string file)
    {
        if (!File.Exists(file)) return [];
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(file).Length > 65536)
            throw new InvalidDataException("UE4SS mod selection file is unsafe or too large.");
        return File.ReadAllLines(file).ToList();
    }

    private static bool IsSelectionFor(string line, string name)
    {
        var content = line.Split('#', 2)[0];
        var separator = content.LastIndexOf(':');
        return separator > 0 && content[..separator].Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSelection(string file, List<string> lines)
    {
        var temp = file + ".serverapp-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temp, lines);
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
