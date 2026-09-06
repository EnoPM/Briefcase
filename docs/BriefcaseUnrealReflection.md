# Standalone Unreal reflection: milestone 1

This note describes exactly what the first UE4SS-independent runtime does. The
runtime is intentionally read-only. It proves that we can bootstrap Unreal
reflection ourselves before we reuse that information in the gameplay mods.

## 1. How our DLL enters the process

Windows searches the executable directory before System32 for some imported
DLLs. Deceive Inc. imports `version.dll`, so our proxy is loaded naturally at
process startup. The proxy performs two jobs:

1. it loads the real `C:\Windows\System32\version.dll` and stores its 17 export
   addresses;
2. its assembly trampolines jump to those addresses, preserving the behaviour
   expected by the game.

The Unreal runtime is compiled as a static library and linked into the proxy.
After Windows releases its loader lock, a worker thread calls it directly. The
source responsibilities remain separate while the deployed product is a single
`version.dll`.

## 2. Image base, RVA and ASLR

The address printed by a dump is not stable between launches because ASLR
relocates the executable. An RVA is the distance from the image base and does
remain stable for one exact executable build:

```text
runtime address = executable image base + RVA
```

For the current build the profile contains an RVA for `GUObjectArray` and one
for `FName::ToString`. Before using either address, the runtime validates:

- the PE timestamp;
- `SizeOfImage`;
- the first 16 machine-code bytes of `FName::ToString`;
- structural invariants of `GUObjectArray`.

If a game update changes these facts, the runtime logs an error and stops. It
does not guess and does not dereference the old addresses.

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

The metadata alone does not provide a live player instance and does not say how
to interpret every property subclass. A later layer must find a valid instance,
confirm that it belongs to the expected `UClass`, inspect the concrete property
type (`FIntProperty`, `FBoolProperty`, `FObjectProperty`, and so on), and only
then copy or change a correctly sized value.

## 6. Why no values are modified yet

Writing `base + offset` is mechanically easy and semantically dangerous. An
Unreal field may be a packed boolean, an object reference tracked by garbage
collection, a replicated property, or state that must be changed through a
`UFunction` to preserve invariants. This milestone only inventories names,
sizes and offsets. The next stages will add type descriptions and stable object
lookup before any controlled mutation is reintroduced.

## 7. Current source map

- `loader/Briefcase.VersionProxy`: Windows export forwarding and runtime entry point.
- `runtime/Briefcase.UnrealRuntime/src/RuntimeProfile.h`: exact-build addresses and
  fingerprints.
- `runtime/Briefcase.UnrealRuntime/src/UnrealLayout.h`: minimal UE 4.27 memory views.
- `runtime/Briefcase.UnrealRuntime/src/UnrealProbe.cpp`: validation, registry traversal
  and metadata inventory.
- `Briefcase/Briefcase.log`: observable result for the native and managed runtime.

The complete package is produced under `dist/Briefcase`. Its `version.dll` is
placed beside the game executable and its `Briefcase` directory remains intact.

The next milestone should resolve globals by byte signatures bounded to PE
sections, then decode reflected property subclasses. That removes the two hard
coded RVAs while keeping the same fail-closed validation.
