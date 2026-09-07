# Briefcase

Briefcase is a Deceive Inc. mod loader with a small native bootstrap and a
managed .NET mod runtime. Open `Briefcase.slnx` in Rider. No driver, WDK, UE4SS,
or system-wide .NET installation is required at runtime.

## Installed layout

```text
DeceiveInc/Binaries/Win64/
|-- version.dll
`-- Briefcase/
    |-- loader.json
    |-- VERSION
    |-- settings.json
    |-- Briefcase.log
    |-- Core/
    |   |-- Native/
    |   |   `-- Briefcase.UnrealRuntime.dll
    |   |-- Briefcase.ManagedHost.dll
    |   |-- Briefcase.ModApi.dll
    |   |-- Briefcase.SdkEmitter.dll
    |   |-- BuiltIns/
    |   |-- DotNet/
    |   |-- Sdk/
    |   `-- Cache/
    `-- Mods/
```

`version.dll` only forwards the Windows Version API and loads the native runtime
from `Briefcase/Core/Native`. `Briefcase.UnrealRuntime.dll` initializes Unreal,
starts the bundled CoreCLR, and passes a versioned native service table to
`Briefcase.ManagedHost`. The managed host generates or loads the SDK for the
current executable and then loads C# mod DLLs from `Briefcase/Mods` into collectible
`AssemblyLoadContext` instances. A scoped
game-thread API provides queued calls, asynchronous invocation, timers, ticks, and
engine/world lifecycle observations to client and server mods.
F1 opens the managed Briefcase menu. A top switch selects the local **Client**
configuration or remote **Server** administration. The Client view uses vertical
tabs for Briefcase and each configurable user mod.

The dedicated-server package uses a separate headless managed host. It keeps
the same mod lifecycle and generated SDK, but contains no rendering backend,
ImGui dependency, input hook, or F1 configuration window. Server settings are
still persisted in `Briefcase/settings.json`.

## Build

With Visual Studio Build Tools and the .NET 10 SDK installed:

```bat
scripts\build\build_framework.bat Release
```

The complete, portable package is written to `dist/Briefcase`. The command does
not modify the game installation. Framework packages intentionally contain an
empty `Briefcase/Mods` directory; user mods have their own build and release
lifecycles.

Create the two end-user archives locally with:

```powershell
./scripts/release/PrepareRelease.ps1 -Configuration Release
```

The command writes `Briefcase-Client-v<VERSION>.zip` and
`Briefcase-Server-v<VERSION>.zip` to `artifacts/release`. Both archives are
rooted for direct extraction into the corresponding `Binaries/Win64` directory.
`VERSION` is the single source used by managed assemblies, the native runtime,
installed packages, tags, and archive names.

To publish, first merge and review pull requests in an integration branch. Then
run the **Create Version** workflow from the Actions page, enter that branch,
and choose `build`, `minor`, or `major`. The workflow merges its exact remote
revision into `main`, updates `VERSION` in the merge commit, creates the matching
tag, synchronizes the integration branch with that commit, and publishes both
archives. The lower-level **Release** workflow can rebuild an existing tag.

To install the built package with the game closed:

```bat
scripts\deploy_framework.bat Release
```

The deploy script overlays Briefcase-owned runtime files and preserves existing
mod configuration files.

After Briefcase has generated a server SDK once, build the headless package with:

```bat
scripts\build\build_server_framework.bat Release
```

The portable server package is written to `dist/Briefcase.Server`. Server
administration and vanilla community balancing live in `Core/BuiltIns`; its
`Mods` directory is reserved for user-installed server mods.

Install it after stopping the dedicated server:

```bat
scripts\deploy_server_framework.bat Release
```

The deployment also installs `StartBriefcaseServer.bat` in the dedicated-server
installation root. Run that script instead of `DeceiveIncServer.exe`: it reads
the game and query ports from `TripwireServer.ini`, launches the Shipping server
directly in the current terminal, and refuses to create a duplicate instance.
The official graphical configuration launcher is therefore bypassed while the
INI remains editable through Briefcase Server Administration.

The same deployment installs `StartBriefcaseServerNoUI.bat` directly in
`DeceiveInc/Binaries/Win64`. It provides identical console-only behavior with
paths resolved relative to the Shipping executable.

## Tests

Run the fast unit-test suite during development with:

```bat
scripts\test\test_briefcase.bat -Configuration Debug
```

For a continuous TDD loop in Rider's terminal:

```powershell
dotnet watch --project tests/Briefcase.Core.Tests/Briefcase.Core.Tests.csproj test -c Debug
```

The suite covers configuration persistence and validation, the managed game-thread
scheduler, attributed patch discovery, SDK snapshot planning, the shared network
protocol and authentication, native ABI layouts, and release-version calculation.
Coverage is written in Cobertura format below `artifacts/test-results`.

The longer integration validations need the generated client and server SDKs.
Build the release packages first, then run:

```powershell
./scripts/release/PrepareRelease.ps1 -Configuration Release
./scripts/test/TestBriefcase.ps1 -Configuration Release -SkipUnit -IncludeIntegration
```

GitHub runs unit tests and the complete Windows release/integration validation as
separate pull-request checks. The release workflow repeats both gates before it
publishes archives.

## Projects

- `loader/Briefcase.VersionProxy`: `version.dll` bootstrap and Windows export forwarding.
- `runtime/Briefcase.UnrealRuntime`: native Unreal, patching, and hosting services.
- `managed/Briefcase.Rendering`: managed ImGui.NET, Direct3D 11, DirectComposition, and input backend.
- `managed/Briefcase.ModApi`: stable C# API used by mods.
- `managed/Briefcase.ManagedHost`: SDK and managed mod lifecycle.
- `managed/Briefcase.SdkEmitter`: persisted build-specific SDK emitter.
- `managed/Briefcase.SdkGenerator`: source-based validation oracle used by repository tests.
- `managed/builtins/Briefcase.ServerBrowser.Client`: persistent community-server
  directory and game-thread-safe quick connection from the F1 menu.
- `samples/Briefcase.HotReloadSample`: reload lifecycle sample.
- `samples/Briefcase.HelloSample`: minimal C# sample.
- `managed/builtins/ServerAdminControl.Client`: Core client view for balancing,
  server control, and player mod compatibility.
- `managed/builtins/ServerAdminControl.Server`: Core headless administration,
  balancing, and public mod-handshake channels on one TCP endpoint.

Game-specific user mods and historical experiments live in a separate private
repository. The public solution and release pipeline contain no references to
those projects.

See `docs/BriefcaseArchitecture.md`, `docs/CSharpRuntime.md`, `docs/GameThread.md`, `docs/ModConfiguration.md`,
`docs/AutomaticSdkGeneration.md`, `docs/Patching.md`, and
`docs/Versioning.md` for the internal model. Dependency declarations and lifecycle
ordering are documented in `docs/ModDependencies.md`; the player/server manifest
exchange is documented in `docs/ModHandshake.md`.
The paused driver project remains separate at `D:\KernelProjects\EnoMemoryLab`.
