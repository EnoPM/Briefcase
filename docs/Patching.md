# Attributed Unreal and native patches

The generated SDK represents Unreal objects as C# classes. An Unreal patch
intercepts a reflected call that crosses `UObject::ProcessEvent`:
receives an ordinary typed object and targets an ordinary generated method:

```csharp
[UnrealPostfixPatch(typeof(Spy), nameof(Spy.BP_OnCoverRatioUpdate))]
private static void OnCoverRatioUpdatePostfix(Spy __instance)
{
    float currentRatio = __instance.CoverRatio;
}
```

Both identifiers are checked by the compiler:

- `typeof(Spy)` identifies the generated Unreal owner class;
- `nameof(Spy.BP_OnCoverRatioUpdate)` fails to compile if that method is
  removed or renamed;
- `Spy __instance` gives the patch body the properties and methods generated
  for that Unreal class.

Generated UFunctions are instance methods. Mod code can call one directly:

```csharp
__instance.BP_OnCoverRatioUpdate(0.5f);
```

The default reentrancy policy is `SuppressCurrentPatch`. Calling the patched
function from inside that patch invokes the game function but omits the
currently executing patch for the nested call. This prevents accidental
infinite recursion while leaving other patches active.

## Prefixes and postfixes

A prefix runs before the Unreal function. Parameters are selected by their
generated Unreal names and can be changed with `ref`:

```csharp
[UnrealPrefixPatch(typeof(Spy), nameof(Spy.BP_OnCoverRatioUpdate))]
private static void ClampRatio(Spy __instance, ref float NewRatio)
{
    NewRatio = Math.Clamp(NewRatio, 0.0f, 1.0f);
}
```

A prefix may return `false`, or set `ref bool __runOriginal` to `false`, to
skip the original function. A postfix runs afterward. A patch can read a return
value by declaring `__result`, or replace it by declaring `ref __result`:

```csharp
[UnrealPostfixPatch(typeof(SomeGeneratedType), nameof(SomeGeneratedType.GetOpacity))]
private static void ClampOpacity(ref float __result)
{
    __result = Math.Clamp(__result, 0.0f, 1.0f);
}
```

Patch method rules are intentionally small:

- patch methods are `static` and non-generic;
- a prefix returns `void` or `bool`;
- a postfix returns `void`;
- `__instance` must use the exact generated owner class;
- named arguments must match generated Unreal parameter names and C# types;
- `__runOriginal` is available as `ref bool` in a prefix;
- `__result` must match the generated return type and currently requires a
  generated return descriptor.

`FText` parameters are exposed as `UnrealText`. Briefcase converts the value
through Unreal's own text library and copies the UTF-16 result while the patch
callback is active, so the managed mod never receives an Unreal pointer. Reading
an `FText` works in prefixes and postfixes; `ref UnrealText` write-back is not
part of the current contract because replacing an owned `FText` requires Unreal
lifetime operations.

## Optional behavior

The common case needs only `typeof` and `nameof`. Named attribute properties
expose policy when several mods patch the same function:

```csharp
[UnrealPostfixPatch(
    typeof(Spy),
    nameof(Spy.BP_OnCoverRatioUpdate),
    Priority = 100,
    Before = new[] { typeof(AnotherPatchContainer) },
    AfterMods = new[] { "author.required-mod" },
    Reentrancy = PatchReentrancy.SuppressCurrentPatch,
    OnException = PatchExceptionPolicy.DisablePatch,
    RunWhenOriginalSkipped = false)]
private static void Observe(Spy __instance)
{
}
```

- `Priority`: higher values run first for prefixes and last for postfixes.
- `Before` / `After`: order relative to patch container types using
  `typeof(...)`.
- `BeforeMods` / `AfterMods`: order relative to stable mod identifiers.
- `Reentrancy`: suppress this patch, suppress all patches for the target, or
  explicitly allow recursion.
- `OnException`: log and continue, disable the failing patch, or disable the
  mod's patches.
- `RunWhenOriginalSkipped`: lets a postfix opt out when a prefix skipped the
  game function.

Exceptions are never unwound through native Unreal frames. The selected policy
is applied inside the managed dispatcher.

## Direct C++ calls

A `UFUNCTION` can be invoked in two ways. Blueprint and reflective calls cross
`UObject::ProcessEvent`; ordinary C++ code may call the compiled member function
directly. Use `NativePrefixPatch` or `NativePostfixPatch` for the latter:

```csharp
[NativePrefixPatch(
    typeof(DIMenuSubsystem),
    nameof(DIMenuSubsystem.DisplayMainMenu))]
private static void BeforeMainMenu(DIMenuSubsystem __instance)
{
}

[NativePostfixPatch(
    typeof(DIMenuSubsystem),
    nameof(DIMenuSubsystem.DisplayMainMenu),
    RunWhenOriginalSkipped = false)]
private static void AfterMainMenu(DIMenuSubsystem __instance)
{
}
```

The native backend resolves the reflected `exec` wrapper to its compiled
implementation inside the supported executable image, installs one MinHook
trampoline per target, and shares it among every mod registration. Prefixes can
return `false` or use `ref bool __runOriginal`; postfixes observe whether the
original ran. Detours remain as forwarding stubs for the process lifetime while
individual callbacks can be removed safely during hot reload.

The native signature bridge accepts zero to three input parameters. Integral
scalars, booleans and enum storage travel through the general-purpose register
slots; `float` and `double` use the corresponding XMM slots. A generated struct
of 1, 2, 4 or 8 bytes travels as register bits. Larger input structs travel by
address in the C++ ABI and are copied into a private reflected parameter buffer
before a managed callback sees them. Prefix `ref` arguments are copied back to
the actual native call. Scalar and register-sized struct return values are
available as `__result`, including writable `ref __result`.

For example, the generated SDK represents UMG's native `FMargin` input without
exposing a game pointer:

```csharp
[NativePrefixPatch(typeof(UserWidget), nameof(UserWidget.SetPadding))]
private static void BeforeSetPadding(
    UserWidget __instance,
    ref FMargin InPadding)
{
    InPadding.Left = Math.Max(0.0f, InPadding.Left);
}

[NativePostfixPatch(typeof(UserWidget), nameof(UserWidget.IsInViewport))]
private static void AfterIsInViewport(UserWidget __instance, ref bool __result)
{
}
```

These two signatures were runtime-validated against the current client build:
`FMargin` crosses the Windows x64 indirect-struct argument slot as 16 bytes and
the boolean return crosses the scalar result slot. Unreal events such as
`UserWidget.Tick` still belong to `UnrealPrefixPatch`/`UnrealPostfixPatch` when
they are dispatched by `ProcessEvent`; the presence of a generated method alone
does not imply that a direct native implementation exists.

The runtime rejects a fourth input, an ordinary `out`/reference parameter,
UObject parameters, containers, strings, delegates, and large struct returns.
Those signatures require stack copying, handle translation or Unreal lifetime
operations that this ABI does not yet claim to preserve.

Both native attributes still use `typeof(...)`, `nameof(...)`, typed
`__instance`, priority, ordering, reentrancy and exception policy. Registration
also requires a recognized PE timestamp/image size and a validated executable
branch in the native `UFUNCTION` wrapper. Parameterized wrappers may contain
generic `FFrame` reader calls before their final same-module implementation
call; the resolver accepts that bounded pattern. Failure is reported during
mod load instead of installing an uncertain detour.

## Current implementation boundary

At load time, `Briefcase.ManagedHost` discovers these method attributes and validates
the generated target and complete patch signature. Patch declarations retain
`MethodInfo` and `Type` objects from the collectible mod assembly, so the
registry clears them before hot reload unloads that assembly.

Generated method invocation is implemented by Unreal API v2. The native side
resolves the object and owner handles, finds the named UFunction in reflected
metadata, checks `UFunction::ParmsSize`, checks the ProcessEvent vtable entry,
and only then invokes the function. UObject parameters cross the ABI as an
index/serial handle; the native runtime resolves them immediately before ProcessEvent
and converts object outputs back to handles. Game pointers are never exposed to
C#.

For now, call generated functions from Unreal callbacks, which already execute
on the appropriate engine thread. A later game-thread scheduling API will make
calls from arbitrary mod tasks equally simple.

The framework includes the `UObject::ProcessEvent` dispatcher. It
validates the target class and UFunction, installs one process-wide detour on
the engine's `ProcessEvent` implementation, runs prefixes before the original
and postfixes afterward, and waits for an in-flight callback before a
hot-reloaded assembly can unload. The detour stays installed for the process
lifetime and forwards directly when no registrations match an event.

Priority is deterministic inside one managed mod. The `Before`, `After`,
`BeforeMods`, and `AfterMods` declarations are validated and retained, but
cross-mod dependency sorting is not enabled yet. Primitive and generated value
struct parameters support `ref` write-back. UObject, container, string and
delegate parameter marshalling remain explicit future ABI additions.

## Generated value structs

Snapshot schema 2 records `referencedTypePath` for every reflected
`StructProperty`. The generator emits each `/Script/...` `ScriptStruct` as a
blittable C# struct with `LayoutKind.Explicit`, Unreal field offsets, and the
exact reflected native size. Unreal's reflected name `Geometry` consequently
becomes the familiar C++-style C# name `FGeometry`. When a native struct has no
reflected fields, the SDK still emits an opaque, correctly sized value type;
mods can pass it through a patch without inventing its internal layout.

Generated structs implement `IUnrealStructValue` explicitly. This lets the SDK
copy them into a bounded parameter buffer with statically generated code and no
dependency on dynamic marshalling metadata.

Harmony IL transpilers do not apply to Unreal's compiled C++ functions. Native
prefixes and postfixes operate at the function boundary; instruction-level
transpilers remain outside the current contract.
