# Windows release and installer

The release workflow builds a Windows x64 AOT archive and Inno Setup installer.

## Release inputs

`VERSION` is the application version. `NativeSource.ref` pins the exact published
BriefcaseNative commit whose injector and bootstrap will be bundled. Update the
pin whenever a release needs newer native injection components.

The release workflow needs the `UPSTREAM_READ_TOKEN` GitHub Actions secret to
fetch the gated Unreal dependency of BriefcaseNative. The secret is used only
during the native build. The Inno Setup installer is fetched from its official
release and checked against a pinned SHA-256 digest before compiling.

## Workflow

The workflow starts through a manual run on `main`. It defaults to leaving the
GitHub release as a draft so it can be reviewed before publication.

1. Check out ServerApp and the pinned BriefcaseNative source, then fetch native
   dependencies and configure the injector build.
2. Run the application contract tests and verify the marketplace catalog and
   its referenced local assets.
3. Publish the launcher and application for Windows x64 AOT. The complete ZIP
   includes the launcher, its native DLLs, the versioned app, its private native
   injection components, translations, and manifests. Verify that the app and
   launcher can both be staged from that actual ZIP.
4. Compile `installer/Briefcase.iss` using Inno Setup. Silently install it to a
   temporary directory, verify its key files and active version, then uninstall
   it. The installer is a per-user installation by default and creates a Start
   menu shortcut and an optional desktop shortcut to the launcher.
5. Upload the ZIP and installer as workflow artifacts. Create a draft GitHub
   release and verify the SHA-256 digests reported by GitHub for both assets.
   A manual run with **draft** turned off publishes it after verification.

Build/test failure stops before the release is created. An already existing
release tag causes the publish step to fail rather than replacing assets silently.

## Update behavior

The launcher downloads the complete ZIP, verifies its download checksum and
app manifest, prepares a new immutable `App/versions/<version>` directory, and
stages its own files under `App/launcher-updates/<version>`. The new app starts
before the current launcher exits. Once its window is ready, the launcher
activates the app version and closes. The app then waits for that launcher
process to exit, verifies the staged launcher manifest and SHA-256 hashes, and
replaces the launcher executable and its three native DLLs. The executable is
replaced last. A write-protected installation can request UAC at that point.
If the replacement is interrupted or UAC is declined, the verified staged files
remain available and the launcher retries them on the next start.

The installer supplies a complete first version. On a later installer run, it
keeps `App/current.json` if it already selects the same or a newer version;
otherwise it selects the bundled app. It preserves user-edited translation
files. The app's settings, catalogs, and registered server data live outside
the installation directory, and the uninstaller does not remove them or the
servers. Shortcuts point only to `Briefcase.Launcher.exe`.

## Release validation still required

Before publication, run the workflow on a commit containing the intended
native source, then test a normal launcher update, an installer repair over an
updated app, a downgrade attempt with an older installer, and an installation
in a protected directory where UAC is accepted and canceled. The local contract
tests exercise staging and replacement, but they do not substitute for those
Windows end-to-end tests.
