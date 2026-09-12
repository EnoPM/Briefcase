using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

/// <summary>
/// One strong Unreal reference owned by the current mod. Disposing the lease
/// releases it early; otherwise Briefcase releases it automatically when the
/// mod is unloaded or reloaded.
/// </summary>
public sealed class UnrealObjectLease<T> : IDisposable
    where T : UnrealObject, IUnrealObject<T>
{
    private IUnrealLeaseLifetime? _lifetime;

    internal UnrealObjectLease(T value, IUnrealLeaseLifetime lifetime) =>
        (Value, _lifetime) = (value, lifetime);

    public T Value { get; }
    public bool IsDisposed => Volatile.Read(ref _lifetime) is not { IsDisposed: false };

    public void Dispose() => Interlocked.Exchange(ref _lifetime, null)?.Dispose();
}

/// <summary>
/// Mod-scoped loading and lifetime management for Unreal assets. Loading and
/// retaining must run on Unreal's game thread. Releasing is safe during the
/// host's automatic unload sequence.
/// </summary>
public readonly struct UnrealAssetApi
{
    private readonly IUnrealAssetScope? _scope;

    internal UnrealAssetApi(IUnrealAssetScope? scope) => _scope = scope;

    public bool IsAvailable => _scope?.IsAvailable == true;

    /// <summary>
    /// Loads an object from a canonical soft-object path, validates its
    /// generated class and roots it until the returned lease is disposed.
    /// </summary>
    public UnrealObjectLease<T> Load<T>(string path, UnrealClass<T> expectedClass)
        where T : UnrealObject, IUnrealObject<T>
    {
        var scope = Scope;
        var handle = scope.Load(path, expectedClass.Path, out var lifetime);
        try
        {
            return new UnrealObjectLease<T>(
                T.FromObject(scope.Unreal, handle), lifetime);
        }
        catch
        {
            lifetime.Dispose();
            throw;
        }
    }

    /// <summary>Returns false only when the asset path is absent.</summary>
    public bool TryLoad<T>(
        string path,
        UnrealClass<T> expectedClass,
        out UnrealObjectLease<T>? lease)
        where T : UnrealObject, IUnrealObject<T>
    {
        try
        {
            lease = Load(path, expectedClass);
            return true;
        }
        catch (UnrealApiException exception)
            when (exception.Result == Interop.NativeUnrealResult.NotFound)
        {
            lease = null;
            return false;
        }
    }

    /// <summary>
    /// Keeps an already-loaded UObject alive. This is useful when a game API
    /// returns an object that is not otherwise held by a durable Unreal owner.
    /// </summary>
    public UnrealObjectLease<T> Retain<T>(T value)
        where T : UnrealObject, IUnrealObject<T>
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IsNull)
            throw new ArgumentException("The Unreal object is null.", nameof(value));
        return new UnrealObjectLease<T>(value, Scope.Retain(value.Handle));
    }

    private IUnrealAssetScope Scope => _scope ??
        throw new InvalidOperationException(
            "The Briefcase Unreal asset service is unavailable for this mod.");
}

internal interface IUnrealAssetScope : IDisposable
{
    bool IsAvailable { get; }
    UnrealApi Unreal { get; }
    UnrealObjectHandle Load(
        string path, string expectedClassPath, out IUnrealLeaseLifetime lifetime);
    IUnrealLeaseLifetime Retain(UnrealObjectHandle handle);
}

internal interface IUnrealLeaseLifetime : IDisposable
{
    bool IsDisposed { get; }
}
