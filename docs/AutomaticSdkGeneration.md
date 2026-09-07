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
  -> writes an address-free JSON snapshot atomically
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
  Metadata/DeceiveInc.<Target>.<Timestamp>-<ImageSize>.json
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
addresses. They store stable names, inheritance, sizes, offsets, property kinds,
referenced struct paths, function flags and parameter layouts. Runtime operations resolve and validate
the live reflected object again before use.

## Role of System.Reflection.Metadata

Unreal reflection and CLR metadata are different formats. The native runtime
extracts Unreal metadata into JSON. The managed host uses
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

The current generator covers UObject classes, reflected inheritance,
scalar/object properties, `ScriptStruct` value types, primitive enum storage,
ordinary inputs, a single return value, `FString` parameters, and bounded
`TArray<uint8>` parameters. It also exposes object `TextProperty` values and
`FText` function inputs/returns as `UnrealText`:

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
This preserves Unreal's reference-counted lifetime rules. `FText` nested inside
a copied `ScriptStruct` remains excluded until field-by-field struct marshalling
can provide the same ownership guarantee.

Generated structs use explicit offsets and native
size, including opaque structs whose native fields are not reflected. Other
containers, delegates and additional out/ref results still need dedicated ABI
contracts and will be added incrementally.

## Development and runtime use

SDK generation runs inside the bundled CoreCLR and does not require the .NET SDK
or a C# compiler on the player's machine. The generated IL SDK is both a Rider
reference and a runtime dependency shared by all managed mods for that process.

For development, `Briefcase.ManagedHost` loads the exact generated SDK once into the
default load context. Every collectible mod context resolves its SDK reference
to that shared assembly. A mod imports the stable target file instead of copying
the SDK into its output:

```xml
<Import Project="D:\SteamLibrary\steamapps\common\DeceiveInc\DeceiveInc\Binaries\Win64\Briefcase\Core\Sdk\Generated\Client\Current.props" />
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
its distribution contains no ImGui or rendering binaries.

## Persisted IL emitter implementation

The implementation is isolated in `managed/Briefcase.SdkEmitter` and is
referenced by `Briefcase.ManagedHost`.
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

Unsupported containers, strings, delegates, extra out/ref parameters and
invalid reflected layouts are counted and reported. They are never represented
as a guessed CLR type.

Run `scripts\build\validate_persisted_sdk_emitter.bat Release` to emit an
isolated SDK, build a source-generated reference from the same snapshot, compare
their complete supported public surfaces, inspect the emitted PE metadata, and
compile a typed C# consumer against it. The older source generator is retained
as this independent regression oracle; it is no longer packaged or launched by
the game. The consumer exercises the intended mod syntax with `typeof`,
`nameof`, patch attributes, generated method calls, a generated value struct,
and a generic `T.FromObject` constraint.

On the current client snapshot this covers 5,463 types, 17,694 properties or
fields, and 6,963 functions. One invalid type, 5,094 unsupported properties or
fields, and 3,215 unsupported functions are reported explicitly.

At startup, `GeneratedSdkLoader` finds the snapshot for the current validated
game build, invokes this same emitter synchronously, and loads the resulting SDK
before the mod manager begins discovery.
