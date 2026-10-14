using System.Security.Cryptography;

namespace Briefcase.ServerApp;

/// <summary>
/// Gives each Linux instance its own project directory. Unreal reads TripwireServer.ini
/// from that directory and ignores -TripwireServerIni; large Steam files stay shared.
/// </summary>
internal static class LinuxGameView
{
    private const string ExecutableName = "DeceiveIncServer-Linux-Shipping";
    private const string SharedRootMarker = ".briefcase-shared-root";

    internal static string Root(ServerLocation location) => Path.Combine(location.InstanceRoot, "GameView");
    internal static string Executable(ServerLocation location) =>
        Path.Combine(Root(location), "DeceiveInc", "Binaries", "Linux", ExecutableName);

    internal static bool BelongsTo(ServerLocation location, string instanceRoot)
    {
        try
        {
            var marker = Path.Combine(instanceRoot, "GameView", SharedRootMarker);
            return File.Exists(marker) &&
                Path.GetFullPath(File.ReadAllText(marker).Trim()) == Path.GetFullPath(location.InstallRoot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    internal static string Prepare(ServerLocation location)
    {
        if (!OperatingSystem.IsLinux() || !location.IsInstance)
            throw new PlatformNotSupportedException("A managed Linux instance is required.");
        var source = Path.GetFullPath(location.InstallRoot);
        var root = Root(location);
        var project = Path.Combine(root, "DeceiveInc");
        var engine = Path.Combine(root, "Engine");
        var binaries = Path.Combine(project, "Binaries", "Linux");
        foreach (var directory in new[] { location.InstanceRoot, root, project, engine,
                     Path.Combine(project, "Binaries"), binaries, Path.Combine(project, "Saved"),
                     Path.Combine(engine, "Saved") })
            EnsureDirectory(directory);

        foreach (var name in new[] { "steamclient.so", "libsteamwebrtc.so", "linux64" })
            EnsureLink(Path.Combine(root, name), Path.Combine(source, name));
        foreach (var name in new[] { "Binaries", "Content", "Programs" })
            EnsureLink(Path.Combine(engine, name), Path.Combine(source, "Engine", name));
        foreach (var name in new[] { "Content", "Plugins", "Community Balance Template" })
            EnsureLink(Path.Combine(project, name), Path.Combine(source, "DeceiveInc", name));
        EnsureLink(Path.Combine(binaries, "libEOSSDK-Linux-Shipping.so"),
            Path.Combine(source, "DeceiveInc", "Binaries", "Linux", "libEOSSDK-Linux-Shipping.so"));

        var credentials = Path.Combine(source, "DeceiveInc", "Saved", "GameServerCredentials.json");
        var privateCredentials = Path.Combine(project, "Saved", "GameServerCredentials.json");
        if (File.Exists(credentials) && !File.Exists(privateCredentials))
            File.Copy(credentials, privateCredentials);
        CopyPlain(location.ConfigFile, Path.Combine(project, "TripwireServer.ini"));
        CopyPlain(location.BalanceProfileFile, Path.Combine(project, "CommunityBalanceProfile.json"));
        CopyPlain(location.BalanceTemplateFile,
            Path.Combine(location.InstanceRoot, "CommunityBalanceProfile.default.json"));

        var game = Executable(location);
        var shared = location.Shipping;
        if (Exists(game))
        {
            if (File.GetAttributes(game).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The instance game executable cannot be a link.");
            if (Hash(game).AsSpan().SequenceEqual(Hash(shared)))
            {
                WriteMarker(root, source);
                return game;
            }
        }
        var temporary = game + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(shared, temporary);
            File.SetUnixFileMode(temporary, File.GetUnixFileMode(shared));
            File.Move(temporary, game, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        WriteMarker(root, source);
        return game;
    }

    private static void EnsureDirectory(string path)
    {
        if (Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The Linux instance contains a redirected game directory.");
        Directory.CreateDirectory(path);
    }

    private static void EnsureLink(string path, string target)
    {
        if (!Exists(target)) return;
        if (Exists(path))
        {
            var actual = new FileInfo(path).LinkTarget;
            if (actual is null || Path.GetFullPath(actual) != Path.GetFullPath(target))
                throw new InvalidDataException("The Linux instance game view contains an unexpected file: " + path);
            return;
        }
        if (Directory.Exists(target)) Directory.CreateSymbolicLink(path, target);
        else File.CreateSymbolicLink(path, target);
    }

    private static void CopyPlain(string source, string target)
    {
        if (!File.Exists(source) || File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint) ||
            Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The instance configuration is missing or redirected.");
        var temporary = target + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteMarker(string root, string source)
    {
        var marker = Path.Combine(root, SharedRootMarker);
        if (Exists(marker) && File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The Linux instance game view marker is redirected.");
        File.WriteAllText(marker, source + "\n");
    }

    private static byte[] Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return SHA256.HashData(stream);
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
