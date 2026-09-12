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

// Pointer-bearing UE containers are observed only long enough to copy a
// bounded snapshot. None of these views cross the public ABI.
struct FScriptArray {
    void* Data;
    std::int32_t Num;
    std::int32_t Max;
};
static_assert(sizeof(FScriptArray) == 16);

struct FInlineBitAllocator {
    std::uint32_t InlineData[4];
    std::uint32_t* SecondaryData;
};
static_assert(sizeof(FInlineBitAllocator) == 24);

struct FScriptBitArray {
    FInlineBitAllocator Allocator;
    std::int32_t NumBits;
    std::int32_t MaxBits;
};
static_assert(sizeof(FScriptBitArray) == 32);

struct FScriptSparseArray {
    FScriptArray Data;
    FScriptBitArray AllocationFlags;
    std::int32_t FirstFreeIndex;
    std::int32_t NumFreeIndices;
};
static_assert(sizeof(FScriptSparseArray) == 56);

struct FInlineHashAllocator {
    std::int32_t InlineData;
    std::int32_t Padding04;
    std::int32_t* SecondaryData;
};
static_assert(sizeof(FInlineHashAllocator) == 16);

struct FScriptSet {
    FScriptSparseArray Elements;
    FInlineHashAllocator Hash;
    std::int32_t HashSize;
    std::int32_t Padding4C;
};
static_assert(sizeof(FScriptSet) == 80);

struct FScriptSparseArrayLayout {
    std::int32_t Alignment;
    std::int32_t Size;
};
static_assert(sizeof(FScriptSparseArrayLayout) == 8);

struct FScriptSetLayout {
    std::int32_t HashNextIdOffset;
    std::int32_t HashIndexOffset;
    std::int32_t Size;
    FScriptSparseArrayLayout SparseArrayLayout;
};
static_assert(sizeof(FScriptSetLayout) == 20);

struct FScriptMapLayout {
    std::int32_t ValueOffset;
    FScriptSetLayout SetLayout;
};
static_assert(sizeof(FScriptMapLayout) == 24);

struct FWeakObjectPtr {
    std::int32_t ObjectIndex;
    std::int32_t ObjectSerialNumber;
};
static_assert(sizeof(FWeakObjectPtr) == 8);

struct FScriptInterface {
    struct UObject* ObjectPointer;
    void* InterfacePointer;
};
static_assert(sizeof(FScriptInterface) == 16);

struct FLazyObjectPtrView {
    FWeakObjectPtr WeakObject;
    std::int32_t TagAtLastTest;
    std::uint32_t Guid[4];
};
static_assert(sizeof(FLazyObjectPtrView) == 28);

struct FSoftObjectPtrView {
    FWeakObjectPtr WeakObject;
    std::int32_t TagAtLastTest;
    std::int32_t Padding0C;
    FName AssetPathName;
    FStringBuffer SubPathString;
};
static_assert(sizeof(FSoftObjectPtrView) == 40);

struct FScriptDelegate {
    FWeakObjectPtr Object;
    FName FunctionName;
};
static_assert(sizeof(FScriptDelegate) == 16);

struct FFieldPathView {
    void* ResolvedField;
    FWeakObjectPtr ResolvedOwner;
    FScriptArray Path;
};
static_assert(sizeof(FFieldPathView) == 32);

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
// build. These non-owning views expose metadata pointers only; they never model
// live property values or take ownership of an Unreal allocation.
struct FStructProperty : FProperty {
    std::byte Padding60[0x18];
    struct UStruct* Struct;
};
static_assert(offsetof(FStructProperty, Struct) == 0x78);

struct FObjectPropertyBase : FProperty {
    std::byte Padding60[0x18];
    UObject* PropertyClass;
};
static_assert(offsetof(FObjectPropertyBase, PropertyClass) == 0x78);

struct FClassProperty : FObjectPropertyBase {
    UObject* MetaClass;
};
static_assert(offsetof(FClassProperty, MetaClass) == 0x80);

struct FInterfaceProperty : FProperty {
    std::byte Padding60[0x18];
    UObject* InterfaceClass;
};
static_assert(offsetof(FInterfaceProperty, InterfaceClass) == 0x78);

struct FArrayProperty : FProperty {
    std::byte Padding60[0x18];
    FProperty* Inner;
};
static_assert(offsetof(FArrayProperty, Inner) == 0x78);

struct FSetProperty : FProperty {
    std::byte Padding60[0x18];
    FProperty* ElementProperty;
    FScriptSetLayout SetLayout;
};
static_assert(offsetof(FSetProperty, ElementProperty) == 0x78);
static_assert(offsetof(FSetProperty, SetLayout) == 0x80);

struct FMapProperty : FProperty {
    std::byte Padding60[0x18];
    FProperty* KeyProperty;
    FProperty* ValueProperty;
    FScriptMapLayout MapLayout;
    std::uint32_t MapFlags;
    std::uint32_t PaddingA4;
};
static_assert(offsetof(FMapProperty, KeyProperty) == 0x78);
static_assert(offsetof(FMapProperty, ValueProperty) == 0x80);
static_assert(offsetof(FMapProperty, MapLayout) == 0x88);

struct FEnumProperty : FProperty {
    // UE 4.27 stores the numeric property first, followed by its UEnum.
    // Keep this order explicit: swapping both valid-looking pointers can make
    // the metadata writer treat an FProperty as a UObject.
    std::byte Padding60[0x18];
    FProperty* UnderlyingProperty;
    UObject* Enum;
};
static_assert(offsetof(FEnumProperty, UnderlyingProperty) == 0x78);
static_assert(offsetof(FEnumProperty, Enum) == 0x80);

struct FByteProperty : FProperty {
    std::byte Padding60[0x18];
    UObject* Enum;
};
static_assert(offsetof(FByteProperty, Enum) == 0x78);

struct FDelegateProperty : FProperty {
    std::byte Padding60[0x18];
    struct UFunction* SignatureFunction;
};
static_assert(offsetof(FDelegateProperty, SignatureFunction) == 0x78);

struct FFieldPathProperty : FProperty {
    std::byte Padding60[0x18];
    FFieldClass* PropertyClass;
};
static_assert(offsetof(FFieldPathProperty, PropertyClass) == 0x78);
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

// Deceive Inc. uses UE 4.27's non-case-preserving UClass layout. The CDO is
// deliberately the only UClass-specific field exposed by this bounded view.
// Runtime callers additionally validate the pointer against GUObjectArray,
// the exact class, and RF_ClassDefaultObject before publishing a handle.
struct UClass : UStruct {
    std::byte Padding60[0xB8];
    UObject* ClassDefaultObject;
};
static_assert(offsetof(UClass, ClassDefaultObject) == 0x118);
static_assert(sizeof(UClass) == 0x120);

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

struct FEnumNameValue {
    FName Name;
    std::int64_t Value;
};
static_assert(sizeof(FEnumNameValue) == 0x10);

// UEnum stores its reflected names as TArray<TPair<FName, int64>> at 0x50.
// Only that bounded array is read while producing the address-free snapshot.
struct UEnum : UField {
    FStringBuffer CppType;
    FEnumNameValue* Names;
    std::int32_t NamesNum;
    std::int32_t NamesMax;
};
static_assert(offsetof(UEnum, Names) == 0x50);
static_assert(sizeof(UEnum) == 0x60);
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
