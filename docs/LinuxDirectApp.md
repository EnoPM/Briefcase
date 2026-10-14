# Direct Linux application (pre-agent architecture)

## Install on Ubuntu 24.04

Download `Briefcase-X.Y.Z-linux-amd64.deb` from the matching Briefcase release,
then install it with `sudo apt install ./Briefcase-X.Y.Z-linux-amd64.deb`.
Launch **Briefcase** from the applications menu. The package installs the app
under `/opt/briefcase` and keeps each user's profiles and servers in that user's
data directory. Install a newer `.deb` to update this package-managed copy.

The same release also includes `Briefcase.ServerApp-linux-x64-X.Y.Z.zip` for a
portable installation. Extract it to a folder owned by your user and run
`./Briefcase` there. The portable copy can update itself through the launcher.
Do not mix the contents of the ZIP into an installation managed by `apt`.

Linux mods need separate `.so` packages. A Windows mod archive containing a
`.dll` cannot be installed on Ubuntu; the marketplace shows such packages as
unavailable until a Linux build is published for that mod.

This branch starts from the published ServerApp 0.1.5 source, before the client/agent split. It keeps server management inside the Avalonia application on Ubuntu. It does not change the shared SteamCMD game files: each server keeps its INI, balance profile, Briefcase runtime and mods in its own instance directory. On Linux, the game ignores `-TripwireServerIni`; the app therefore creates `GameView` in each instance. The view copies the roughly 110 MB Shipping executable, links immutable Steam assets, and keeps the project's INI, balance profile and `Saved` logs private. A separate executable inode is required by the native launcher's safety checks. SteamCMD updates are blocked while an instance using that shared installation runs.

## Local AOT test build

On Ubuntu, with .NET 10, Clang, and zlib development headers installed:

```sh
./scripts/Publish-LinuxLocal.sh \
  --native-package /path/to/BriefcaseNative-Server-linux-x64-0.8.7.zip \
  --output "$HOME/Briefcase-linux-test"
"$HOME/Briefcase-linux-test/Briefcase"
```

The script verifies the package identity, bundles its private native server launcher, and publishes both the splash launcher (`Briefcase`) and the Avalonia app as Linux x64 AOT executables. It creates `Briefcase.desktop` in the output folder; copying that file to `~/Desktop` or `~/.local/share/applications` is optional. The script does not start the app and does not modify an existing output folder.

For an updater-compatible installation and archive, use `Publish-LinuxRelease.sh` with the same `--native-package` and a new `--output` directory. It also accepts `--version X.Y.Z`. The script creates `Briefcase.ServerApp-linux-x64-X.Y.Z.zip` beside the output directory, with manifests and SHA-256 records for the app and launcher. Add `--enable-updates` only for a published release whose GitHub release has this exact Linux archive and digest. The launcher keeps the prior app version selected until the new main window opens. It stages a verified launcher replacement, which the app applies after the old launcher exits. Run `python3 scripts/Test-LinuxRelease.py /path/to/release-folder` to check the splash-to-app handoff on a private X display; this does not use the user's desktop or profile.

The app uses the bundled native package as an offline fallback. If a newer verified Linux server archive becomes available in the latest BriefcaseNative GitHub release, it downloads and installs that instead. This source branch does not publish any release.

## Linux mods

Catalog schema 3 supports `packages.windows-x64` and `packages.linux-x64`, each with `downloadUrl`, `archiveSha256`, and optional `installId`. `installId` identifies the actual Briefcase/Mods directory and can differ from the catalog's stable ID. Schema 1 and 2 catalogs remain readable; their Windows archives are visible but cannot be installed on Linux.

Linux mod ZIPs require `ModPackage.json` with an exact file inventory and SHA-256 per file, plus `Briefcase/Mods/<installId>/briefcase.mod.json` and the declared `.so`. The app rejects unexpected files, links, traversal, mismatched IDs or versions, and changed hashes. Installation stages a new directory, keeps the existing `Data/config.json`, swaps the directory while the server is stopped, and records catalog metadata. Installed native mods can be enabled, disabled and removed through `Briefcase/settings.json`.

## Verification so far

- Ubuntu VM: fresh server download, instance creation, per-instance Briefcase 0.8.7 installation, server start and stop.
- Linux app contracts: schema 2 Windows-only entries unavailable, schema 3 Linux package selection, native mod install/update preserving configuration, tampered package rejection, mod disable and uninstall.
- PregameTimer: SDK 0.8.7 build and tests passed; the dedicated server logged `Mod loaded: briefcase.pregame-timer` and armed its reflected hook. The disposable server instance then stopped normally.
- Linux updater contracts: exact archive inventory, SHA-256 validation, executable permissions, activation, launcher replacement and rejected tampered archive passed. The real AOT archive also installed through these checks, and its launcher opened the app on an isolated display.
- A temporary copy of the real AOT installation applied a staged launcher replacement after the old launcher exited. A failed app startup left the previous version selected.
- Against Steam build 25777350 on the VM, a new Linux instance saved its server settings and one-map rotation, opened and saved its balance profile without changing the shared template, and opened and saved the installed PregameTimer configuration.
- A disposable managed server installed the corrected native archive through the bundled native launcher, then launched from the app's `ServerProcess` path. The game's own log confirmed its private server name, player cap and balance profile; the shared Steam executable kept its SHA-256. The matching supervisor stopped it, and no test server remained running. The native administration service also provisioned an identity in that instance after its path handling was corrected. The same Linux game build can emit a malloc-corruption/pure-virtual diagnostic on SIGTERM even without Briefcase or the game view; this is a separate vanilla shutdown behavior observed in the VM.
- The Windows app and launcher still compile, and the existing Windows ServerApp contracts pass. Steam recovery now retries transient Windows file locks during its final directory swap.
- The Linux native host now provides a private Unix socket per server process. The standalone app uses the existing live snapshot parser over that socket. On the Ubuntu VM, the current AOT build connected to an actual modded server and received `Lobby`, the active map and an empty player roster; its C# live client and isolated profile checks passed. The server log confirmed PregameTimer was loaded and armed. The user has since tested kick with a connected player; team commands still need a connected-player test.
- A separate `Briefcase Linux Bridge Test` desktop launcher on the VM uses `/home/briefcase/BridgeTest/AppData` and the disposable instance under `/home/briefcase/BridgeTest/5a48b2339c274b67a6d66a7e3458f5d0`. The original app profile and shared SteamCMD build are unchanged.
- The Ubuntu Overview now retries its Unix bridge connection when the game starts after the app. A regression test creates the socket after the first failed connection and verifies that map and player-count data arrive. The per-instance Linux log reader uses a read-only POSIX file descriptor because Unreal locks its active log against .NET `FileStream`; against the VM's running server it returned `88.166.204.6:57779`, UPnP failed and EOS connected. The game does not report Linux firewall rules, so the UI labels that check "Not checked" rather than guessing a result. A corrected AOT build is at `/home/briefcase/Briefcase-linux-bridge-fixed2`, with the desktop shortcut updated to launch it.
- The Linux level names `LVL_Variant_FragrantShore_Night` and `LVL_Variant_HardSell_Day` are mapped through the bundled English and French translation files. The VM test app's external language files have been updated; reopening the app selects the new aliases.
- The Linux bridge now collects the connected player's platform and IDs, selected agent, account and agent levels, gadgets, loadout, upgrades and live health using reflected game data. The changed native host compiled and all 17 Linux contracts passed; a copy was placed in the disposable VM instance with its previous library preserved as `.pre-player-details`. In a connected-player playtest, the user confirmed that the player appeared with all details in the Ubuntu app. The server log reported the Linux collector ready and no bridge error during that check.
- Unreal appends bare `MapRotation` entries when it saves the server settings. The VM's old AOT app lacked the later rotation repair, leaving 92 entries in the instance INI for seven selected maps. The instance INI was backed up and repaired to eight directives (clear plus seven maps). The Linux supervisor now directs Unreal's `-TripwireServerIni` writes to the disposable `GameView` copy; the app refreshes that copy from the canonical instance INI before launch. A 25-second launch/stop test changed the disposable copy from 8 to 22 directives while the canonical file stayed at 8 and the app still read exactly 7 maps. The `Briefcase Linux Bridge Test` shortcut now points at `/home/briefcase/Briefcase-linux-bridge-fixed3`, rebuilt with the rotation repair and the new supervisor. Linux launcher contracts and Linux ServerApp contracts passed.

## Required before a public Linux release

- Publish and validate a Linux BriefcaseNative server archive; the current 0.8.7 GitHub release has no Linux server asset. The corrected local test package is in `artifacts/vm/native-package-v7` of the BriefcaseNative workspace.
- Validate team commands with connected players. The enriched Linux live roster was confirmed in the Ubuntu app, and kick has been tested; effect and spectator controls are still Windows-only. The VM's bundled native archive still contains the earlier bridge until a new local package is built.
- Revalidate Linux native sites for PlayerCap and SuspicionControl against the current Shipping ELF. Their old Linux offsets must not be reused merely by changing the hash. Build/package SpectatorMode if parity with Windows is required. PregameTimer is the working Linux mod used for this test.
- Publish a Linux application archive and test a real GitHub update and rollback from one published version to another. Local packaging, manifest verification, activation, launcher replacement and splash-to-app handoff are implemented; the current test package keeps network updates disabled.
- Exercise configuration, balance, installation, mod forms and translations end-to-end in the AOT app with the current game build before publishing.
