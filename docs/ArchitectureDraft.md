# Architecture draft (may be outdated)

# Briefcase ServerApp architecture

`Briefcase.ServerApp` is a separate Windows desktop application built with
Avalonia XAML and published as a Native AOT executable. It is not injected into
Shipping and does not replace the game's vanilla configuration window. The
existing `version.dll` proxy remains responsible for loading Briefcase and
checking updates when Shipping starts.

The app keeps a local list of dedicated-server installations in the user's
`LocalApplicationData/Briefcase/ServerApp/settings.json`. An existing server is
added by selecting its Shipping executable. A new server is installed into an
empty chosen directory with Valve's Windows SteamCMD and anonymous app
`5007710`; SteamCMD output and download progress are shown in the UI.
After a fresh SteamCMD bootstrap, a transient `Missing configuration` response
gets one automatic retry. An interrupted empty `steamapps` directory does not
prevent that retry. Errors remain visible when no server has been selected.
The same SteamCMD can compare the public and installed build IDs and update a
stopped server. Entries are independent, so more than one server can run at a
time. Removing an entry leaves its files intact; deleting an installation
requires an explicit confirmation and a clean stop. The application rejects
linked paths before removing an installation.

The app selects a dedicated server's `DeceiveInc/Binaries/Win64` directory.
Starting the server launches `DeceiveIncServer-Win64-Shipping.exe` directly,
with that Win64 directory as the working directory and the headless flags
`-unattended -NoSplash -NOCONSOLE -nullrhi -nosound`. It reads `GamePort` and
`QueryPort` from the server INI and passes them as command-line options. The
app detects only the process whose executable path matches the selected copy.
Stopping asks Briefcase's existing local Windows stop event to clean up mod
callbacks and waits for its acknowledgment. It then allows the process five
seconds to exit before terminating it. It will not force-stop a server that
has not acknowledged cleanup.

The dashboard refreshes once per second. A framework-owned UE4SS server bridge
samples the authoritative game state on the game thread and streams snapshots
to the app. The roster excludes inactive PlayerStates cached by Deceive Inc
after a disconnect, as well as bots, and requires a live controller connection.
The server name, mode and capacity come from the selected server's
INI. If the live connection is unavailable, the app shows no player count or
roster instead of guessing from a log.
Each server row also compares the running process start time to the server INI
and mod configuration timestamps. A newer file produces a restart warning.

`IServerTransport` in ServerApp owns connection establishment. Its Windows
implementation uses a local named pipe named `Briefcase.Server.<processId>`;
the UI and snapshot reader do not depend on that transport. The native bridge
likewise uses an `IServerTransport` implementation. The same line-delimited
UTF-8 JSON protocol can be carried over a Unix socket or an authenticated
network stream in a later release. The current channel only publishes
snapshots; kick and team-change commands have not been implemented.
The current snapshot schema is:

```json
{
  "schemaVersion": 2,
  "type": "snapshot",
  "processId": 1234,
  "state": "In match",
  "map": "DI_SR",
  "players": [{ "name": "Player name" }]
}
```

The reader accepts only the selected server PID and snapshots received within
five seconds. It limits message length and player entries. If no fresh snapshot
exists, the Players page explains why names are unavailable. An empty-lobby
snapshot was validated against the running development server on 7 October
2026; roster names still need validation with actual connected players.

The Configuration page edits the known `TripwireServer.ini` fields and map
rotation with form controls, preserves unrelated sections and writes a backup.
The Mods page reads active UE4SS mods from `ue4ss/Mods` and `mods.txt`, then
recursively renders primitive JSON values from each mod's `Data/config.json`;
unknown JSON structure is retained. Legacy packages under `Briefcase/Mods`
are not shown as active UE4SS mods.
The editor does not yet add or remove array entries.

The Updates page can check Steam's public build ID, update the game while
stopped, install Briefcase from a matching GitHub release, check and install
Briefcase releases through the native updater's `--install-only` option, and
run Microsoft's Visual C++ x64 redistributable with its normal UAC prompt.
The new native option must be included in a future Briefcase release before
an installed older package can be updated through this button. Starting
Shipping still checks framework and mod releases through the proxy.

The Marketplace page reads administrator-supplied HTTPS catalog URLs. Each
catalog is a small JSON document with this initial shape:

```json
{
  "schemaVersion": 1,
  "mods": [{
    "id": "BriefcaseExample",
    "name": "Example mod",
    "description": "What it changes",
    "version": "1.0.0",
    "downloadUrl": "https://example.org/BriefcaseExample-windows-x64-1.0.0.zip",
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
  }]
}
```

The ZIP contains regular files only under `ue4ss/Mods/<id>/`, including
`dlls/main.dll`. The app verifies the advertised SHA-256 and contained paths,
preserves an existing `Data/config.json` when updating and enables the mod in
`ue4ss/Mods/mods.txt`. The source URLs can be added and removed in Settings.
The catalog contract is new; current UE4SS mod release ZIPs can be indexed by
a compatible catalog after the live installation flow is tested.

Build with `dotnet build Briefcase.ServerApp/Briefcase.ServerApp.csproj`.
Run the contract executable at
`Briefcase.ServerApp.Tests/Briefcase.ServerApp.Tests.csproj`.
`scripts/Publish-ServerApp.ps1` stages the four Windows x64 AOT runtime files
in `dist/ServerApp` and packages them in
`dist/Briefcase.ServerApp-win-x64.zip`. The archive contains no PDB files.

