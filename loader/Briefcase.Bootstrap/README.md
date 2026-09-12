# Briefcase.Bootstrap

`Briefcase.Bootstrap.dll` is the injection entry point used by
`Briefcase.Launcher.exe`. Its exported function validates a versioned request,
loads the adjacent `Briefcase.UnrealRuntime.dll`, and invokes the same runtime
ABI as the `version.dll` proxy. `DllMain` performs no runtime initialization.