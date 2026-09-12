# Building Briefcase

This page is intended for Briefcase contributors and mod developers. Players
install the ready-to-use archives described in the root README and do not need
these tools.

## Requirements

- Windows x64;
- Visual Studio 2026 or Visual Studio Build Tools with the MSVC v143 x64 tools;
- the .NET 10 SDK.

Rider, Visual Studio, and other editors can all open the repository. The build
does not depend on a particular IDE.

## Local game paths

Repository scripts never contain a contributor's installation paths. Copy the
local template once and edit the ignored copy:

```powershell
Copy-Item scripts/LocalPaths.example.ps1 scripts/LocalPaths.ps1
```

`scripts/LocalPaths.ps1` configures the client and dedicated-server `Win64`
directories for deployment and fast development builds. Command-line arguments
take priority over environment variables, and environment variables take
priority over this local file:

- `-GameWin64` or `BRIEFCASE_CLIENT_GAME_DIR` for the client;
- `-ServerWin64` or `BRIEFCASE_SERVER_GAME_DIR` for the dedicated server.

The local file is ignored by Git. Do not add machine-specific paths to tracked
PowerShell or batch files.

## Build the client framework

From the repository root, run:

```bat
scripts\build\build_framework.bat Release
```

The portable client package is written to `dist/Briefcase`. The command does
not modify a game installation.

## Build the server framework

Run:

```bat
scripts\build\build_server_framework.bat Release
```

The headless server package is written to `dist/Briefcase.Server`. It contains
no Avalonia UI, rendering backend, input hook, or F1 menu.

## Create release archives

Run:

```powershell
./scripts/release/PrepareRelease.ps1 -Configuration Release
```

This creates the following files below `artifacts/release`:

- `Briefcase-Client-v<VERSION>.zip`;
- `Briefcase-Server-v<VERSION>.zip`.

Each archive is rooted so that it can be extracted directly into the matching
`DeceiveInc/Binaries/Win64` directory. The root `VERSION` file is the version
source used by managed assemblies, native binaries, archive names, and release
tags.

## Tests

Run the unit-test suite with:

```bat
scripts\test\test_briefcase.bat -Configuration Debug
```

For a continuous TDD loop:

```powershell
dotnet watch --project tests/Briefcase.Core.Tests/Briefcase.Core.Tests.csproj test -c Debug
```

Build the release packages before running all integration validations:

```powershell
./scripts/release/PrepareRelease.ps1 -Configuration Release
./scripts/test/TestBriefcase.ps1 -Configuration Release -SkipUnit -IncludeIntegration
```

Coverage is written in Cobertura format below `artifacts/test-results`.
GitHub runs the unit tests and the Windows release/integration validation as
separate pull-request checks.

## Repository areas

- `loader`: the small Windows proxy that starts Briefcase;
- `runtime`: native Unreal discovery, reflection, invocation, and patching;
- `managed`: the .NET host, public mod APIs, SDK tools, UI, and built-in modules;
- `samples`: small example mods;
- `tests` and `validation`: automated verification;
- `docs`: architecture and mod-development documentation.

Game-specific user mods are kept in separate repositories. Release packages
contain an empty `Briefcase/Mods` folder for mods installed by the player or
server administrator.

## Versioned releases

After changes have been reviewed, run the **Create Version** workflow from the
GitHub Actions page. Select the integration branch and choose `build`, `minor`,
or `major`. The workflow merges that branch into `main`, updates `VERSION`,
creates the matching tag, validates the release, and publishes both archives.

See [Versioning](Versioning.md) for the complete release model.
