# Standalone Unreal reflection

This note describes the UE4SS-independent runtime and its value ABI. Reflection
discovery is read-only; generated property and function operations are enabled
only for value categories whose copying and lifetime rules are implemented.

## 1. How our DLL enters the process

Windows searches the executable directory before System32 for some imported
DLLs. Deceive Inc. imports `version.dll`, so our proxy is loaded naturally at
process startup. The proxy performs two jobs:

1. it loads the real `C:\Windows\System32\version.dll` and stores its 17 export
   addresses;
2. its assembly trampolines jump to those addresses, preserving the behaviour
   expected by the game.

After Windows releases its loader lock, a worker thread loads
`Briefcase/Core/Native/Briefcase.UnrealRuntime.dll` by absolute path. It resolves
the runtime's single versioned bootstrap export and passes the proxy module plus
the process's initial thread ID. The proxy therefore contains no Unreal or .NET
hosting implementation.

## 2. Image base, RVA and ASLR

The address printed by a dump is not stable between launches because ASLR
relocates the executable. An RVA is the distance from the image base and does
remain stable for one exact executable build:

```text
runtime address = executable image base + RVA
```

The client and server profiles contain the expected PE timestamp and
`SizeOfImage`, but no Unreal symbol addresses. After selecting the profile, the
runtime validates the mapped PE section table and scans only `.text`:

- `FName::ToString` must match one masked function signature;
- every matching UE 4.27 chunked-object lookup decodes a RIP-relative address;
- all decoded lookups must agree on one `GUObjectArray` address inside `.data`;
- the resolved function must be executable;
- the resolved object array must pass its existing structural invariants.

If a game update changes these facts, the runtime logs the failed stage and
stops before publishing the Unreal API. It never falls back to a stale RVA.

## 3. The global UObject registry

`GUObjectArray` is the engine-owned registry of live reflected objects. In UE
4.27 its storage is chunked. A global index is split like this:

```text
chunk index = object index / 65536
slot index  = object index % 65536
object      = chunks[chunk index][slot index].Object
```

Each `FUObjectItem` is 0x18 bytes in this build. The probe validates the array,
chunk and item memory with `VirtualQuery`, then checks that
`UObject::InternalIndex` equals the registry index. This does not prove that an
arbitrary pointer is safe forever; it gives us a strong consistency check for
this bounded, one-shot traversal.

## 4. The base UObject fields

Every reflected object starts with the same fields used by the probe:

```text
+0x00  virtual-function table pointer
+0x08  object flags
+0x0C  internal object-array index
+0x10  UClass* describing the object's runtime type
+0x18  FName identifying the object
+0x20  UObject* Outer describing ownership/path nesting
```

An object path is reconstructed by following `OuterPrivate` toward the package,
then joining the names in reverse order. For example:

```text
/Script/DeceiveInc.Spy
```

The probe calls the game's own `FName::ToString`. An `FName` is an 8-byte pair
of integers, not an inline string. Its comparison index refers to Unreal's name
pool and its number distinguishes names such as `Actor_0` and `Actor_1`.

## 5. Class metadata and property offsets

A `UClass` is itself a `UObject` and inherits the `UStruct` reflection layout.
The fields used here are:

```text
UStruct +0x40  SuperStruct
UStruct +0x48  Children        (UFunction/UField chain)
UStruct +0x50  ChildProperties (FProperty chain)
UStruct +0x58  PropertiesSize
```

Each `FProperty` describes one field. It is metadata, not the field value:

```text
FProperty +0x28  NamePrivate
FProperty +0x38  ArrayDim
FProperty +0x3C  ElementSize
FProperty +0x40  PropertyFlags
FProperty +0x4C  OffsetInternal
```

Suppose reflection says that `Health` has `OffsetInternal = 0x500`. For a live
spy instance at address `0x0000012340000000`, the bytes representing that
instance field begin at:

```text
0x0000012340000000 + 0x500
```

The metadata alone does not provide a live player instance. Access still starts
by finding a valid instance, confirming its `UClass`, and resolving the live
property descriptor again before copying a correctly sized value.

The schema 4 collector decodes the concrete metadata payload that begins at
`FProperty + 0x78` in this UE 4.27 build:

- `FBoolProperty` field size, byte offset and masks;
- referenced `UStruct`, `UClass`, interface, enum and delegate signature paths;
- `FArrayProperty.Inner` and `FSetProperty.ElementProperty`;
- `FMapProperty.KeyProperty` and `ValueProperty`;
- `FEnumProperty.UnderlyingProperty`;
- weak, lazy and soft object categories, plus field paths;
- `UEnum` names and signed 64-bit values.

Container properties form a recursive tree. For example, a reflected map can be
described as `TMap<FName, TArray<Vector>>`. When a mod reads it, the native
runtime walks the live `TArray`, `TSet` or `TMap`, validates every allocation and
copies it into the pointer-free BVC1 value format. Recursion is bounded to eight
levels, values are capped at 32 MiB, containers at 100,000 entries, and every
nested pointer passes the same readability checks as the root property.

The generated SDK exposes this data through `Type.Reflection`,
`Type.Metadata.Properties` and `Type.Metadata.Functions`. Function parameters
retain their Unreal flags. The generated methods consequently expose ordinary
inputs, C# `out` parameters and writable C# `ref` parameters directly.

## 6. Reading and writing values safely

Writing `base + offset` is mechanically easy and semantically dangerous. An
Unreal field may be a packed boolean, an object reference tracked by garbage
collection, a replicated property, or state that must be changed through a
`UFunction` to preserve invariants.

Briefcase resolves the live `FProperty` by owner and name for every operation,
then checks its cached offset, size, array dimension and property kind. Scalar,
packed boolean, object-handle, `FName` and fixed-layout reflected struct
properties can be assigned. An owning reflected struct can also be assigned
when its complete tree contains only those values, `FString`, `FText` and nested
admitted structs. The runtime initializes a temporary with `FProperty`, decodes
BVC1, destroys the old value, and transfers the validated temporary into the
property. Writes must run on the Unreal game thread.

Generated function wrappers apply the same ownership rule to parameters.
`FString` is exposed as `string` and `FText` as `UnrealText`, including returns,
pure `out` parameters and writable `ref` parameters. Briefcase never copies the
native pointer-bearing layout into C#. It initializes a temporary with the
reflected parameter `FProperty`, converts values through BVC1/BVO1 around
`ProcessEvent`, and destroys every initialized parameter in reverse order.

Containers are exposed as immutable `UnrealArray<T>`, `UnrealSet<T>` and
`UnrealMap<TKey,TValue>` snapshots. Their generated setters reconstruct complete
Unreal-owned storage through the reflected allocator, initialization, hash and
destruction rules. Interfaces, lazy/soft references, delegates, multicast
delegates and field paths likewise become address-free managed value objects.
Dynamic multicast delegates additionally carry their reflected `UFunction`
signature, which lets `context.Events` copy event arguments without exposing
the native parameter buffer.

### Typed object access and lifecycle

Unreal API v14 exposes class default objects, immediate Outer relationships and
`EObjectFlags` through serial-checked handles. Generated classes can be resolved
without untyped casts:

```csharp
var settings = context.Unreal.GetDefaultObject(MySettings.StaticClass);
var asset = context.Unreal.FindObject("/Game/Data/MyAsset.MyAsset", MyAsset.StaticClass);
Console.WriteLine(settings.IsClassDefaultObject);
Console.WriteLine(asset.Outer?.Path);
```

`FindObject` resolves an object that is already loaded. Subsystem helpers call
the vanilla `SubsystemBlueprintLibrary` UFunctions. `CreateObject` calls
`GameplayStatics.SpawnObject`; `SpawnActor` uses the deferred vanilla spawn path.
These calls run on Unreal's game thread. A managed wrapper observes an object but
does not root it: the supplied Outer or World retains ownership, and the handle
becomes stale after destruction or garbage collection.

Unreal API v15 adds `context.Assets.Load`, `TryLoad` and `Retain`. These return
mod-scoped strong-reference leases; the host releases every outstanding lease on
unload or reload. Asset loading uses the reflected vanilla soft-object loading
functions and validates the requested generated class before returning a wrapper.

Unreal API v16 adds scoped subscriptions to inline dynamic multicast delegates.
The runtime installs an ordinary Unreal script-delegate entry on the exact
publisher object. That entry targets a validated class-default-object method
whose reflected parameter buffer exactly matches the delegate signature. The
existing ProcessEvent detour intercepts only that object/method pair, reports the
real publisher to managed code and skips the sink method's implementation. The
callback runs on the game thread and its arguments use the same bounded
value-copying rules as ProcessEvent patches. Briefcase disables the callback
synchronously during unload, then removes the physical Unreal binding on a later
game-thread dispatch so it never mutates an array while Unreal is iterating it.
Sparse delegates are rejected until their separate storage model has an explicit
implementation.

## 7. Current source map

- `loader/Briefcase.VersionProxy`: Windows export forwarding and runtime entry point.
- `runtime/Briefcase.Native.Foundation`: process-wide native logging.
- `runtime/Briefcase.Unreal.Discovery`: executable identities, bounded PE views,
  masked signature scanning, RIP decoding, and fail-closed symbol discovery.
- `runtime/Briefcase.Unreal.Reflection`: UE 4.27 layout views, memory validation,
  object handles, names, paths, and reflected member lookup.
- `runtime/Briefcase.Unreal.Marshalling`: reflected property classification,
  engine-owned value lifetime, GMalloc access, safe `FText` conversion, and the
  bounded BVC1/BVO1 codec for structs and owning containers.
- `runtime/Briefcase.Unreal.Invocation`: captured game-thread identity, direct and
  prepared `ProcessEvent` calls, tagged prepared tokens, and owned output buffers.
- `runtime/Briefcase.Unreal.Patching`: the single global `ProcessEvent` detour,
  game-thread callback scheduling, multicast delegate bindings, reflected/native
  patch registration, and bounded patch-value access.
- `runtime/Briefcase.Unreal.Metadata`: reflected SDK inventory plus JSON and
  Briefcase Snapshot encoding.
- `runtime/Briefcase.UnrealRuntime/src/UnrealProbe.cpp`: non-patching public API
  composition and validated Unreal bootstrap orchestration.
- `Briefcase/Briefcase.log`: observable result for the native and managed runtime.

The complete package is produced under `dist/Briefcase`. Its `version.dll` is
placed beside the game executable, while `Briefcase.UnrealRuntime.dll` is stored
under `Briefcase/Core/Native`; the rest of the `Briefcase` directory remains intact.
