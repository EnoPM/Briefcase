# ServerAdminControl.Client

This Briefcase Core built-in is the in-game administration client for a Deceive
Inc. community server. The **Server** view in the F1 window uses vertical
**Server**, **Players**, **Balancing**, **Mods**, and **Compatibility** pages. It
edits the typed dedicated-server configuration, restarts the process, edits
vanilla community balancing, controls managed server mods, and inspects the
player/server mod manifests.

## Data flow

1. The client calls `Server_RequestCommunityBalanceProfile` after connecting.
2. The server replies through `Client_ReceiveCommunityBalanceProfile`.
3. Briefcase copies the RPC's `TArray<uint8>` and `FString` while the Unreal
   parameter buffer is valid. No native game pointer reaches the mod.
4. The mod decompresses the zlib payload and wraps every override as a typed
   setting with a category, section, readable name, description and range.
5. Numbers, booleans and text use appropriate validated controls in the F1 tab.
6. Applying changes sends the reconstructed profile to
   `ServerAdminControl.Server`, then requests the profile through the game's RPC.

The **Server mods** page lists the server's `Briefcase/Mods` directory. It can
enable, disable, load, unload, reload and refresh installed DLLs. Commands use
the DLL file name; clients cannot submit an arbitrary server file-system path.
The administration endpoint is a Core built-in and does not appear in the
manageable server-mod list.

Administration and compatibility share the configured Briefcase endpoint,
`127.0.0.1:47000/TCP` by default. Unreal keeps its game traffic on `50000/UDP`.
The frame's channel field determines which bounded protocol handles it.

Administration protocol version 7 authenticates every command with a one-use,
ten-second challenge. The client derives a key from the password entered on the
**Server** page with PBKDF2-SHA256, then signs the nonce, request ID and requested
operation with HMAC-SHA256. The password itself is never transmitted. It must
match `AdminPassword` in the dedicated server's `TripwireServer.ini`.

The wrapper keeps source identifiers and unknown properties internally. New
settings therefore remain visible after a game update, using a text editor when
their value type cannot be inferred yet. The user interface never exposes the
JSON transport representation.

## Manual test

Build the complete framework with:

```bat
scripts\build\build_framework.bat Release
```

Or rebuild only this mod after the framework and generated SDK have already
been deployed:

```bat
scripts\build\build_server_admin_control_client.bat Release
```

Connect to a community server, press F1, select **Server** at the top, and enter
the administration password before refreshing the configuration.
The **Balancing** page shows searchable categories and documented settings; the
**Server mods** page exposes the server-side Briefcase lifecycle controls.
