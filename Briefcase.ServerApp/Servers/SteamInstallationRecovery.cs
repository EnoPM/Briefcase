namespace Briefcase.ServerApp;

/// <summary>Replaces a failed SteamCMD update with a verified fresh download on the same volume.</summary>
public static class SteamInstallationRecovery
{
    private const string StageSuffix = ".briefcase-stage-";
    private const string BackupSuffix = ".briefcase-backup-";

    public static string NewStagePath(string installRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot)) + StageSuffix + Guid.NewGuid().ToString("N");

    public static void Activate(string installRoot, string stageRoot, string branch)
    {
        branch = ServerInstallation.ValidateBranch(branch);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var stage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageRoot));
        ValidateStage(root, stage);
        if (!Directory.Exists(root) || !Directory.Exists(stage) ||
            SteamCmdService.InstalledBuildIdAtRoot(stage) is null ||
            !File.Exists(Path.Combine(stage, "DeceiveInc", "Binaries", "Win64", "DeceiveIncServer-Win64-Shipping.exe")))
            throw new InvalidDataException("The clean Steam download is incomplete; the old server was left intact.");
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            new DirectoryInfo(stage).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Server installation directories cannot be links.");

        var backup = root + BackupSuffix + Guid.NewGuid().ToString("N");
        ValidateSibling(root, backup, BackupSuffix);
        CopyLocalData(root, stage);
        File.WriteAllText(Path.Combine(stage, ".briefcase-steam-branch"), branch);
        Directory.Move(root, backup);
        try { Directory.Move(stage, root); }
        catch
        {
            Directory.Move(backup, root);
            throw;
        }
        // The new installation is active. A locked backup may remain for manual cleanup.
        try { Directory.Delete(backup, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    public static void DeleteStageIfPresent(string installRoot, string stageRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var stage = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stageRoot));
        ValidateStage(root, stage);
        if (Directory.Exists(stage) && !new DirectoryInfo(stage).Attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(stage, recursive: true);
    }

    private static void ValidateStage(string root, string stage) => ValidateSibling(root, stage, StageSuffix);

    private static void ValidateSibling(string root, string candidate, string suffix)
    {
        var parent = Path.GetDirectoryName(root);
        if (parent is null || root == Path.GetPathRoot(root) ||
            !string.Equals(parent, Path.GetDirectoryName(candidate), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(candidate).StartsWith(Path.GetFileName(root) + suffix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(candidate)[(Path.GetFileName(root).Length + suffix.Length)..], "N", out _))
            throw new InvalidDataException("The temporary Steam directory is outside the installation folder.");
    }

    private static void CopyLocalData(string root, string stage)
    {
        foreach (var relative in new[]
        {
            Path.Combine("DeceiveInc", "Saved"),
            Path.Combine("DeceiveInc", "Binaries", "Win64", "Briefcase"),
            Path.Combine("DeceiveInc", "Binaries", "Win64", "ue4ss")
        }) CopyTreeIfPresent(Path.Combine(root, relative), Path.Combine(stage, relative));
        foreach (var relative in new[]
        {
            Path.Combine("DeceiveInc", "CommunityBalanceProfile.json"),
            Path.Combine("DeceiveInc", "Binaries", "Win64", "version.dll"),
            Path.Combine("DeceiveInc", "Binaries", "Win64", "version.dll.disabled")
        })
        {
            var source = Path.Combine(root, relative);
            if (!File.Exists(source)) continue;
            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Local server files cannot be links.");
            var target = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
    }

    private static void CopyTreeIfPresent(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        if (new DirectoryInfo(source).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Local server directories cannot be links.");
        Directory.CreateDirectory(target);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Local server files cannot be links.");
            var destination = Path.Combine(target, entry.Name);
            if (entry is DirectoryInfo)
                CopyTreeIfPresent(entry.FullName, destination);
            else if (entry is FileInfo)
                File.Copy(entry.FullName, destination, overwrite: true);
        }
    }
}
