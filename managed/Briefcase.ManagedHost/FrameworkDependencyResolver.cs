using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Briefcase.ManagedHost;

/// <summary>
/// Resolves framework-owned NuGet dependencies from one explicit directory.
/// Briefcase assemblies remain in Core so their identity is obvious, while
/// external managed and native libraries live in Core/ThirdPartyLibraries.
/// </summary>
internal static class FrameworkDependencyResolver
{
    private static readonly object Sync = new();
    private static AssemblyLoadContext? _context;
    private static string? _directory;

    public static void Attach(string coreDirectory)
    {
        var directory = Path.GetFullPath(
            Path.Combine(coreDirectory, "ThirdPartyLibraries"));
        lock (Sync)
        {
            if (_context is not null &&
                string.Equals(_directory, directory, StringComparison.OrdinalIgnoreCase))
                return;

            if (_context is not null)
            {
                _context.Resolving -= ResolveManaged;
                _context.ResolvingUnmanagedDll -= ResolveUnmanaged;
            }

            _directory = directory;
            _context = AssemblyLoadContext.GetLoadContext(typeof(EntryPoint).Assembly) ??
                       AssemblyLoadContext.Default;
            _context.Resolving += ResolveManaged;
            _context.ResolvingUnmanagedDll += ResolveUnmanaged;
        }
    }

    internal static string? FindManagedLibrary(
        string directory, AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        return IsSimpleFileName(name)
            ? ExistingFile(directory, name + ".dll")
            : null;
    }

    internal static string? FindNativeLibrary(string directory, string libraryName)
    {
        if (!IsSimpleFileName(libraryName)) return null;
        var exact = ExistingFile(directory, libraryName);
        if (exact is not null) return exact;
        return Path.HasExtension(libraryName)
            ? null
            : ExistingFile(directory, libraryName + ".dll");
    }

    private static Assembly? ResolveManaged(
        AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var directory = Volatile.Read(ref _directory);
        var path = directory is null
            ? null
            : FindManagedLibrary(directory, assemblyName);
        return path is null ? null : context.LoadFromAssemblyPath(path);
    }

    private static nint ResolveUnmanaged(Assembly assembly, string libraryName)
    {
        var directory = Volatile.Read(ref _directory);
        var path = directory is null
            ? null
            : FindNativeLibrary(directory, libraryName);
        return path is null ? 0 : NativeLibrary.Load(path);
    }

    private static string? ExistingFile(string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        return File.Exists(path) ? path : null;
    }

    private static bool IsSimpleFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
}