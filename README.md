# Briefcase ServerApp

Briefcase ServerApp is a Windows application for installing and managing Deceive Inc. dedicated servers. It provides a visual way to install the game server, add an existing installation, start and stop servers, edit server settings and map rotation, install Briefcase, manage mods, and view connected players.

## Requirements

- Windows x64
- Enough free disk space for the Deceive Inc. dedicated server
- An internet connection for initial installation and updates

## Getting started

1. Once a release is available, use the Windows installer or extract the complete ZIP into one folder. Keep the `App`, `Launcher`, and `Translations` folders beside `Briefcase.exe`.
2. Run `Briefcase.exe`. A small splash screen checks for an application update, shows download progress when needed, and opens Briefcase. It closes automatically when the main window appears. A verified newer launcher is then installed by the app after the old launcher exits.

If you installed version 0.1.0, install version 0.1.1 once using the installer or replace the complete portable folder. The 0.1.0 launcher cannot perform the executable rename automatically.
3. Select the **+** button beside **Servers**. In the dialog, choose **Install a new server** or **Open an existing server**. A new server uses the data directory's `servers` folder by default; check **Choose a different installation folder** to use another empty folder. For an existing server, select `DeceiveIncServer.exe` in the installation folder or `DeceiveIncServer-Win64-Shipping.exe` in `DeceiveInc\Binaries\Win64`.
4. Select your server in the left column. Use **Configuration** to set its name, ports, player limit and map rotation.
5. Use **Balancing** to choose **Characters**, **Gadgets**, or **Game**. For a character, choose their portrait, then **Weapons**, **Passives**, or **Expertises**, and finally the named variant. You can search by its game name, setting label, key, or value from any step. Save your changes when finished; the app keeps a backup of the previous profile.
6. On **Overview**, install Briefcase. The application checks its version and installs a newer release while the server is stopped. It can also check the game's dedicated-server version.
7. Open **Mods → Available Mods** to browse the official Briefcase catalog, select a mod, and install it. The app adds the official catalog to new and existing settings once; you can remove it or add other catalog URLs in Settings. The app checks catalogs when it opens and caches validated copies for offline use. Use **Refresh catalogs** to check again. A mod's author, documentation, community link, version, and update status appear here when its catalog supplies them.
8. Select **Start server** on the Overview page. Before launch, the application applies known Briefcase and installed-mod updates and prepares the server configuration. It renames a local `version.dll` to `version.dll.disabled`, explains this change in the app, and starts the headless server with injection components bundled privately with ServerApp.
9. Open **Mods → Installed Mods** to edit a mod's configuration. Stop the server first if you want to enable, disable, or uninstall a mod. Uninstalling removes that mod's configuration too and asks for confirmation.

The **Players** page appears while a selected server is running. It shows live player information and offers a kick action. If you change server or mod settings while a server is running, restart it to apply those changes.

The application can manage multiple server installations. Removing a server from the list leaves its files in place; deleting its installation requires a separate confirmation.

Briefcase and installed mod updates are performed by ServerApp while the server is stopped. Briefcase's injected libraries do not download updates during game startup. Mods installed from catalogs retain their catalog source and version so the app can identify newer releases. The app verifies archive checksums before installing them, and verifies a logo's checksum when the catalog provides one.

After ServerApp has disabled `version.dll`, starting the dedicated server directly through Steam runs it without Briefcase or UE4SS mods. ServerApp continues to launch its managed, modded server through the injector. The mod files remain under `Win64/Briefcase` and `Win64/ue4ss`; their location alone does not load them into a direct Steam launch.

## Language

Open the gear icon in the upper-right corner to choose a language. Save it and restart the application. This also changes displayed game names, including maps, characters, weapons, passives, and expertises.

You can edit the JSON files in `Translations` beside `Briefcase.exe`; files in this shared folder remain available across app updates. To add a language, copy `en.json` to a new file such as `es.json`, set `language.name` to the name users should see in the language list, and translate the values while keeping the keys. Map names in `game.maps.*` are also recognized as aliases when a server reports them; `game.map_alias.*` maps additional server level names to map IDs. Restart the application after adding or editing a file. Missing or invalid translations fall back to bundled translations and then built-in English text. When running the app directly during development, use the `Translations` folder beside its executable.

## Data directory

Open the gear icon to create or switch application profiles. Your existing data becomes the **Default** profile. Each new profile starts with a separate, empty data directory under `%LOCALAPPDATA%\Briefcase\Profiles`; creating or switching a profile restarts the application. Servers already running under another profile continue to run and can be managed when you switch back. The profile list and active selection are stored outside the profiles' data directories.

Within the active profile, choose **Briefcase data directory** to move its settings, downloads, catalogs, and servers to another empty folder. Other profiles keep their own locations. If a server in that directory is running, Briefcase warns you and asks for confirmation before stopping it and moving the data. The move is canceled if a server cannot be stopped cleanly. Servers added from other folders remain in place. New servers use the selected directory's `servers` folder unless you choose a different installation folder when creating one.

## Game artwork

This repository includes selected Deceive Inc. interface artwork under `Briefcase.ServerApp/Assets/Game/UI`. The artwork remains the property of its respective rights holders. The publisher has allowed these images to be used for this community application.

## Releases

The Windows release workflow, installer layout, and validation requirements are described in [docs/InstallerPlan.md](docs/InstallerPlan.md). Releases are started manually from GitHub Actions and checked as drafts before publication.

To test the launcher with the application in its installed layout, publish both as local Windows AOT builds:

```powershell
./scripts/Deploy-ServerAppLocal.ps1 -OutputDirectory 'D:\BriefcaseLocalTest'
```

Run `D:\BriefcaseLocalTest\Briefcase.exe`. The script builds the native injection components, publishes the launcher and app, and deploys them in the same directory structure as a release. It does not create an archive. Network updates are disabled by default; pass `-EnableUpdates` if you explicitly want to test them. You can run the script again against the same folder after closing the app and launcher; it keeps previous app versions and selects the newly deployed version.
