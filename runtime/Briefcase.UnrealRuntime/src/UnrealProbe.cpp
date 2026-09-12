#include "UnrealProbe.h"

#include "Log.h"
#include "PeImageView.h"
#include "RuntimeProfile.h"
#include "RuntimeSymbolResolver.h"
#include "UnrealMarshalling.h"
#include "UnrealInvocation.h"
#include "UnrealPatching.h"
#include "UnrealValueCodec.h"
#include "UnrealMetadataSnapshot.h"
#include "UnrealReflection.h"

#include <Briefcase/BriefcaseModApi.h>
#include <Windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cwchar>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <span>
#include <string>
#include <string_view>
#include <tuple>
#include <type_traits>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {
using namespace briefcase::unreal;
const briefcase::profile::RuntimeProfile* ActiveProfile{};

struct RootLeaseState {
    std::uint32_t Count{};
    bool RootAddedByBriefcase{};
};
std::mutex RootLeaseMutex;
std::unordered_map<std::uint64_t, RootLeaseState> RootLeases;
constexpr LONG RootSetFlag = 1L << 30;

void dumpProperties(const UObject* object, FNameToString convert) {
    const auto* structure = reinterpret_cast<const UStruct*>(object);
    if (!readable(structure, sizeof(UStruct))) return;
    briefcase::log(L"  PropertiesSize=" + std::to_wstring(structure->PropertiesSize));
    auto* field = structure->ChildProperties;
    for (unsigned visited = 0; field && visited < 256; ++visited) {
        if (!readable(field, sizeof(FProperty))) {
            briefcase::log(L"  property traversal stopped: unreadable FField");
            break;
        }
        const auto* property = reinterpret_cast<const FProperty*>(field);
        const auto propertyName = nameToString(field->NamePrivate, convert);
        briefcase::log(L"  property " + propertyName + L" offset=" + hexadecimal(static_cast<std::uintptr_t>(property->OffsetInternal)) +
                 L" elementSize=" + std::to_wstring(property->ElementSize) + L" arrayDim=" + std::to_wstring(property->ArrayDim));
        field = field->Next;
    }
}

void dumpFunctions(const UObject* object, FNameToString convert) {
    const auto* structure = reinterpret_cast<const UStruct*>(object);
    if (!readable(structure, sizeof(UStruct))) return;
    auto* child = structure->Children;
    for (unsigned visited = 0; child && visited < 512; ++visited) {
        if (!readable(child, sizeof(UField)) || !readable(child->ClassPrivate, sizeof(UObject))) break;
        const auto typeName = nameToString(child->ClassPrivate->NamePrivate, convert);
        if (typeName == L"Function" && readable(reinterpret_cast<const std::byte*>(child) + 0xB6, sizeof(std::uint16_t))) {
            const auto parameterSize = *reinterpret_cast<const std::uint16_t*>(reinterpret_cast<const std::byte*>(child) + 0xB6);
            briefcase::log(L"  function " + nameToString(child->NamePrivate, convert) + L" parameterBytes=" + std::to_wstring(parameterSize));
        }
        child = child->Next;
    }
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiFindObject(
    void*, const char* utf8Path, std::uint32_t pathLength, BriefcaseObjectHandle* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!result || !writable(result, sizeof(*result))) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    std::wstring requestedPath;
    if (!decodeUtf8(utf8Path, pathLength, requestedPath)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto separator = requestedPath.find_last_of(L'.');
    const auto requestedLeaf = separator == std::wstring::npos
        ? requestedPath : requestedPath.substr(separator + 1);
    const auto count = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < count; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject))) continue;
        const auto* object = item->Object;
        if (object->InternalIndex != index ||
            nameToString(object->NamePrivate, RuntimeNameConverter) != requestedLeaf)
            continue;
        if (objectPath(object, RuntimeNameConverter) != requestedPath) continue;
        return makeHandle(object, *result) ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_STALE_HANDLE;
    }
    return BRIEFCASE_UNREAL_NOT_FOUND;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiFindObjectsOfClass(
    void*, BriefcaseObjectHandle classHandle, BriefcaseObjectHandle* results,
    std::uint32_t capacity, std::uint32_t* written, std::uint32_t* total) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!written || !total || !writable(written, sizeof(*written)) ||
        !writable(total, sizeof(*total)) ||
        (capacity && (!results || !writable(results, sizeof(*results) * capacity))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* requestedClass = resolveObject(classHandle);
    if (!requestedClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(requestedClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    *written = 0;
    *total = 0;
    const auto count = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < count; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject)) ||
            item->Object->InternalIndex != index || !objectIsA(item->Object, requestedClass))
            continue;
        ++*total;
        if (*written < capacity && makeHandle(item->Object, results[*written]))
            ++*written;
    }
    return *written < *total ? BRIEFCASE_UNREAL_BUFFER_TOO_SMALL : BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetObjectName(
    void*, BriefcaseObjectHandle handle, char* destination,
    std::uint32_t capacity, std::uint32_t* requiredBytes) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    const auto* object = resolveObject(handle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    return writeUtf8(nameToString(object->NamePrivate, RuntimeNameConverter),
                     destination, capacity, requiredBytes);
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetObjectPath(
    void*, BriefcaseObjectHandle handle, char* destination,
    std::uint32_t capacity, std::uint32_t* requiredBytes) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    const auto* object = resolveObject(handle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    return writeUtf8(objectPath(object, RuntimeNameConverter),
                     destination, capacity, requiredBytes);
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetObjectClass(
    void*, BriefcaseObjectHandle handle, BriefcaseObjectHandle* classHandle) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!classHandle || !writable(classHandle, sizeof(*classHandle)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* object = resolveObject(handle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    return makeHandle(object->ClassPrivate, *classHandle) ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_STALE_HANDLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiIsObjectA(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle classHandle, BriefcaseBool* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!result || !writable(result, sizeof(*result))) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* object = resolveObject(objectHandle);
    const auto* requestedClass = resolveObject(classHandle);
    if (!object || !requestedClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(requestedClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    *result = objectIsA(object, requestedClass) ? 1u : 0u;
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetClassDefaultObject(
    void*, BriefcaseObjectHandle classHandle, BriefcaseObjectHandle* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!result || !writable(result, sizeof(*result)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *result = {std::numeric_limits<std::uint32_t>::max(), 0};

    const auto* classObject = resolveObject(classHandle);
    if (!classObject) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(classObject)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    const auto* unrealClass = reinterpret_cast<const UClass*>(classObject);
    if (!readable(unrealClass, sizeof(UClass))) return BRIEFCASE_UNREAL_UNREADABLE;

    const auto* defaultObject = unrealClass->ClassDefaultObject;
    if (!defaultObject) return BRIEFCASE_UNREAL_NOT_FOUND;
    constexpr std::uint32_t ClassDefaultObjectFlag = 0x00000010u;
    if (!isRegisteredObject(defaultObject) ||
        (defaultObject->ObjectFlags & ClassDefaultObjectFlag) == 0 ||
        !objectIsA(defaultObject, classObject))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    return makeHandle(defaultObject, *result)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_STALE_HANDLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetObjectOuter(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!result || !writable(result, sizeof(*result)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* object = resolveObject(objectHandle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    return makeHandle(object->OuterPrivate, *result)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_STALE_HANDLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetObjectFlags(
    void*, BriefcaseObjectHandle objectHandle, std::uint32_t* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!result || !writable(result, sizeof(*result)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* object = resolveObject(objectHandle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    *result = object->ObjectFlags;
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiGetPropertyInfo(
    void*, BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, BriefcasePropertyInfo* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!result || !readable(result, sizeof(result->StructSize)) ||
        !writable(result, sizeof(*result)) || result->StructSize < sizeof(BriefcasePropertyInfo))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = findProperty(ownerClass, name);
    if (!property) return BRIEFCASE_UNREAL_NOT_FOUND;

    *result = {};
    result->StructSize = sizeof(BriefcasePropertyInfo);
    result->Kind = propertyKind(property);
    result->Offset = property->OffsetInternal;
    result->ElementSize = property->ElementSize;
    result->ArrayDimension = property->ArrayDim;
    result->Flags = property->PropertyFlags;
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReadProperty(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, void* output, std::uint32_t outputSize) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    const auto* object = resolveObject(objectHandle);
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!object || !ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass) || !objectIsA(object, ownerClass))
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = findProperty(ownerClass, name);
    if (!property) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (property->OffsetInternal != expectedOffset ||
        property->ElementSize != expectedElementSize ||
        property->ArrayDim != expectedArrayDimension ||
        propertyKind(property) != expectedKind ||
        (expectedKind != BRIEFCASE_PROPERTY_STRUCT &&
         canonicalSize(expectedKind) != expectedElementSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    if (expectedOffset < 0 || expectedElementSize <= 0) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    const auto* actualClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    if (!readable(actualClass, sizeof(UStruct)) || actualClass->PropertiesSize < 0)
        return BRIEFCASE_UNREAL_UNREADABLE;
    const auto end = static_cast<std::uint64_t>(expectedOffset) +
                     static_cast<std::uint64_t>(expectedElementSize);
    if (end > static_cast<std::uint64_t>(actualClass->PropertiesSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const auto* source = reinterpret_cast<const std::byte*>(object) + expectedOffset;
    if (!readable(source, static_cast<std::size_t>(expectedElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;

    if (expectedKind == BRIEFCASE_PROPERTY_OBJECT) {
        if (!output || outputSize < sizeof(BriefcaseObjectHandle) ||
            !writable(output, sizeof(BriefcaseObjectHandle)))
            return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
        BriefcaseObjectHandle referencedHandle{};
        if (!copyCanonicalFixedValue(
                property, source, reinterpret_cast<std::byte*>(&referencedHandle),
                sizeof(referencedHandle), 0)) return BRIEFCASE_UNREAL_STALE_HANDLE;
        return safeCopy(output, &referencedHandle, sizeof(referencedHandle))
            ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
    }

    if (expectedKind == BRIEFCASE_PROPERTY_BOOL) {
        if (!output || outputSize < sizeof(BriefcaseBool) ||
            !writable(output, sizeof(BriefcaseBool)))
            return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
        // Unreal native bools and Blueprint bitfields can share one byte.
        // Apply the reflected field mask; copying the whole storage byte would
        // report a neighbouring flag as this property.
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        if (!readable(boolean, sizeof(FBoolProperty)) || boolean->FieldSize != 1 ||
            boolean->ByteOffset >= expectedElementSize || boolean->FieldMask == 0)
            return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
        std::uint8_t storage{};
        if (!safeCopy(&storage, source + boolean->ByteOffset, sizeof(storage)))
            return BRIEFCASE_UNREAL_UNREADABLE;
        const auto value = (storage & boolean->FieldMask) != 0
            ? static_cast<BriefcaseBool>(1) : static_cast<BriefcaseBool>(0);
        return safeCopy(output, &value, sizeof(value))
            ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
    }

    if (expectedKind == BRIEFCASE_PROPERTY_STRUCT) {
        if (!output || outputSize < static_cast<std::uint32_t>(expectedElementSize) ||
            !writable(output, static_cast<std::size_t>(expectedElementSize)))
            return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
        std::memset(output, 0, static_cast<std::size_t>(expectedElementSize));
        return copyCanonicalFixedValue(
            property, source, reinterpret_cast<std::byte*>(output),
            static_cast<std::size_t>(expectedElementSize), 0)
            ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
    }

    if (!output || outputSize < static_cast<std::uint32_t>(expectedElementSize) ||
        !writable(output, static_cast<std::size_t>(expectedElementSize)))
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    return safeCopy(output, source, static_cast<std::size_t>(expectedElementSize))
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReadStringProperty(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    std::uint16_t* destination, std::uint32_t capacityCharacters,
    std::uint32_t* requiredCharacters) {
    constexpr std::uint32_t MaximumCharacters = 64u * 1024u;
    if (!requiredCharacters || !writable(requiredCharacters, sizeof(*requiredCharacters)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* object = resolveObject(objectHandle);
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!object || !ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass) || !objectIsA(object, ownerClass))
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = findProperty(ownerClass, name);
    if (!property) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (property->OffsetInternal != expectedOffset ||
        property->ElementSize != expectedElementSize ||
        property->ArrayDim != expectedArrayDimension ||
        propertyKind(property) != BRIEFCASE_PROPERTY_STRING ||
        expectedElementSize != sizeof(FStringBuffer) || expectedOffset < 0)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    const auto* actualClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    if (!readable(actualClass, sizeof(UStruct)) || actualClass->PropertiesSize < 0 ||
        static_cast<std::uint64_t>(expectedOffset) + sizeof(FStringBuffer) >
            static_cast<std::uint64_t>(actualClass->PropertiesSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    FStringBuffer value{};
    const auto* source = reinterpret_cast<const std::byte*>(object) + expectedOffset;
    if (!safeCopy(&value, source, sizeof(value)) || value.Num < 0 ||
        value.Max < value.Num || static_cast<std::uint32_t>(value.Num) > MaximumCharacters)
        return BRIEFCASE_UNREAL_UNREADABLE;
    *requiredCharacters = static_cast<std::uint32_t>(value.Num);
    if (value.Num == 0) return BRIEFCASE_UNREAL_OK;
    const auto byteCount = static_cast<std::size_t>(value.Num) * sizeof(std::uint16_t);
    if (!value.Data || !readable(value.Data, byteCount)) return BRIEFCASE_UNREAL_UNREADABLE;
    if (!destination || capacityCharacters < *requiredCharacters)
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (!writable(destination,
                  static_cast<std::size_t>(capacityCharacters) * sizeof(std::uint16_t)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    return safeCopy(destination, value.Data, byteCount)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReadTextProperty(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    std::uint16_t* destination, std::uint32_t capacityCharacters,
    std::uint32_t* requiredCharacters) {
    const auto* object = resolveObject(objectHandle);
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!object || !ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass) || !objectIsA(object, ownerClass))
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = findProperty(ownerClass, name);
    if (!property) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (property->OffsetInternal != expectedOffset || expectedOffset < 0 ||
        property->ElementSize != expectedElementSize || expectedElementSize != 24 ||
        property->ArrayDim != expectedArrayDimension || expectedArrayDimension != 1 ||
        propertyKind(property) != BRIEFCASE_PROPERTY_TEXT)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const auto* actualClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    if (!readable(actualClass, sizeof(UStruct)) || actualClass->PropertiesSize < 0 ||
        static_cast<std::uint64_t>(expectedOffset) + 24 >
            static_cast<std::uint64_t>(actualClass->PropertiesSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    TextConversionRuntime runtime{};
    if (!resolveTextConversionRuntime(runtime)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    return textToUtf16(
        runtime, reinterpret_cast<const std::byte*>(object) + expectedOffset,
        destination, capacityCharacters, requiredCharacters);
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReadValueProperty(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, std::uint8_t* destination,
    std::uint32_t capacity, std::uint32_t* requiredBytes) {
    if (!requiredBytes || !writable(requiredBytes, sizeof(*requiredBytes)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *requiredBytes = 0;
    const UObject* object{};
    const FProperty* property{};
    std::byte* value{};
    const auto resolved = resolvePropertyAccess(
        objectHandle, ownerClassHandle, utf8Name, nameLength, expectedOffset,
        expectedElementSize, expectedArrayDimension, expectedKind,
        object, property, value);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;

    ValueWireBuilder wire;
    if (!wire.append(ValueWireMagic) || !appendValueNode(property, value, wire, 0))
        return BRIEFCASE_UNREAL_UNREADABLE;
    if (wire.Bytes.size() > std::numeric_limits<std::uint32_t>::max())
        return BRIEFCASE_UNREAL_UNREADABLE;
    *requiredBytes = static_cast<std::uint32_t>(wire.Bytes.size());
    if (!destination || capacity < *requiredBytes)
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (!writable(destination, capacity)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    return safeCopy(destination, wire.Bytes.data(), wire.Bytes.size())
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWriteProperty(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, const void* input, std::uint32_t inputSize) {
    if (!input || inputSize == 0 || !readable(input, inputSize))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (expectedKind == BRIEFCASE_PROPERTY_STRING || expectedKind == BRIEFCASE_PROPERTY_TEXT ||
        expectedKind == BRIEFCASE_PROPERTY_ARRAY || expectedKind == BRIEFCASE_PROPERTY_SET ||
        expectedKind == BRIEFCASE_PROPERTY_MAP)
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    const UObject* object{};
    const FProperty* property{};
    std::byte* value{};
    const auto resolved = resolvePropertyAccess(
        objectHandle, ownerClassHandle, utf8Name, nameLength, expectedOffset,
        expectedElementSize, expectedArrayDimension, expectedKind,
        object, property, value);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;
    const auto canonicalSize = expectedKind == BRIEFCASE_PROPERTY_BOOL ? 1u :
        expectedKind == BRIEFCASE_PROPERTY_OBJECT
            ? static_cast<std::uint32_t>(sizeof(BriefcaseObjectHandle))
            : static_cast<std::uint32_t>(expectedElementSize);
    if (inputSize != canonicalSize) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    if (!writable(value, static_cast<std::size_t>(expectedElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    return writeCanonicalFixedValue(
        property, value, reinterpret_cast<const std::byte*>(input), inputSize, 0)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

struct AssetLoadingRuntime {
    UObject* Library{};
    const UFunction* MakeSoftObjectPath{};
    const FProperty* SoftObjectPathReturn{};
    const UFunction* LoadAssetBlocking{};
    const FProperty* SoftObjectInput{};
};

// StaticLoadObject is not exported by Shipping builds and its internal
// signature is especially easy to misidentify. Both supported Deceive Inc.
// executables expose these stable, reflected UE 4.27 functions instead. The
// bridge validates their complete layouts before invoking either one.
bool resolveAssetLoadingRuntime(AssetLoadingRuntime& result) {
    static std::mutex cacheMutex;
    static const FUObjectArray* cacheOwner{};
    static AssetLoadingRuntime cache{};
    const std::scoped_lock lock(cacheMutex);
    if (cacheOwner == RuntimeObjects && cache.Library && cache.MakeSoftObjectPath &&
        cache.SoftObjectPathReturn && cache.LoadAssetBlocking && cache.SoftObjectInput &&
        readable(cache.Library, sizeof(UObject)) &&
        readable(cache.MakeSoftObjectPath, sizeof(UFunction)) &&
        readable(cache.LoadAssetBlocking, sizeof(UFunction))) {
        result = cache;
        return true;
    }

    auto* library = const_cast<UObject*>(findObjectByPath(
        L"/Script/Engine.Default__KismetSystemLibrary"));
    const auto* libraryClass = findObjectByPath(L"/Script/Engine.KismetSystemLibrary");
    if (!library || !libraryClass || !isClassObject(libraryClass)) return false;
    const auto* makePath = findFunction(libraryClass, L"MakeSoftObjectPath");
    const auto* load = findFunction(libraryClass, L"LoadAsset_Blocking");
    if (!makePath || makePath->ParmsSize != 40 || !load || load->ParmsSize != 48)
        return false;
    const auto* pathInput = findFunctionParameter(makePath, L"StrProperty", 0, 16);
    const auto* pathReturn = findFunctionParameter(makePath, L"StructProperty", 16, 24);
    const auto* assetInput = findFunctionParameter(load, L"SoftObjectProperty", 0, 40);
    const auto* assetReturn = findFunctionParameter(load, L"ObjectProperty", 40, 8);
    if (!pathInput || !pathReturn || !assetInput || !assetReturn) return false;

    cache = {library, makePath, pathReturn, load, assetInput};
    cacheOwner = RuntimeObjects;
    result = cache;
    return true;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiLoadObject(
    void*, BriefcaseObjectHandle expectedClassHandle,
    const char* utf8Path, std::uint32_t pathLength,
    BriefcaseObjectHandle* result) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    if (!result || !writable(result, sizeof(*result)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *result = {std::numeric_limits<std::uint32_t>::max(), 0};

    const auto* expectedClass = resolveObject(expectedClassHandle);
    if (!expectedClass || !isClassObject(expectedClass))
        return BRIEFCASE_UNREAL_STALE_HANDLE;
    std::wstring path;
    if (!decodeUtf8(utf8Path, pathLength, path) ||
        path.empty() || path.find(L'\0') != std::wstring::npos)
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;

    if (const auto* loaded = findObjectByPath(path)) {
        if (!objectIsA(loaded, expectedClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
        return makeHandle(loaded, *result)
            ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_STALE_HANDLE;
    }

    AssetLoadingRuntime runtime{};
    if (!resolveAssetLoadingRuntime(runtime)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    const auto processEvent = processEventFor(runtime.Library);
    if (!processEvent) return BRIEFCASE_UNREAL_UNREADABLE;

    // MakeSoftObjectPath borrows this FString for the synchronous call. Its
    // Data points into the decoded std::wstring and is never handed to an
    // FProperty destructor.
    std::array<std::byte, 40> makeParameters{};
    auto* input = reinterpret_cast<FStringBuffer*>(makeParameters.data());
    input->Data = path.data();
    input->Num = static_cast<std::int32_t>(path.size() + 1);
    input->Max = input->Num;
    auto* softPath = makeParameters.data() + 16;
    if (!initializePropertyValue(runtime.SoftObjectPathReturn, softPath))
        return BRIEFCASE_UNREAL_UNREADABLE;
    if (!safeProcessEvent(processEvent, runtime.Library,
                          runtime.MakeSoftObjectPath, makeParameters.data())) {
        destroyPropertyValue(runtime.SoftObjectPathReturn, softPath);
        return BRIEFCASE_UNREAL_UNREADABLE;
    }

    std::array<std::byte, 48> loadParameters{};
    auto* softObject = loadParameters.data();
    if (!initializePropertyValue(runtime.SoftObjectInput, softObject)) {
        destroyPropertyValue(runtime.SoftObjectPathReturn, softPath);
        return BRIEFCASE_UNREAL_UNREADABLE;
    }

    // UE 4.27 FSoftObjectPtr stores FSoftObjectPath after its 16-byte weak
    // object cache. Move the owning path into the initialized soft-object
    // parameter, then zero the source so exactly one FProperty destructor owns
    // the possible SubPathString allocation.
    const auto moved = safeCopy(softObject + 16, softPath, 24);
    if (moved) std::memset(softPath, 0, 24);
    if (!moved) {
        destroyPropertyValue(runtime.SoftObjectInput, softObject);
        destroyPropertyValue(runtime.SoftObjectPathReturn, softPath);
        return BRIEFCASE_UNREAL_UNREADABLE;
    }

    const auto invoked = safeProcessEvent(
        processEvent, runtime.Library, runtime.LoadAssetBlocking, loadParameters.data());
    UObject* loaded{};
    const auto copied = invoked && safeCopy(&loaded, loadParameters.data() + 40, sizeof(loaded));
    const auto destroyedAsset = destroyPropertyValue(runtime.SoftObjectInput, softObject);
    const auto destroyedPath = destroyPropertyValue(runtime.SoftObjectPathReturn, softPath);
    if (!invoked || !copied || !destroyedAsset || !destroyedPath)
        return BRIEFCASE_UNREAL_UNREADABLE;
    if (!loaded) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (!isRegisteredObject(loaded)) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!objectIsA(loaded, expectedClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    return makeHandle(loaded, *result)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_STALE_HANDLE;
}

std::uint64_t rootLeaseKey(BriefcaseObjectHandle handle) noexcept {
    return (static_cast<std::uint64_t>(handle.Index) << 32) | handle.SerialNumber;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiAcquireObjectRoot(
    void*, BriefcaseObjectHandle objectHandle) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    // Acquiring must happen on the game thread so a GC pass cannot invalidate
    // the object between serial validation and setting RootSet.
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    const auto* object = resolveObject(objectHandle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    auto* item = itemAt(RuntimeObjects->ObjObjects, object->InternalIndex);
    if (!item || item->Object != object) return BRIEFCASE_UNREAL_STALE_HANDLE;

    const std::scoped_lock lock(RootLeaseMutex);
    const auto key = rootLeaseKey(objectHandle);
    if (auto existing = RootLeases.find(key); existing != RootLeases.end()) {
        if (existing->second.Count == std::numeric_limits<std::uint32_t>::max())
            return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
        ++existing->second.Count;
        return BRIEFCASE_UNREAL_OK;
    }
    const auto previous = InterlockedOr(
        reinterpret_cast<volatile LONG*>(&item->Flags), RootSetFlag);
    RootLeases.emplace(key, RootLeaseState{1, (previous & RootSetFlag) == 0});
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReleaseObjectRoot(
    void*, BriefcaseObjectHandle objectHandle) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    const std::scoped_lock lock(RootLeaseMutex);
    const auto found = RootLeases.find(rootLeaseKey(objectHandle));
    if (found == RootLeases.end()) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (--found->second.Count != 0) return BRIEFCASE_UNREAL_OK;

    const auto owned = found->second.RootAddedByBriefcase;
    RootLeases.erase(found);
    if (!owned) return BRIEFCASE_UNREAL_OK;
    if (objectHandle.Index > static_cast<std::uint32_t>(
            std::numeric_limits<std::int32_t>::max()))
        return BRIEFCASE_UNREAL_STALE_HANDLE;
    auto* item = itemAt(
        RuntimeObjects->ObjObjects, static_cast<std::int32_t>(objectHandle.Index));
    if (!item || static_cast<std::uint32_t>(item->SerialNumber) != objectHandle.SerialNumber)
        return BRIEFCASE_UNREAL_STALE_HANDLE;
    InterlockedAnd(reinterpret_cast<volatile LONG*>(&item->Flags), ~RootSetFlag);
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWriteTextProperty(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, const std::uint16_t* characters,
    std::uint32_t characterCount) {
    if (expectedKind != BRIEFCASE_PROPERTY_STRING && expectedKind != BRIEFCASE_PROPERTY_TEXT)
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    if (characterCount >= 65'536 ||
        (characterCount && (!characters ||
         !readable(characters, static_cast<std::size_t>(characterCount) * sizeof(std::uint16_t)))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    std::vector<std::uint16_t> terminated(characterCount + 1);
    if (characterCount && !safeCopy(
            terminated.data(), characters,
            static_cast<std::size_t>(characterCount) * sizeof(std::uint16_t)))
        return BRIEFCASE_UNREAL_UNREADABLE;

    const UObject* object{};
    const FProperty* property{};
    std::byte* value{};
    const auto resolved = resolvePropertyAccess(
        objectHandle, ownerClassHandle, utf8Name, nameLength, expectedOffset,
        expectedElementSize, expectedArrayDimension, expectedKind,
        object, property, value);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;
    if (!writable(value, static_cast<std::size_t>(expectedElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    TextConversionRuntime runtime{};
    if (!resolveTextConversionRuntime(runtime)) return BRIEFCASE_UNREAL_UNSUPPORTED;

    if (expectedKind == BRIEFCASE_PROPERTY_TEXT) {
        if (expectedElementSize != 24) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
        OwnedTextValue temporary;
        if (!constructText(runtime, terminated.data(), characterCount, temporary))
            return BRIEFCASE_UNREAL_UNREADABLE;
        std::array<std::byte, 24> bytes{};
        const auto copied = safeCopy(bytes.data(), temporary.Parameters.data() + 16, bytes.size());
        if (!copied || !destroyPropertyValue(property, value) ||
            !safeCopy(value, bytes.data(), bytes.size())) {
            destroyText(temporary);
            return BRIEFCASE_UNREAL_UNREADABLE;
        }
        temporary.Initialized = false; // ownership moved into the UObject property
        return BRIEFCASE_UNREAL_OK;
    }

    if (expectedElementSize != sizeof(FStringBuffer))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    OwnedStringValue temporary;
    if (!constructString(runtime, terminated.data(), characterCount, temporary))
        return BRIEFCASE_UNREAL_UNREADABLE;
    std::array<std::byte, sizeof(FStringBuffer)> bytes{};
    const auto copied = safeCopy(bytes.data(), temporary.Parameters.data() + 24, bytes.size());
    if (!copied || !destroyPropertyValue(property, value) ||
        !safeCopy(value, bytes.data(), bytes.size())) {
        if (temporary.Initialized)
            destroyPropertyValue(temporary.Property, temporary.Parameters.data() + 24);
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    temporary.Initialized = false; // ownership moved into the UObject property
    return BRIEFCASE_UNREAL_OK;
}

const UObject* findLiveInstanceOfClass(const UObject* requestedClass) {
    if (!requestedClass || !isClassObject(requestedClass) || !RuntimeObjects) return nullptr;

    const auto count = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < count; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject))) continue;
        const auto* object = item->Object;
        if (object->InternalIndex != index || !objectIsA(object, requestedClass)) continue;

        // A class default object describes the class but is not a widget shown
        // to the player. Only a live instance proves that the screen exists.
        const auto name = nameToString(object->NamePrivate, RuntimeNameConverter);
        if (name.starts_with(L"Default__")) continue;
        return object;
    }
    return nullptr;
}

const UObject* findLiveInstance(std::wstring_view classPath, const UObject*& cachedClass) {
    if (cachedClass && !isRegisteredObject(cachedClass)) cachedClass = nullptr;
    if (!cachedClass) cachedClass = findObjectByPath(classPath);
    return findLiveInstanceOfClass(cachedClass);
}

bool waitForDedicatedServerStartup(const FUObjectArray* objects) {
    // A dedicated server has no frontend widget. Retain the existing bounded
    // registry-population boundary so server mods do not resolve their classes
    // against the first, still incomplete native object set.
    constexpr std::int32_t StartupObjectThreshold = 50'000;
    constexpr unsigned StartupReadinessAttempts = 120;
    const auto initialCount = objects->ObjObjects.NumElements;
    for (unsigned attempt = 0; attempt < StartupReadinessAttempts &&
         saneObjectArray(objects) &&
         objects->ObjObjects.NumElements < StartupObjectThreshold; ++attempt) {
        Sleep(250);
    }
    if (!saneObjectArray(objects)) return false;
    briefcase::log(L"dedicated-server startup registry ready: " +
        std::to_wstring(initialCount) + L" -> " +
        std::to_wstring(objects->ObjObjects.NumElements));
    return true;
}

bool waitForClientStartupScreen(const FUObjectArray* objects) {
    constexpr std::wstring_view LoginMenuClass =
        L"/Script/DeceiveInc.LoginMenuUserWidget";
    constexpr std::wstring_view MainMenuClass =
        L"/Script/DeceiveInc.DIMainMenuUserWidget";
    constexpr unsigned PollsBetweenProgressLogs = 40; // ten seconds

    const UObject* loginMenuClass{};
    const UObject* mainMenuClass{};
    briefcase::log(L"managed startup deferred: waiting for the login screen after shader compilation");

    for (unsigned poll = 0; saneObjectArray(objects); ++poll) {
        if (const auto* loginMenu = findLiveInstance(LoginMenuClass, loginMenuClass)) {
            briefcase::log(L"client startup screen ready: " + objectPath(loginMenu, RuntimeNameConverter) +
                L"; objects=" + std::to_wstring(objects->ObjObjects.NumElements));
            return true;
        }

        // Also accept the main menu in case the player manually dismisses the
        // login screen between two polls. Both widgets are created after the
        // startup shader phase, so neither can release managed code too early.
        if (const auto* mainMenu = findLiveInstance(MainMenuClass, mainMenuClass)) {
            briefcase::log(L"client main menu ready: " + objectPath(mainMenu, RuntimeNameConverter) +
                L"; objects=" + std::to_wstring(objects->ObjObjects.NumElements));
            return true;
        }

        if (poll != 0 && poll % PollsBetweenProgressLogs == 0) {
            briefcase::log(L"managed startup still deferred; waiting for the login screen; objects=" +
                std::to_wstring(objects->ObjObjects.NumElements));
        }
        Sleep(250);
    }

    briefcase::log(L"managed startup remains disabled because GUObjectArray became invalid");
    return false;
}

const BriefcaseUnrealApi UnrealApi{
    sizeof(BriefcaseUnrealApi), BRIEFCASE_UNREAL_API_VERSION, nullptr,
    apiFindObject, apiFindObjectsOfClass, apiGetObjectName, apiGetObjectPath,
    apiGetObjectClass, apiIsObjectA, apiGetPropertyInfo, apiReadProperty,
    apiInvokeFunction, apiReadStringProperty,
    apiInvokeFunctionText, apiReadTextProperty, apiInvokeNativeBoolean,
    apiReadValueProperty, apiWriteProperty, apiWriteTextProperty,
    apiPrepareFunction, apiInvokePreparedFunction,
    apiPrepareProperty, apiReadPreparedProperty, apiWritePreparedProperty,
    apiInvokePreparedValueFunction, apiReleaseValueBuffer,
    apiInvokePreparedValueFunctionV2, apiWritePreparedValueProperty,
    apiGetClassDefaultObject, apiGetObjectOuter, apiGetObjectFlags,
    apiLoadObject, apiAcquireObjectRoot, apiReleaseObjectRoot,
    apiSubscribeMulticastDelegate, apiUnsubscribeDelegate};
const BriefcaseGameThreadApi GameThreadApi{
    sizeof(BriefcaseGameThreadApi), BRIEFCASE_GAME_THREAD_API_VERSION, nullptr,
    apiRegisterGameThreadCallback, apiUnregisterGameThreadCallback,
    apiRequestGameThreadPump, apiIsGameThread, {}};
const BriefcasePatchingApi PatchingApi{
    sizeof(BriefcasePatchingApi), BRIEFCASE_PATCHING_API_VERSION, nullptr,
    apiRegisterPatch, apiUnregisterPatch,
    apiRegisterNativePatch, apiUnregisterNativePatch,
    apiCopyPatchByteArray, apiCopyPatchString, apiCopyPatchText,
    apiCopyPatchValue, apiWritePatchValue, apiWritePatchText,
    apiWritePatchEncodedValue};

} // namespace

namespace briefcase {

const BriefcaseUnrealApi* getUnrealApi() { return isUnrealApiReady() ? &UnrealApi : nullptr; }
const BriefcasePatchingApi* getPatchingApi() { return isUnrealApiReady() ? &PatchingApi : nullptr; }
const BriefcaseGameThreadApi* getGameThreadApi() {
    return isUnrealApiReady() ? &GameThreadApi : nullptr;
}
void captureGameThreadId(DWORD threadId) {
    setCapturedGameThreadId(threadId);
}
bool isUnrealApiReady() {
    return RuntimeObjects && RuntimeNameConverter && RuntimeMalloc &&
           saneObjectArray(RuntimeObjects);
}
const profile::RuntimeProfile* getRuntimeProfile() { return ActiveProfile; }

void logUnsupportedRuntimeProfile(const IMAGE_NT_HEADERS64& nt) {
    wchar_t executablePathBuffer[32768]{};
    const auto executablePathLength = GetModuleFileNameW(
        nullptr, executablePathBuffer,
        static_cast<DWORD>(std::size(executablePathBuffer)));
    const auto executableName =
        executablePathLength > 0 && executablePathLength < std::size(executablePathBuffer)
            ? std::filesystem::path(executablePathBuffer).filename().wstring()
            : std::wstring{L"<unavailable>"};

    std::wostringstream identity;
    identity << L"unsupported executable identity: file=" << executableName
             << L", timestamp=0x" << std::uppercase << std::hex
             << std::setfill(L'0') << std::setw(8)
             << nt.FileHeader.TimeDateStamp
             << L", imageSize=0x" << std::setw(8)
             << nt.OptionalHeader.SizeOfImage;
    log(identity.str());

    profile::TargetKind target{};
    std::wstring_view targetName;
    std::wstring_view sdkAssemblyName;
    if (_wcsicmp(executableName.c_str(), L"DeceiveInc-Win64-Shipping.exe") == 0) {
        target = profile::TargetKind::Client;
        targetName = L"Client";
        sdkAssemblyName = L"Briefcase.DeceiveInc.Client.Sdk";
    } else if (_wcsicmp(
                   executableName.c_str(),
                   L"DeceiveIncServer-Win64-Shipping.exe") == 0) {
        target = profile::TargetKind::Server;
        targetName = L"Server";
        sdkAssemblyName = L"Briefcase.DeceiveInc.Server.Sdk";
    } else {
        log(L"RuntimeProfile candidate was not generated because the executable target is unknown");
        return;
    }

    std::wostringstream candidate;
    candidate << L"RuntimeProfile candidate (verify signatures and layouts before enabling): "
              << L"{ TargetKind::"
              << (target == profile::TargetKind::Client ? L"Client" : L"Server")
              << L", L\"" << targetName << L"\", L\"" << sdkAssemblyName
              << L"\", 0x" << std::uppercase << std::hex
              << std::setfill(L'0') << std::setw(8)
              << nt.FileHeader.TimeDateStamp
              << L", 0x" << std::setw(8) << nt.OptionalHeader.SizeOfImage
              << L" };";
    log(candidate.str());
}

void runUnrealProbe(HMODULE self, UnrealRuntimeReadyCallback onRuntimeReady) {
    wchar_t modulePath[32768]{};
    if (!GetModuleFileNameW(self, modulePath, static_cast<DWORD>(std::size(modulePath)))) return;
    log(L"standalone metadata probe started (no UE4SS imports)");

    auto* base = reinterpret_cast<std::byte*>(GetModuleHandleW(nullptr));
    if (!readable(base, sizeof(IMAGE_DOS_HEADER))) {
        log(L"main executable image is unavailable");
        return;
    }
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE || dos->e_lfanew <= 0 || !readable(base + dos->e_lfanew, sizeof(IMAGE_NT_HEADERS64))) {
        log(L"invalid PE headers");
        return;
    }
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) {
        log(L"invalid PE signature");
        return;
    }
    const auto* runtimeProfile = profile::find(
        nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage);
    if (!runtimeProfile) {
        log(L"unsupported executable build; profile refused");
        logUnsupportedRuntimeProfile(*nt);
        return;
    }

    const auto image = discovery::PeImageView::create(
        std::span<const std::byte>(base, nt->OptionalHeader.SizeOfImage));
    if (!image) {
        log(L"mapped PE image failed bounded section validation");
        return;
    }
    const auto text = image->section(".text");
    if (!text || !readableRange(text->Bytes.data(), text->Bytes.size())) {
        log(L"executable .text section is not fully readable");
        return;
    }
    const auto symbolResolution = discovery::resolveRuntimeSymbols(*image);
    if (!symbolResolution.succeeded()) {
        const auto failure = discovery::runtimeSymbolFailureName(symbolResolution.Failure);
        log(L"runtime signature resolution refused: " +
            std::wstring(failure.begin(), failure.end()));
        return;
    }
    auto* nameFunctionAddress = base + symbolResolution.Symbols.FNameToStringRva;
    const auto* objects = reinterpret_cast<const FUObjectArray*>(
        base + symbolResolution.Symbols.GUObjectArrayRva);
    void* allocator{};
    if (!safeCopy(
            &allocator, base + symbolResolution.Symbols.GMallocRva,
            sizeof(allocator)) || !validUnrealAllocator(allocator) ||
        !executable(nameFunctionAddress) || !readable(objects, sizeof(FUObjectArray))) {
        log(L"resolved Unreal symbols have incompatible memory protection");
        return;
    }
    const auto convert = reinterpret_cast<FNameToString>(nameFunctionAddress);

    for (unsigned attempt = 0; attempt < 120 && !saneObjectArray(objects); ++attempt) Sleep(250);
    if (!saneObjectArray(objects)) {
        log(L"GUObjectArray did not become structurally valid");
        return;
    }

    const auto initialObjectCount = objects->ObjObjects.NumElements;

    // The runtime APIs need a structurally valid object array and the reviewed
    // executable profile. SDK extraction can then wait for the larger metadata
    // registry without changing the resolved runtime addresses.
    RuntimeNameConverter = convert;
    RuntimeObjects = objects;
    RuntimeMalloc = allocator;
    ActiveProfile = runtimeProfile;
    configurePatchingRuntime(runtimeProfile, base, nt->OptionalHeader.SizeOfImage);
    log(L"runtime APIs ready at object count=" + std::to_wstring(initialObjectCount));

    // The client creates LoginMenuUserWidget only after its startup shader
    // phase. Use that concrete game state as the managed boundary instead of an
    // object-count or time heuristic. The dedicated server has no frontend and
    // can start managed mods as soon as its object registry is valid.
    const auto startupReady = runtimeProfile->Target == profile::TargetKind::Server
        ? waitForDedicatedServerStartup(objects)
        : waitForClientStartupScreen(objects);
    const auto objectCountSnapshot = objects->ObjObjects.NumElements;

    // This is the first point at which CoreCLR, the SDK, mods, configuration UI
    // and the overlay renderer are allowed to initialize.
    if (startupReady && onRuntimeReady) {
        log(runtimeProfile->Target == profile::TargetKind::Server
            ? L"managed startup released for the dedicated server"
            : L"managed startup released at the client login screen");
        beginManagedStartupBarrier();
        onRuntimeReady(self);
        completeManagedStartupBarrier();
    }

    const auto gameBinaryDirectory = std::filesystem::path(modulePath).parent_path();
    if (!metadata::shouldCaptureSdkSnapshot(gameBinaryDirectory, *runtimeProfile)) {
        log(L"sdk snapshot: existing build snapshot reused; live metadata scan skipped "
            L"(set sdkSnapshotRefresh to always for development recapture)");
        log(L"metadata probe completed; no game memory was modified");
        return;
    }
    log(L"runtime target=" + std::wstring(runtimeProfile->TargetName) +
        L" sdk=" + std::wstring(runtimeProfile->SdkAssemblyName));
    log(L"imageBase=" + hexadecimal(reinterpret_cast<std::uintptr_t>(base)));
    log(L"GUObjectArray=" + hexadecimal(reinterpret_cast<std::uintptr_t>(objects)) +
        L" objects=" + std::to_wstring(objectCountSnapshot) +
        L" chunks=" + std::to_wstring(objects->ObjObjects.NumChunks));
    log(L"FName::ToString=" + hexadecimal(reinterpret_cast<std::uintptr_t>(nameFunctionAddress)) +
        L" signatureMatches=" + std::to_wstring(symbolResolution.Symbols.FNameMatchCount));
    log(L"GUObjectArray signature references=" +
        std::to_wstring(symbolResolution.Symbols.GUObjectReferenceCount) +
        L" unanimousTarget=true");
    log(L"GMalloc=" + hexadecimal(reinterpret_cast<std::uintptr_t>(allocator)) +
        L" signatureReferences=" +
        std::to_wstring(symbolResolution.Symbols.GMallocReferenceCount) +
        L" unanimousTarget=true");
    // SDK extraction is automatic for every supported executable profile. The
    // snapshot is build-specific and address-free, so client and server output
    // can coexist and be consumed by the same managed generator.
    metadata::writeSdkSnapshot(
        gameBinaryDirectory, *runtimeProfile);

    struct TargetMetadata {
        std::wstring_view LeafName;
        std::wstring_view ObjectPath;
    };
    const std::array<TargetMetadata, 3> targets{{
        {L"Spy", L"/Script/DeceiveInc.Spy"},
        {L"EOSServerBrowserSubsystem", L"/Script/DeceiveInc.EOSServerBrowserSubsystem"},
        {L"DIOnlinePartyInvite", L"/Script/DeceiveInc.DIOnlinePartyInvite"}
    }};
    std::array<const UObject*, targets.size()> found{};
    std::int32_t validObjects = 0;

    // Freeze the upper bound. Unreal may append objects concurrently while maps
    // load; those newer objects belong to a future snapshot.
    for (std::int32_t index = 0; index < objectCountSnapshot; ++index) {
        const auto* item = itemAt(objects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject))) continue;
        const auto* object = item->Object;
        if (object->InternalIndex != index || !readable(object->ClassPrivate, sizeof(UObject))) continue;
        ++validObjects;
        const auto leafName = nameToString(object->NamePrivate, convert);
        for (std::size_t target = 0; target < targets.size(); ++target) {
            // Most objects are rejected after one name conversion. Constructing
            // every Outer chain would multiply the cost by its path depth.
            if (found[target] || leafName != targets[target].LeafName) continue;
            if (objectPath(object, convert) == targets[target].ObjectPath) found[target] = object;
        }
    }
    log(L"validated UObject entries=" + std::to_wstring(validObjects));

    for (std::size_t target = 0; target < targets.size(); ++target) {
        if (!found[target]) {
            log(L"target missing: " + std::wstring(targets[target].ObjectPath));
            continue;
        }
        log(L"metadata " + std::wstring(targets[target].ObjectPath) + L" object=" + hexadecimal(reinterpret_cast<std::uintptr_t>(found[target])));
        dumpProperties(found[target], convert);
        dumpFunctions(found[target], convert);
    }
    log(L"metadata probe completed; no game memory was modified");
}

} // namespace briefcase
