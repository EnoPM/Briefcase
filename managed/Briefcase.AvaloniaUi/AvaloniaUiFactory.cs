using System.Reflection;
using System.Runtime.Loader;
using Briefcase.ManagedHost;
using Briefcase.Rendering;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Reflection entry point loaded by Briefcase.Rendering. This assembly owns only
/// the startup/menu window; the configuration content is another lazy module.
/// </summary>
public static class AvaloniaUiFactory
{
    public static object Create(
        nint gameWindow,
        object configuration,
        Action<bool> visibilityChanged,
        Func<AvaloniaWindowPlacement>? loadPlacement,
        Action<AvaloniaWindowPlacement>? savePlacement,
        Action<string> info,
        Action<string> error)
    {
        ConfigurationRegistry registry;
        FrameworkStartupProgress startup;
        var recreateMenuOnClose = false;
        var margins = AvaloniaMenuMargins.Default;
        switch (configuration)
        {
            case AvaloniaUiState state:
                registry = state.Configuration;
                startup = state.StartupProgress;
                recreateMenuOnClose = state.MenuLifetime == AvaloniaMenuLifetime.PerOpen;
                margins = state.MenuMargins;
                break;
            case ConfigurationRegistry legacyRegistry:
                registry = legacyRegistry;
                startup = new FrameworkStartupProgress();
                startup.Complete();
                break;
            default:
                throw new ArgumentException(
                    "The Avalonia UI received an incompatible configuration model.",
                    nameof(configuration));
        }

        return new AvaloniaOverlayHost(
            gameWindow,
            () => CreateMenu(registry),
            visibilityChanged,
            loadPlacement,
            savePlacement,
            info,
            error,
            startup,
            recreateMenuOnClose,
            margins.HorizontalPercent,
            margins.VerticalPercent);
    }

    private static object CreateMenu(ConfigurationRegistry registry)
    {
        const string assemblyName = "Briefcase.AvaloniaMenu";
        var uiAssembly = typeof(AvaloniaUiFactory).Assembly;
        var loadContext = AssemblyLoadContext.GetLoadContext(uiAssembly) ??
                          AssemblyLoadContext.Default;
        var assembly = loadContext.Assemblies.FirstOrDefault(candidate =>
            string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
        if (assembly is null)
        {
            var directory = Path.GetDirectoryName(uiAssembly.Location) ??
                            throw new InvalidOperationException(
                                "The Avalonia overlay assembly has no installation directory.");
            var path = Path.Combine(directory, assemblyName + ".dll");
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    "The Briefcase Avalonia menu module is not installed.", path);
            assembly = loadContext.LoadFromAssemblyPath(path);
        }

        var factoryType = assembly.GetType(
            "Briefcase.AvaloniaMenu.AvaloniaMenuFactory", throwOnError: true)!;
        var factory = factoryType.GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(factoryType.FullName, "Create");
        return factory.Invoke(null, [registry]) ??
               throw new InvalidOperationException("The Avalonia menu factory returned null.");
    }
}
