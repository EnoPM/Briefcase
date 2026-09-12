# Briefcase.Unreal.Reflection

This static library owns the validated, read-only foundation used to inspect
Unreal objects:

- UE 4.27 native layout views;
- committed-memory and protection checks;
- `GUObjectArray` lookup and stable object handles;
- `FName`, object path, class, property, and function discovery;
- bounded UTF-8 conversion at the native ABI boundary.

It does not install hooks, invoke game functions, marshal owning Unreal values,
or load managed mods. `Briefcase.UnrealRuntime.dll` links it statically.
