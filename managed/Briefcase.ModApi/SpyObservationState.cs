using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

/// <summary>
/// Immutable description of one live Deceive Inc. spy. The dedicated tracking
/// service publishes these records once; rendering and gameplay mods consume
/// the same snapshot without repeating Unreal calls.
/// </summary>
public sealed record SpyObservation(
    UnrealObjectHandle Handle,
    DateTime SampledAtUtc,
    string Name,
    string Path,
    bool IsLocal,
    bool IsBot,
    float CoverRatio,
    string ActiveTool,
    FVector WorldPosition,
    FVector AimPosition,
    FVector Velocity,
    FVector2D ScreenPosition,
    bool IsProjected,
    float DistanceMeters);

/// <summary>A complete point-in-time view published by the tracking service.</summary>
public sealed record SpyObservationSnapshot(
    IReadOnlyList<SpyObservation> Spies,
    UnrealObjectHandle LocalSpyHandle,
    UnrealObjectHandle LocalControllerHandle,
    bool LocalSpyAlive,
    string Status,
    long CallbackCount)
{
    public static SpyObservationSnapshot Waiting { get; } = new(
        [], new UnrealObjectHandle(uint.MaxValue, 0),
        new UnrealObjectHandle(uint.MaxValue, 0), false,
        "Waiting for the shared Spy tracker.", 0);
}

/// <summary>
/// Local-player tick forwarded by the shared observer. Handles keep this
/// contract independent from build-specific generated SDK wrapper classes.
/// </summary>
public readonly record struct LocalSpyTick(
    UnrealObjectHandle SpyHandle,
    UnrealObjectHandle ControllerHandle,
    float DeltaSeconds,
    SpyObservationSnapshot Snapshot);

/// <summary>
/// A consumer's requested observation frequency. Multiple consumers combine by
/// taking the highest rate; setting zero pauses that consumer's world samples.
/// </summary>
public interface ISpyObservationDemand : IDisposable
{
    int SamplesPerSecond { get; set; }
}

/// <summary>
/// Process-wide exchange point for Deceive Inc. target observations. This type
/// lives in Briefcase.ModApi's shared load context, so collectible mod load
/// contexts all see the same reference while remaining independently unloadable.
/// </summary>
public static class SpyObservationState
{
    private static readonly object SubscriberGate = new();
    private static readonly object DemandGate = new();
    private static SpyObservationSnapshot _current = SpyObservationSnapshot.Waiting;
    private static Action<LocalSpyTick>[] _localTickSubscribers = [];
    private static ObservationDemand[] _demands = [];
    private static int _requestedSamplesPerSecond;

    public static SpyObservationSnapshot Current => Volatile.Read(ref _current);

    public static void Publish(SpyObservationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    /// <summary>
    /// Subscribes to the already-observed local Spy tick. Disposing the token is
    /// mandatory during mod unload so its collectible load context is released.
    /// </summary>
    public static IDisposable SubscribeLocalTick(Action<LocalSpyTick> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (SubscriberGate)
            _localTickSubscribers = [.. _localTickSubscribers, callback];
        return new LocalTickSubscription(callback);
    }

    public static void DispatchLocalTick(LocalSpyTick tick)
    {
        Action<LocalSpyTick>[] subscribers;
        lock (SubscriberGate) subscribers = _localTickSubscribers;
        foreach (var subscriber in subscribers) subscriber(tick);
    }

    public static ISpyObservationDemand RequestSamples(int samplesPerSecond)
    {
        var demand = new ObservationDemand(samplesPerSecond);
        lock (DemandGate)
        {
            _demands = [.. _demands, demand];
            RefreshRequestedRateLocked();
        }
        return demand;
    }

    // This property is read from every Spy tick. Consumers update the cached
    // maximum only when their demand changes, so the hot path is lock-free.
    public static int RequestedSamplesPerSecond =>
        Volatile.Read(ref _requestedSamplesPerSecond);

    public static void Reset() => Volatile.Write(ref _current, SpyObservationSnapshot.Waiting);

    private sealed class LocalTickSubscription(Action<LocalSpyTick> callback) : IDisposable
    {
        private Action<LocalSpyTick>? _callback = callback;

        public void Dispose()
        {
            var removed = Interlocked.Exchange(ref _callback, null);
            if (removed is null) return;
            lock (SubscriberGate)
                _localTickSubscribers = _localTickSubscribers
                    .Where(candidate => candidate != removed)
                    .ToArray();
        }
    }

    private static void RefreshRequestedRateLocked()
    {
        var maximum = 0;
        foreach (var demand in _demands)
            maximum = Math.Max(maximum, demand.CurrentSamplesPerSecond);
        Volatile.Write(ref _requestedSamplesPerSecond, maximum);
    }

    private sealed class ObservationDemand : ISpyObservationDemand
    {
        private int _samplesPerSecond;
        private int _disposed;

        public ObservationDemand(int samplesPerSecond) =>
            _samplesPerSecond = Math.Clamp(samplesPerSecond, 0, 60);

        public int SamplesPerSecond
        {
            get => Volatile.Read(ref _samplesPerSecond);
            set
            {
                ObjectDisposedException.ThrowIf(
                    Volatile.Read(ref _disposed) != 0, this);
                var requested = Math.Clamp(value, 0, 60);
                if (requested == Volatile.Read(ref _samplesPerSecond)) return;
                lock (DemandGate)
                {
                    Volatile.Write(ref _samplesPerSecond, requested);
                    RefreshRequestedRateLocked();
                }
            }
        }

        public int CurrentSamplesPerSecond => Volatile.Read(ref _samplesPerSecond);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (DemandGate)
            {
                _demands = _demands.Where(candidate => candidate != this).ToArray();
                RefreshRequestedRateLocked();
            }
        }
    }
}
