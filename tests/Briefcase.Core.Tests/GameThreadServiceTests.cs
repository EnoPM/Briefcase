using System.Runtime.InteropServices;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Core.Tests;

public sealed class GameThreadServiceTests
{
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

    public void Start(Action<GameThreadPump> callback) => _callback = callback;
    public void RequestPump() => RequestCount++;

    public void Pump(UnrealObjectHandle world)
    {
        _pumping = true;
        try { _callback!(new GameThreadPump(123, ++_sequence, 1.0f / 60.0f, world)); }
        finally { _pumping = false; }
    }

    public void Dispose() => _callback = null;
}
