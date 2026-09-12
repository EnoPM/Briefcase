# Briefcase.Unreal.Patching

This native static library owns every hook that can intercept Unreal execution:

- deduplicated `UObject::ProcessEvent` detours for the implementations observed
  in cooked class vtables;
- a bounded `UFunction::Func` transport for void native reflected calls;
- the UE 4.27 `AActor::BeginPlay` lifecycle bridge used by
  `ReceiveBeginPlay` patches;
- game-thread callback scheduling driven by `ProcessEvent`;
- reflected prefix and postfix patches;
- multicast delegate sink bindings;
- native function detours and their bounded parameter layouts;
- patch-call value copying and replacement.

Keeping these features together lets Briefcase deduplicate native targets and
perform one coordinated dispatch for each intercepted Unreal call.
The module stays inactive until `configurePatchingRuntime` receives a validated
runtime profile and mapped-image bounds.
