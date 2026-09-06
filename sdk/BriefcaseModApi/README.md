# Briefcase native ABI

These headers define the internal boundary between `version.dll` and
`Briefcase.ManagedHost`. Mod authors use `Briefcase.ModApi` from C#; they do not
compile against these headers or deploy a native mod DLL.

`Briefcase/BriefcaseModApi.h` contains fixed-width C structures and function
pointers. `Briefcase/BriefcaseMod.hpp` provides small C++ helpers for the native
runtime. Each public service table starts with its structure size and API
version, allowing the native and managed sides to validate compatibility before
calling one another.

The ABI covers core logging, Unreal object and reflection services, rendering,
input, and patch dispatch. It avoids C++ containers, exceptions, CRT-owned
memory, and raw Unreal pointers across the managed boundary.
