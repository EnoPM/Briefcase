using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

/// <summary>A safe identity for a world observed by the game-thread pump.</summary>
public readonly record struct UnrealWorldInfo(UnrealObjectHandle Handle, string Path);

/// <summary>One bounded scheduler pulse executed on Unreal's game thread.</summary>
public readonly record struct GameThreadTick(
    float DeltaSeconds,
    ulong Sequence,
    uint ThreadId,
    UnrealWorldInfo? World);

/// <summary>
/// Schedules managed work on Unreal's game thread. Every callback belongs to
/// the current mod scope and is removed automatically before that mod unloads.
/// Returned IDisposable tokens are still useful when work should stop earlier.
/// </summary>
public readonly struct GameThreadApi
{
    private readonly IGameThreadScope? _scope;

    internal GameThreadApi(IGameThreadScope scope) => _scope = scope;

    public bool IsAvailable => _scope?.IsAvailable == true;
    public bool IsGameThread => _scope?.IsGameThread == true;
    public bool IsEngineReady => _scope?.IsEngineReady == true;
    public UnrealWorldInfo? CurrentWorld => _scope?.CurrentWorld;

    public IDisposable Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.Post(action);
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsGameThread)
        {
            action();
            return;
        }
        InvokeAsync(action).GetAwaiter().GetResult();
    }

    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return IsGameThread ? action() : InvokeAsync(action).GetAwaiter().GetResult();
    }

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.InvokeAsync(action, cancellationToken);
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.InvokeAsync(action, cancellationToken);
    }

    public Task NextTickAsync(CancellationToken cancellationToken = default) =>
        Scope.NextTickAsync(cancellationToken);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) =>
        Scope.DelayAsync(delay, cancellationToken);

    public IDisposable RunAfter(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.RunAfter(delay, action);
    }

    public IDisposable RunEvery(TimeSpan interval, Action<GameThreadTick> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.RunEvery(interval, action);
    }

    public IDisposable OnTick(Action<GameThreadTick> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.OnTick(action);
    }

    public IDisposable OnEngineReady(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.OnEngineReady(action);
    }

    public IDisposable OnWorldCreated(Action<UnrealWorldInfo> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.OnWorldCreated(action);
    }

    public IDisposable OnWorldDestroyed(Action<UnrealWorldInfo> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.OnWorldDestroyed(action);
    }

    public IDisposable OnMapLoaded(Action<UnrealWorldInfo> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Scope.OnMapLoaded(action);
    }

    private IGameThreadScope Scope => _scope ??
        throw new InvalidOperationException("The Briefcase game-thread service is unavailable.");
}

internal interface IGameThreadScope : IDisposable
{
    bool IsAvailable { get; }
    bool IsGameThread { get; }
    bool IsEngineReady { get; }
    UnrealWorldInfo? CurrentWorld { get; }
    IDisposable Post(Action action);
    Task InvokeAsync(Action action, CancellationToken cancellationToken);
    Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken);
    Task NextTickAsync(CancellationToken cancellationToken);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    IDisposable RunAfter(TimeSpan delay, Action action);
    IDisposable RunEvery(TimeSpan interval, Action<GameThreadTick> action);
    IDisposable OnTick(Action<GameThreadTick> action);
    IDisposable OnEngineReady(Action action);
    IDisposable OnWorldCreated(Action<UnrealWorldInfo> action);
    IDisposable OnWorldDestroyed(Action<UnrealWorldInfo> action);
    IDisposable OnMapLoaded(Action<UnrealWorldInfo> action);
}
