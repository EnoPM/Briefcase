# Managed mod development

C# is the supported Briefcase authoring model. A mod is an ordinary .NET IL
assembly that references `Briefcase.ModApi` and the generated SDK matching its
target and game build.

```csharp
using Briefcase.DeceiveInc;
using Briefcase.ModApi;

public sealed class MyMod : BriefcaseMod
{
    public override ModInfo Info { get; } = new(
        "author.my-mod", "My Mod", "Author", "1.0.0", "Description");

    public override void Load(ModContext context)
    {
        context.Info($"Spy.CoverRatio offset: 0x{Spy.Properties.CoverRatio.Offset:X}");
    }
}
```

The game installation contains the framework assemblies and generated SDK in
`Briefcase/Core`. Mod DLLs live in `Briefcase/Mods`; developers and players do
not copy the SDK beside each mod.

`Directory.Build.props` selects the installed generated SDK when available and
otherwise selects the repository output under `artifacts/build-sdk`. Run
`scripts\build\build_framework.bat Release` once after cloning so Rider can
resolve the generated game types. There is no static SDK fallback.

Each mod is loaded in a collectible `AssemblyLoadContext`. Briefcase shadows its
files into `Briefcase/Core/Cache`, so Rider can replace the original DLL. On a
change, the manager debounces the file event, calls `Unload()`, releases the old
context, and loads the new generation. Mods must detach callbacks and stop their
own background work in `Unload()`.

Game wrappers expose validated Unreal handles rather than process addresses.
For example:

```csharp
var spies = context.Unreal.FindObjects(Spy.StaticClass);
var coverRatio = spies[0].Read(Spy.Properties.CoverRatio);
```

The native runtime revalidates object identity, class ownership, reflected
layout, and buffer size for every operation.
