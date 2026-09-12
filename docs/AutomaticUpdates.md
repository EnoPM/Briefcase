# Automatic updates

Briefcase checks the latest stable release on GitHub whenever the game client or
dedicated server starts. Drafts and prereleases are ignored.

If the installed `Briefcase/VERSION` is older than the release:

1. Briefcase selects the archive matching the current process: Client or Server.
2. It downloads the archive to a temporary directory.
3. The downloaded byte count is checked against the GitHub asset metadata.
4. Its SHA-256 digest is compared with the digest published by GitHub.
5. The ZIP structure and embedded `Briefcase/VERSION` are validated.
6. A standalone installer is copied outside `Briefcase/Core`.
7. The current process exits, the installer replaces framework files, and the
   game or server starts again.

The client displays this work in the centered startup overlay, including a
download progress bar. The headless server writes the same stages to its log and
terminal.

## Preserved files

An automatic update replaces only files owned by the Briefcase distribution:

- `version.dll`;
- `Briefcase/Core`;
- `Briefcase/VERSION`;
- the packaged license and readme;
- `StartBriefcaseServer.bat` for a server package.

The following data is preserved:

- every DLL in `Briefcase/Mods`;
- `Briefcase/loader.json`;
- `Briefcase/settings.json` and all mod configuration;
- `Briefcase/Briefcase.log` and `Briefcase/Briefcase-update.log`;
- the dedicated-server configuration stored by Deceive Inc.

The installer keeps a backup while replacing files. If replacement fails, it
restores the previous installation and attempts to restart it. Recovery files
are kept when automatic rollback cannot complete.

## Configuration

Automatic updates are enabled by default. These optional keys live at the root
of `Briefcase/loader.json`:

```json
{
  "automaticUpdates": true,
  "updateRestartMode": "auto"
}
```

`updateRestartMode` affects the game client:

| Value | Behavior |
| --- | --- |
| `auto` | Restart through Steam when the process exposes a Steam app ID; otherwise restart the Shipping executable. |
| `steam` | Always ask Steam to restart Deceive Inc. |
| `executable` | Restart the current Shipping executable directly. |

The dedicated server always restarts its Shipping executable with the same
command-line arguments so its ports and unattended logging options are retained.

Set `automaticUpdates` to `false` only when you need to keep one exact Briefcase
build for development. Manual installation remains available by extracting a
new matching archive over the Win64 directory while the game or server is
stopped.

## Failure behavior

A network, GitHub, download, digest, or archive validation failure does not
prevent Briefcase from starting. It logs the reason and continues with the
installed version. An installation error is also recorded in:

```text
DeceiveInc\Binaries\Win64\Briefcase\Briefcase-update.log
```
