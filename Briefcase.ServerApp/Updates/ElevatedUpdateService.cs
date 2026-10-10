using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

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
        string? instanceRoot = null, string? instanceId = null, string? dataDirectory = null)
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
                     instanceRoot ?? "", instanceId ?? "", dataDirectory ?? "" })
            start.ArgumentList.Add(value);
        progress.Report(AppTranslations.Get("updates.administrator_waiting",
            "Waiting for administrator approval, then applying the update…"));
        Process? launched;
        try { launched = Process.Start(start); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new Win32Exception(1223, AppTranslations.Get("updates.administrator_cancelled",
                "Administrator approval was cancelled. The operation was not completed."));
        }
        using var process = launched ?? throw new Win32Exception("Administrator approval was not granted.");
        progress.Report(AppTranslations.Get("updates.administrator_running",
            "Administrator approval granted. Applying the installation or update…"));
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("The administrator-approved update failed. Check access to the server folder.");
    }

    public static Task InstallSteamVersionAsync(string installRoot, bool updating, string branch,
        string? betaPassword, IProgress<string> progress, CancellationToken cancellationToken) =>
        RunAsync("steam-version", installRoot, progress, cancellationToken,
            branch, updating ? "update" : "install", betaPassword);

    public static async Task<int?> RunMaintenanceAsync(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != MaintenanceArgument) return null;
        if (arguments.Length != 9) return 1;
        try
        {
            var operation = arguments[1];
            if (operation == "steam-version")
            {
                if (arguments[6].Length != 0 || arguments[7].Length != 0 || arguments[8].Length != 0 ||
                    arguments[4] is not ("install" or "update")) return 1;
                var branch = ServerInstallation.ValidateBranch(arguments[3]);
                var root = Path.GetFullPath(arguments[2]);
                var updating = arguments[4] == "update";
                if (updating)
                {
                    if (!ServerLocation.TryCreate(Path.Combine(root, "DeceiveInc", "Binaries", "Win64"),
                            out var existing, out _)) return 1;
                    using var running = ServerProcess.FindAnyInstallation(existing!);
                    if (running is not null) return 1;
                }
                await new SteamCmdService(AppDataStore.SteamCmdRoot).InstallOrUpdateAsync(root, updating,
                    new Progress<string>(_ => { }), CancellationToken.None, branch,
                    arguments[5].Length == 0 ? null : arguments[5]);
                return 0;
            }
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
                    var dataDirectory = arguments[8];
                    if (!Guid.TryParseExact(instanceId, "N", out _) ||
                        !Path.IsPathFullyQualified(instanceRoot) || !Path.IsPathFullyQualified(dataDirectory) ||
                        !AppDataStore.IsInside(instanceRoot, Path.Combine(dataDirectory, "servers")) ||
                        !string.Equals(Path.GetFileName(instanceRoot), instanceId, StringComparison.OrdinalIgnoreCase) ||
                        !IsRegisteredInstance(dataDirectory, instanceId, location!.Win64, instanceRoot))
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
            InvalidOperationException or HttpRequestException or ArgumentException or Win32Exception or JsonException)
        { return 1; }
    }

    private static bool IsRegisteredInstance(string dataDirectory, string instanceId, string win64,
        string instanceRoot)
    {
        var file = Path.Combine(dataDirectory, "settings.json");
        if (!File.Exists(file) || new FileInfo(file).Length is <= 0 or > 1024 * 1024 ||
            File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return false;
        using var document = JsonDocument.Parse(File.ReadAllBytes(file));
        if (!document.RootElement.TryGetProperty("servers", out var servers) ||
            servers.ValueKind != JsonValueKind.Array) return false;
        return servers.EnumerateArray().Any(server =>
            server.ValueKind == JsonValueKind.Object &&
            server.TryGetProperty("id", out var id) && id.GetString() == instanceId &&
            server.TryGetProperty("win64", out var registeredWin64) &&
            string.Equals(Path.GetFullPath(registeredWin64.GetString() ?? ""), win64,
                StringComparison.OrdinalIgnoreCase) &&
            server.TryGetProperty("instanceRoot", out var registeredRoot) &&
            string.Equals(Path.GetFullPath(registeredRoot.GetString() ?? ""),
                Path.GetFullPath(instanceRoot), StringComparison.OrdinalIgnoreCase));
    }
}
