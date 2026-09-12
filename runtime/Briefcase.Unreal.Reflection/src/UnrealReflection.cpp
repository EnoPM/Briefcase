#include "UnrealReflection.h"

#include <Windows.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <limits>
#include <sstream>

namespace briefcase::unreal {
namespace {
constexpr std::int32_t ObjectsPerChunk = 64 * 1024;
} // namespace

const FUObjectArray* RuntimeObjects{};
FNameToString RuntimeNameConverter{};

bool readable(const void* address, std::size_t size) {
    if (!address || size == 0) return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(address, &memory, sizeof(memory))) return false;
    if (memory.State != MEM_COMMIT || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    const auto begin = reinterpret_cast<std::uintptr_t>(address);
    const auto regionEnd = reinterpret_cast<std::uintptr_t>(memory.BaseAddress) + memory.RegionSize;
    return begin <= regionEnd && size <= regionEnd - begin;
}

bool readableRange(const void* address, std::size_t size) {
    if (!address || size == 0) return false;
    auto cursor = reinterpret_cast<std::uintptr_t>(address);
    if (size > std::numeric_limits<std::uintptr_t>::max() - cursor) return false;
    const auto end = cursor + size;
    while (cursor < end) {
        MEMORY_BASIC_INFORMATION memory{};
        if (!VirtualQuery(reinterpret_cast<const void*>(cursor), &memory, sizeof(memory)) ||
            memory.State != MEM_COMMIT || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
            return false;
        const auto region = reinterpret_cast<std::uintptr_t>(memory.BaseAddress);
        if (memory.RegionSize > std::numeric_limits<std::uintptr_t>::max() - region)
            return false;
        const auto regionEnd = region + memory.RegionSize;
        if (regionEnd <= cursor) return false;
        cursor = std::min(end, regionEnd);
    }
    return true;
}

bool writable(void* address, std::size_t size) {
    if (!readable(address, size)) return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(address, &memory, sizeof(memory))) return false;
    constexpr DWORD writeMask = PAGE_READWRITE | PAGE_WRITECOPY |
                                PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return (memory.Protect & writeMask) != 0;
}

bool executable(const void* address) {
    if (!address) return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(address, &memory, sizeof(memory)) || memory.State != MEM_COMMIT ||
        (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)))
        return false;
    constexpr DWORD executeMask = PAGE_EXECUTE | PAGE_EXECUTE_READ |
                                  PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
    return (memory.Protect & executeMask) != 0;
}

bool safeCopy(void* destination, const void* source, std::size_t size) noexcept {
    __try {
        std::memcpy(destination, source, size);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::wstring hexadecimal(std::uintptr_t value) {
    std::wostringstream stream;
    stream << L"0x" << std::hex << value;
    return stream.str();
}

bool tryConvertName(const FName* name, FStringBuffer* result,
                    FNameToString convert) noexcept {
    if (!name || !result || !convert) return false;
    __try {
        convert(name, result);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::wstring nameToString(const FName& name, FNameToString convert) {
    // Supplying our own capacity avoids an engine allocation for normal names.
    // The function receives the normal Unreal FString layout: Data/Num/Max.
    std::array<wchar_t, 1024> storage{};
    FStringBuffer result{storage.data(), 0, static_cast<std::int32_t>(storage.size())};
    // Runtime layouts are discovered from native memory. A bad or newly
    // unsupported layout must make metadata incomplete, never crash the game.
    if (!tryConvertName(&name, &result, convert)) return {};
    if (!result.Data || result.Num < 0 || result.Num > result.Max || result.Num > 1023) return L"<invalid-name>";
    if (!readable(result.Data, (static_cast<std::size_t>(result.Num) + 1) * sizeof(wchar_t))) return L"<unreadable-name>";
    const auto length = result.Num > 0 && result.Data[result.Num - 1] == L'\0' ? result.Num - 1 : result.Num;
    return std::wstring(result.Data, result.Data + length);
}

bool isRegisteredObject(const UObject* object);

std::wstring objectPath(const UObject* object, FNameToString convert) {
    std::array<std::wstring, 32> parts{};
    std::size_t count = 0;
    for (auto* current = object; current && count < parts.size(); current = current->OuterPrivate) {
        // Readability alone is insufficient: an FProperty can reside in a
        // committed page too. Only UObject instances present in GUObjectArray
        // are allowed to participate in an object path.
        if (!isRegisteredObject(current)) return {};
        auto part = nameToString(current->NamePrivate, convert);
        if (part.empty() || part.front() == L'<') return {};
        parts[count++] = std::move(part);
    }
    std::wstring path;
    while (count) {
        const auto& part = parts[--count];
        if (!path.empty()) path.push_back(L'.');
        path.append(part);
    }
    return path;
}

FUObjectItem* itemAt(const FChunkedFixedUObjectArray& array, std::int32_t index);

const UObject* findObjectByPath(std::wstring_view requestedPath) {
    if (!RuntimeObjects || !RuntimeNameConverter) return nullptr;
    const auto separator = requestedPath.find_last_of(L'.');
    const auto requestedLeaf = separator == std::wstring_view::npos
        ? requestedPath : requestedPath.substr(separator + 1);
    const auto count = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < count; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject))) continue;
        const auto* object = item->Object;
        if (object->InternalIndex != index ||
            nameToString(object->NamePrivate, RuntimeNameConverter) != requestedLeaf)
            continue;
        if (objectPath(object, RuntimeNameConverter) == requestedPath) return object;
    }
    return nullptr;
}

FUObjectItem* itemAt(const FChunkedFixedUObjectArray& array, std::int32_t index) {
    if (index < 0 || index >= array.NumElements || !array.Objects) return nullptr;
    const auto chunkIndex = index / ObjectsPerChunk;
    const auto withinChunk = index % ObjectsPerChunk;
    if (chunkIndex < 0 || chunkIndex >= array.NumChunks || !readable(array.Objects + chunkIndex, sizeof(FUObjectItem*))) return nullptr;
    auto* chunk = array.Objects[chunkIndex];
    if (!chunk || !readable(chunk + withinChunk, sizeof(FUObjectItem))) return nullptr;
    return chunk + withinChunk;
}

bool isRegisteredObject(const UObject* object) {
    if (!RuntimeObjects || !readable(object, sizeof(UObject)) ||
        object->InternalIndex < 0)
        return false;
    const auto* item = itemAt(RuntimeObjects->ObjObjects, object->InternalIndex);
    return item && item->Object == object;
}

bool saneObjectArray(const FUObjectArray* objects) {
    if (!readable(objects, sizeof(FUObjectArray))) return false;
    const auto& array = objects->ObjObjects;
    if (!array.Objects || array.NumElements < 1000 || array.NumElements > 4'000'000) return false;
    if (array.MaxElements < array.NumElements || array.MaxElements > 8'000'000) return false;
    if (array.NumChunks <= 0 || array.MaxChunks < array.NumChunks || array.MaxChunks > 256) return false;
    return readable(array.Objects, static_cast<std::size_t>(array.NumChunks) * sizeof(FUObjectItem*));
}

bool decodeUtf8(const char* text, std::uint32_t length, std::wstring& result) {
    if (!text || length == 0 || length > 4096 || !readable(text, length)) return false;
    const auto required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text,
                                               static_cast<int>(length), nullptr, 0);
    if (required <= 0) return false;
    result.resize(static_cast<std::size_t>(required));
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text,
                               static_cast<int>(length), result.data(), required) == required;
}

BriefcaseUnrealResult writeUtf8(const std::wstring& text, char* destination,
                          std::uint32_t capacity, std::uint32_t* requiredBytes) {
    if (!requiredBytes || !writable(requiredBytes, sizeof(*requiredBytes))) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto bytes = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(),
                                           static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (bytes < 0 || static_cast<std::uint64_t>(bytes) + 1 > std::numeric_limits<std::uint32_t>::max())
        return BRIEFCASE_UNREAL_UNREADABLE;
    *requiredBytes = static_cast<std::uint32_t>(bytes) + 1;
    if (!destination || capacity < *requiredBytes) return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (!writable(destination, capacity)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (bytes && WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(),
                                     static_cast<int>(text.size()), destination, bytes,
                                     nullptr, nullptr) != bytes)
        return BRIEFCASE_UNREAL_UNREADABLE;
    destination[bytes] = '\0';
    return BRIEFCASE_UNREAL_OK;
}

const UObject* resolveObject(BriefcaseObjectHandle handle) {
    if (!RuntimeObjects || !RuntimeNameConverter || !saneObjectArray(RuntimeObjects)) return nullptr;
    if (handle.Index > static_cast<std::uint32_t>(std::numeric_limits<std::int32_t>::max())) return nullptr;
    const auto* item = itemAt(RuntimeObjects->ObjObjects, static_cast<std::int32_t>(handle.Index));
    if (!item || static_cast<std::uint32_t>(item->SerialNumber) != handle.SerialNumber ||
        !readable(item->Object, sizeof(UObject)) ||
        item->Object->InternalIndex != static_cast<std::int32_t>(handle.Index))
        return nullptr;
    return item->Object;
}

bool makeHandle(const UObject* object, BriefcaseObjectHandle& result) {
    if (!object) {
        result = {std::numeric_limits<std::uint32_t>::max(), 0};
        return true;
    }
    if (!RuntimeObjects || !readable(object, sizeof(UObject)) || object->InternalIndex < 0) return false;
    const auto* item = itemAt(RuntimeObjects->ObjObjects, object->InternalIndex);
    if (!item || item->Object != object) return false;
    result = {static_cast<std::uint32_t>(object->InternalIndex),
              static_cast<std::uint32_t>(item->SerialNumber)};
    return true;
}

bool isClassObject(const UObject* object) {
    if (!object || !readable(object, sizeof(UStruct)) ||
        !readable(object->ClassPrivate, sizeof(UStruct))) return false;

    // Native UClass instances have /Script/CoreUObject.Class as their direct
    // metaclass. Cooked Blueprint classes use BlueprintGeneratedClass (or a
    // derived metaclass), whose UStruct inheritance chain eventually reaches
    // Class. Accepting that chain lets the public reflection and patch APIs use
    // Blueprint classes with the same validated handles as native classes.
    auto* metaClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    for (unsigned depth = 0; metaClass && depth < 64; ++depth) {
        if (!readable(metaClass, sizeof(UStruct))) return false;
        if (nameToString(metaClass->NamePrivate, RuntimeNameConverter) == L"Class")
            return true;
        metaClass = metaClass->SuperStruct;
    }
    return false;
}

bool objectIsA(const UObject* object, const UObject* requestedClass) {
    if (!object || !requestedClass || !readable(object->ClassPrivate, sizeof(UStruct))) return false;
    auto* current = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    for (unsigned depth = 0; current && depth < 256; ++depth) {
        if (!readable(current, sizeof(UStruct))) return false;
        if (current == requestedClass) return true;
        current = current->SuperStruct;
    }
    return false;
}

const FProperty* findProperty(const UObject* ownerClass, const std::wstring& propertyName) {
    if (!isClassObject(ownerClass)) return nullptr;
    auto* structure = reinterpret_cast<const UStruct*>(ownerClass);
    for (unsigned inheritanceDepth = 0; structure && inheritanceDepth < 256; ++inheritanceDepth) {
        if (!readable(structure, sizeof(UStruct))) return nullptr;
        auto* field = structure->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return nullptr;
            if (nameToString(field->NamePrivate, RuntimeNameConverter) == propertyName)
                return reinterpret_cast<const FProperty*>(field);
            field = field->Next;
        }
        structure = structure->SuperStruct;
    }
    return nullptr;
}

const UFunction* findFunction(const UObject* ownerClass, const std::wstring& functionName) {
    if (!isClassObject(ownerClass)) return nullptr;
    auto* structure = reinterpret_cast<const UStruct*>(ownerClass);
    for (unsigned inheritanceDepth = 0; structure && inheritanceDepth < 256; ++inheritanceDepth) {
        if (!readable(structure, sizeof(UStruct))) return nullptr;
        auto* child = structure->Children;
        for (unsigned visited = 0; child && visited < 4096; ++visited) {
            if (!readable(child, sizeof(UField)) || !readable(child->ClassPrivate, sizeof(UObject)))
                return nullptr;
            if (nameToString(child->ClassPrivate->NamePrivate, RuntimeNameConverter) == L"Function" &&
                nameToString(child->NamePrivate, RuntimeNameConverter) == functionName &&
                readable(child, sizeof(UFunction)))
                return reinterpret_cast<const UFunction*>(child);
            child = child->Next;
        }
        structure = structure->SuperStruct;
    }
    return nullptr;
}

const FProperty* findFunctionParameter(
    const UFunction* function, std::wstring_view typeName,
    std::int32_t offset, std::int32_t size) {
    if (!function) return nullptr;
    auto* field = function->ChildProperties;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty)) ||
            !readable(field->ClassPrivate, sizeof(FFieldClass))) return nullptr;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if (property->OffsetInternal == offset && property->ElementSize == size &&
            property->ArrayDim == 1 &&
            nameToString(reinterpret_cast<const FFieldClass*>(
                property->ClassPrivate)->Name, RuntimeNameConverter) == typeName)
            return property;
        field = field->Next;
    }
    return nullptr;
}

std::wstring propertyTypeName(const FProperty* property) {
    if (!property || !readable(property->ClassPrivate, sizeof(FFieldClass)))
        return L"UnknownProperty";
    return nameToString(
        reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
        RuntimeNameConverter);
}

} // namespace briefcase::unreal
