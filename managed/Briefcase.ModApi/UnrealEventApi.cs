namespace Briefcase.ModApi;

/// <summary>
/// A pointer-free snapshot of one Unreal delegate invocation. Values are copied
/// while ProcessEvent owns the native parameter buffer, so handlers may safely
/// retain this object after returning.
/// </summary>
public sealed class UnrealEventArguments
{
    private readonly IReadOnlyDictionary<string, object?> _values;

    internal UnrealEventArguments(IReadOnlyDictionary<string, object?> values) =>
        _values = values;

    public int Count => _values.Count;
    public IEnumerable<string> Names => _values.Keys;
    public object? this[string name] => _values[name];

    public T Get<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_values.TryGetValue(name, out var value))
            throw new KeyNotFoundException($"The Unreal event has no parameter named '{name}'.");
        if (value is T typed) return typed;
        throw new InvalidCastException(
            $"Unreal event parameter '{name}' is {value?.GetType().FullName ?? "null"}, " +
            $"not {typeof(T).FullName}.");
    }

    public bool TryGet<T>(string name, out T value)
    {
        if (_values.TryGetValue(name, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default!;
        return false;
    }
}

/// <summary>
/// Mod-scoped access to dynamic multicast delegates exposed by the generated
/// SDK. Briefcase automatically disposes every subscription before hot reload.
/// </summary>
public readonly struct UnrealEventApi
{
    private readonly IUnrealEventScope? _scope;
    internal UnrealEventApi(IUnrealEventScope scope) => _scope = scope;

    public bool IsAvailable => _scope?.IsAvailable == true;

    public IDisposable Subscribe<TSource>(
        TSource source,
        UnrealProperty<UnrealMulticastDelegate> unrealEvent,
        Action<TSource, UnrealEventArguments> handler)
        where TSource : UnrealObject
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        return Scope.Subscribe(source, unrealEvent, handler);
    }

    private IUnrealEventScope Scope => _scope ??
        throw new InvalidOperationException("The Briefcase Unreal event service is unavailable.");
}

internal interface IUnrealEventScope : IDisposable
{
    bool IsAvailable { get; }
    IDisposable Subscribe<TSource>(
        TSource source,
        UnrealProperty<UnrealMulticastDelegate> unrealEvent,
        Action<TSource, UnrealEventArguments> handler)
        where TSource : UnrealObject;
}
