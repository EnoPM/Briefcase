using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public sealed class SteamCmdService
{
    public sealed record BuildCheck(string? Installed, string? Public, string? Error)
    {
        public bool UpdateAvailable => long.TryParse(Installed, out var local) &&
            long.TryParse(Public, out var latest) && latest > local;
        public bool Current => Installed is not null && Installed == Public;
    }
    public static string? InstalledBuildId(ServerLocation location)
    {
        return InstalledBuildIdAtRoot(location.InstallRoot);
    }
    public static string? InstalledBuildIdAtRoot(string installRoot)
    {
        var manifest = Path.Combine(installRoot, "steamapps", $"appmanifest_{ServerAppId}.acf");
        if (!File.Exists(manifest)) return null;
        try
        {
            var id = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(?<id>\\d+)\"").Groups["id"].Value;
            return id.Length == 0 ? null : id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public const int ServerAppId = 5007710;
    private static readonly Uri DownloadUrl = new("https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly string _toolsFolder;

    public SteamCmdService(string? toolsFolder = null) => _toolsFolder = toolsFolder ?? AppDataStore.SteamCmdRoot;

    public async Task<string> EnsureInstalledAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_toolsFolder);
        var executable = Path.Combine(_toolsFolder, "steamcmd.exe");
        if (File.Exists(executable)) return executable;
        progress.Report(AppTranslations.Get("status.preparing_download", "Preparing the download…"));
        using var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 100_000_000)
            throw new InvalidDataException("SteamCMD download is unexpectedly large.");
        var archiveFile = Path.Combine(_toolsFolder, "steamcmd.zip.download");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(archiveFile))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > 100_000_000) throw new InvalidDataException("SteamCMD download is unexpectedly large.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (response.Content.Headers.ContentLength is > 0 and var expected)
                        progress.Report(AppTranslations.Format("status.downloading_tools", "Preparing download: {0}%",
                            total * 100 / expected));
                }
            }
            using var archive = ZipFile.OpenRead(archiveFile);
            var item = archive.Entries.SingleOrDefault(entry => entry.FullName.Equals("steamcmd.exe", StringComparison.OrdinalIgnoreCase));
            if (item is null || item.Length is <= 0 or > 100_000_000)
                throw new InvalidDataException("Valve's archive has no valid steamcmd.exe.");
            var tempExecutable = executable + ".download";
            item.ExtractToFile(tempExecutable, true);
            File.Move(tempExecutable, executable, true);
            return executable;
        }
        finally { if (File.Exists(archiveFile)) File.Delete(archiveFile); }
    }

    public async Task<string> InstallOrUpdateAsync(string installRoot, bool updating,
        IProgress<string> progress, CancellationToken cancellationToken,
        string branch = "public", string? betaPassword = null)
    {
        branch = ServerInstallation.ValidateBranch(branch);
        if (betaPassword is { Length: > 128 } || betaPassword?.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("The Steam beta password is invalid.");
        var root = Path.GetFullPath(installRoot);
        var directory = new DirectoryInfo(root);
        if (directory.Parent is null || directory.FullName == directory.Root.FullName ||
            (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Select a normal, non-root server directory.");
        var shipping = Path.Combine(root, "DeceiveInc", "Binaries", "Win64", "DeceiveIncServer-Win64-Shipping.exe");
        var marker = Path.Combine(root, ".briefcase-steam-branch");
        if (File.Exists(marker) && File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The server installation marker cannot be a link.");
        var resume = File.Exists(marker) && File.ReadAllText(marker).Trim() == branch;
        if (!updating && !resume && directory.Exists && directory.EnumerateFileSystemInfos().Any(entry =>
                !entry.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
                entry is not DirectoryInfo steamApps || steamApps.EnumerateFileSystemInfos().Any()))
            throw new InvalidDataException("A new server requires an empty directory (an empty SteamCMD steamapps folder is allowed for retries).");
        ServerLocation? location = null;
        if (updating && !ServerLocation.TryCreate(Path.GetDirectoryName(shipping), out location, out _))
            throw new InvalidDataException("The selected server installation is incomplete.");
        if (updating)
        {
            using var process = ServerProcess.FindAnyInstallation(location!);
            if (process is not null) throw new InvalidOperationException("Stop the server before updating game files.");
        }
        var steamcmd = await EnsureInstalledAsync(progress, cancellationToken);
        Directory.CreateDirectory(root);
        if (!updating && !resume) File.WriteAllText(marker, branch);
        progress.Report(AppTranslations.Get(updating ? "status.updating_server" : "status.installing_server",
            updating ? "Updating the dedicated server…" : "Installing the dedicated server…"));
        try
        {
            await RunSteamCmdAsync(steamcmd, root, branch, betaPassword, progress, cancellationToken);
        }
        catch (SteamCmdRunException firstFailure) when (updating && !firstFailure.FileAccessDenied &&
                                                       !cancellationToken.IsCancellationRequested)
        {
            // An old depot manifest can become inaccessible after a branch is published.
            // A fresh install does not ask SteamCMD for that old manifest.
            progress.Report(AppTranslations.Get("status.retrying_clean_server_download",
                "The update failed; downloading a clean server copy before replacing the old one…"));
            var stage = SteamInstallationRecovery.NewStagePath(root);
            try
            {
                Directory.CreateDirectory(stage);
                File.WriteAllText(Path.Combine(stage, ".briefcase-steam-branch"), branch);
                try
                {
                    await RunSteamCmdAsync(steamcmd, stage, branch, betaPassword, progress, cancellationToken);
                }
                catch (SteamCmdRunException secondFailure) when (secondFailure.FileAccessDenied)
                {
                    throw new UnauthorizedAccessException(secondFailure.Message, secondFailure);
                }
                catch (SteamCmdRunException secondFailure)
                {
                    throw new InvalidOperationException(
                        "The existing installation update failed, and a clean download also failed. " +
                        secondFailure.Message, secondFailure);
                }
                using (var process = ServerProcess.FindAnyInstallation(location!))
                    if (process is not null)
                        throw new InvalidOperationException("Stop the server before replacing game files.");
                SteamInstallationRecovery.Activate(root, stage, branch);
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception recoveryFailure) when (recoveryFailure is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "SteamCMD could not update the existing installation (" + firstFailure.Message +
                    "). The clean-install recovery did not complete: " + recoveryFailure.Message,
                    recoveryFailure);
            }
            finally
            {
                try { SteamInstallationRecovery.DeleteStageIfPresent(root, stage); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (SteamCmdRunException failure) when (failure.FileAccessDenied)
        {
            throw new UnauthorizedAccessException(failure.Message, failure);
        }
        catch (SteamCmdRunException failure)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }
        progress.Report(AppTranslations.Get("status.server_files_ready", "Dedicated server files are ready."));
        return shipping;
    }

    private async Task RunSteamCmdAsync(string steamcmd, string root, string branch, string? betaPassword,
        IProgress<string> progress, CancellationToken cancellationToken)
    {
        var shipping = Path.Combine(root, "DeceiveInc", "Binaries", "Win64", "DeceiveIncServer-Win64-Shipping.exe");
        var start = new ProcessStartInfo(steamcmd)
        {
            WorkingDirectory = _toolsFolder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in AppUpdateArguments(root, branch, betaPassword))
            start.ArgumentList.Add(arg);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var child = Process.Start(start) ?? throw new InvalidOperationException("SteamCMD did not start.");
            using var cancellation = cancellationToken.Register(() =>
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var succeeded = false;
            var needsBootstrapRetry = false;
            string? failureLine = null;
            string? lastLine = null;
            void Record(string line)
            {
                if (!string.IsNullOrEmpty(betaPassword))
                    line = line.Replace(betaPassword, "[redacted]", StringComparison.Ordinal);
                lastLine = line;
                if (line.Contains("Missing configuration", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Localize::AddFile() failed to load file \"public/steambootstrapper_", StringComparison.OrdinalIgnoreCase))
                    needsBootstrapRetry = true;
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Failed", StringComparison.OrdinalIgnoreCase)) failureLine = line;
                if (Regex.IsMatch(line, @"progress:\s*\d", RegexOptions.IgnoreCase))
                    progress.Report(line);
            }
            var output = ConsumeAsync(child.StandardOutput, line =>
            {
                if (line.Contains($"Success! App '{ServerAppId}' fully installed.", StringComparison.Ordinal)) succeeded = true;
                Record(line);
            }, cancellationToken);
            var error = ConsumeAsync(child.StandardError, Record, cancellationToken);
            await Task.WhenAll(output, error, child.WaitForExitAsync(cancellationToken));
            if (child.ExitCode == 0 && succeeded && File.Exists(shipping) && InstalledBuildIdAtRoot(root) is not null)
                return;
            if (attempt == 0 && needsBootstrapRetry)
            {
                progress.Report(AppTranslations.Get("status.retrying_server_download", "Retrying the server download…"));
                await Task.Delay(1500, cancellationToken);
                continue;
            }
            var detail = failureLine ?? lastLine ?? $"exit code {child.ExitCode}";
            var fileAccessDenied = (detail.Contains("access denied", StringComparison.OrdinalIgnoreCase) &&
                !detail.Contains("manifest", StringComparison.OrdinalIgnoreCase) &&
                !detail.Contains("depot", StringComparison.OrdinalIgnoreCase)) ||
                detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
            throw new SteamCmdRunException("SteamCMD could not install the dedicated server: " + detail +
                ". SteamCMD logs are in " + Path.Combine(_toolsFolder, "logs") + ".", fileAccessDenied);
        }
        throw new SteamCmdRunException("SteamCMD exhausted its installation retries.", false);
    }

    private sealed class SteamCmdRunException(string message, bool fileAccessDenied) : Exception(message)
    {
        public bool FileAccessDenied { get; } = fileAccessDenied;
    }

    public static IReadOnlyList<string> AppUpdateArguments(string installRoot, string branch,
        string? betaPassword = null)
    {
        branch = ServerInstallation.ValidateBranch(branch);
        var arguments = new List<string>
        {
            "+force_install_dir", Path.GetFullPath(installRoot), "+login", "anonymous",
            "+app_update", ServerAppId.ToString()
        };
        // Explicitly reset SteamCMD's remembered beta when returning to the public branch.
        arguments.AddRange(["-beta", branch]);
        if (branch != "public" && !string.IsNullOrWhiteSpace(betaPassword))
            arguments.AddRange(["-betapassword", betaPassword]);
        arguments.AddRange(["validate", "+quit"]);
        return arguments;
    }

    public async Task<BuildCheck> CheckBuildAsync(ServerLocation location, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var manifest = Path.Combine(location.InstallRoot, "steamapps", $"appmanifest_{ServerAppId}.acf");
        if (!File.Exists(manifest)) return new(null, null, "Steam build ID unavailable for this imported server.");
        var local = InstalledBuildId(location);
        if (local is null) return new(null, null, "Installed Steam build ID could not be read.");
        var steamcmd = await EnsureInstalledAsync(progress, cancellationToken);
        var start = new ProcessStartInfo(steamcmd)
        {
            WorkingDirectory = _toolsFolder, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "+login", "anonymous", "+app_info_update", "1",
                     "+app_info_print", ServerAppId.ToString(), "+quit" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("SteamCMD did not start.");
        var output = new System.Text.StringBuilder();
        var consumeOut = ConsumeAsync(process.StandardOutput, line =>
        {
            if (output.Length < 4_000_000) output.AppendLine(line);
        }, cancellationToken);
        var consumeError = ConsumeAsync(process.StandardError, _ => { }, cancellationToken);
        await Task.WhenAll(consumeOut, consumeError, process.WaitForExitAsync(cancellationToken));
        if (process.ExitCode != 0) return new(local, null, "SteamCMD could not check the public build.");
        var latest = PublicBuildIdFromAppInfo(output.ToString());
        if (latest is null) return new(local, null, "Steam did not expose a public build ID; availability is unknown.");
        return new(local, latest, null);
    }

    public static string? PublicBuildIdFromAppInfo(string appInfo)
    {
        var branches = FindBlock(appInfo, "branches");
        var publicBranch = branches is null ? null : FindBlock(branches, "public");
        if (publicBranch is null) return null;
        var id = Regex.Match(publicBranch, "\"buildid\"\\s+\"(?<id>\\d+)\"").Groups["id"].Value;
        return id.Length == 0 ? null : id;
    }

    private static string? FindBlock(string text, string key)
    {
        var opener = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\\{");
        if (!opener.Success) return null;
        var start = opener.Index + opener.Length;
        var depth = 1;
        var quoted = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var character = text[i];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
            }
            else if (character == '"') quoted = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return text[start..i];
        }
        return null;
    }

    private static async Task ConsumeAsync(StreamReader reader, Action<string> report, CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var line = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] is '\r' or '\n')
                {
                    if (line.Length > 0) { report(line.ToString()); line.Clear(); }
                }
                else if (line.Length < 4096) line.Append(buffer[i]);
            }
        }
        if (line.Length > 0) report(line.ToString());
    }
}
