# Native startup modules

Some game changes must run before Unreal initializes a subsystem. Waiting for
reflection, SDK extraction, and CoreCLR is too late for those changes even when
the rest of a mod is written in C#.

Briefcase loads optional native startup modules from:

```text
Briefcase/Mods/Startup/*.dll
```

Loading happens before the Unreal metadata probe and before the managed host.
Each library exports `BriefcaseInitializeStartupModule` from
`BriefcaseStartupModule.h`. Briefcase supplies the main game module, the PE
timestamp and image size, the Briefcase directory, and a logging callback.
Successful modules remain loaded until the process exits.

Startup modules should contain only work that cannot wait for managed startup.
They must validate an exact executable build, bound every image access, require
unique signatures, and fail without modifying memory when validation is
ambiguous. Configuration, user interfaces, and normal mod behavior belong in a
managed mod.

The public runtime contains no game-specific startup patch. A mod distributes
its own companion DLL beside its managed assembly and may expose a private
status export so its C# component can verify that early initialization ran.
