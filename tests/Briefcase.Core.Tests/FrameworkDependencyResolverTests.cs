using System.Reflection;
using Briefcase.ManagedHost;

namespace Briefcase.Core.Tests;

public sealed class FrameworkDependencyResolverTests
{
    [Fact]
    public void Managed_library_is_resolved_from_the_explicit_directory()
    {
        using var directory = new TemporaryDirectory();
        var library = Path.Combine(directory.Path, "Example.Library.dll");
        File.WriteAllBytes(library, [0]);

        Assert.Equal(library, FrameworkDependencyResolver.FindManagedLibrary(
            directory.Path, new AssemblyName("Example.Library")));
    }

    [Fact]
    public void Native_library_name_can_omit_the_dll_extension()
    {
        using var directory = new TemporaryDirectory();
        var library = Path.Combine(directory.Path, "example-native.dll");
        File.WriteAllBytes(library, [0]);

        Assert.Equal(library, FrameworkDependencyResolver.FindNativeLibrary(
            directory.Path, "example-native"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("subdirectory/library")]
    public void Library_names_cannot_escape_the_third_party_directory(string name)
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(FrameworkDependencyResolver.FindManagedLibrary(
            directory.Path, new AssemblyName(name.Replace('/', '.'))));
        Assert.Null(FrameworkDependencyResolver.FindNativeLibrary(directory.Path, name));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Briefcase.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}