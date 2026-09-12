using Briefcase.ManagedHost;

namespace Briefcase.AvaloniaMenu;

/// <summary>
/// Reflection boundary used by the lightweight Avalonia overlay assembly.
/// The complete configuration tree is created only when the menu is opened.
/// </summary>
public static class AvaloniaMenuFactory
{
    public static object Create(object configuration)
    {
        if (configuration is not ConfigurationRegistry registry)
            throw new ArgumentException(
                "The Avalonia menu received an incompatible configuration model.",
                nameof(configuration));
        return new AvaloniaConfigurationView(registry);
    }
}
