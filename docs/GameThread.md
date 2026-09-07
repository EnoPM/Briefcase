# Game-thread scheduling and engine lifecycle

Unreal object calls must execute on the game thread unless the engine explicitly
documents another threading contract. Briefcase captures the executable's
initial thread when Windows attaches `version.dll`, then uses the already
validated `UObject::ProcessEvent` detour as a pump normally bounded to 120 Hz,
with explicit wake-ups for newly queued work. A worker never
pretends to be Unreal's game thread and no native address crosses into a mod.

## Scheduling work

Every mod receives its own `GameThreadApi` scope:

```csharp
context.GameThread.Post(() => player.SomeGeneratedFunction());

await context.GameThread.InvokeAsync(() =>
{
    player.SomeGeneratedFunction();
});

await context.GameThread.DelayAsync(TimeSpan.FromSeconds(1));
```

`Invoke` runs immediately when its caller is already on the game thread and
otherwise waits for the next pump. Prefer `InvokeAsync` in asynchronous code so
the calling thread is not blocked.

Timers also run on the game thread:

```csharp
IDisposable timer = context.GameThread.RunEvery(
    TimeSpan.FromMilliseconds(250),
    tick => context.Info($"Game-thread pulse {tick.Sequence}"));
```

The timer token can stop it early. Briefcase also removes every queued action,
timer and lifecycle subscription belonging to a mod before unloading that mod's
collectible assembly. A mod therefore cannot leave a callback rooted by the
framework after hot reload.

## Lifecycle observations

```csharp
context.GameThread.OnEngineReady(() => context.Info("Engine ready"));
context.GameThread.OnWorldCreated(world => context.Info($"World: {world.Path}"));
context.GameThread.OnMapLoaded(world => context.Info($"Map: {world.Path}"));
context.GameThread.OnWorldDestroyed(world => context.Info($"Left: {world.Path}"));
```

The native pump finds a world only by walking the validated `Outer` chain of the
object whose `ProcessEvent` invocation caused that pulse. The first event owned
by a new world is Briefcase's `OnMapLoaded` boundary. It means the world is live
and can service reflected calls; it is deliberately not a promise that every
streaming level or asset has finished loading.

`UnrealWorldInfo` contains a serial-checked object handle and its path. Convert
the handle through `context.Unreal.FromReference` inside a game-thread callback
when raw generated object access is needed.

Match, local-player and pawn transitions are game-specific concepts. They will
be layered on this engine lifecycle rather than guessed by the native runtime.
