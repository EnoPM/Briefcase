# Create your first mod

This tutorial creates a small client mod that writes to `Briefcase.log` and
uses one generated Deceive Inc. type. It does not depend on a particular IDE.

## Prerequisites

- Briefcase Client installed and launched once;
- the .NET 10 SDK for development;
- the full path to the installed `Briefcase` folder.

Running Briefcase once is necessary because the client SDK is generated for
the detected game executable.

## Create the project

Create an empty directory and add `MyFirstMod.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <Target Name="RequireBriefcaseRoot" BeforeTargets="PrepareForBuild">
    <Error Condition="'$(BriefcaseRoot)' == ''"
           Text="Pass -p:BriefcaseRoot=... with the installed Briefcase folder." />
  </Target>

  <ItemGroup>
    <Reference Include="Briefcase.ModApi">
      <HintPath>$(BriefcaseRoot)\Core\Briefcase.ModApi.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <Import Project="$(BriefcaseRoot)\Core\Sdk\Generated\Client\Current.props"
          Condition="Exists('$(BriefcaseRoot)\Core\Sdk\Generated\Client\Current.props')" />
</Project>
```

Add `MyFirstMod.cs`:

```csharp
using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace MyFirstMod;

public sealed class EntryPoint : BriefcaseMod
{
    public override ModInfo Info { get; } = new(
        Id: "example.my-first-mod",
        Name: "My First Mod",
        Author: "Your name",
        Version: "1.0.0",
        Description: "A minimal Briefcase client mod.",
        RequiredCapabilities: BriefcaseAbi.CoreCapability |
                              BriefcaseAbi.GameThreadCapability);

    public override void Load(ModContext context)
    {
        context.Info("My First Mod loaded.");

        context.GameThread.OnEngineReady(() =>
            context.Info($"The generated SDK knows {Spy.UnrealPath}."));
    }

    public override void Unload()
    {
        // Dispose subscriptions and stop mod-owned workers here.
    }
}
```

`ModInfo.Id` is the stable identity used by configuration and dependencies. It
must remain the same when the DLL file or display name changes.

## Build

Run the following command, replacing the path with your installation:

```powershell
dotnet build -c Release -p:BriefcaseRoot="D:\Games\DeceiveInc\DeceiveInc\Binaries\Win64\Briefcase"
```

Create a package directory below `Briefcase\Mods`, copy
`bin\Release\net10.0\MyFirstMod.dll` into it, and add a minimal manifest:

```text
Briefcase\Mods\my.first-mod\
├── briefcase.mod.json
└── MyFirstMod.dll
```

```json
{
  "schemaVersion": 1,
  "entryAssembly": "MyFirstMod.dll",
  "id": "my.first-mod",
  "version": "1.0.0",
  "dependencies": []
}
```

The manifest identity and version must match `ModInfo`. Do not copy
`Briefcase.ModApi.dll` or the generated SDK DLL with the mod; Briefcase already
owns and shares those assemblies. Add an `updates` object when the mod has its
own GitHub releases, as described in
[Mod packages and marketplace](../ModPackages.md).
Start the game and open `Briefcase.log`. A successful load contains the two
messages emitted above. Press **F1**, open **Mods**, and confirm that **My First
Mod** appears in the installed list.

## Continue

- [Explore the generated SDK](explore-sdk.md) to find other game types.
- [Game-thread scheduling](../GameThread.md) explains when Unreal access is
  allowed.
- [Mod configuration](../ModConfiguration.md) adds settings without requiring
  a custom UI.
- [Client mod UI](../ClientModUi.md) covers complex Avalonia panels and
  overlays.
- [Patching](../Patching.md) covers attributed prefixes and postfixes.
