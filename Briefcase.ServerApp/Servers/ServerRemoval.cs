namespace Briefcase.ServerApp;

public static class ServerRemoval
{
    public static async Task DeleteFilesAsync(ServerLocation location)
    {
        using (var running = ServerProcess.Find(location))
            if (running is not null) await ServerProcess.StopAsync(location, CancellationToken.None);
        using (var running = ServerProcess.Find(location))
            if (running is not null) throw new InvalidOperationException("The server is still running; its files were retained.");
        await Task.Run(() => DeleteValidated(location));
    }

    private static void DeleteValidated(ServerLocation location)
    {
        var root = Path.GetFullPath(location.IsInstance ? location.InstanceRoot : location.InstallRoot);
        var info = new DirectoryInfo(root);
        if (location.IsInstance &&
            (!info.Name.Equals(location.InstanceId, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(info.Parent?.Name, "servers", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The instance directory is outside the expected servers folder.");
        if (info.Parent is null || info.FullName == info.Root.FullName ||
            !File.Exists(location.Shipping))
            throw new InvalidOperationException("The selected directory is not a complete server installation. Remove this entry from the list instead.");
        if (AppContext.BaseDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The application is inside this server directory and cannot delete itself.");
        for (var part = info; part is not null; part = part.Parent)
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The installation path contains a link; deletion was cancelled.");
        var pending = new Stack<DirectoryInfo>();
        pending.Push(info);
        while (pending.TryPop(out var directory))
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("The installation contains a link; deletion was cancelled.");
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        Directory.Delete(root, recursive: true);
    }
}
