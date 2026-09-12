# Briefcase documentation

Briefcase is a C# modding framework built specifically for **Deceive Inc.** It
loads managed mods on the game client and on dedicated servers, generates an
SDK for the installed game build, and provides safe services for interacting
with Unreal Engine.

This documentation is written for mod developers. Players who only want to
install Briefcase or a mod can follow the [installation guide](getting-started/installation.md).

[Download Briefcase](https://github.com/EnoPM/Briefcase/releases) ·
[View the source](https://github.com/EnoPM/Briefcase) ·
[Report a problem](https://github.com/EnoPM/Briefcase/issues)

> [!NOTE]
> Briefcase is under active development. The generated SDK and the runtime
> profile are tied to a particular Deceive Inc. executable build.

## Choose where to start

| Goal | Start here |
| --- | --- |
| Install Briefcase on a client or server | [Install Briefcase](getting-started/installation.md) |
| Build a minimal C# mod | [Create your first mod](getting-started/first-mod.md) |
| Find game classes, properties, and methods | [Explore the generated SDK](getting-started/explore-sdk.md) |
| Add simple settings to the F1 menu | [Mod configuration](ModConfiguration.md) |
| Build a custom Avalonia panel or overlay | [Client mod UI](ClientModUi.md) |
| Run code safely against Unreal objects | [Game-thread scheduling](GameThread.md) |
| Observe or alter an Unreal function | [Patching](Patching.md) |

## What Briefcase provides

- a managed C# runtime for client and server mods;
- automatic loading, unloading, reloading, and dependency ordering;
- a generated SDK containing known Deceive Inc. types for the current build;
- game-thread scheduling and Unreal world lifecycle notifications;
- typed configuration stored and rendered by Briefcase;
- a client-only Avalonia UI API for complex panels and overlays;
- Unreal object discovery, properties, functions, events, and attributed
  patches;
- a headless server runtime and remote server-administration services.

Briefcase deliberately keeps the public documentation focused on concepts and
examples. It does not publish thousands of generated SDK members as web pages.
Instead, the [SDK exploration guide](getting-started/explore-sdk.md) explains
how to inspect the exact SDK generated for your installed game build.

## A typical mod workflow

1. Install and run Briefcase once so it can prepare the SDK for the current
   game build.
2. Explore that SDK with a .NET assembly browser or your editor.
3. Create a .NET class library that references `Briefcase.ModApi` and imports
   the generated SDK props file.
4. Derive one class from `BriefcaseMod` and describe it with `ModInfo`.
5. Build the DLL and copy it into `Briefcase/Mods`.
6. Read `Briefcase.log`, then use the F1 menu to manage the mod on the client.

Continue with [Create your first mod](getting-started/first-mod.md).
