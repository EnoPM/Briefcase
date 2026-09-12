using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.ManagedHost;

internal readonly record struct GameThreadPump(
    uint ThreadId,
    ulong Sequence,
    float DeltaSeconds,
    UnrealObjectHandle CurrentWorld);

internal interface IGameThreadDriver : IDisposable
{
    bool IsAvailable { get; }
    bool IsGameThread { get; }
    bool TryStart(Action<GameThreadPump> callback);
    void RequestPump();
}

/// <summary>
/// Process-wide scheduler. Native code only supplies a verified game-thread
/// pulse; queueing, timers, lifecycle policy and mod ownership remain managed.
/// </summary>
internal sealed class GameThreadService : IDisposable
{
    private readonly object _gate = new();
    private readonly object _driverGate = new();
    private readonly IGameThreadDriver _driver;
    private readonly Func<UnrealObjectHandle, string?> _worldPath;
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private readonly TimeSpan _driverRetryDelay;
    private readonly CancellationTokenSource _driverStartCancellation = new();
    private readonly HashSet<Registration> _registrations = [];
    private bool _engineReady;
    private UnrealWorldInfo? _currentWorld;
    private volatile bool _disposed;

    [ThreadStatic] private static Scope? _executingScope;

    public unsafe GameThreadService(ModContext context)
        : this(
            new NativeGameThreadDriver(context.GameThreadNative),
            handle => ResolveWorldPath(context.Unreal, handle),
            context.Info,
            context.Error)
    {
    }

    internal GameThreadService(
        IGameThreadDriver driver,
        Func<UnrealObjectHandle, string?> worldPath,
        Action<string> info,
        Action<string> error,
        TimeSpan? driverRetryDelay = null)
    {
        _driver = driver;
        _worldPath = worldPath;
        _info = info;
        _error = error;
        _driverRetryDelay = driverRetryDelay ?? TimeSpan.FromMilliseconds(250);

        if (!_driver.IsAvailable)
        {
            _error("The native game-thread API is unavailable. The Briefcase UI remains active, " +
                   "but Unreal work cannot run.");
            return;
        }

        if (TryStartDriver())
        {
            _info("Game-thread scheduler registered; waiting for the first engine pulse.");
            return;
        }

        _info("Game-thread scheduler is waiting for a stable Unreal ProcessEvent anchor; " +
              "framework startup continues.");
        _ = RetryDriverStartAsync(_driverStartCancellation.Token);
    }

    private bool TryStartDriver()
    {
        lock (_driverGate)
        {
            return !_disposed && _driver.IsAvailable && _driver.TryStart(Pump);
        }
    }

    private async Task RetryDriverStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_driverRetryDelay, cancellationToken).ConfigureAwait(false);
                if (!TryStartDriver()) continue;

                _info("Game-thread scheduler registered after Unreal completed early startup; " +
                      "waiting for the first engine pulse.");
                _driver.RequestPump();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal framework shutdown while Unreal was still starting.
        }
        catch (Exception exception)
        {
            _error($"Deferred game-thread scheduler registration failed: {exception}");
        }
    }

    public IGameThreadScope CreateScope(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Scope(this, owner.Trim());
        }
    }

    private static string? ResolveWorldPath(UnrealApi unreal, UnrealObjectHandle handle)
    {
        if (handle.IsNull) return null;
        try { return unreal.FromReference(new UnrealObjectReference(handle)).Path; }
        catch { return null; }
    }

    private void Pump(GameThreadPump pump)
    {
        List<(Registration Registration, DispatchContext Context)> dispatch = [];
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (_disposed) return;
            var tick = new GameThreadTick(
                pump.DeltaSeconds, pump.Sequence, pump.ThreadId, _currentWorld);

            if (!_engineReady)
            {
                _engineReady = true;
                AddKind(dispatch, RegistrationKind.EngineReady, new DispatchContext(tick, null));
                _info($"Game thread ready on Windows thread {pump.ThreadId}.");
            }

            UnrealWorldInfo? observed = null;
            if (!pump.CurrentWorld.IsNull)
            {
                if (_currentWorld?.Handle == pump.CurrentWorld)
                {
                    observed = _currentWorld;
                }
                else
                {
                    var path = _worldPath(pump.CurrentWorld);
                    if (!string.IsNullOrWhiteSpace(path))
                        observed = new UnrealWorldInfo(pump.CurrentWorld, path);
                }
            }

            if (_currentWorld?.Handle != observed?.Handle)
            {
                if (_currentWorld is { } destroyed)
                    AddKind(dispatch, RegistrationKind.WorldDestroyed,
                        new DispatchContext(tick, destroyed));
                _currentWorld = observed;
                tick = tick with { World = observed };
                if (observed is { } created)
                {
                    var worldContext = new DispatchContext(tick, created);
                    AddKind(dispatch, RegistrationKind.WorldCreated, worldContext);
                    // The native pump first sees a world through an event emitted
                    // by an object owned by that world. This is Briefcase's safe,
                    // address-free map-ready boundary.
                    AddKind(dispatch, RegistrationKind.MapLoaded, worldContext);
                }
            }

            tick = tick with { World = _currentWorld };
            AddKind(dispatch, RegistrationKind.Tick, new DispatchContext(tick, null));
            foreach (var registration in _registrations.ToArray())
            {
                if (registration.Kind is not (RegistrationKind.Once or RegistrationKind.Periodic) ||
                    registration.DueTimestamp > now) continue;
                dispatch.Add((registration, new DispatchContext(tick, null)));
                if (registration.Kind == RegistrationKind.Periodic)
                    registration.DueTimestamp = checked(now + registration.IntervalTicks);
            }
        }

        foreach (var item in dispatch)
        {
            Execute(item.Registration, item.Context);
            if (item.Registration.Kind is RegistrationKind.Once or RegistrationKind.EngineReady)
                Remove(item.Registration, cancel: false);
        }
    }

    private void AddKind(
        List<(Registration Registration, DispatchContext Context)> dispatch,
        RegistrationKind kind,
        DispatchContext context)
    {
        foreach (var registration in _registrations.Where(item => item.Kind == kind).ToArray())
            dispatch.Add((registration, context));
    }

    private void Execute(Registration registration, DispatchContext context)
    {
        if (!registration.Scope.Enter()) return;
        var previous = _executingScope;
        _executingScope = registration.Scope;
        try
        {
            registration.Callback(context);
        }
        catch (Exception exception)
        {
            _error($"Game-thread callback failed for {registration.Scope.Owner}: {exception}");
        }
        finally
        {
            _executingScope = previous;
            registration.Scope.Exit();
        }
    }

    private Registration Add(
        Scope scope,
        RegistrationKind kind,
        Action<DispatchContext> callback,
        TimeSpan delay = default,
        TimeSpan interval = default,
        Action? cancelled = null)
    {
        ValidateDelay(delay, nameof(delay));
        ValidateDelay(interval, nameof(interval));
        var due = checked(Stopwatch.GetTimestamp() + ToTimestampTicks(delay));
        var registration = new Registration(
            this, scope, kind, callback, due, ToTimestampTicks(interval), cancelled);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            scope.ThrowIfDisposed();
            _registrations.Add(registration);
        }
        _driver.RequestPump();
        return registration;
    }

    private static void ValidateDelay(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero || value.TotalDays > 365)
            throw new ArgumentOutOfRangeException(name, "The duration must be between zero and 365 days.");
    }

    private static long ToTimestampTicks(TimeSpan value) =>
        checked((long)Math.Ceiling(value.TotalSeconds * Stopwatch.Frequency));

    private void Remove(Registration registration, bool cancel)
    {
        lock (_gate) RemoveLocked(registration, cancel);
    }

    private void RemoveLocked(Registration registration, bool cancel)
    {
        if (!_registrations.Remove(registration)) return;
        registration.Detach(cancel);
    }

    private void RemoveScope(Scope scope)
    {
        lock (_gate)
        {
            foreach (var registration in _registrations
                         .Where(item => ReferenceEquals(item.Scope, scope)).ToArray())
                RemoveLocked(registration, cancel: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var registration in _registrations.ToArray())
                RemoveLocked(registration, cancel: true);
        }
        _driverStartCancellation.Cancel();
        lock (_driverGate) _driver.Dispose();
        _driverStartCancellation.Dispose();
    }

    private sealed class Scope(GameThreadService owner, string name) : IGameThreadScope
    {
        private readonly object _executionGate = new();
        private bool _disposed;
        private int _executions;

        public string Owner { get; } = name;
        public bool IsAvailable => !_disposed && owner._driver.IsAvailable;
        public bool IsGameThread => IsAvailable && owner._driver.IsGameThread;
        public bool IsEngineReady { get { lock (owner._gate) return owner._engineReady; } }
        public UnrealWorldInfo? CurrentWorld { get { lock (owner._gate) return owner._currentWorld; } }

        public IDisposable Post(Action action) =>
            owner.Add(this, RegistrationKind.Once, _ => action());

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            if (IsGameThread)
            {
                try { action(); return Task.CompletedTask; }
                catch (Exception exception) { return Task.FromException(exception); }
            }
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellation = default;
            var registration = owner.Add(
                this, RegistrationKind.Once,
                _ =>
                {
                    cancellation.Dispose();
                    try { action(); completion.TrySetResult(); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                },
                cancelled: () => completion.TrySetCanceled());
            if (cancellationToken.CanBeCanceled)
                cancellation = cancellationToken.Register(registration.Dispose);
            return completion.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<T>(cancellationToken);
            if (IsGameThread)
            {
                try { return Task.FromResult(action()); }
                catch (Exception exception) { return Task.FromException<T>(exception); }
            }
            var completion = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellation = default;
            var registration = owner.Add(
                this, RegistrationKind.Once,
                _ =>
                {
                    cancellation.Dispose();
                    try { completion.TrySetResult(action()); }
                    catch (Exception exception) { completion.TrySetException(exception); }
                },
                cancelled: () => completion.TrySetCanceled());
            if (cancellationToken.CanBeCanceled)
                cancellation = cancellationToken.Register(registration.Dispose);
            return completion.Task;
        }

        public Task NextTickAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellation = default;
            var registration = owner.Add(
                this, RegistrationKind.Once,
                _ => { cancellation.Dispose(); completion.TrySetResult(); },
                cancelled: () => completion.TrySetCanceled());
            if (cancellationToken.CanBeCanceled)
                cancellation = cancellationToken.Register(registration.Dispose);
            return completion.Task;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration cancellation = default;
            var registration = owner.Add(
                this, RegistrationKind.Once,
                _ => { cancellation.Dispose(); completion.TrySetResult(); }, delay,
                cancelled: () => completion.TrySetCanceled());
            if (cancellationToken.CanBeCanceled)
                cancellation = cancellationToken.Register(registration.Dispose);
            return completion.Task;
        }

        public IDisposable RunAfter(TimeSpan delay, Action action) =>
            owner.Add(this, RegistrationKind.Once, _ => action(), delay);

        public IDisposable RunEvery(TimeSpan interval, Action<GameThreadTick> action)
        {
            if (interval <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
            return owner.Add(this, RegistrationKind.Periodic, value => action(value.Tick), interval, interval);
        }

        public IDisposable OnTick(Action<GameThreadTick> action) =>
            owner.Add(this, RegistrationKind.Tick, value => action(value.Tick));

        public IDisposable OnEngineReady(Action action)
        {
            lock (owner._gate)
            {
                if (!owner._engineReady)
                    return owner.Add(this, RegistrationKind.EngineReady, _ => action());
            }
            return Post(action);
        }

        public IDisposable OnWorldCreated(Action<UnrealWorldInfo> action) =>
            owner.Add(this, RegistrationKind.WorldCreated,
                value => action(value.World!.Value));

        public IDisposable OnWorldDestroyed(Action<UnrealWorldInfo> action) =>
            owner.Add(this, RegistrationKind.WorldDestroyed,
                value => action(value.World!.Value));

        public IDisposable OnMapLoaded(Action<UnrealWorldInfo> action) =>
            owner.Add(this, RegistrationKind.MapLoaded,
                value => action(value.World!.Value));

        public bool Enter()
        {
            lock (_executionGate)
            {
                if (_disposed) return false;
                _executions++;
                return true;
            }
        }

        public void Exit()
        {
            lock (_executionGate)
            {
                _executions--;
                if (_executions == 0) Monitor.PulseAll(_executionGate);
            }
        }

        public void ThrowIfDisposed()
        {
            lock (_executionGate) ObjectDisposedException.ThrowIf(_disposed, this);
        }

        public void Dispose()
        {
            lock (_executionGate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            owner.RemoveScope(this);
            if (ReferenceEquals(_executingScope, this)) return;
            lock (_executionGate)
                while (_executions != 0) Monitor.Wait(_executionGate);
        }
    }

    private sealed class Registration(
        GameThreadService owner,
        Scope scope,
        RegistrationKind kind,
        Action<DispatchContext> callback,
        long dueTimestamp,
        long intervalTicks,
        Action? cancelled) : IDisposable
    {
        private Action<DispatchContext>? _callback = callback;
        private Action? _cancelled = cancelled;
        public Scope Scope { get; } = scope;
        public RegistrationKind Kind { get; } = kind;
        public long DueTimestamp { get; set; } = dueTimestamp;
        public long IntervalTicks { get; } = intervalTicks;
        public Action<DispatchContext> Callback => _callback ?? (_ => { });

        public void Dispose() => owner.Remove(this, cancel: true);

        public void Detach(bool cancel)
        {
            _callback = null;
            var cancellation = Interlocked.Exchange(ref _cancelled, null);
            if (cancel) cancellation?.Invoke();
        }
    }

    private readonly record struct DispatchContext(GameThreadTick Tick, UnrealWorldInfo? World);
    private enum RegistrationKind { Once, Periodic, Tick, EngineReady, WorldCreated, WorldDestroyed, MapLoaded }
}

internal sealed unsafe class NativeGameThreadDriver : IGameThreadDriver
{
    private readonly object _gate = new();
    private NativeGameThreadApi* _api;
    private ulong _registrationId;
    private GCHandle _callbackHandle;

    public NativeGameThreadDriver(NativeGameThreadApi* api) => _api = api;

    public bool IsAvailable
    {
        get
        {
            lock (_gate) return IsAvailableLocked();
        }
    }

    public bool IsGameThread
    {
        get
        {
            NativeGameThreadApi* api;
            lock (_gate)
            {
                if (!IsAvailableLocked()) return false;
                api = _api;
            }
            return api->IsGameThread(api->Context) != 0;
        }
    }

    public bool TryStart(Action<GameThreadPump> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            if (!IsAvailableLocked()) return false;
            if (_registrationId != 0) return true;

            var callbackHandle = GCHandle.Alloc(callback);
            ulong registration = 0;
            if (_api->RegisterCallback(
                    _api->Context, &Pump, (void*)GCHandle.ToIntPtr(callbackHandle),
                    &registration) == 0 || registration == 0)
            {
                callbackHandle.Free();
                return false;
            }

            _callbackHandle = callbackHandle;
            _registrationId = registration;
            return true;
        }
    }

    public void RequestPump()
    {
        NativeGameThreadApi* api;
        lock (_gate)
        {
            if (!IsAvailableLocked()) return;
            api = _api;
        }
        api->RequestPump(api->Context);
    }

    private bool IsAvailableLocked() => _api != null &&
        _api->ApiVersion >= BriefcaseAbi.GameThreadApiVersion &&
        _api->RegisterCallback != null && _api->UnregisterCallback != null &&
        _api->RequestPump != null && _api->IsGameThread != null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Pump(void* context, NativeGameThreadFrame* frame)
    {
        if (context == null || frame == null ||
            frame->StructSize < (uint)sizeof(NativeGameThreadFrame)) return;
        try
        {
            if (GCHandle.FromIntPtr((nint)context).Target is Action<GameThreadPump> callback)
                callback(new GameThreadPump(
                    frame->ThreadId, frame->Sequence, frame->DeltaSeconds,
                    frame->CurrentWorld));
        }
        catch
        {
            // Managed exceptions must never cross ProcessEvent's native frame.
        }
    }

    public void Dispose()
    {
        NativeGameThreadApi* api;
        ulong registrationId;
        GCHandle callbackHandle;
        lock (_gate)
        {
            api = _api;
            if (api == null) return;
            _api = null;
            registrationId = _registrationId;
            _registrationId = 0;
            callbackHandle = _callbackHandle;
            _callbackHandle = default;
        }

        // Native unregistration waits for any callback already in flight.
        // Never hold the driver lock while waiting: a managed callback is
        // allowed to query IsGameThread or request another pump.
        if (registrationId != 0)
            api->UnregisterCallback(api->Context, registrationId);
        if (callbackHandle.IsAllocated) callbackHandle.Free();
    }
}
