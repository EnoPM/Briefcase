using System.Diagnostics;
using System.IO.Compression;

namespace Briefcase.Updater;

/// <summary>
/// Applies a verified release after the parent game process has stopped.
/// Only framework-owned paths are replaced; user configuration and mods are
/// intentionally outside the transaction.
/// </summary>
public static class UpdateInstallerEngine
{
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromMinutes(2);

    public static void WaitForParent(int parentProcessId, TextWriter log)
    {
        if (parentProcessId <= 0 || parentProcessId == Environment.ProcessId) return;
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            log.WriteLine($"Waiting for process {parentProcessId} to exit...");
            if (!parent.WaitForExit((int)ParentExitTimeout.TotalMilliseconds))
                throw new TimeoutException(
                    $"Process {parentProcessId} did not exit within {ParentExitTimeout.TotalMinutes:0} minutes.");
        }
        catch (ArgumentException)
        {
            // The parent exited before the installer opened its process handle.
        }
    }

    public static void Apply(UpdateInstallRequest request, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(log);
        var packageKind = ValidateRequest(request);
        ReleaseArchiveValidator.Validate(
            request.ArchivePath, request.Version, packageKind);

        var installRoot = Path.GetFullPath(request.InstallRoot);
        var transactionRoot = Path.Combine(
            installRoot, $".briefcase-update-{Guid.NewGuid():N}");
        var payloadRoot = Path.Combine(transactionRoot, "payload");
        var backupRoot = Path.Combine(transactionRoot, "backup");
        Directory.CreateDirectory(payloadRoot);
        Directory.CreateDirectory(backupRoot);

        var completed = false;
        try
        {
            ExtractPayload(request.ArchivePath, payloadRoot, packageKind);
            ApplyTransaction(installRoot, payloadRoot, backupRoot, packageKind, log);
            completed = true;
            log.WriteLine($"Briefcase {request.Version} installed successfully.");
        }
        finally
        {
            if (completed)
                TryDeleteDirectory(transactionRoot, log);
            else
                log.WriteLine($"Recovery files were preserved at {transactionRoot}.");
        }
    }

    public static void Restart(UpdateInstallRequest request, TextWriter log)
    {
        if (!request.Restart) return;
        Thread.Sleep(500);

        if (string.Equals(request.RestartMode, "steam", StringComparison.OrdinalIgnoreCase))
        {
            if (request.PackageKind != nameof(BriefcasePackageKind.Client) ||
                string.IsNullOrWhiteSpace(request.SteamAppId) ||
                request.SteamAppId.Any(character => character is < '0' or > '9'))
                throw new InvalidDataException("The Steam restart request is invalid.");

            var uri = $"steam://rungameid/{request.SteamAppId}";
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            log.WriteLine($"Requested client restart through Steam ({request.SteamAppId}).");
            return;
        }

        if (!string.Equals(request.RestartMode, "executable", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported restart mode '{request.RestartMode}'.");

        var executable = Path.GetFullPath(request.RestartExecutable);
        var installRoot = Path.GetFullPath(request.InstallRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The restart executable is outside the installation directory.");
        var expectedName = request.PackageKind == nameof(BriefcasePackageKind.Client)
            ? "DeceiveInc-Win64-Shipping.exe"
            : "DeceiveIncServer-Win64-Shipping.exe";
        if (!string.Equals(Path.GetFileName(executable), expectedName,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unexpected restart executable '{Path.GetFileName(executable)}'.");

        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.InstallRoot,
            UseShellExecute = false
        };
        foreach (var argument in request.RestartArguments) start.ArgumentList.Add(argument);
        var process = Process.Start(start) ??
                      throw new InvalidOperationException(
                          "Windows did not restart Deceive Inc.");
        log.WriteLine($"Restarted {expectedName} (PID={process.Id}).");
    }

    private static BriefcasePackageKind ValidateRequest(UpdateInstallRequest request)
    {
        if (request.SchemaVersion != UpdateInstallRequest.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported update request schema {request.SchemaVersion}.");
        if (!ReleaseVersion.TryParse(request.Version, out _, out var normalized) ||
            !string.Equals(normalized, request.Version, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid update version '{request.Version}'.");
        if (!Enum.TryParse<BriefcasePackageKind>(
                request.PackageKind, ignoreCase: false, out var packageKind))
            throw new InvalidDataException(
                $"Invalid package kind '{request.PackageKind}'.");

        var installRoot = Path.GetFullPath(request.InstallRoot);
        if (!Directory.Exists(installRoot) ||
            !Directory.Exists(Path.Combine(installRoot, "Briefcase", "Core")) ||
            !File.Exists(Path.Combine(installRoot, "Briefcase", "VERSION")))
            throw new InvalidDataException(
                "The update target is not an existing Briefcase installation.");
        if (!File.Exists(Path.GetFullPath(request.ArchivePath)))
            throw new FileNotFoundException(
                "The downloaded update archive is missing.", request.ArchivePath);

        var installedText = File.ReadAllText(
            Path.Combine(installRoot, "Briefcase", "VERSION")).Trim();
        if (!ReleaseVersion.TryParse(installedText, out var installedVersion, out _) ||
            !ReleaseVersion.TryParse(request.Version, out var updateVersion, out _) ||
            updateVersion <= installedVersion)
            throw new InvalidDataException(
                $"Briefcase {request.Version} is not newer than the installed version '{installedText}'.");
        return packageKind;
    }

    private static void ExtractPayload(
        string archivePath,
        string payloadRoot,
        BriefcasePackageKind packageKind)
    {
        var payloadPrefix = Path.GetFullPath(payloadRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var normalized = ReleaseArchiveValidator.NormalizeEntryName(entry.FullName);
            if (!ReleaseArchiveValidator.IsInstallablePath(normalized, packageKind) ||
                normalized.EndsWith('/'))
                continue;

            var destination = Path.GetFullPath(Path.Combine(
                payloadRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Release path escapes the transaction directory: '{normalized}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void ApplyTransaction(
        string installRoot,
        string payloadRoot,
        string backupRoot,
        BriefcasePackageKind packageKind,
        TextWriter log)
    {
        var directoryReplacement = new Replacement(
            Path.Combine(payloadRoot, "Briefcase", "Core"),
            Path.Combine(installRoot, "Briefcase", "Core"),
            Path.Combine(backupRoot, "Briefcase", "Core"),
            IsDirectory: true,
            Required: true);
        var replacements = new List<Replacement>
        {
            directoryReplacement,
            FileReplacement("version.dll", required: true),
            FileReplacement(Path.Combine("Briefcase", "VERSION"), required: true),
            FileReplacement("LICENSE", required: false),
            FileReplacement("README-Briefcase.txt", required: false)
        };
        if (packageKind == BriefcasePackageKind.Server)
            replacements.Add(FileReplacement("StartBriefcaseServer.bat", required: false));

        var applied = new List<AppliedReplacement>();
        try
        {
            foreach (var replacement in replacements)
            {
                if (!Exists(replacement.Source, replacement.IsDirectory))
                {
                    if (replacement.Required)
                        throw new InvalidDataException(
                            $"The extracted update is missing '{replacement.Source}'.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(replacement.Backup)!);
                var hadPrevious = Exists(replacement.Destination, replacement.IsDirectory);
                if (hadPrevious)
                    Move(replacement.Destination, replacement.Backup, replacement.IsDirectory);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(replacement.Destination)!);
                    Move(replacement.Source, replacement.Destination, replacement.IsDirectory);
                    applied.Add(new AppliedReplacement(replacement, hadPrevious));
                    log.WriteLine(
                        $"Updated {Path.GetRelativePath(installRoot, replacement.Destination)}.");
                }
                catch
                {
                    if (hadPrevious && Exists(replacement.Backup, replacement.IsDirectory))
                        Move(replacement.Backup, replacement.Destination, replacement.IsDirectory);
                    throw;
                }
            }
        }
        catch
        {
            log.WriteLine("Update failed; restoring the previous Briefcase files.");
            foreach (var item in applied.AsEnumerable().Reverse())
            {
                TryDelete(item.Replacement.Destination, item.Replacement.IsDirectory);
                if (item.HadPrevious &&
                    Exists(item.Replacement.Backup, item.Replacement.IsDirectory))
                    Move(
                        item.Replacement.Backup,
                        item.Replacement.Destination,
                        item.Replacement.IsDirectory);
            }
            throw;
        }

        Replacement FileReplacement(string relativePath, bool required) => new(
            Path.Combine(payloadRoot, relativePath),
            Path.Combine(installRoot, relativePath),
            Path.Combine(backupRoot, relativePath),
            IsDirectory: false,
            Required: required);
    }

    private static void Move(string source, string destination, bool directory)
    {
        Retry(() =>
        {
            if (directory) Directory.Move(source, destination);
            else File.Move(source, destination);
        });
    }

    private static void Retry(Action action)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException exception)
            {
                last = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                last = exception;
            }
            Thread.Sleep(250);
        }
        throw new IOException("Could not replace a Briefcase file after 10 seconds.", last);
    }

    private static bool Exists(string path, bool directory) =>
        directory ? Directory.Exists(path) : File.Exists(path);

    private static void TryDelete(string path, bool directory)
    {
        try
        {
            if (directory)
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Rollback will still restore every path it can.
        }
    }

    private static void TryDeleteDirectory(string path, TextWriter log)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            log.WriteLine($"Could not remove update transaction files: {exception.Message}");
        }
    }

    private sealed record Replacement(
        string Source,
        string Destination,
        string Backup,
        bool IsDirectory,
        bool Required);

    private sealed record AppliedReplacement(
        Replacement Replacement,
        bool HadPrevious);
}
