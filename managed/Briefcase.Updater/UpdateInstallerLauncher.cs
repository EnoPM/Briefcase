using System.Diagnostics;
using System.Text.Json;

namespace Briefcase.Updater;

/// <summary>
/// Copies the installer out of Briefcase/Core before starting it. The copied
/// executable remains usable after the running framework directory is replaced.
/// </summary>
public static class UpdateInstallerLauncher
{
    private const string DeceiveIncSteamAppId = "820520";

    public static void Schedule(
        PreparedBriefcaseUpdate update,
        string installRoot,
        string installerExecutable,
        string restartPreference,
        Action<string>? info = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        var source = Path.GetFullPath(installerExecutable);
        if (!File.Exists(source))
            throw new FileNotFoundException("The Briefcase update installer is missing.", source);

        var staging = Path.GetFullPath(update.StagingDirectory);
        Directory.CreateDirectory(staging);
        var temporaryInstaller = Path.Combine(staging, "Briefcase.UpdateInstaller.exe");
        File.Copy(source, temporaryInstaller, overwrite: true);

        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException(
                             "The current executable path is unavailable.");
        var (restartMode, steamAppId) = ResolveRestart(
            update.PackageKind, restartPreference, installRoot);
        var request = new UpdateInstallRequest
        {
            ParentProcessId = Environment.ProcessId,
            InstallRoot = Path.GetFullPath(installRoot),
            ArchivePath = Path.GetFullPath(update.ArchivePath),
            Version = update.DisplayVersion,
            PackageKind = update.PackageKind.ToString(),
            RestartMode = restartMode,
            RestartExecutable = Path.GetFullPath(executable),
            RestartArguments = Environment.GetCommandLineArgs().Skip(1).ToArray(),
            SteamAppId = steamAppId,
            Restart = true
        };
        var requestPath = Path.Combine(staging, "install-request.json");
        File.WriteAllText(
            requestPath,
            JsonSerializer.Serialize(
                request,
                UpdateInstallJsonContext.Default.UpdateInstallRequest));

        var start = new ProcessStartInfo(temporaryInstaller)
        {
            WorkingDirectory = staging,
            UseShellExecute = false,
            CreateNoWindow = update.PackageKind == BriefcasePackageKind.Client
        };
        start.ArgumentList.Add(requestPath);
        var process = Process.Start(start) ??
                      throw new InvalidOperationException(
                          "Windows did not start the Briefcase update installer.");
        info?.Invoke(
            $"Update installer started (PID={process.Id}, restart={restartMode}).");
    }

    internal static (string Mode, string? SteamAppId) ResolveRestart(
        BriefcasePackageKind packageKind,
        string? preference,
        string? installRoot = null)
    {
        if (packageKind == BriefcasePackageKind.Server)
            return ("executable", null);

        var normalized = preference?.Trim().ToLowerInvariant();
        if (normalized is not (null or "" or "auto" or "steam" or "executable"))
            normalized = "auto";

        var environmentAppId = Environment.GetEnvironmentVariable("SteamAppId");
        if (normalized == "executable")
            return ("executable", null);
        if (normalized == "steam")
            return ("steam", IsNumericAppId(environmentAppId)
                ? environmentAppId
                : DeceiveIncSteamAppId);
        if (IsNumericAppId(environmentAppId))
            return ("steam", environmentAppId);

        var normalizedRoot = installRoot?.Replace('/', '\\');
        return normalizedRoot?.Contains(
                   "\\steamapps\\common\\",
                   StringComparison.OrdinalIgnoreCase) == true
            ? ("steam", DeceiveIncSteamAppId)
            : ("executable", null);
    }

    private static bool IsNumericAppId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 12 &&
        value.All(character => character is >= '0' and <= '9');
}
