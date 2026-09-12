using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Core.Tests;

public sealed class GameThreadServiceTests
{
    [Fact]
    public void Prepared_Unreal_ABI_v16_layouts_match_the_public_header()
    {
        Assert.Equal((uint)16, BriefcaseAbi.UnrealApiVersion);
        Assert.Equal((uint)7, BriefcaseAbi.PatchingApiVersion);
        Assert.Equal(32, Marshal.SizeOf<NativePreparedParameter>());
        Assert.Equal(32, Marshal.SizeOf<NativeOwnedValueBuffer>());
        Assert.Equal(40, Marshal.SizeOf<NativeValueInput>());
        Assert.Equal(280, Marshal.SizeOf<NativeUnrealApi>());
        Assert.Equal(104, Marshal.SizeOf<NativePatchingApi>());
    }

    [Fact]
    public void Native_game_thread_contract_has_the_expected_x64_layout()
    {
        Assert.Equal(64, Marshal.SizeOf<NativeGameThreadFrame>());
        Assert.Equal(112, Marshal.SizeOf<NativeHostApi>());
    }

    [Fact]
    public void Pump_dispatches_work_and_world_lifecycle_in_order()
    {
        var driver = new ManualGameThreadDriver();
        var errors = new List<string>();
        using var service = CreateService(driver, errors);
        using var scope = service.CreateScope("test.mod");
        var api = new GameThreadApi(scope);
        var events = new List<string>();
        var posted = false;

        api.OnEngineReady(() => events.Add("engine"));
        api.OnWorldCreated(world => events.Add("created:" + world.Path));
        api.OnMapLoaded(world => events.Add("map:" + world.Path));
        api.OnWorldDestroyed(world => events.Add("destroyed:" + world.Path));
        api.OnTick(tick => events.Add("tick:" + tick.Sequence));
        api.Post(() => posted = true);

        driver.Pump(new UnrealObjectHandle(10, 20));

        Assert.True(posted);
        Assert.True(api.IsEngineReady);
        Assert.Equal(new UnrealObjectHandle(10, 20), api.CurrentWorld?.Handle);
        Assert.Equal([
            "engine",
            "created:/Game/Maps/TestWorld.TestWorld",
            "map:/Game/Maps/TestWorld.TestWorld",
            "tick:1"
        ], events);

        driver.Pump(default);
        Assert.Equal("destroyed:/Game/Maps/TestWorld.TestWorld", events[^2]);
        Assert.Equal("tick:2", events[^1]);
        Assert.Null(api.CurrentWorld);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task InvokeAsync_completes_on_the_next_pump()
    {
        var driver = new ManualGameThreadDriver();
        using var service = CreateService(driver, []);
        using var scope = service.CreateScope("test.mod");
        var api = new GameThreadApi(scope);

        var resultTask = api.InvokeAsync(() => 42);
        Assert.False(resultTask.IsCompleted);
        driver.Pump(default);

        Assert.Equal(42, await resultTask);
        Assert.True(driver.RequestCount > 0);
    }

    [Fact]
    public async Task Disposing_a_scope_cancels_pending_work_and_removes_timers()
    {
        var driver = new ManualGameThreadDriver();
        using var service = CreateService(driver, []);
        var scope = service.CreateScope("test.mod");
        var api = new GameThreadApi(scope);
        var timerRuns = 0;
        api.RunEvery(TimeSpan.FromMilliseconds(1), _ => timerRuns++);
        var pending = api.DelayAsync(TimeSpan.FromDays(1));

        await Task.Delay(10);
        driver.Pump(default);
        Assert.Equal(1, timerRuns);

        scope.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await Task.Delay(10);
        driver.Pump(default);
        Assert.Equal(1, timerRuns);
    }

    [Fact]
    public void Callback_failure_is_isolated_and_reported()
    {
        var driver = new ManualGameThreadDriver();
        var errors = new List<string>();
        using var service = CreateService(driver, errors);
        using var first = service.CreateScope("broken.mod");
        using var second = service.CreateScope("healthy.mod");
        var healthyRuns = 0;
        new GameThreadApi(first).OnTick(_ => throw new InvalidOperationException("boom"));
        new GameThreadApi(second).OnTick(_ => healthyRuns++);

        driver.Pump(default);

        Assert.Equal(1, healthyRuns);
        Assert.Single(errors);
        Assert.Contains("broken.mod", errors[0]);
        Assert.Contains("boom", errors[0]);
    }

    [Fact]
    public async Task Driver_registration_retries_without_blocking_framework_startup()
    {
        var driver = new DeferredGameThreadDriver();
        var info = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        using var service = new GameThreadService(
            driver,
            _ => null,
            info.Enqueue,
            errors.Enqueue,
            TimeSpan.FromMilliseconds(5));
        using var scope = service.CreateScope("test.mod");
        var api = new GameThreadApi(scope);
        var executed = false;
        api.Post(() => executed = true);

        await driver.RetryObserved.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(driver.Attempts >= 2);
        Assert.False(driver.IsStarted);
        driver.AllowRegistration();
        await driver.RegistrationCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(driver.IsStarted);

        driver.Pump(default);

        Assert.True(executed);
        Assert.Contains(info, message => message.Contains(
            "framework startup continues", StringComparison.Ordinal));
        Assert.Contains(info, message => message.Contains(
            "registered after Unreal", StringComparison.Ordinal));
        Assert.Empty(errors);
    }

    [Fact]
    public void Missing_native_driver_keeps_non_Unreal_services_available()
    {
        var errors = new List<string>();
        using var service = new GameThreadService(
            new UnavailableGameThreadDriver(),
            _ => null,
            _ => { },
            errors.Add);

        using var scope = service.CreateScope("ui.mod");
        var api = new GameThreadApi(scope);

        Assert.False(api.IsEngineReady);
        Assert.Single(errors);
        Assert.Contains("UI remains active", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Timers_reject_invalid_durations()
    {
        var driver = new ManualGameThreadDriver();
        using var service = CreateService(driver, []);
        using var scope = service.CreateScope("test.mod");
        var api = new GameThreadApi(scope);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            api.RunEvery(TimeSpan.Zero, _ => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            api.RunAfter(TimeSpan.FromMilliseconds(-1), () => { }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = api.DelayAsync(TimeSpan.FromDays(366));
        });
    }

    private static GameThreadService CreateService(
        ManualGameThreadDriver driver, List<string> errors) =>
        new(driver,
            handle => handle.Index == 10 ? "/Game/Maps/TestWorld.TestWorld" : null,
            _ => { }, errors.Add);
}

internal sealed class ManualGameThreadDriver : IGameThreadDriver
{
    private Action<GameThreadPump>? _callback;
    private bool _pumping;
    private ulong _sequence;

    public bool IsAvailable => true;
    public bool IsGameThread => _pumping;
    public int RequestCount { get; private set; }

    public bool TryStart(Action<GameThreadPump> callback)
    {
        _callback = callback;
        return true;
    }
    public void RequestPump() => RequestCount++;

    public void Pump(UnrealObjectHandle world)
    {
        _pumping = true;
        try { _callback!(new GameThreadPump(123, ++_sequence, 1.0f / 60.0f, world)); }
        finally { _pumping = false; }
    }

    public void Dispose() => _callback = null;
}

internal sealed class DeferredGameThreadDriver : IGameThreadDriver
{
    private readonly TaskCompletionSource<bool> _registrationCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _retryObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<GameThreadPump>? _callback;
    private int _attempts;
    private int _allowRegistration;
    private int _started;
    private ulong _sequence;

    public bool IsAvailable => true;
    public bool IsGameThread => false;
    public int Attempts => Volatile.Read(ref _attempts);
    public bool IsStarted => Volatile.Read(ref _started) != 0;
    public Task RegistrationCompleted => _registrationCompleted.Task;
    public Task RetryObserved => _retryObserved.Task;

    public bool TryStart(Action<GameThreadPump> callback)
    {
        if (Interlocked.Increment(ref _attempts) >= 2)
            _retryObserved.TrySetResult(true);
        if (Volatile.Read(ref _allowRegistration) == 0) return false;
        _callback = callback;
        Volatile.Write(ref _started, 1);
        _registrationCompleted.TrySetResult(true);
        return true;
    }

    public void AllowRegistration() => Volatile.Write(ref _allowRegistration, 1);
    public void RequestPump() { }

    public void Pump(UnrealObjectHandle world) =>
        _callback!(new GameThreadPump(123, ++_sequence, 1.0f / 60.0f, world));

    public void Dispose() => _callback = null;
}

internal sealed class UnavailableGameThreadDriver : IGameThreadDriver
{
    public bool IsAvailable => false;
    public bool IsGameThread => false;
    public bool TryStart(Action<GameThreadPump> callback) => false;
    public void RequestPump() { }
    public void Dispose() { }
}
