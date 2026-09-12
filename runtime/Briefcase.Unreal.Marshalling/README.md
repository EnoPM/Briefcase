# Briefcase.Unreal.Marshalling

This static library owns Unreal value lifetime and representation rules:

- guarded `ProcessEvent` invocation used by reflected conversion helpers;
- `FProperty` initialization, destruction, hashing, and equality;
- allocations made through the engine's `GMalloc` instance;
- validated `FText` conversion through `KismetTextLibrary`;
- classification of reflected property kinds and prepared plain values.

`UnrealValueCodec` also owns the bounded BVC1/BVO1 wire format used to
exchange recursive structs and owning arrays, sets, and maps without exposing
their process-local native representations.

Hooks, patch registration, and managed hosting remain outside this library.
