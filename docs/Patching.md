# Attributed Unreal and native patches

The generated SDK represents Unreal objects as C# classes. An Unreal patch
receives an ordinary typed object and targets an ordinary generated method.
Briefcase observes reflected calls through `UObject::ProcessEvent` and, for
void native UFunctions, through the function's `UFunction::Func` thunk:

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
and replacing an `FText` works in prefixes and postfixes. Write-back uses the
reflected property's initialize and destroy operations instead of copying the
private `FText` bytes.

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

Generated method invocation is implemented by the versioned Unreal API. For safe,
fixed-layout signatures, API v10 resolves the owner and UFunction and validates
`UFunction::ParmsSize`, parameter offsets, kinds and flags once. Later calls use
the opaque prepared token and a generated stack buffer. API v11 additionally
initializes owning `USTRUCT` output storage through `FProperty`, copies its
fields through BVC1, and destroys it after `ProcessEvent`. API v13 reconstructs
owning inputs, containers and writable references in initialized native storage. The dynamic path
keeps the same per-call lookup for other owning or runtime-described values. UObject
parameters cross either ABI as index/serial handles; the native runtime resolves
them immediately before `ProcessEvent` and converts object outputs back to
handles. Game pointers are never exposed to C#.

Generated invocations must run on Unreal's captured game thread. Direct calls
from another thread fail with `WrongThread`; mods can use `GameThread.Invoke`,
`InvokeAsync`, timers or tick callbacks to schedule the work.

The framework includes coordinated `UObject::ProcessEvent` and bounded
`UFunction::Func` dispatchers. It validates the target class and UFunction,
deduplicates the native implementations present in cooked vtables, runs
prefixes before the original and postfixes afterward, and waits for an
in-flight callback before a hot-reloaded assembly can unload. `ReceiveBeginPlay`
also uses the profiled UE 4.27 `AActor::BeginPlay` slot so native actors that
bypass reflected dispatch still expose the expected lifecycle patch. Installed
detours stay for the process lifetime and forward directly when no registration
matches an event.

Priority and ordering are deterministic across loaded mods. Primitive, fixed or managed generated value struct, UObject-handle, `FString`
and `FText` patch parameters use type-specific copying. Managed owning structs
are available as address-free values to callbacks. Patching API v7 accepts
writable `ref` owning structs made from scalars, handles, `FName`, strings, text
and nested admitted structs, including `TArray`, `TSet` and `TMap` fields, and
reconstructs them through `FProperty` before native control returns. Direct
container `ref` parameters use the same BVC1 path. Delegate bindings remain
read-only.

## Generated value structs

Snapshot schema 3 records the recursive property tree and referenced type path
for every `StructProperty`. Fixed structs are blittable C# structs with
`LayoutKind.Explicit`, Unreal field offsets and the exact reflected native size;
they implement `IUnrealStructValue`. Unreal's reflected name `Geometry`
consequently becomes the familiar C++-style C# name `FGeometry`.

Owning structs implement `IUnrealManagedStructValue`. Their generated fields
use managed strings, text and immutable container snapshots, and each field is
identified by `[UnrealStructField(name, offset)]`. Patch callbacks can inspect
them without retaining native pointers. A `ref` owning struct uses BVC1 for
write-back when every nested field belongs to the supported writable set.

Harmony IL transpilers do not apply to Unreal's compiled C++ functions. Native
prefixes and postfixes operate at the function boundary; instruction-level
transpilers remain outside the current contract.
