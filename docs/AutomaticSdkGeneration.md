# Automatic client and server SDK generation

## Goal

The same proxy and `Briefcase.UnrealRuntime.dll` can run inside the Deceive Inc.
client or dedicated server. The process that loads them determines the generated SDK:

```text
DeceiveInc-Win64-Shipping.exe -> Briefcase.DeceiveInc.Client.Sdk
DeceiveIncServer-Win64-Shipping.exe -> Briefcase.DeceiveInc.Server.Sdk
```

The target is not selected by a mod or configuration file. The native runtime
reads the host PE timestamp and image size, selects the matching reflection
profile, then validates a code fingerprint before accessing Unreal metadata.
An unknown build is rejected instead of being scanned with stale offsets.

## Pipeline

```text
version.dll
  -> loads Briefcase/Core/Native/Briefcase.UnrealRuntime.dll
  -> the runtime identifies Client or Server and the exact build
  -> walks GUObjectArray and Unreal reflection metadata in-process
  -> writes an address-free binary snapshot atomically
  -> starts the bundled CoreCLR and Briefcase.ManagedHost
  -> Briefcase.ManagedHost converts the snapshot into a deterministic emission plan
  -> PersistedAssemblyBuilder writes the target-specific IL SDK directly
  -> validates the PE identity and its public runtime references
  -> publishes .ready last
  -> loads the committed SDK before loading managed mods
```

The generated files live beside the executable:

```text
Briefcase/Core/Sdk/
  Metadata/DeceiveInc.<Target>.<Timestamp>-<ImageSize>.bsnap
  Generated/<Target>/<Timestamp>-<ImageSize>/
    bin/Release/net10.0/Briefcase.DeceiveInc.<Target>.Sdk.dll
    snapshot.txt
    .complete
    .ready
  Generated/<Target>/Current.props
```

Repository builds use the address-free reference snapshot under
`sdk/snapshots` when an installed runtime has not emitted a newer snapshot yet.
Both inputs pass through the same `Briefcase.SdkEmitter`; no hand-written or
static game SDK participates in compilation.

Snapshots intentionally contain no UObject, UClass, UFunction or property
addresses. Schema 3 stores stable names, inheritance, sizes, offsets, function
flags, parameter layouts and UEnum values. Every FProperty also has an
address-free recursive type tree: referenced type paths, array/set elements,
map keys and values, enum storage types and packed-boolean masks. Runtime
operations resolve and validate the live reflected object again before use.
Schema 2 snapshots remain readable so a packaged fallback SDK can still be
built before a process has produced its first schema 3 snapshot.

### Snapshot format

The runtime uses the Briefcase Snapshot format (`binary`) by default. Integers
are little-endian, UTF-8 strings use a
7-bit encoded byte length, collections start with a signed 32-bit element count,
and nullable nodes have an explicit presence byte. A `BRSK` signature and a
separate binary-format version make incompatible files fail before allocation.
Readers bound the file size, string lengths, collection counts and recursive
type depth.

For development, switch one setting in `Briefcase/loader.json`:

```json
{
  "schemaVersion": 1,
  "sdkSnapshotFormat": "json"
}
```

Briefcase captures a snapshot once for each executable build. To deliberately
replace an existing snapshot during SDK development, set
`sdkSnapshotRefresh` to `always` beside the selected format. Restore it to
`missing` after capture so the live UObject registry scan does not compete with
game startup.

Accepted values are `binary` and `json`. After an atomic write succeeds,
the runtime removes the snapshot for the same target and build in the other
format, so switching formats never leaves two production copies. Repository
reference snapshots remain JSON and are read by the same shared contract.

## Role of System.Reflection.Metadata

Unreal reflection and CLR metadata are different formats. The native runtime
extracts Unreal metadata into the configured snapshot format. The managed host uses
`PersistedAssemblyBuilder` to translate that snapshot directly into a normal IL
assembly. No C# source project or external `dotnet build` process is created at
game startup.

`System.Reflection.Metadata`, `PEReader` and `MetadataReader` validate the saved
assembly without executing it. The generator checks its exact assembly identity
and rejects private runtime implementation references before publishing it.

## Generated API

The assembly distinguishes the target while namespaces remain stable. Both SDKs
emit game types under `Briefcase.DeceiveInc` and engine types under
`Briefcase.Unreal.<Module>`. A source file using APIs available on both sides can
therefore compile against either SDK without conditional namespace imports.

Every generated class and structure exposes the complete reflected description,
even when a value does not yet have a safe managed transport contract:

```csharp
var type = Spy.Reflection;
var property = Spy.Metadata.Properties.Inventory;
var function = Spy.Metadata.Functions.GetEquippedWeapon;

Console.WriteLine(type.Path);
Console.WriteLine(property.Type);       // for example TArray<InventoryEntry>
Console.WriteLine(function.Parameters[0].IsOutput);
```

`Properties` and `Functions` on a generated class are the executable ABI
surface. `Metadata.Properties` and `Metadata.Functions` are the complete,
read-only reflection surface. Generated enums are real CLR enums with an
`UnrealTypePathAttribute`; `FName` is represented by `UnrealName`.

Generated UObject types are classes with inheritance and typed methods:
```csharp
using Briefcase.DeceiveInc;
using Briefcase.ModApi;

[UnrealPostfixPatch(typeof(Spy), nameof(Spy.BP_OnCoverRatioUpdate))]
private static void OnCoverRatioUpdatePostfix(Spy __instance)
{
    var currentRatio = __instance.CoverRatio;
    __instance.AddRecoverReduction(0.05f);
}
```

Calling the same patched function from its own postfix would create reentrancy,
so the example deliberately calls a different generated method.

The callable ABI covers UObject classes, reflected inheritance, scalar and
object properties, `FName`, `ScriptStruct` value types, ordinary inputs,
multiple `out/ref` values, bounded `TArray<uint8>` inputs, and every parameter
direction for `FString` and `FText`: input, return, pure `out` and writable
`ref`. It also reads owning `USTRUCT` fields recursively and supports those
structs as pure `out` parameters or return values.
For example:

```csharp
// Implicit string conversion keeps common calls concise.
controller.ClientReturnToMainMenuWithTextReason("Server restarted by an administrator.");

// Keeping a distinct type still documents the Unreal signature accurately.
UnrealText label = textLibrary.Conv_StringToText("Localized label");
Console.WriteLine(label.Value);
```

The ABI never copies the private 24-byte `FText` representation into managed
code. The native bridge converts UTF-16 through `KismetTextLibrary`, constructs
the owning engine value immediately before `ProcessEvent`, converts outputs
back to UTF-16, and destroys every temporary with the reflected `FProperty`.
This preserves Unreal's reference-counted lifetime rules.

The generator chooses one of two representations for each `ScriptStruct`.
Structs made only of fixed values use explicit offsets and native size and keep
the zero-allocation canonical path. A struct containing `FString`, `FText`, a
container, a delegate, a soft reference, or another owning struct becomes an
ordinary managed value struct implementing `IUnrealManagedStructValue`. Its
fields carry `UnrealStructFieldAttribute`, which records the Unreal name and
native offset used by the BVC1 decoder. It contains copied managed values and no
native addresses.

Arrays, sets and maps are copied recursively into immutable `UnrealArray<T>`,
`UnrealSet<T>` and `UnrealMap<TKey,TValue>` snapshots. Interfaces,
weak/lazy/soft references, delegates and field paths are exposed without native
addresses. Scalar, object-handle, `FName`, fixed generated struct, `FString` and
`FText` properties have generated setters. API v13 also emits a setter for an
owning struct when its complete field tree can be reconstructed with reflected
initialization: scalars, object handles, `FName`, `FString`, `FText` and nested
admitted structs. `TArray`, `TSet` and `TMap` properties receive setters when
their complete element tree follows the same rules. Delegate bindings remain
read-only.

Every aggregate copy uses the bounded BVC1 wire format. It has a 32 MiB value
limit, 100,000-element container limit and eight-level recursion limit. Object
pointers become serial-checked handles before managed code sees them.

## Prepared generated fast path

Unreal API v10 introduced the generated prepared path. API v11 extends it with
owning `USTRUCT` outputs, API v12 adds owning inputs and writable references,
API v13 adds allocator-backed `TArray`, `TSet` and `TMap` reconstruction, and
API v14 adds validated class-default-object, Outer and object-flag access, and
API v15 adds typed synchronous asset loading plus counted strong-reference leases.
These operations still return only index/serial handles; generated code never
receives an Unreal address.
The generated
assembly stores each property and function descriptor in a static field. On the
first use, the native runtime resolves the owner and reflected member, validates
the complete generated parameter layout, and returns an opaque process-local
token. The token contains no Unreal address and cannot be used as one.

For scalar, `FName`, UObject-handle and fixed-layout struct signatures, the
wrapper builds an address-free canonical buffer with `localloc`. Pointer-free
signatures use it directly. When a struct contains nested UObject or weak-object
fields, the native prepared plan recursively resolves those handles into a
temporary ProcessEvent buffer and converts outputs back to handles. Steady-state
calls create no `object[]`, boxing, managed buffer, list, dictionary or UTF-8
name. Native code still validates the instance index/serial handle and its owner
class on every call. Calls from any thread other than the captured game thread
fail with `WrongThread` before Unreal is invoked.

Generated structs enter the canonical prepared path when every nested field has
fixed storage: scalars, packed booleans, `FName`, object handles and other
admitted structs. An owning struct made from strings, text and those fixed
values uses the BVC1 input path instead.

For an owning `FString`, `FText` or managed-struct `out` parameter or return
value, API v11 creates the real native value with the reflected `FProperty`,
invokes `ProcessEvent`, emits one bounded BVO1 envelope containing recursive
BVC1 nodes, then destroys the native value. API v13 performs the inverse for
inputs and `ref`: it initializes native storage with the reflected `FProperty`,
decodes the managed BVC1 snapshot into that storage, invokes `ProcessEvent`,
copies outputs to BVO1, and destroys the temporary. At no point does an owning
Unreal pointer cross into C#.

Container storage is allocated through the executable's `GMalloc`. Every child
is initialized and destroyed through its reflected `FProperty`; set elements and
map keys use `GetValueTypeHash` and `Identical`. A set or map whose key property
does not advertise Unreal's hash contract remains read-only. Counts are limited
to 100,000 elements, encoded values to 32 MiB and nesting to eight levels.

The allocation regression baseline lives in
`PreparedUnrealApiTests.Prepared_invocation_has_zero_steady_state_managed_allocations`.
It warms both paths, then verifies that 1,000 prepared calls allocate zero managed
bytes while the dynamic descriptor path remains a measurable non-zero baseline.

## Development and runtime use

SDK generation runs inside the bundled CoreCLR and does not require the .NET SDK
or a C# compiler on the player's machine. The generated IL SDK is both a Rider
reference and a runtime dependency shared by all managed mods for that process.

For development, `Briefcase.ManagedHost` loads the exact generated SDK once into the
default load context. Every collectible mod context resolves its SDK reference
to that shared assembly. A mod imports the stable target file instead of copying
the SDK into its output:

```xml
<Import Project="C:\Path\To\DeceiveInc\DeceiveInc\Binaries\Win64\Briefcase\Core\Sdk\Generated\Client\Current.props" />
```

`Current.props` adds a direct build-specific assembly reference with
`Private="false"`. Rider can index its metadata without loading a generated
project into the solution. `Briefcase.ManagedHost` loads that exact SDK once into the
framework load context, and every collectible mod context resolves its SDK
reference to the shared assembly.

The cache key contains both the emitter schema and the SHA-256 hash of the
snapshot. A changed snapshot is regenerated even when the executable build ID
has not changed. A build is not visible to `Briefcase.ManagedHost` until its assembly
identity and `Current.props` have been validated and `.ready` has been written
as the final commit marker.

Client and dedicated-server generation have both been runtime-validated against
the September 2026 beta executables. The server build emits
`Briefcase.DeceiveInc.Server.Sdk` and runs through the headless managed host;
its distribution contains no client UI or rendering binaries.

## Persisted IL emitter implementation

The binary and JSON contract is isolated in `managed/Briefcase.SdkSnapshots`.
`managed/Briefcase.SdkEmitter` consumes that contract and is referenced by
`Briefcase.ManagedHost`.
It uses `PersistedAssemblyBuilder` to emit a real IL SDK assembly directly from
the Unreal snapshot, without generating a C# project or starting `dotnet build`.
Its code is split into four explicit stages:

1. `SdkEmissionPlan` assigns deterministic namespaces and names, then selects
   only members represented safely by the current managed ABI.
2. `SnapshotSdkEmitter` defines every type shell before emitting fields,
   properties, functions, inheritance and cross-struct references.
3. `CoreReferenceNormalizer` converts the persisted runtime implementation
   reference into the public `System.Runtime` reference expected by Roslyn.
4. `FullSurfaceValidator` compares every emitted type and supported member with
   an SDK produced by the established source generator from the same snapshot.

Members outside the callable ABI are counted and reported, but remain available
through their exact metadata descriptors. Invalid structure layouts are omitted
from the copied CLR struct surface instead of being represented with a guessed
type or offset.

Run `scripts\build\validate_persisted_sdk_emitter.bat Release` to emit an
isolated SDK, build a source-generated reference from the same snapshot, compare
their complete supported public surfaces, inspect the emitted PE metadata, and
compile a typed C# consumer against it. The older source generator is retained
as this independent regression oracle; it is no longer packaged or launched by
the game. The consumer exercises the intended mod syntax with `typeof`,
`nameof`, patch attributes, generated method calls, a generated value struct,
and a generic `T.FromObject` constraint.

The exact generated surface is reported by the build validation because it
depends on the current client and server snapshots. Schema 3 supplies the
recursive type and enum information required for aggregate getters.

At startup, `GeneratedSdkLoader` finds the snapshot for the current validated
game build, invokes this same emitter synchronously, and loads the resulting SDK
before the mod manager begins discovery.
