# Windows installer plan

Status: deferred. Keep distributing the complete AOT ZIP until the installer is implemented and tested.

## Goal and layout

Use the existing `scripts/Publish-ServerApp.ps1` output as the installer input. An initial per-user installer (Inno Setup is a suitable candidate) should place the application under `%LOCALAPPDATA%\Programs\Briefcase`, register it with Windows, and create Start menu and optional desktop shortcuts to `Briefcase.Launcher.exe` only. It should not install a dedicated server or any gameplay mods.

Keep the published directory structure intact:

```text
Briefcase.Launcher.exe
libSkiaSharp.dll, libHarfBuzzSharp.dll, av_libglesv2.dll
UpdateSource.json
Translations/en.json, Translations/fr.json
App/current.json
App/versions/<version>/Briefcase.ServerApp.exe
App/versions/<version>/manifest.json
App/versions/<version>/Native/Briefcase.ServerInjector.exe
App/versions/<version>/Native/Briefcase.ServerBootstrap.dll
App/versions/<version>/Translations/en.json, Translations/fr.json
```

The installer supplies a complete first version. The launcher remains the entry point for later application updates: it checks the configured release, downloads and verifies the AOT archive, installs a new immutable `App/versions/<version>` directory, starts it, and then changes `App/current.json`. The installer should not become a second automatic updater. A future all-users installation under Program Files may be offered separately; its writes need administrator approval.

## Installation and repair rules

- Give the installer a stable application identity and upgrade path. Preserve the selected installation directory on upgrades.
- A repair or reinstall must not replace an existing `App/current.json` with a pointer to an older bundled version. It must not delete other installed version directories before the launcher has successfully switched versions.
- Do not overwrite user-edited translation files without an explicit repair choice. The built-in English fallback remains available.
- Keep server installations and `%LOCALAPPDATA%\Briefcase\ServerApp\settings.json` outside the install directory. Uninstalling the app must never remove registered server installations, Briefcase, UE4SS mods, or their configurations. Decide separately whether the uninstaller offers to remove application preferences.
- Package the launcher, the initial app version, native injection components, and all required native runtime DLLs together. Shortcuts must never point directly to a versioned app executable.
- Keep automatic GitHub update checks disabled while the release repository is private for users without access. Enable `UpdateSource.json` only when the release and distribution policy is ready.
- Sign the installer and executables before broad public distribution when a suitable Windows code-signing identity is available. Keep artwork redistribution pending the publisher's permission.

## Acceptance checks before release

Test a clean per-user install, a normal launch, an in-app update, a repair over an already updated version, a reinstall from an older installer, and uninstall/reinstall. Confirm that the active version, editable translations, saved server list, and external server files survive the relevant operations. Also test an installation in a protected directory, where update writes may request UAC, and cancellation of that request. Do not add an AOT archive build to every ordinary source edit; build the installer from a deliberate release package.
