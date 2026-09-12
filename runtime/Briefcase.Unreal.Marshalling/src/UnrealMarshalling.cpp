#include "UnrealMarshalling.h"

#include "RuntimeProfile.h"

#include <Windows.h>
#include <cstring>
#include <limits>
#include <mutex>
#include <string_view>

namespace briefcase::unreal {

void* RuntimeMalloc{};

bool safeProcessEvent(ProcessEventFn processEvent, UObject* object,
                      const UFunction* function, void* parameters) noexcept {
    __try {
        processEvent(object, function, parameters);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

using PropertyValueFn = void(__fastcall*)(const FProperty*, void*);

bool safePropertyValueCall(
    const FProperty* property, std::size_t vtableIndex, void* value) noexcept {
    if (!property || !value || !readable(property, sizeof(FProperty)))
        return false;
    auto** vtable = reinterpret_cast<void**>(property->VTable);
    if (!readable(vtable, (vtableIndex + 1) * sizeof(void*))) return false;
    const auto function = reinterpret_cast<PropertyValueFn>(vtable[vtableIndex]);
    if (!executable(reinterpret_cast<const void*>(function))) return false;
    __try {
        function(property, value);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool initializePropertyValue(const FProperty* property, void* value) noexcept {
    return safePropertyValueCall(
        property, briefcase::profile::InitializeValueInternalVTableIndex, value);
}

bool destroyPropertyValue(const FProperty* property, void* value) noexcept {
    return safePropertyValueCall(
        property, briefcase::profile::DestroyValueInternalVTableIndex, value);
}

using PropertyHashFn = std::uint32_t(__fastcall*)(const FProperty*, const void*);
using PropertyIdenticalFn = bool(__fastcall*)(
    const FProperty*, const void*, const void*, std::uint32_t);
using PropertyAlignmentFn = std::uint32_t(__fastcall*)(const FProperty*);
using MallocFn = void*(__fastcall*)(void*, std::size_t, std::uint32_t);
using FreeFn = void(__fastcall*)(void*, void*);

bool validUnrealAllocator(void* allocator) {
    if (!allocator || !readable(allocator, sizeof(void*))) return false;
    void** vtable{};
    if (!safeCopy(&vtable, allocator, sizeof(vtable))) return false;
    const auto mallocIndex = briefcase::profile::MallocVTableIndex;
    const auto freeIndex = briefcase::profile::FreeVTableIndex;
    if (!readable(vtable, (freeIndex + 1) * sizeof(void*))) return false;
    return executable(vtable[mallocIndex]) && executable(vtable[freeIndex]);
}

bool safePropertyHash(
    const FProperty* property, const void* value, std::uint32_t& result) noexcept {
    if (!property || !value || !readable(property, sizeof(FProperty))) return false;
    auto** vtable = reinterpret_cast<void**>(property->VTable);
    const auto index = briefcase::profile::GetValueTypeHashVTableIndex;
    if (!readable(vtable, (index + 1) * sizeof(void*))) return false;
    const auto function = reinterpret_cast<PropertyHashFn>(vtable[index]);
    if (!executable(reinterpret_cast<const void*>(function))) return false;
    __try {
        result = function(property, value);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool safePropertyIdentical(
    const FProperty* property, const void* left, const void* right,
    bool& result) noexcept {
    if (!property || !left || !right || !readable(property, sizeof(FProperty)))
        return false;
    auto** vtable = reinterpret_cast<void**>(property->VTable);
    const auto index = briefcase::profile::IdenticalVTableIndex;
    if (!readable(vtable, (index + 1) * sizeof(void*))) return false;
    const auto function = reinterpret_cast<PropertyIdenticalFn>(vtable[index]);
    if (!executable(reinterpret_cast<const void*>(function))) return false;
    __try {
        result = function(property, left, right, 0);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool safePropertyAlignment(
    const FProperty* property, std::uint32_t& result) noexcept {
    if (!property || !readable(property, sizeof(FProperty))) return false;
    auto** vtable = reinterpret_cast<void**>(property->VTable);
    const auto index = briefcase::profile::GetMinAlignmentVTableIndex;
    if (!readable(vtable, (index + 1) * sizeof(void*))) return false;
    const auto function = reinterpret_cast<PropertyAlignmentFn>(vtable[index]);
    if (!executable(reinterpret_cast<const void*>(function))) return false;
    __try {
        result = function(property);
        return result != 0 && result <= 4096 && (result & (result - 1)) == 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool safeUnrealFree(void* allocation) noexcept;

bool safeUnrealMalloc(
    std::size_t size, std::uint32_t alignment, void*& result) noexcept {
    result = nullptr;
    if (!RuntimeMalloc || size == 0 || alignment == 0 || alignment > 4096 ||
        (alignment & (alignment - 1)) != 0 ||
        !readable(RuntimeMalloc, sizeof(void*))) return false;
    void** vtable{};
    if (!safeCopy(&vtable, RuntimeMalloc, sizeof(vtable))) return false;
    const auto index = briefcase::profile::MallocVTableIndex;
    if (!readable(vtable, (index + 1) * sizeof(void*))) return false;
    const auto function = reinterpret_cast<MallocFn>(vtable[index]);
    if (!executable(reinterpret_cast<const void*>(function))) return false;
    void* allocation{};
    __try {
        allocation = function(RuntimeMalloc, size, alignment);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    if (!allocation) return false;
    __try {
        std::memset(allocation, 0, size);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        safeUnrealFree(allocation);
        return false;
    }
    result = allocation;
    return true;
}

bool safeUnrealFree(void* allocation) noexcept {
    if (!allocation) return true;
    if (!RuntimeMalloc || !readable(RuntimeMalloc, sizeof(void*))) return false;
    void** vtable{};
    if (!safeCopy(&vtable, RuntimeMalloc, sizeof(vtable))) return false;
    const auto index = briefcase::profile::FreeVTableIndex;
    if (!readable(vtable, (index + 1) * sizeof(void*))) return false;
    const auto function = reinterpret_cast<FreeFn>(vtable[index]);
    if (!executable(reinterpret_cast<const void*>(function))) return false;
    __try {
        function(RuntimeMalloc, allocation);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}


bool resolveTextConversionRuntime(TextConversionRuntime& result) {
    static std::mutex cacheMutex;
    static const FUObjectArray* cacheOwner{};
    static TextConversionRuntime cache{};
    const std::scoped_lock lock(cacheMutex);
    if (cacheOwner == RuntimeObjects && cache.Library && cache.StringToText &&
        cache.StringToTextReturn && cache.TextToString && cache.TextToStringReturn &&
        readable(cache.Library, sizeof(UObject)) &&
        readable(cache.StringToText, sizeof(UFunction)) &&
        readable(cache.TextToString, sizeof(UFunction))) {
        result = cache;
        return true;
    }

    auto* library = const_cast<UObject*>(findObjectByPath(
        L"/Script/Engine.Default__KismetTextLibrary"));
    const auto* libraryClass = findObjectByPath(L"/Script/Engine.KismetTextLibrary");
    if (!library || !libraryClass || !isClassObject(libraryClass)) return false;
    const auto* stringToText = findFunction(libraryClass, L"Conv_StringToText");
    const auto* textToString = findFunction(libraryClass, L"Conv_TextToString");
    if (!stringToText || stringToText->ParmsSize != 40 ||
        !textToString || textToString->ParmsSize != 40) return false;
    const auto* stringToTextReturn = findFunctionParameter(
        stringToText, L"TextProperty", 16, 24);
    const auto* textToStringReturn = findFunctionParameter(
        textToString, L"StrProperty", 24, 16);
    if (!stringToTextReturn || !textToStringReturn) return false;
    cache = {library, stringToText, stringToTextReturn,
             textToString, textToStringReturn};
    cacheOwner = RuntimeObjects;
    result = cache;
    return true;
}

ProcessEventFn processEventFor(const UObject* object) {
    if (!object || !readable(object, sizeof(UObject))) return nullptr;
    auto** vtable = reinterpret_cast<void**>(object->VTable);
    if (!readable(vtable + briefcase::profile::ProcessEventVTableIndex, sizeof(void*)))
        return nullptr;
    const auto function = reinterpret_cast<ProcessEventFn>(
        vtable[briefcase::profile::ProcessEventVTableIndex]);
    return executable(reinterpret_cast<const void*>(function)) ? function : nullptr;
}


bool constructText(
    const TextConversionRuntime& runtime, const std::uint16_t* characters,
    std::uint32_t characterCount, OwnedTextValue& result) {
    constexpr std::uint32_t MaximumCharacters = 64u * 1024u;
    if (characterCount >= MaximumCharacters || !characters ||
        !readable(characters,
            (static_cast<std::size_t>(characterCount) + 1) * sizeof(std::uint16_t)) ||
        characters[characterCount] != 0)
        return false;
    auto* input = reinterpret_cast<FStringBuffer*>(result.Parameters.data());
    input->Data = reinterpret_cast<wchar_t*>(const_cast<std::uint16_t*>(characters));
    input->Num = static_cast<std::int32_t>(characterCount + 1);
    input->Max = input->Num;
    auto* output = result.Parameters.data() + 16;
    result.Property = runtime.StringToTextReturn;
    if (!initializePropertyValue(result.Property, output)) return false;
    result.Initialized = true;
    const auto processEvent = processEventFor(runtime.Library);
    if (!processEvent ||
        !safeProcessEvent(processEvent, runtime.Library, runtime.StringToText,
                          result.Parameters.data())) {
        destroyPropertyValue(result.Property, output);
        result.Initialized = false;
        return false;
    }
    return true;
}

void destroyText(OwnedTextValue& value) noexcept {
    if (!value.Initialized) return;
    destroyPropertyValue(value.Property, value.Parameters.data() + 16);
    value.Initialized = false;
}

BriefcaseUnrealResult textToUtf16(
    const TextConversionRuntime& runtime, const void* textValue,
    std::uint16_t* destination, std::uint32_t capacityCharacters,
    std::uint32_t* requiredCharacters) {
    constexpr std::uint32_t MaximumCharacters = 64u * 1024u;
    if (!requiredCharacters || !writable(requiredCharacters, sizeof(*requiredCharacters)) ||
        !textValue || !readable(textValue, 24))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    std::array<std::byte, 40> parameters{};
    if (!safeCopy(parameters.data(), textValue, 24)) return BRIEFCASE_UNREAL_UNREADABLE;
    auto* output = parameters.data() + 24;
    if (!initializePropertyValue(runtime.TextToStringReturn, output))
        return BRIEFCASE_UNREAL_UNREADABLE;
    const auto processEvent = processEventFor(runtime.Library);
    if (!processEvent || !safeProcessEvent(
            processEvent, runtime.Library, runtime.TextToString, parameters.data())) {
        destroyPropertyValue(runtime.TextToStringReturn, output);
        return BRIEFCASE_UNREAL_UNREADABLE;
    }

    FStringBuffer converted{};
    auto status = BRIEFCASE_UNREAL_OK;
    if (!safeCopy(&converted, output, sizeof(converted)) || converted.Num < 0 ||
        converted.Max < converted.Num ||
        static_cast<std::uint32_t>(converted.Num) > MaximumCharacters) {
        status = BRIEFCASE_UNREAL_UNREADABLE;
    } else {
        *requiredCharacters = static_cast<std::uint32_t>(converted.Num);
        const auto bytes = static_cast<std::size_t>(converted.Num) * sizeof(std::uint16_t);
        if (converted.Num == 0) {
            status = BRIEFCASE_UNREAL_OK;
        } else if (!converted.Data || !readable(converted.Data, bytes)) {
            status = BRIEFCASE_UNREAL_UNREADABLE;
        } else if (!destination || capacityCharacters < *requiredCharacters) {
            status = BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
        } else if (!writable(destination,
                       static_cast<std::size_t>(capacityCharacters) * sizeof(std::uint16_t))) {
            status = BRIEFCASE_UNREAL_INVALID_ARGUMENT;
        } else if (!safeCopy(destination, converted.Data, bytes)) {
            status = BRIEFCASE_UNREAL_UNREADABLE;
        }
    }
    if (!destroyPropertyValue(runtime.TextToStringReturn, output) &&
        status == BRIEFCASE_UNREAL_OK)
        status = BRIEFCASE_UNREAL_UNREADABLE;
    return status;
}

bool constructString(
    const TextConversionRuntime& runtime, const std::uint16_t* characters,
    std::uint32_t characterCount, OwnedStringValue& result) {
    OwnedTextValue text;
    if (!constructText(runtime, characters, characterCount, text)) return false;
    if (!safeCopy(result.Parameters.data(), text.Parameters.data() + 16, 24)) {
        destroyText(text);
        return false;
    }
    auto* output = result.Parameters.data() + 24;
    result.Property = runtime.TextToStringReturn;
    if (!initializePropertyValue(result.Property, output)) {
        destroyText(text);
        return false;
    }
    result.Initialized = true;
    const auto processEvent = processEventFor(runtime.Library);
    const auto ok = processEvent && safeProcessEvent(
        processEvent, runtime.Library, runtime.TextToString, result.Parameters.data());
    destroyText(text);
    if (!ok) {
        destroyPropertyValue(result.Property, output);
        result.Initialized = false;
    }
    return ok;
}


BriefcasePropertyKind propertyKind(const FProperty* property) {
    if (!property || !readable(property->ClassPrivate, sizeof(FFieldClass))) return BRIEFCASE_PROPERTY_UNKNOWN;
    const auto* fieldClass = reinterpret_cast<const FFieldClass*>(property->ClassPrivate);
    const auto type = nameToString(fieldClass->Name, RuntimeNameConverter);
    if (type == L"Int8Property") return BRIEFCASE_PROPERTY_INT8;
    if (type == L"Int16Property") return BRIEFCASE_PROPERTY_INT16;
    if (type == L"UInt16Property") return BRIEFCASE_PROPERTY_UINT16;
    if (type == L"IntProperty") return BRIEFCASE_PROPERTY_INT32;
    if (type == L"UInt32Property") return BRIEFCASE_PROPERTY_UINT32;
    if (type == L"Int64Property") return BRIEFCASE_PROPERTY_INT64;
    if (type == L"UInt64Property") return BRIEFCASE_PROPERTY_UINT64;
    if (type == L"FloatProperty") return BRIEFCASE_PROPERTY_FLOAT;
    if (type == L"DoubleProperty") return BRIEFCASE_PROPERTY_DOUBLE;
    if (type == L"BoolProperty") return BRIEFCASE_PROPERTY_BOOL;
    if (type == L"ByteProperty") return BRIEFCASE_PROPERTY_BYTE;
    if (type == L"EnumProperty") {
        if (property->ElementSize == 1) return BRIEFCASE_PROPERTY_BYTE;
        if (property->ElementSize == 2) return BRIEFCASE_PROPERTY_UINT16;
        if (property->ElementSize == 4) return BRIEFCASE_PROPERTY_UINT32;
        if (property->ElementSize == 8) return BRIEFCASE_PROPERTY_UINT64;
        return BRIEFCASE_PROPERTY_UNKNOWN;
    }
    if (type == L"ObjectProperty" || type == L"ClassProperty") return BRIEFCASE_PROPERTY_OBJECT;
    if (type == L"WeakObjectProperty") return BRIEFCASE_PROPERTY_OBJECT;
    if (type == L"StructProperty") return BRIEFCASE_PROPERTY_STRUCT;
    if (type == L"StrProperty") return BRIEFCASE_PROPERTY_STRING;
    if (type == L"TextProperty") return BRIEFCASE_PROPERTY_TEXT;
    if (type == L"NameProperty") return BRIEFCASE_PROPERTY_NAME;
    if (type == L"ArrayProperty") return BRIEFCASE_PROPERTY_ARRAY;
    if (type == L"SetProperty") return BRIEFCASE_PROPERTY_SET;
    if (type == L"MapProperty") return BRIEFCASE_PROPERTY_MAP;
    if (type == L"InterfaceProperty") return BRIEFCASE_PROPERTY_INTERFACE;
    if (type == L"LazyObjectProperty") return BRIEFCASE_PROPERTY_LAZY_OBJECT;
    if (type == L"SoftObjectProperty") return BRIEFCASE_PROPERTY_SOFT_OBJECT;
    if (type == L"SoftClassProperty") return BRIEFCASE_PROPERTY_SOFT_CLASS;
    if (type == L"DelegateProperty") return BRIEFCASE_PROPERTY_DELEGATE;
    if (type == L"MulticastDelegateProperty" ||
        type == L"MulticastInlineDelegateProperty")
        return BRIEFCASE_PROPERTY_MULTICAST_DELEGATE;
    if (type == L"FieldPathProperty") return BRIEFCASE_PROPERTY_FIELD_PATH;
    return BRIEFCASE_PROPERTY_UNKNOWN;
}

bool isPreparedPlainProperty(const FProperty* property, unsigned depth) {
    if (!property || depth > 32 || !readable(property, sizeof(FProperty)) ||
        property->ArrayDim != 1 || property->OffsetInternal < 0 ||
        property->ElementSize <= 0) return false;
    const auto kind = propertyKind(property);
    switch (kind) {
    case BRIEFCASE_PROPERTY_INT8:
    case BRIEFCASE_PROPERTY_INT16:
    case BRIEFCASE_PROPERTY_UINT16:
    case BRIEFCASE_PROPERTY_INT32:
    case BRIEFCASE_PROPERTY_UINT32:
    case BRIEFCASE_PROPERTY_INT64:
    case BRIEFCASE_PROPERTY_UINT64:
    case BRIEFCASE_PROPERTY_FLOAT:
    case BRIEFCASE_PROPERTY_DOUBLE:
    case BRIEFCASE_PROPERTY_BOOL:
    case BRIEFCASE_PROPERTY_BYTE:
    case BRIEFCASE_PROPERTY_NAME:
        return true;
    case BRIEFCASE_PROPERTY_STRUCT: {
        if (!readable(property, sizeof(FStructProperty))) return false;
        const auto* structure = reinterpret_cast<const FStructProperty*>(property)->Struct;
        if (!structure || !readable(structure, sizeof(UStruct)) ||
            structure->PropertiesSize != property->ElementSize) return false;
        for (auto* current = structure; current; current = current->SuperStruct) {
            if (!readable(current, sizeof(UStruct))) return false;
            auto* field = current->ChildProperties;
            for (unsigned visited = 0; field && visited < 4096; ++visited) {
                if (!isPreparedPlainProperty(
                        reinterpret_cast<const FProperty*>(field), depth + 1)) return false;
                field = field->Next;
            }
        }
        return true;
    }
    default:
        // Object references use the canonical prepared branch below. Owning
        // strings, text and containers stay on the lifetime-aware dynamic ABI.
        return false;
    }
}

bool isDirectPreparedObject(const FProperty* property) {
    if (!property || !readable(property->ClassPrivate, sizeof(FFieldClass))) return false;
    const auto name = nameToString(
        reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
        RuntimeNameConverter);
    return name == L"ObjectProperty" || name == L"ClassProperty";
}

BriefcaseUnrealResult resolvePropertyAccess(
    BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, const UObject*& object,
    const FProperty*& property, std::byte*& value) {
    object = resolveObject(objectHandle);
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!object || !ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass) || !objectIsA(object, ownerClass))
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    property = findProperty(ownerClass, name);
    if (!property) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (expectedOffset < 0 || expectedElementSize <= 0 || expectedArrayDimension != 1 ||
        property->OffsetInternal != expectedOffset ||
        property->ElementSize != expectedElementSize ||
        property->ArrayDim != expectedArrayDimension || propertyKind(property) != expectedKind)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const auto* actualClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    if (!readable(actualClass, sizeof(UStruct)) || actualClass->PropertiesSize < 0 ||
        static_cast<std::uint64_t>(expectedOffset) +
            static_cast<std::uint64_t>(expectedElementSize) >
            static_cast<std::uint64_t>(actualClass->PropertiesSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    value = reinterpret_cast<std::byte*>(const_cast<UObject*>(object)) + expectedOffset;
    return readable(value, static_cast<std::size_t>(expectedElementSize))
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

} // namespace briefcase::unreal
