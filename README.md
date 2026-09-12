# Briefcase

[![Checks](https://github.com/EnoPM/Briefcase/actions/workflows/checks.yml/badge.svg)](https://github.com/EnoPM/Briefcase/actions/workflows/checks.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

[Documentation](https://enopm.github.io/Briefcase/) ·
[Releases](https://github.com/EnoPM/Briefcase/releases) ·
[Report a problem](https://github.com/EnoPM/Briefcase/issues)

Briefcase is a mod loader for **Deceive Inc.** It lets you add community-made
mods to the game or to a dedicated server by copying files into the game
folder. Everything Briefcase needs is included in the download: you do not
need to install .NET, UE4SS, a driver, or development tools.

The client version provides an in-game menu. Press **F1** to open it and
configure Briefcase and compatible mods. The server version runs without a
graphical interface and is intended for Deceive Inc. dedicated servers.

Briefcase is still in development. A game update can require a new compatible
Briefcase release before mods work again.

## Download

Open the [Briefcase Releases page](https://github.com/EnoPM/Briefcase/releases)
and download the archive that matches where you want to use mods:

| Archive | Use it for |
| --- | --- |
| `Briefcase-Client-vX.Y.Z.zip` | The Deceive Inc. game client |
| `Briefcase-Server-vX.Y.Z.zip` | A Deceive Inc. dedicated server |

The two archives are different. Do not install the client archive on a server
or the server archive in the game client.

## Install Briefcase on the game client

1. Close Deceive Inc.
2. Download `Briefcase-Client-vX.Y.Z.zip` from the Releases page.
3. In Steam, open the properties of Deceive Inc. and browse its installed
   files.
4. Open `DeceiveInc\Binaries\Win64` inside the installation folder.
5. Extract the archive directly into that `Win64` folder.
6. Start Deceive Inc. normally.

The resulting folder should contain:

```text
DeceiveInc\Binaries\Win64\
|-- version.dll
`-- Briefcase\
```

Briefcase displays its loading progress while the game starts. Once the game
is ready, press **F1** to open the configuration menu.

### Install a client mod

Close the game and follow the instructions supplied by the mod author. A
typical mod is copied into:

```text
DeceiveInc\Binaries\Win64\Briefcase\Mods
```

Start the game again and press **F1**. The mod appears in **Mods** when
Briefcase has loaded it successfully. Some mods may require a compatible
community server or other mods; their download page should list those
requirements.

## Install Briefcase on a dedicated server

1. Stop the dedicated server.
2. Download `Briefcase-Server-vX.Y.Z.zip` from the Releases page.
3. Open `DeceiveInc\Binaries\Win64` inside the dedicated-server installation.
4. Extract the archive directly into that `Win64` folder.
5. Start the server with `StartBriefcaseServer.bat`, which is included in the
   archive.

The resulting folder should contain:

```text
DeceiveInc\Binaries\Win64\
|-- version.dll
|-- StartBriefcaseServer.bat
`-- Briefcase\
```

`StartBriefcaseServer.bat` starts the dedicated server in a terminal without
the graphical configuration launcher. It uses the game and query ports from
the server's existing `TripwireServer.ini` file. Press **Ctrl+C** in that
terminal to stop the server.

### Install a server mod

Stop the server and follow the instructions supplied by the mod author. Server
mods are normally copied into:

```text
DeceiveInc\Binaries\Win64\Briefcase\Mods
```

Start the server again with `StartBriefcaseServer.bat`. Server-side mods do not
create an F1 menu on the server.

## Automatic updates

Briefcase checks for a newer stable release whenever the game or dedicated
server starts. The client shows a simple progress bar over the game while the
update downloads. The server reports the progress in its terminal and log.

After verification, Briefcase closes the current process, installs the update,
and starts the game or server again. Installed mods, saved settings,
`loader.json`, server configuration, and logs are preserved.

If an update cannot be checked or downloaded, the installed version continues
to load. Manual installation remains available by extracting the matching
archive over the Win64 folder while the game or server is stopped. See the
[automatic update guide](https://enopm.github.io/Briefcase/AutomaticUpdates.html)
for configuration and recovery details.

## Remove Briefcase

Close the game or stop the server, then remove:

- `version.dll` installed by Briefcase;
- the complete `Briefcase` folder;
- `StartBriefcaseServer.bat` for a server installation.

If another tool previously used its own `version.dll`, restore the backup of
that file after removing Briefcase.

## Troubleshooting

Briefcase writes its log here:

```text
DeceiveInc\Binaries\Win64\Briefcase\Briefcase.log
```

If Briefcase does not start:

1. Check that `version.dll` and the `Briefcase` folder are directly inside
   `DeceiveInc\Binaries\Win64`.
2. Check that you installed the correct client or server archive.
3. Check the Releases page for a version compatible with the current game
   update.
4. Read the last error in `Briefcase.log`.

When reporting a problem, open a
[GitHub issue](https://github.com/EnoPM/Briefcase/issues) and attach the log
after removing any information you do not want to share publicly.

## For mod developers

Briefcase mods are written in C# and can use a generated SDK that describes the
supported Deceive Inc. types for the installed game build. Start with the
[developer build guide](docs/Building.md), then see the documentation for
[the C# runtime](docs/CSharpRuntime.md),
[configuration](docs/ModConfiguration.md),
[mod dependencies](docs/ModDependencies.md),
[the game thread](docs/GameThread.md), and
[Unreal patching](docs/Patching.md).

The generated SDK is an aid for discovering the game API. It can be explored
with any .NET assembly browser, including dnSpyEx or dotPeek; no particular IDE
is required.

## License

Briefcase is released under the [MIT License](LICENSE).
