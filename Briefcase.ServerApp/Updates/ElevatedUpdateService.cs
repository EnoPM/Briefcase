using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;

namespace Briefcase.ServerApp;

/// <summary>Runs only the requested file update as administrator; the UI stays unelevated.</summary>
internal static class ElevatedUpdateService
{
    private const string MaintenanceArgument = "--serverapp-maintenance";

    public static bool AccessDenied(Exception error) => error is UnauthorizedAccessException ||
        error is IOException io && (io.HResult & 0xffff) == 5;

    public static bool NeedsElevation(string directory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(directory));
        while (!current.Exists)
            current = current.Parent ?? throw new InvalidDataException("The update destination is unavailable.");
        try
        {
            var probe = Path.Combine(current.FullName, ".briefcase-write-" + Guid.NewGuid().ToString("N"));
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
            return false;
        }
        catch (Exception error) when (AccessDenied(error)) { return true; }
    }

    public static async Task RunAsync(string operation, string win64, IProgress<string> progress,
        CancellationToken cancellationToken, string? catalog = null, string? modId = null, string? hash = null,
        string? instanceRoot = null, string? instanceId = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Briefcase.ServerApp.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Administrator-approved updates require the application executable.", executable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var value in new[] { MaintenanceArgument, operation, win64, catalog ?? "", modId ?? "", hash ?? "",
                     instanceRoot ?? "", instanceId ?? "" })
            start.ArgumentList.Add(value);
        progress.Report("Waiting for administrator approval, then applying the update…");
        using var process = Process.Start(start) ?? throw new Win32Exception("Administrator approval was not granted.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The administrator-approved update failed. Check access to the server folder.");
    }

    public static async Task<int?> RunMaintenanceAsync(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != MaintenanceArgument) return null;
        if (arguments.Length != 8) return 1;
        try
        {
            var operation = arguments[1];
            if (!ServerLocation.TryCreate(arguments[2], out var location, out _)) return 1;
            if (operation is "game" or "briefcase")
                using (var running = ServerProcess.FindAnyInstallation(location!))
                    if (running is not null) return 1;
            var progress = new Progress<string>(_ => { });
            switch (operation)
            {
                case "game":
                    var tools = AppDataStore.SteamCmdRoot;
                    await new SteamCmdService(tools).InstallOrUpdateAsync(location!.InstallRoot, true, progress,
                        CancellationToken.None);
                    break;
                case "briefcase":
                    await new BriefcaseInstallService().InstallOrUpdateAsync(location!, progress, CancellationToken.None);
                    break;
                case "mod":
                    var catalog = arguments[3];
                    var modId = arguments[4];
                    var hash = arguments[5];
                    if (catalog.Length is < 1 or > 2048 || modId.Length is < 1 or > 64 || hash.Length != 64)
                        return 1;
                    var instanceRoot = arguments[6];
                    var instanceId = arguments[7];
                    if (!Guid.TryParseExact(instanceId, "N", out _) ||
                        !Path.IsPathFullyQualified(instanceRoot) ||
                        !AppDataStore.IsInside(instanceRoot, AppDataStore.ServersRoot) ||
                        !string.Equals(Path.GetFileName(instanceRoot), instanceId, StringComparison.OrdinalIgnoreCase))
                        return 1;
                    if (!ServerLocation.TryCreate(new ServerRow(instanceId, location!.Win64, false, instanceRoot),
                            out location, out _)) return 1;
                    using (var running = ServerProcess.Find(location!))
                        if (running is not null) return 1;
                    var marketplace = new ModMarketplace();
                    var mods = await marketplace.ReadCatalogAsync(catalog, CancellationToken.None);
                    var mod = mods.SingleOrDefault(item => item.Id == modId &&
                        item.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
                    if (mod is null) return 1;
                    var downloads = AppDataStore.DownloadsRoot;
                    var archive = await marketplace.DownloadAsync(mod, downloads, progress, CancellationToken.None);
                    try
                    {
                        var logo = await ModLogo.VerifiedBytesAsync(mod.LogoUrl, mod.LogoSha256, CancellationToken.None);
                        if (mod.LogoSha256 is not null && logo is null) return 1;
                        var formOverride = await ModMarketplace.DownloadFormOverrideAsync(mod, CancellationToken.None);
                        var formTranslations = await ModMarketplace.DownloadFormTranslationsAsync(mod, CancellationToken.None);
                        ModMarketplace.Install(location!, mod, archive, logo, formOverride, formTranslations);
                    }
                    finally { if (File.Exists(archive)) File.Delete(archive); }
                    break;
                default: return 1;
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or HttpRequestException or ArgumentException or Win32Exception) { return 1; }
    }
}
