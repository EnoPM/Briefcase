using Briefcase.ModApi;

namespace Briefcase.ClientModApi;

/// <summary>
/// Entry point for client-only configuration panels. The assembly containing
/// this API is absent from Briefcase's dedicated-server distribution.
/// </summary>
public readonly struct ClientUiApi
{
    private readonly IClientUiScope? _scope;

    internal ClientUiApi(IClientUiScope? scope) => _scope = scope;

    public bool IsAvailable => _scope is not null;

    /// <summary>
    /// Adds toolkit-neutral content to this mod's detail view inside the
    /// framework-owned Mods page.
    /// </summary>
    public IDisposable RegisterPanel(UiComponent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Scope().RegisterPanel(content);
    }

    /// <summary>
    /// Adds toolkit-neutral content to Briefcase's Servers workspace. The panel
    /// still runs in the game client; it is never loaded by the server.
    /// </summary>
    public IDisposable RegisterServerPanel(string name, UiComponent content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        return Scope().RegisterServerPanel(name.Trim(), content);
    }

    private IClientUiScope Scope() => _scope ?? throw new InvalidOperationException(
        "The Briefcase client UI is unavailable in this mod context. " +
        "This capability is supplied only to client mods.");
}

/// <summary>Client-only extensions for a managed Briefcase mod context.</summary>
public static class ClientModContextExtensions
{
    /// <summary>
    /// Returns the UI capability scoped to the current client mod. Calling
    /// this from a headless/server host throws before a panel can be registered.
    /// </summary>
    public static ClientUiApi Ui(this ModContext context) =>
        new(context.GetExtension<IClientUiScope>());
}

internal interface IClientUiScope
{
    IDisposable RegisterPanel(UiComponent content);
    IDisposable RegisterServerPanel(string name, UiComponent content);
}
