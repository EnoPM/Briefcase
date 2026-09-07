namespace Briefcase.Core.Tests;

internal static class RepositoryPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VERSION")) &&
                File.Exists(Path.Combine(directory.FullName, "Briefcase.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the Briefcase repository root.");
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "Briefcase.Tests", Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal sealed class TemporaryFile : IDisposable
{
    public string Path { get; }

    private TemporaryFile(string contents, string extension)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Briefcase.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "input" + extension);
        File.WriteAllText(Path, contents);
    }

    public static TemporaryFile Json(string contents) => new(contents, ".json");

    public void Dispose()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
