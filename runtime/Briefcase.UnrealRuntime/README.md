# Briefcase.UnrealRuntime

This static x64 library provides the native half of Briefcase. It is linked
directly into the proxy `version.dll`; no separate runtime DLL is distributed.

Its responsibilities include:

1. validating the executable profile before accessing Unreal data;
2. resolving and validating Unreal reflection metadata;
3. exposing versioned object, property, invocation, patching, rendering, and
   input services to `Briefcase.ManagedHost`;
4. writing native diagnostics to `Briefcase/Briefcase.log`.

The runtime does not use UE4SS. ASLR is handled with addresses relative to the
loaded image, and the exact PE timestamp, image size, signatures, and structural
invariants are checked before the corresponding feature becomes available. An
unknown game build fails closed.

Build the complete framework with:

```bat
scripts\build\build_framework.bat Release
```

Install the resulting `dist/Briefcase` package with the game closed by running
`scripts\deploy_framework.bat Release`.
