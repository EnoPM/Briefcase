# Briefcase.Launcher

This experimental x64 console launcher starts a Deceive Inc. client or dedicated
server, loads `Briefcase/Core/Native/Briefcase.Bootstrap.dll` with
`LoadLibraryW`, invokes its explicit bootstrap export, and waits for the game to
exit.

Before starting anything, the launcher looks for a running process whose full
executable path exactly matches the requested Shipping executable. If found, it
attaches to that process and injects Briefcase without launching another copy.
If the runtime is already loaded, the launcher reports that state and does not
initialize it twice. Command-line arguments are ignored when attaching because
the target has already started.

For an injection-only test, remove or temporarily rename the local `version.dll`.
The launcher refuses to run while that proxy is present unless
`--allow-local-version` is supplied explicitly.

```text
Briefcase.Launcher.exe
Briefcase.Launcher.exe --executable "D:\path\DeceiveInc-Win64-Shipping.exe"
Briefcase.Launcher.exe -- --game-argument
Briefcase.Launcher.exe --launcher "D:\path\DeceiveInc.exe" --wait-for "D:\path\DeceiveInc-Win64-Shipping.exe"
```
