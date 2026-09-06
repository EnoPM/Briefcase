#pragma once

#include <cstddef>
#include <cstdint>

namespace briefcase::unreal {

// These are non-owning views over UE 4.27 objects in the current game build.
// They contain only fields used by the probe. Never construct or delete them.
struct FName {
    std::uint32_t ComparisonIndex;
    std::uint32_t Number;
};
static_assert(sizeof(FName) == 8);

struct FStringBuffer {
    wchar_t* Data;
    std::int32_t Num;
    std::int32_t Max;
};
static_assert(sizeof(FStringBuffer) == 16);

struct UObject {
    void* VTable;
    std::uint32_t ObjectFlags;
    std::int32_t InternalIndex;
    UObject* ClassPrivate;
    FName NamePrivate;
    UObject* OuterPrivate;
};
static_assert(sizeof(UObject) == 0x28);
static_assert(offsetof(UObject, ClassPrivate) == 0x10);
static_assert(offsetof(UObject, NamePrivate) == 0x18);
static_assert(offsetof(UObject, OuterPrivate) == 0x20);

struct UField : UObject {
    UField* Next;
    std::byte Padding30[0x10];
};
static_assert(sizeof(UField) == 0x40);
static_assert(offsetof(UField, Next) == 0x28);

struct FField {
    void* VTable;
    void* ClassPrivate;
    std::byte Owner[0x10];
    FField* Next;
    FName NamePrivate;
    std::uint32_t FlagsPrivate;
    std::uint32_t Padding34;
};
static_assert(sizeof(FField) == 0x38);
static_assert(offsetof(FField, Next) == 0x20);
static_assert(offsetof(FField, NamePrivate) == 0x28);

// Deceive Inc. uses the non-case-preserving UE 4.27 layout. FField::ClassPrivate
// points to this metadata object; its leading FName identifies FloatProperty,
// ObjectProperty, and the other reflected property kinds.
struct FFieldClass {
    FName Name;
};

struct FProperty : FField {
    std::int32_t ArrayDim;
    std::int32_t ElementSize;
    std::uint64_t PropertyFlags;
    std::uint16_t RepIndex;
    std::byte BlueprintReplicationCondition;
    std::byte Padding4B;
    std::int32_t OffsetInternal;
    FName RepNotifyFunc;
    FProperty* PropertyLinkNext;
};
static_assert(offsetof(FProperty, ArrayDim) == 0x38);
static_assert(offsetof(FProperty, ElementSize) == 0x3C);
static_assert(offsetof(FProperty, PropertyFlags) == 0x40);
static_assert(offsetof(FProperty, OffsetInternal) == 0x4C);
static_assert(offsetof(FProperty, PropertyLinkNext) == 0x58);

// UE 4.27 non-case-preserving FBoolProperty. These four bytes describe both
// native bools (FieldMask 0xFF) and packed Blueprint bitfields. Keeping the
// layout beside FProperty makes every direct metadata offset explicit and
// ties it to the executable profile validated by this runtime.
struct FBoolProperty : FProperty {
    std::byte Padding60[0x18];
    std::uint8_t FieldSize;
    std::uint8_t ByteOffset;
    std::uint8_t ByteMask;
    std::uint8_t FieldMask;
};
static_assert(offsetof(FBoolProperty, FieldSize) == 0x78);
static_assert(offsetof(FBoolProperty, ByteOffset) == 0x79);
static_assert(offsetof(FBoolProperty, ByteMask) == 0x7A);
static_assert(offsetof(FBoolProperty, FieldMask) == 0x7B);

// Every typed FProperty subclass starts its payload at 0x78 in this UE 4.27
// build. FStructProperty keeps the UScriptStruct describing its value there.
// Recording that object path in the SDK snapshot lets generated C# signatures
// say FGeometry instead of the unhelpful generic name "StructProperty".
struct FStructProperty : FProperty {
    std::byte Padding60[0x18];
    struct UStruct* Struct;
};
static_assert(offsetof(FStructProperty, Struct) == 0x78);

// Container properties use the same typed-property payload offset. Inner
// describes the element type of a TArray and is metadata, never array data.
struct FArrayProperty : FProperty {
    std::byte Padding60[0x18];
    FProperty* Inner;
};
static_assert(offsetof(FArrayProperty, Inner) == 0x78);

struct UStruct : UField {
    UStruct* SuperStruct;
    UField* Children;
    FField* ChildProperties;
    std::int32_t PropertiesSize;
    std::int32_t MinAlignment;
};
static_assert(offsetof(UStruct, SuperStruct) == 0x40);
static_assert(offsetof(UStruct, Children) == 0x48);
static_assert(offsetof(UStruct, ChildProperties) == 0x50);
static_assert(offsetof(UStruct, PropertiesSize) == 0x58);

struct UFunction : UStruct {
    std::byte Padding60[0x50];
    std::uint32_t FunctionFlags;
    std::uint8_t NumParms;
    std::uint8_t PaddingB5;
    std::uint16_t ParmsSize;
    std::uint16_t ReturnValueOffset;
    std::uint16_t RPCId;
    std::uint16_t RPCResponseId;
    std::uint16_t PaddingBE;
    FProperty* FirstPropertyToInit;
    UFunction* EventGraphFunction;
    std::int32_t EventGraphCallOffset;
    std::int32_t PaddingD4;
    void* Func;
};
static_assert(offsetof(UFunction, FunctionFlags) == 0xB0);
static_assert(offsetof(UFunction, NumParms) == 0xB4);
static_assert(offsetof(UFunction, ParmsSize) == 0xB6);
static_assert(offsetof(UFunction, Func) == 0xD8);
static_assert(sizeof(UFunction) == 0xE0);

struct FUObjectItem {
    UObject* Object;
    std::int32_t Flags;
    std::int32_t ClusterRootIndex;
    std::int32_t SerialNumber;
    std::int32_t Padding14;
};
static_assert(sizeof(FUObjectItem) == 0x18);

struct FChunkedFixedUObjectArray {
    FUObjectItem** Objects;
    FUObjectItem* PreAllocatedObjects;
    std::int32_t MaxElements;
    std::int32_t NumElements;
    std::int32_t MaxChunks;
    std::int32_t NumChunks;
};
static_assert(sizeof(FChunkedFixedUObjectArray) == 0x20);

struct FUObjectArray {
    std::int32_t ObjFirstGCIndex;
    std::int32_t ObjLastNonGCIndex;
    std::int32_t MaxObjectsNotConsideredByGC;
    bool OpenForDisregardForGC;
    std::byte Padding0D[3];
    FChunkedFixedUObjectArray ObjObjects;
};
static_assert(offsetof(FUObjectArray, ObjObjects) == 0x10);

} // namespace briefcase::unreal
