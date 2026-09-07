# C# runtime and managed mod loading

## Startup

`version.dll` loads `Briefcase/Core/Native/Briefcase.UnrealRuntime.dll`. The
native runtime then loads `Briefcase/Core/DotNet/host/fxr/<version>/hostfxr.dll`
and starts the bundled CoreCLR with `Briefcase.ManagedHost.runtimeconfig.json`.
It obtains `Briefcase.ManagedHost.EntryPoint.Initialize` and passes the versioned
native `BriefcaseHostApi` table to managed code.

The runtime is framework-dependent relative to the private bundled .NET tree.
The user does not need to install .NET globally.

## Loading and reload

`Briefcase.ManagedHost` watches:

```text
DeceiveInc/Binaries/Win64/Briefcase/Mods/
```

Every mod gets a collectible `AssemblyLoadContext`. Before loading, Briefcase
copies the DLL and PDB to `Briefcase/Core/Cache`; the loaded file therefore does
not lock Rider's output. A stable file change is debounced, the previous mod's
`Unload()` method is called, and a fresh shadow copy is loaded.

The Briefcase tab in the F1 menu lists installed DLLs separately from loaded
mods. Enabled is a persistent startup policy. Load, Reload, and Unload affect
the current session immediately. Copying a new DLL or replacing an existing one
is detected by the same directory watcher.

A surviving delegate, static reference, GC handle, timer, or executing thread
can keep the old context alive. The host records that condition in
`Briefcase/Briefcase.log`.

## Build and install

```bat
scripts\build\build_framework.bat Release
scripts\deploy_framework.bat Release
```

For a quick development iteration, use the corresponding per-mod script under
`scripts/build`; it builds and copies that mod into the installed
`Briefcase/Mods` directory.

Briefcase supports one runtime model: regular C# IL assemblies on the bundled
CoreCLR. The SDK is loaded once by the manager and shared with every collectible
mod context.
