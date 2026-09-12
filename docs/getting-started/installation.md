# Install Briefcase

Briefcase releases contain everything required at runtime. Players and server
administrators do not need Visual Studio, an external .NET installation,
UE4SS, or a kernel driver.

Download the appropriate archive from
[GitHub Releases](https://github.com/EnoPM/Briefcase/releases):

| Archive | Destination |
| --- | --- |
| `Briefcase-Client-vX.Y.Z.zip` | The Deceive Inc. game client |
| `Briefcase-Server-vX.Y.Z.zip` | A Deceive Inc. dedicated server |

## Game client

1. Close Deceive Inc.
2. In Steam, open the game properties and browse the installed files.
3. Open `DeceiveInc\Binaries\Win64`.
4. Extract the client archive directly into that folder.
5. Start the game normally.

The `Win64` folder must now contain both `version.dll` and a `Briefcase`
folder. Briefcase shows its loading progress during startup. Press **F1** after
the game is ready to open its configuration menu.

Install client mod files in:

```text
DeceiveInc\Binaries\Win64\Briefcase\Mods
```

Follow the instructions supplied with each mod because it may have additional
dependencies or require a compatible community server.

## Dedicated server

1. Stop the dedicated server.
2. Open `DeceiveInc\Binaries\Win64` in its installation folder.
3. Extract the server archive directly into that folder.
4. Run `StartBriefcaseServer.bat` from the same folder.

The script starts the Shipping server in a terminal and uses the existing game
and query ports from `TripwireServer.ini`. Press **Ctrl+C** in the terminal to
stop it.

Install server mod files in:

```text
DeceiveInc\Binaries\Win64\Briefcase\Mods
```

The server runtime is headless. It does not contain Avalonia, an overlay, input
handling, or an F1 menu.

## Logs

Client and server installations write their log to:

```text
DeceiveInc\Binaries\Win64\Briefcase\Briefcase.log
```

The log records the detected game build, runtime initialization, SDK status,
loaded mods, dependencies, and errors. Include the relevant section when
opening a [GitHub issue](https://github.com/EnoPM/Briefcase/issues), after
removing any information you do not want to share publicly.

## Update or remove Briefcase

Briefcase automatically checks the latest stable GitHub release at startup. A
new client version is downloaded behind the centered progress overlay. A new
server version reports its progress in the terminal and log. Briefcase then
closes, installs the verified package, and restarts automatically.

Installed mods, `loader.json`, saved settings, logs, and server configuration
are preserved. See [Automatic updates](../AutomaticUpdates.md) for the exact
replacement rules and optional settings.

To update manually, close the game or server and extract the newer matching
archive over the existing installation.

To remove Briefcase, delete its `version.dll`, its `Briefcase` folder, and the
server start script when applicable. Restore a previous `version.dll` if you
replaced one belonging to another tool.
