# Unreal object access and lifecycle

Briefcase keeps every `UObject*` and `UClass*` inside the native runtime. Public
C# APIs exchange `UnrealObjectHandle`, whose object-array index is paired with
Unreal's serial number. Reuse of an object slot therefore invalidates an old
wrapper instead of redirecting it to another object.

## Identity

```csharp
var defaultWeapon = context.Unreal.GetDefaultObject(ProjectileWeapon.StaticClass);
var loaded = context.Unreal.FindObject(
    "/Game/Weapons/MyWeapon.MyWeapon",
    AgentWeaponData.StaticClass);

var world = context.Unreal.FromReference(
    new UnrealObjectReference(worldInfo.Handle),
    World.StaticClass);

UnrealObject? packageOrOwner = loaded.Outer;
UnrealObjectFlags flags = loaded.Flags;
```

`GetDefaultObject` validates the UE 4.27 `UClass::ClassDefaultObject` field against
GUObjectArray, the requested class and `RF_ClassDefaultObject`. `FindObject` only
resolves objects that Unreal has already loaded.

## Asset loading and retention

Unreal API v15 adds a mod-scoped asset service:

```csharp
context.GameThread.OnEngineReady(() =>
{
    using var weaponData = context.Assets.Load(
        "/Game/Data/MyWeapon.MyWeapon",
        AgentWeaponData.StaticClass);

    // Value is a generated, typed UObject wrapper.
    Console.WriteLine(weaponData.Value.Path);
});
```

`Load` accepts a canonical soft-object path and an expected generated class. The
native runtime builds an `FSoftObjectPath` with
`KismetSystemLibrary.MakeSoftObjectPath`, invokes the vanilla
`LoadAsset_Blocking` UFunction, then validates the result with `IsA`. Both
functions and their complete parameter layouts are resolved and checked at
runtime; Briefcase contains no address for either function.

Loading is synchronous and must run on Unreal's game thread. `TryLoad` returns
`false` only when the path does not resolve. Other failures, including an
incorrect class or thread, remain explicit exceptions.

The returned `UnrealObjectLease<T>` holds a strong Unreal root. Dispose the lease
as soon as the asset is no longer needed. A mod can also call `Retain` for an
object obtained from another API. Briefcase counts leases, preserves a root that
predated Briefcase, and releases all remaining leases automatically before the
mod load context is discarded during unload or reload. A lease keeps the object
alive; it does not transfer ownership of actors or replace `DestroyActor`.

The typed `FromReference` overload performs an `IsA` check before constructing
the generated wrapper. Use it for handles supplied by world callbacks or other
Briefcase services when the expected generated type is known.

## Subsystems

The four helpers below invoke Unreal's reflected `SubsystemBlueprintLibrary`:

- `GetEngineSubsystem<T>`;
- `GetGameInstanceSubsystem<T>`;
- `GetWorldSubsystem<T>`;
- `GetLocalPlayerSubsystem<T>`.

Context-based helpers require a live world-context object. All four must execute
on the game thread, exactly like an ordinary generated UFunction call.

## Creation

`CreateObject<T>` calls the vanilla `GameplayStatics.SpawnObject` UFunction and
requires an Outer. It rejects Actor classes. `SpawnActor<TActor,TTransform>`
accepts only the generated `/Script/CoreUObject.Transform` structure, validates
that the class derives from Actor, uses Unreal's deferred spawn sequence and
destroys the unfinished actor if finalization fails.

Spawn only after a game map is loaded. Unreal also exposes short-lived worlds
during splash screens and startup; those handles are valid UObjects but are not
valid gameplay spawn contexts.

```csharp
var created = context.Unreal.CreateObject(MyObject.StaticClass, outer);
var actor = context.Unreal.SpawnActor(
    MyActor.StaticClass,
    world,
    transform,
    UnrealSpawnCollisionHandling.AlwaysSpawn);

context.Unreal.DestroyActor(actor);
```

Wrappers do not root objects. `CreateObject` results follow their Outer's normal
Unreal lifetime. Spawned actors belong to their World and should be explicitly
destroyed by the mod when no longer needed.
