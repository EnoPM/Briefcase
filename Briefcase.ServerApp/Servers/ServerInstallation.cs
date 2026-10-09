using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

/// <summary>A Steam branch installed once and shared by any number of server instances.</summary>
public sealed record ServerInstallation(string Id, string Branch, string InstallRoot)
{
    public string Win64 => Path.Combine(InstallRoot, "DeceiveInc", "Binaries", "Win64");
    public string Shipping => Path.Combine(Win64, "DeceiveIncServer-Win64-Shipping.exe");
    public string? BuildId => SteamCmdService.InstalledBuildIdAtRoot(InstallRoot);
    public string DisplayName => BuildId is { } build
        ? $"{Branch} · Build {build}" : $"{Branch} · {AppTranslations.Get("versions.build_unknown", "Build unknown")}";
    public string Details => InstallRoot;

    public static string ValidateBranch(string? branch)
    {
        var value = branch?.Trim().ToLowerInvariant() ?? "";
        if (!Regex.IsMatch(value, "^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Enter a Steam branch name using lowercase letters, numbers, '-' or '_'.");
        return value;
    }
}
