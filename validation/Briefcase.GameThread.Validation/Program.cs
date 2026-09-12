using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

Assert(System.Runtime.InteropServices.Marshal.SizeOf<NativeGameThreadFrame>() == 64,
    "Managed game-thread frame does not match the native ABI.");
Assert(System.Runtime.InteropServices.Marshal.SizeOf<NativeHostApi>() == 112,
    "Managed host service table does not match the native ABI.");

var driver = new ManualDriver();
var errors = new List<string>();
using var service = new GameThreadService(
    driver,
    handle => handle.Index == 10 ? "/Game/Maps/TestWorld.TestWorld" : null,
    _ => { },
    errors.Add);
var scope = service.CreateScope("validation");
var api = new GameThreadApi(scope);

var order = new List<string>();
api.OnEngineReady(() => order.Add("engine"));
api.OnWorldCreated(world => order.Add("created:" + world.Path));
api.OnMapLoaded(world => order.Add("map:" + world.Path));
api.OnWorldDestroyed(world => order.Add("destroyed:" + world.Path));
api.OnTick(tick => order.Add("tick:" + tick.Sequence));
var posted = false;
api.Post(() => posted = true);

driver.Pump(new UnrealObjectHandle(10, 20));
Assert(posted, "Posted work did not execute.");
Assert(api.IsEngineReady, "Engine-ready state was not retained.");
Assert(api.CurrentWorld?.Handle == new UnrealObjectHandle(10, 20), "Current world was not retained.");
Assert(order.SequenceEqual([
    "engine",
    "created:/Game/Maps/TestWorld.TestWorld",
    "map:/Game/Maps/TestWorld.TestWorld",
    "tick:1"
]), "First-pump lifecycle order is incorrect: " + string.Join(", ", order));

var resultTask = api.InvokeAsync(() => 42);
driver.Pump(new UnrealObjectHandle(10, 20));
Assert(await resultTask == 42, "InvokeAsync did not return its result.");

var timerRuns = 0;
api.RunEvery(TimeSpan.FromMilliseconds(1), _ => timerRuns++);
await Task.Delay(5);
driver.Pump(new UnrealObjectHandle(10, 20));
Assert(timerRuns == 1, "Periodic work did not execute exactly once.");

var cancelledTask = api.DelayAsync(TimeSpan.FromDays(1));
scope.Dispose();
await AssertCancelled(cancelledTask);
await Task.Delay(5);
driver.Pump(new UnrealObjectHandle(uint.MaxValue, 0));
Assert(timerRuns == 1, "A disposed mod scope retained its timer.");
Assert(errors.Count == 0, "Scheduler logged validation errors: " + string.Join(" | ", errors));

Console.WriteLine("Game-thread scheduler validation passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertCancelled(Task task)
{
    try
    {
        await task;
        throw new InvalidOperationException("Expected a cancelled task.");
    }
    catch (TaskCanceledException)
    {
    }
}

internal sealed class ManualDriver : IGameThreadDriver
{
    private Action<GameThreadPump>? _callback;
    private bool _pumping;
    private ulong _sequence;
    public bool IsAvailable => true;
    public bool IsGameThread => _pumping;
    public bool TryStart(Action<GameThreadPump> callback)
    {
        _callback = callback;
        return true;
    }
    public void RequestPump() { }
    public void Pump(UnrealObjectHandle world)
    {
        _pumping = true;
        try { _callback!(new GameThreadPump(123, ++_sequence, 1.0f / 60.0f, world)); }
        finally { _pumping = false; }
    }
    public void Dispose() => _callback = null;
}
