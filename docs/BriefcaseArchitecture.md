# Briefcase architecture

## Managed startup boundary

The native runtime resolves and validates the minimum Unreal symbols on its
bootstrap worker, then waits for Deceive Inc's initial object registry to reach
the reviewed startup threshold. CoreCLR and the minimal managed scheduler are
created after that boundary. SDK validation, mod discovery, and Core services
then continue on a below-normal-priority startup thread while the game keeps
rendering. The selected client UI shows real progress for those phases and only
constructs the complete configuration tree when startup finishes. The log
records `managed startup deferred` and `managed startup released` around the
native wait, so no Briefcase UI competes with the initial shader phase.

## Installation model

The only file installed beside `DeceiveInc-Win64-Shipping.exe` is
`version.dll`. Every other framework file lives below `Briefcase`:

```text
Briefcase/
|-- loader.json
|-- settings.json
|-- Briefcase.log
|-- Core/
|   |-- Native/
|   |   `-- Briefcase.UnrealRuntime.dll
|   |-- Briefcase.ManagedHost.dll
|   |-- Briefcase.ModApi.dll
|   |-- Briefcase.Rendering.dll
|   |-- Briefcase.SdkEmitter.dll
|   |-- Briefcase.SdkSnapshots.dll
|   |-- Ui/Avalonia/
|   |   |-- Briefcase.AvaloniaUi.dll
|   |   `-- Briefcase.AvaloniaMenu.dll
|   |-- DotNet/
|   |-- Sdk/
|   `-- Cache/
`-- Mods/
```

Mods are ordinary C# IL assemblies. The SDK is a shared development and runtime
dependency in `Core/Sdk`; a mod package does not include its own SDK copy.

## Startup lifecycle

```text
Windows loads version.dll
  -> Windows Version API exports are forwarded to System32/version.dll
  -> a worker loads Briefcase/Core/Native/Briefcase.UnrealRuntime.dll
  -> the native runtime validates the Deceive Inc. executable profile
  -> Unreal reflection and patching services are initialized
  -> the private CoreCLR starts from Briefcase/Core/DotNet
  -> external framework DLLs resolve from Briefcase/Core/ThirdPartyLibraries
  -> Briefcase.ManagedHost registers the scoped game-thread scheduler
  -> Briefcase.AvaloniaUi creates the independent startup surface
  -> a below-normal-priority worker loads or emits the SDK for this executable
  -> installed mods are inspected and loaded in dependency order
  -> Core services start and the startup surface is released
  -> the first F1 opening loads Briefcase.AvaloniaMenu and builds its visual tree
  -> file changes unload and reload the affected collectible context
```

Initialization runs outside `DllMain` and the Windows loader lock.

## Native-managed boundary

Briefcase keeps native C++ limited to operations that require an unmanaged
entry point or direct cooperation with the game's native code:

- the minimal `version.dll` proxy and Windows export forwarding;
- the separate native runtime and CoreCLR bootstrap;
- native detours and their machine-code trampolines;
- guarded Unreal memory and reflection operations at the native ABI boundary;
- the smallest callbacks needed to cross safely between Unreal and managed code.

The managed runtime owns framework policy and orchestration, mod discovery and
lifecycle, configuration, logging, generated SDK consumption, input state, and
the mod UI. `Briefcase.AvaloniaUi` owns the overlay window and startup view;
`Briefcase.AvaloniaMenu` owns the lazily constructed configuration tree.
`Briefcase.Rendering` coordinates F1 and focus polling, optional game-window
chrome, and toolkit-neutral mod overlay callbacks. `Briefcase.AvaloniaUi` draws
those command batches through Skia. Mods receive `RenderFrame.Overlay` and never
own an Avalonia control, renderer, or native graphics resource.

The native runtime and managed host exchange a C-shaped, versioned
`BriefcaseHostApi` table. It contains fixed-width values and function pointers;
it never transfers C++ containers, exceptions, CRT ownership, or raw Unreal
pointers. Each service table starts with `StructSize` and `ApiVersion`.

The API is split into independent services:

- Core logging and build identity.
- Validated Unreal object, reflection, property, and function operations.
- Scoped game-thread scheduling, timers, and engine/world lifecycle events.
- Unreal and native prefix/postfix patch dispatch.

C# mod authors consume these services through `Briefcase.ModApi`, not through
the native ABI headers.

## Framework configuration

F1 opens the framework-owned Avalonia configuration window. Its fixed navigation
contains Home, Servers, and Mods. Servers stores gameplay and administration
endpoints, joins a selected server, and opens authenticated remote administration
inside the same workspace. Mods lists and configures every client mod without
giving individual mods control of the top-level navigation. The selected view,
selected mod, and every typed setting are persisted in `Briefcase/settings.json`.
The managed mod manager creates a configuration scope before calling a mod's
`Load`, which lets `ConfigurationApi.Bind` restore values before runtime work
starts. Disposing that scope during hot reload removes entries and custom panels
before the collectible assembly is released.

The Mods page displays the installed DLL catalogue and controls persistent
enablement plus immediate load, reload, and unload operations. Disabled filenames
are retained in `settings.json`, so the loader can skip them before executing any
mod code on the next startup.

Indispensable game services load from `Briefcase/Core/BuiltIns`. They use the
same typed API and generated SDK as mods, but are outside the user-controlled
hot-reload graph. Client/server administration and vanilla community balancing
use this path.

See `ModConfiguration.md` for the public API and examples.

## Unreal object safety

Managed code represents a `UObject` with an object-array index and serial
number. Native code resolves this handle for every operation, which rejects
objects destroyed or replaced by garbage collection.

Generated descriptors contain property names and expected layouts. A read
validates the handle, owner class, inheritance, reflected kind, offset, element
size, array dimension, object bounds, and page accessibility before copying data.
Mods therefore ask for a typed property instead of embedding a process address:

```csharp
var spies = context.Unreal.FindObjects(Spy.StaticClass);
var coverRatio = spies[0].Read(Spy.Properties.CoverRatio);
```

## Generated SDK

The native reflection inventory writes an address-free snapshot for the detected
client or dedicated-server executable. `Briefcase.SdkEmitter` turns it into a
build-specific IL assembly with `PersistedAssemblyBuilder`:

```text
Briefcase/Core/Sdk/
|-- Metadata/DeceiveInc.<Target>.<Build>.bsnap
`-- Generated/<Target>/
    |-- Current.props
    `-- <Build>/
        |-- .ready
        |-- .complete
        |-- snapshot.txt
        `-- bin/Release/net10.0/Briefcase.DeceiveInc.<Target>.Sdk.dll
```

The compact binary format is the runtime default. Setting
`sdkSnapshotFormat` to `json` in `loader.json` produces the equivalent
human-readable development snapshot instead. The host validates the executable
identity and snapshot hash, publishes through
completion markers, and reuses the cached assembly only when all identities
match. It loads the target SDK before mods and shares that assembly with their
collectible contexts.

## Mod lifecycle

Each DLL in `Briefcase/Mods` is shadow-copied to `Briefcase/Core/Cache` and
loaded in its own collectible `AssemblyLoadContext`. A rebuild can therefore
replace the source DLL while the game is running. The manager calls
`BriefcaseMod.Unload()` before releasing a context.

A mod must unregister callbacks, stop timers, and release framework references
in `Unload()`. Otherwise the CLR reports that the old context remains alive.

Briefcase uses this managed lifecycle for development and distribution. The
historical C++ and Lua mods remain source references only.
