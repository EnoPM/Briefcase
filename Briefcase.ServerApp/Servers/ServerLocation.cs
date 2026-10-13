namespace Briefcase.ServerApp;

public sealed class ServerLocation
{
    public string Win64 { get; }
    public string Shipping { get; }
    public string InstallRoot { get; }
    public string BriefcaseRoot => Path.Combine(IsInstance ? InstanceRoot : Win64, "Briefcase");
    public string InstanceId { get; private set; } = "";
    public string InstanceRoot { get; private set; } = "";
    public bool IsInstance => InstanceId.Length > 0;
    public string LiveKey => IsInstance ? InstanceId : Win64;
    public string Ue4ssRoot => Path.Combine(IsInstance ? InstanceRoot : Win64, "ue4ss");
    public string ModsRoot => Path.Combine(Ue4ssRoot, "Mods");
    public string ConfigFile { get; private set; }
    public string BalanceProfileFile { get; private set; }
    public string BalanceTemplateFile { get; }

    private ServerLocation(string win64)
    {
        Win64 = win64;
        Shipping = Path.Combine(win64, "DeceiveIncServer-Win64-Shipping.exe");
        var gameRoot = Directory.GetParent(Directory.GetParent(win64)!.FullName)!.FullName;
        InstallRoot = Directory.GetParent(gameRoot)!.FullName;
        ConfigFile = Path.Combine(gameRoot, "Saved", "Config", "WindowsServer", "TripwireServer.ini");
        BalanceProfileFile = Path.Combine(gameRoot, "CommunityBalanceProfile.json");
        BalanceTemplateFile = Path.Combine(gameRoot, "Community Balance Template", "CommunityBalanceProfile.default.json");
    }

    public static bool TryCreate(ServerRow? row, out ServerLocation? location, out string error)
    {
        if (row is null) return TryCreate((string?)null, out location, out error);
        if (!TryCreate(row.Win64, out location, out error)) return false;
        if (string.IsNullOrWhiteSpace(row.InstanceRoot)) return true;
        if (!Guid.TryParseExact(row.Id, "N", out _) || !Path.IsPathFullyQualified(row.InstanceRoot))
        {
            location = null;
            error = "The server instance identity or directory is invalid.";
            return false;
        }
        location!.InstanceId = row.Id;
        location.InstanceRoot = Path.GetFullPath(row.InstanceRoot);
        location.ConfigFile = Path.Combine(location.InstanceRoot, "TripwireServer.ini");
        location.BalanceProfileFile = Path.Combine(location.InstanceRoot, "CommunityBalanceProfile.json");
        return true;
    }

    public static bool TryCreateFromExecutable(string? executable, out ServerLocation? location, out string error)
    {
        location = null;
        error = "Select DeceiveIncServer.exe or DeceiveIncServer-Win64-Shipping.exe from a dedicated server installation.";
        if (string.IsNullOrWhiteSpace(executable)) return false;

        string full;
        try { full = Path.GetFullPath(executable.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }

        if (!File.Exists(full)) return false;
        var name = Path.GetFileName(full);
        if (name.Equals("DeceiveIncServer.exe", StringComparison.OrdinalIgnoreCase))
            return TryCreate(Path.Combine(Path.GetDirectoryName(full)!, "DeceiveInc", "Binaries", "Win64"),
                out location, out error);
        if (name.Equals("DeceiveIncServer-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase))
            return TryCreate(Path.GetDirectoryName(full), out location, out error);
        return false;
    }

    public static bool TryCreate(string? path, out ServerLocation? location, out string error)
    {
        location = null;
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Choose the dedicated server's Win64 directory.";
            return false;
        }
        try
        {
            var full = Path.GetFullPath(path.Trim());
            var win64 = new DirectoryInfo(full);
            if (!win64.Exists ||
                !win64.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
                win64.Parent?.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) != true ||
                win64.Parent.Parent?.Name.Equals("DeceiveInc", StringComparison.OrdinalIgnoreCase) != true)
            {
                error = "Select DeceiveInc\\Binaries\\Win64 from the dedicated server installation.";
                return false;
            }
            var candidate = new ServerLocation(win64.FullName);
            if (!File.Exists(candidate.Shipping))
            {
                error = "The dedicated server Shipping executable was not found in Win64.";
                return false;
            }
            location = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = "The server directory could not be opened.";
            return false;
        }
    }
}
