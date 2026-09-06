#include "UnrealProbe.h"

#include "Log.h"
#include "RuntimeProfile.h"
#include "UnrealLayout.h"

#include <Briefcase/BriefcaseModApi.h>
#include <MinHook.h>
#include <hde/hde64.h>
#include <Windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cwchar>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <tuple>
#include <type_traits>
#include <utility>
#include <vector>

namespace {
using namespace briefcase::unreal;
constexpr std::int32_t ObjectsPerChunk = 64 * 1024;
using FNameToString = void(__fastcall*)(const FName*, FStringBuffer*);
const FUObjectArray* RuntimeObjects{};
FNameToString RuntimeNameConverter{};
const briefcase::profile::RuntimeProfile* ActiveProfile{};
std::byte* RuntimeImageBase{};
std::size_t RuntimeImageSize{};

bool readable(const void* address, std::size_t size) {
    if (!address || size == 0) return false;
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(address, &memory, sizeof(memory))) return false;
    if (memory.State != MEM_COMMIT || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    const auto begin = reinterpret_cast<std::uintptr_t>(address);
    const auto regionEnd = reinterpret_cast<std::uintptr_t>(memory.BaseAddress) + memory.RegionSize;
    return begin <= regionEnd && size <= regionEnd - begin;
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

std::wstring nameToString(const FName& name, FNameToString convert) {
    // Supplying our own capacity avoids an engine allocation for normal names.
    // The function receives the normal Unreal FString layout: Data/Num/Max.
    std::array<wchar_t, 1024> storage{};
    FStringBuffer result{storage.data(), 0, static_cast<std::int32_t>(storage.size())};
    convert(&name, &result);
    if (!result.Data || result.Num < 0 || result.Num > result.Max || result.Num > 1023) return L"<invalid-name>";
    if (!readable(result.Data, (static_cast<std::size_t>(result.Num) + 1) * sizeof(wchar_t))) return L"<unreadable-name>";
    const auto length = result.Num > 0 && result.Data[result.Num - 1] == L'\0' ? result.Num - 1 : result.Num;
    return std::wstring(result.Data, result.Data + length);
}

std::wstring objectPath(const UObject* object, FNameToString convert) {
    std::array<std::wstring, 32> parts{};
    std::size_t count = 0;
    for (auto* current = object; current && count < parts.size(); current = current->OuterPrivate) {
        if (!readable(current, sizeof(UObject))) return L"<invalid-object-path>";
        parts[count++] = nameToString(current->NamePrivate, convert);
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

using ProcessEventFn = void(__fastcall*)(UObject*, const UFunction*, void*);

struct PatchRegistration {
    std::uint64_t Id{};
    const UObject* TargetClass{};
    FName FunctionName{};
    std::uint16_t ParameterSize{};
    BriefcasePatchPhase Phase{};
    BriefcasePatchCallbackFn Callback{};
    void* UserContext{};
    bool Active{true};
    std::atomic_uint64_t InvocationCount{0};
    std::recursive_mutex InvocationMutex;
};

std::mutex PatchStateMutex;
std::vector<std::shared_ptr<PatchRegistration>> PatchRegistrations;
std::uint64_t NextPatchRegistrationId{1};
std::atomic<ProcessEventFn> OriginalProcessEvent{};
void* ProcessEventTarget{};
std::atomic_bool FirstProcessEventObserved{false};

void __fastcall processEventHook(UObject* object, const UFunction* function, void* parameters);

ProcessEventFn originalProcessEvent(void** vtable) {
    (void)vtable;
    return OriginalProcessEvent.load(std::memory_order_acquire);
}

struct MatchingRegistrations {
    std::vector<std::shared_ptr<PatchRegistration>> Prefixes;
    std::vector<std::shared_ptr<PatchRegistration>> Postfixes;
};

MatchingRegistrations registrationsFor(
    const UObject* object, const UFunction* function) {
    MatchingRegistrations result;
    if (!function || !readable(function, sizeof(UFunction))) return result;
    const std::scoped_lock lock(PatchStateMutex);
    for (const auto& registration : PatchRegistrations) {
        if (!registration->Active ||
            registration->FunctionName.ComparisonIndex != function->NamePrivate.ComparisonIndex ||
            registration->FunctionName.Number != function->NamePrivate.Number ||
            registration->ParameterSize != function->ParmsSize ||
            !objectIsA(object, registration->TargetClass)) continue;
        (registration->Phase == BRIEFCASE_PATCH_PREFIX
            ? result.Prefixes : result.Postfixes).push_back(registration);
    }
    return result;
}

bool safePatchCallback(
    const std::shared_ptr<PatchRegistration>& registration,
    BriefcasePatchCall* call,
    BriefcaseBool* runOriginal) noexcept {
    __try {
        registration->Callback(registration->UserContext, call, runOriginal);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void invokePatchCallbacks(
    const std::vector<std::shared_ptr<PatchRegistration>>& registrations,
    BriefcasePatchCall& call,
    BriefcaseBool& runOriginal) {
    for (const auto& registration : registrations) {
        const std::scoped_lock invocationLock(registration->InvocationMutex);
        if (!registration->Active) continue;
        if (registration->InvocationCount.fetch_add(1, std::memory_order_relaxed) == 0)
            briefcase::log(L"patching host: first callback for " +
                     objectPath(registration->TargetClass, RuntimeNameConverter) + L"." +
                     nameToString(registration->FunctionName, RuntimeNameConverter));
        if (!safePatchCallback(registration, &call, &runOriginal))
            briefcase::log(L"patching host: managed callback raised a native exception");
    }
}

void __fastcall processEventHook(
    UObject* object, const UFunction* function, void* parameters) {
    auto** vtable = object ? reinterpret_cast<void**>(object->VTable) : nullptr;
    const auto original = originalProcessEvent(vtable);
    if (!original) return;

    if (!FirstProcessEventObserved.exchange(true, std::memory_order_relaxed)) {
        const auto functionName = function && readable(function, sizeof(UFunction))
            ? nameToString(function->NamePrivate, RuntimeNameConverter)
            : L"<unreadable>";
        briefcase::log(L"patching host: global ProcessEvent detour received its first event: " +
                 functionName);
    }

    const auto registrations = registrationsFor(object, function);
    if (registrations.Prefixes.empty() && registrations.Postfixes.empty()) {
        original(object, function, parameters);
        return;
    }

    BriefcaseObjectHandle instance{};
    if (!makeHandle(object, instance)) {
        original(object, function, parameters);
        return;
    }
    const auto parameterSize = function && readable(function, sizeof(UFunction))
        ? static_cast<std::uint32_t>(function->ParmsSize) : 0u;
    BriefcaseBool runOriginal = 1;
    BriefcasePatchCall call{
        sizeof(BriefcasePatchCall), BRIEFCASE_PATCH_PREFIX, instance, 0,
        parameters, parameterSize, 0, {}};
    invokePatchCallbacks(registrations.Prefixes, call, runOriginal);

    if (runOriginal != 0) {
        original(object, function, parameters);
        call.OriginalRan = 1;
    }
    call.Phase = BRIEFCASE_PATCH_POSTFIX;
    invokePatchCallbacks(registrations.Postfixes, call, runOriginal);
}

// A reflected parameter buffer and the native Windows x64 call use different
// layouts. The bridge below explicitly translates between them. RCX contains
// `this`; the next three ordinal argument slots use RDX/R8/R9 for general
// values or XMM1/XMM2/XMM3 for float/double. Larger structs are passed by an
// address and copied into a private reflected buffer before managed code runs.
// A structure return larger than eight bytes uses the MSVC member-function ABI:
// `this` remains in RCX and a caller-owned result buffer is passed in RDX. The
// bridge supports that form for parameterless functions. Stack arguments remain
// rejected.
enum class NativeRegisterKind { Gp, Float32, Float64 };
struct NativeValueLayout {
    std::int32_t Offset{};
    std::int32_t Size{};
    NativeRegisterKind Register{};
    bool Indirect{};
    bool Object{};
};

constexpr std::size_t MaxNativeTargets = 16;
struct NativePatchRegistration;
struct NativeTarget {
    void* Address{};
    void* Original{};
    std::size_t Slot{};
    std::uint32_t ParameterSize{};
    std::vector<NativeValueLayout> Inputs;
    NativeValueLayout Return{};
    bool HasReturn{};
};

struct NativePatchRegistration {
    std::uint64_t Id{};
    NativeTarget* Target{};
    const UObject* TargetClass{};
    FName FunctionName{};
    BriefcasePatchPhase Phase{};
    BriefcasePatchCallbackFn Callback{};
    void* UserContext{};
    bool Active{true};
    std::atomic_uint64_t InvocationCount{0};
    std::recursive_mutex InvocationMutex;
};

std::mutex NativePatchStateMutex;
std::vector<std::unique_ptr<NativeTarget>> NativeTargets;
std::vector<std::shared_ptr<NativePatchRegistration>> NativePatchRegistrations;
std::array<std::atomic<NativeTarget*>, MaxNativeTargets> NativeSlots{};
std::uint64_t NextNativePatchRegistrationId{1};

bool insideRuntimeImage(const void* address) {
    if (!RuntimeImageBase || !RuntimeImageSize || !address) return false;
    const auto value = reinterpret_cast<std::uintptr_t>(address);
    const auto begin = reinterpret_cast<std::uintptr_t>(RuntimeImageBase);
    return value >= begin && value - begin < RuntimeImageSize;
}

void* relativeBranchTarget(const std::byte* instruction, std::size_t length) {
    if (length < 5 || !readable(instruction, length)) return nullptr;
    std::int32_t displacement{};
    std::memcpy(&displacement, instruction + 1, sizeof(displacement));
    return const_cast<std::byte*>(instruction) + length + displacement;
}

void* resolveVirtualImplementation(
    const UObject* targetClass, std::size_t vtableByteOffset) {
    if (!RuntimeObjects || !targetClass || vtableByteOffset % sizeof(void*) != 0)
        return nullptr;
    const auto slot = vtableByteOffset / sizeof(void*);
    // Refuse implausibly large offsets before using metadata-derived memory as
    // a vtable index. The current GetAimedPosition slot is 0xA10 / 8.
    if (slot >= 1024) return nullptr;

    std::vector<void*> exactCandidates;
    std::vector<void*> derivedCandidates;
    const auto addCandidate = [](std::vector<void*>& values, void* candidate) {
        if (insideRuntimeImage(candidate) && executable(candidate) &&
            std::find(values.begin(), values.end(), candidate) == values.end())
            values.push_back(candidate);
    };

    const auto objectCount = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < objectCount; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        auto* object = item ? item->Object : nullptr;
        if (!object || !readable(object, sizeof(UObject)) ||
            !objectIsA(object, targetClass)) continue;
        auto** vtable = reinterpret_cast<void**>(object->VTable);
        if (!readable(vtable + slot, sizeof(void*))) continue;
        auto* candidate = vtable[slot];
        if (object->ClassPrivate == targetClass)
            addCandidate(exactCandidates, candidate);
        else
            addCandidate(derivedCandidates, candidate);
    }

    // Prefer the implementation from an object whose concrete UClass is the
    // requested generated class. If only subclasses are instantiated, accept
    // their slot only when every observed subclass resolves to one address.
    if (exactCandidates.size() == 1) return exactCandidates.front();
    if (exactCandidates.empty() && derivedCandidates.size() == 1)
        return derivedCandidates.front();
    return nullptr;
}

void* resolveNativeImplementation(
    const UFunction* function, const UObject* targetClass) {
    if (!function || !function->Func || !insideRuntimeImage(function->Func) ||
        !executable(function->Func)) return nullptr;
    auto* code = static_cast<const std::byte*>(function->Func);
    std::vector<void*> calls;
    std::vector<std::size_t> virtualSlots;
    bool vtableLoadedIntoRax = false;
    for (std::size_t offset = 0; offset < 256;) {
        if (!readable(code + offset, 16)) return nullptr;
        hde64s decoded{};
        const auto length = static_cast<std::size_t>(hde64_disasm(code + offset, &decoded));
        if (!length || (decoded.flags & F_ERROR)) return nullptr;

        // Generated wrappers for virtual BlueprintNativeEvent implementations
        // use `mov rax, [rcx]` followed by `call [rax+slot]`. That call has no
        // relative E8 target, so recover its vtable byte offset and resolve it
        // against live objects of the requested class.
        if (decoded.opcode == 0x8B && decoded.rex_w != 0 &&
            decoded.modrm_mod == 0 && decoded.modrm_reg == 0 &&
            decoded.modrm_rm == 1)
            vtableLoadedIntoRax = true;
        if (vtableLoadedIntoRax && decoded.opcode == 0xFF &&
            decoded.modrm_reg == 2 && decoded.modrm_mod == 2 &&
            decoded.modrm_rm == 0 && (decoded.flags & F_DISP32) != 0) {
            const auto byteOffset = static_cast<std::size_t>(decoded.disp.disp32);
            if (byteOffset % sizeof(void*) == 0 &&
                std::find(virtualSlots.begin(), virtualSlots.end(), byteOffset) ==
                    virtualSlots.end())
                virtualSlots.push_back(byteOffset);
        }

        if (decoded.opcode == 0xE9) {
            auto* target = relativeBranchTarget(code + offset, length);
            const auto sourceValue = reinterpret_cast<std::uintptr_t>(code);
            const auto targetValue = reinterpret_cast<std::uintptr_t>(target);
            const auto distance = sourceValue > targetValue
                ? sourceValue - targetValue : targetValue - sourceValue;
            // A nearby jump is control flow inside the exec wrapper and still
            // has the FFrame ABI. Only a unique far tail jump can be the native
            // member implementation with RCX = this.
            return distance > 512 && insideRuntimeImage(target) && executable(target)
                ? target : nullptr;
        }
        if (decoded.opcode == 0xE8) {
            auto* target = relativeBranchTarget(code + offset, length);
            if (insideRuntimeImage(target) && executable(target)) calls.push_back(target);
        }
        if (decoded.opcode == 0xC3 || decoded.opcode == 0xC2) break;
        offset += length;
    }
    if (virtualSlots.size() == 1)
        if (auto* implementation = resolveVirtualImplementation(
                targetClass, virtualSlots.front()))
            return implementation;

    if (calls.size() == 1) return calls.front();

    // Parameterized exec wrappers first call generic FFrame readers and then
    // call the native member implementation. For the plain scalar/blittable
    // signatures accepted by buildNativeLayout there is no destructor cleanup,
    // so that implementation is the final direct call before the wrapper's
    // first return. UE keeps it in the same generated module, while the frame
    // helpers are much farther away. The distance bound prevents a generic
    // engine helper from being accepted when that expected layout is absent.
    if (!calls.empty()) {
        auto* candidate = calls.back();
        const auto sourceValue = reinterpret_cast<std::uintptr_t>(code);
        const auto candidateValue = reinterpret_cast<std::uintptr_t>(candidate);
        const auto distance = sourceValue > candidateValue
            ? sourceValue - candidateValue : candidateValue - sourceValue;
        constexpr std::uintptr_t MaxGeneratedModuleDistance = 4u * 1024u * 1024u;
        if (distance <= MaxGeneratedModuleDistance)
            return candidate;
    }
    return nullptr;
}

bool safeNativePatchCallback(
    const std::shared_ptr<NativePatchRegistration>& registration,
    BriefcasePatchCall* call,
    BriefcaseBool* runOriginal) noexcept {
    __try {
        registration->Callback(registration->UserContext, call, runOriginal);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void invokeNativeCallbacks(
    const std::vector<std::shared_ptr<NativePatchRegistration>>& registrations,
    BriefcasePatchCall& call,
    BriefcaseBool& runOriginal) {
    for (const auto& registration : registrations) {
        const std::scoped_lock invocationLock(registration->InvocationMutex);
        if (!registration->Active) continue;
        if (registration->InvocationCount.fetch_add(1, std::memory_order_relaxed) == 0)
            briefcase::log(L"native patching host: first callback for " +
                     objectPath(registration->TargetClass, RuntimeNameConverter) + L"." +
                     nameToString(registration->FunctionName, RuntimeNameConverter));
        if (!safeNativePatchCallback(registration, &call, &runOriginal))
            briefcase::log(L"native patching host: managed callback raised a native exception");
    }
}

using NativeGp = std::uint64_t;

template<typename T>
NativeGp argumentBits(T value) {
    NativeGp bits{};
    static_assert(sizeof(T) <= sizeof(bits));
    std::memcpy(&bits, &value, sizeof(T));
    return bits;
}

bool writeNativeArgument(
    std::vector<std::byte>& buffer, const NativeValueLayout& layout, NativeGp bits) {
    if (layout.Offset < 0 || layout.Size <= 0 ||
        static_cast<std::size_t>(layout.Offset) + static_cast<std::size_t>(layout.Size) > buffer.size())
        return false;
    auto* destination = buffer.data() + layout.Offset;
    if (layout.Object) {
        BriefcaseObjectHandle handle{};
        if (!makeHandle(reinterpret_cast<const UObject*>(
                static_cast<std::uintptr_t>(bits)), handle))
            return false;
        static_assert(sizeof(handle) == sizeof(bits));
        std::memcpy(destination, &handle, sizeof(handle));
        return true;
    }
    if (layout.Indirect) {
        const auto* source = reinterpret_cast<const void*>(static_cast<std::uintptr_t>(bits));
        return readable(source, static_cast<std::size_t>(layout.Size)) &&
               safeCopy(destination, source, static_cast<std::size_t>(layout.Size));
    }
    if (layout.Size > static_cast<std::int32_t>(sizeof(bits))) return false;
    std::memcpy(destination, &bits, static_cast<std::size_t>(layout.Size));
    return true;
}

template<typename T>
T readNativeArgument(
    const NativeTarget& target, std::size_t index,
    const std::vector<std::byte>& buffer, T original) {
    if (index >= target.Inputs.size()) return original;
    const auto& layout = target.Inputs[index];
    if constexpr (std::is_same_v<T, NativeGp>) {
        if (layout.Object) {
            BriefcaseObjectHandle handle{};
            static_assert(sizeof(handle) == sizeof(T));
            std::memcpy(&handle, buffer.data() + layout.Offset, sizeof(handle));
            return reinterpret_cast<NativeGp>(
                const_cast<UObject*>(resolveObject(handle)));
        }
        if (layout.Indirect)
            return reinterpret_cast<NativeGp>(buffer.data() + layout.Offset);
    }
    T value{};
    if (layout.Size > static_cast<std::int32_t>(sizeof(T))) return original;
    std::memcpy(&value, buffer.data() + layout.Offset, static_cast<std::size_t>(layout.Size));
    return value;
}

template<typename T>
void writeNativeReturn(
    const NativeTarget& target, std::vector<std::byte>& buffer, T value) {
    if (!target.HasReturn) return;
    const auto& layout = target.Return;
    if constexpr (std::is_same_v<T, NativeGp>) {
        if (layout.Object) {
            BriefcaseObjectHandle handle{
                std::numeric_limits<std::uint32_t>::max(), 0};
            makeHandle(reinterpret_cast<const UObject*>(
                static_cast<std::uintptr_t>(value)), handle);
            std::memcpy(buffer.data() + layout.Offset, &handle, sizeof(handle));
            return;
        }
    }
    const auto count = std::min<std::size_t>(sizeof(T), static_cast<std::size_t>(layout.Size));
    std::memcpy(buffer.data() + layout.Offset, &value, count);
}

template<typename T>
T readNativeReturn(const NativeTarget& target, const std::vector<std::byte>& buffer) {
    T value{};
    if (!target.HasReturn) return value;
    if constexpr (std::is_same_v<T, NativeGp>) {
        if (target.Return.Object) {
            BriefcaseObjectHandle handle{};
            std::memcpy(&handle, buffer.data() + target.Return.Offset, sizeof(handle));
            return reinterpret_cast<NativeGp>(
                const_cast<UObject*>(resolveObject(handle)));
        }
    }
    const auto count = std::min<std::size_t>(sizeof(T), static_cast<std::size_t>(target.Return.Size));
    std::memcpy(&value, buffer.data() + target.Return.Offset, count);
    return value;
}

template<typename Return, typename Argument1, typename Argument2, typename Argument3>
Return dispatchNative(
    std::size_t slot, UObject* object,
    Argument1 argument1, Argument2 argument2, Argument3 argument3) {
    auto* target = NativeSlots[slot].load(std::memory_order_acquire);
    using Function = Return(__fastcall*)(UObject*, Argument1, Argument2, Argument3);
    if (!target || !target->Original) {
        if constexpr (!std::is_void_v<Return>) return Return{};
        else return;
    }
    const auto original = reinterpret_cast<Function>(target->Original);

    std::vector<std::shared_ptr<NativePatchRegistration>> prefixes;
    std::vector<std::shared_ptr<NativePatchRegistration>> postfixes;
    {
        const std::scoped_lock lock(NativePatchStateMutex);
        for (const auto& registration : NativePatchRegistrations) {
            if (!registration->Active || registration->Target != target ||
                !objectIsA(object, registration->TargetClass)) continue;
            (registration->Phase == BRIEFCASE_PATCH_PREFIX ? prefixes : postfixes)
                .push_back(registration);
        }
    }

    BriefcaseObjectHandle instance{};
    if (!makeHandle(object, instance)) {
        if constexpr (std::is_void_v<Return>) {
            original(object, argument1, argument2, argument3);
            return;
        } else {
            return original(object, argument1, argument2, argument3);
        }
    }
    std::vector<std::byte> parameters(target->ParameterSize);
    if constexpr (!std::is_void_v<Return>) {
        // A skipped native UObject-returning prefix starts with Briefcase's
        // canonical null handle instead of an all-zero, potentially valid
        // object-array index.
        if (target->HasReturn && target->Return.Object)
            writeNativeReturn(*target, parameters, NativeGp{});
    }
    const std::array<NativeGp, 3> incoming{
        argumentBits(argument1), argumentBits(argument2), argumentBits(argument3)};
    for (std::size_t index = 0; index < target->Inputs.size(); ++index) {
        if (!writeNativeArgument(parameters, target->Inputs[index], incoming[index])) {
            if constexpr (std::is_void_v<Return>) {
                original(object, argument1, argument2, argument3);
                return;
            } else {
                return original(object, argument1, argument2, argument3);
            }
        }
    }
    BriefcaseBool runOriginal = 1;
    BriefcasePatchCall call{
        sizeof(BriefcasePatchCall), BRIEFCASE_PATCH_PREFIX, instance, 0,
        parameters.empty() ? nullptr : parameters.data(), target->ParameterSize, 0, {}};
    invokeNativeCallbacks(prefixes, call, runOriginal);
    if (runOriginal != 0) {
        const auto first = readNativeArgument(*target, 0, parameters, argument1);
        const auto second = readNativeArgument(*target, 1, parameters, argument2);
        const auto third = readNativeArgument(*target, 2, parameters, argument3);
        if constexpr (std::is_void_v<Return>) {
            original(object, first, second, third);
        } else {
            const auto result = original(object, first, second, third);
            writeNativeReturn(*target, parameters, result);
        }
        call.OriginalRan = 1;
    }
    call.Phase = BRIEFCASE_PATCH_POSTFIX;
    invokeNativeCallbacks(postfixes, call, runOriginal);
    if constexpr (!std::is_void_v<Return>)
        return readNativeReturn<Return>(*target, parameters);
}

template<std::size_t Slot, typename Return, typename Argument1, typename Argument2, typename Argument3>
Return __fastcall nativeHook(
    UObject* object, Argument1 argument1, Argument2 argument2, Argument3 argument3) {
    return dispatchNative<Return>(Slot, object, argument1, argument2, argument3);
}

// MSVC places the hidden buffer for a large C++ member-function return after
// `this`, hence RCX = object and RDX = returnStorage. The original function
// writes into that storage and returns the same pointer in RAX. Mirroring that
// ABI lets managed postfixes inspect and replace a reflected 12-byte FVector.
void* dispatchNativeIndirectReturn(
    std::size_t slot, UObject* object, void* returnStorage) {
    auto* target = NativeSlots[slot].load(std::memory_order_acquire);
    using Function = void*(__fastcall*)(UObject*, void*);
    if (!target || !target->Original || !target->HasReturn ||
        !target->Return.Indirect || !target->Inputs.empty())
        return returnStorage;
    const auto original = reinterpret_cast<Function>(target->Original);
    if (!writable(returnStorage, static_cast<std::size_t>(target->Return.Size)))
        return original(object, returnStorage);

    std::vector<std::shared_ptr<NativePatchRegistration>> prefixes;
    std::vector<std::shared_ptr<NativePatchRegistration>> postfixes;
    {
        const std::scoped_lock lock(NativePatchStateMutex);
        for (const auto& registration : NativePatchRegistrations) {
            if (!registration->Active || registration->Target != target ||
                !objectIsA(object, registration->TargetClass)) continue;
            (registration->Phase == BRIEFCASE_PATCH_PREFIX ? prefixes : postfixes)
                .push_back(registration);
        }
    }

    BriefcaseObjectHandle instance{};
    if (!makeHandle(object, instance)) return original(object, returnStorage);

    std::vector<std::byte> parameters(target->ParameterSize);
    BriefcaseBool runOriginal = 1;
    BriefcasePatchCall call{
        sizeof(BriefcasePatchCall), BRIEFCASE_PATCH_PREFIX, instance, 0,
        parameters.empty() ? nullptr : parameters.data(), target->ParameterSize, 0, {}};
    invokeNativeCallbacks(prefixes, call, runOriginal);

    void* returned = returnStorage;
    if (runOriginal != 0) {
        returned = original(object, returnStorage);
        if (!safeCopy(
                parameters.data() + target->Return.Offset, returnStorage,
                static_cast<std::size_t>(target->Return.Size)))
            return returned;
        call.OriginalRan = 1;
    }
    call.Phase = BRIEFCASE_PATCH_POSTFIX;
    invokeNativeCallbacks(postfixes, call, runOriginal);
    safeCopy(
        returnStorage, parameters.data() + target->Return.Offset,
        static_cast<std::size_t>(target->Return.Size));
    return returned ? returned : returnStorage;
}

template<std::size_t Slot>
void* __fastcall nativeIndirectReturnHook(UObject* object, void* returnStorage) {
    return dispatchNativeIndirectReturn(Slot, object, returnStorage);
}

template<std::size_t... Slots>
constexpr auto makeNativeIndirectReturnHooks(std::index_sequence<Slots...>) {
    using Function = void*(__fastcall*)(UObject*, void*);
    return std::array<Function, sizeof...(Slots)>{
        &nativeIndirectReturnHook<Slots>...};
}

void* nativeIndirectReturnHookForSlot(std::size_t slot) {
    static constexpr auto hooks = makeNativeIndirectReturnHooks(
        std::make_index_sequence<MaxNativeTargets>{});
    return slot < hooks.size() ? reinterpret_cast<void*>(hooks[slot]) : nullptr;
}

template<typename Return, typename Argument1, typename Argument2, typename Argument3,
         std::size_t... Slots>
constexpr auto makeNativeHooks(std::index_sequence<Slots...>) {
    using Function = Return(__fastcall*)(UObject*, Argument1, Argument2, Argument3);
    return std::array<Function, sizeof...(Slots)>{
        &nativeHook<Slots, Return, Argument1, Argument2, Argument3>...};
}

template<typename Return, typename Argument1, typename Argument2, typename Argument3>
void* nativeHookForSlot(std::size_t slot) {
    static constexpr auto hooks = makeNativeHooks<Return, Argument1, Argument2, Argument3>(
        std::make_index_sequence<MaxNativeTargets>{});
    return slot < hooks.size() ? reinterpret_cast<void*>(hooks[slot]) : nullptr;
}

template<typename Return, typename Argument1, typename Argument2>
void* selectThirdNativeArgument(NativeRegisterKind kind, std::size_t slot) {
    switch (kind) {
    case NativeRegisterKind::Float32:
        return nativeHookForSlot<Return, Argument1, Argument2, float>(slot);
    case NativeRegisterKind::Float64:
        return nativeHookForSlot<Return, Argument1, Argument2, double>(slot);
    default:
        return nativeHookForSlot<Return, Argument1, Argument2, NativeGp>(slot);
    }
}

template<typename Return, typename Argument1>
void* selectSecondNativeArgument(
    NativeRegisterKind second, NativeRegisterKind third, std::size_t slot) {
    switch (second) {
    case NativeRegisterKind::Float32:
        return selectThirdNativeArgument<Return, Argument1, float>(third, slot);
    case NativeRegisterKind::Float64:
        return selectThirdNativeArgument<Return, Argument1, double>(third, slot);
    default:
        return selectThirdNativeArgument<Return, Argument1, NativeGp>(third, slot);
    }
}

template<typename Return>
void* selectFirstNativeArgument(
    NativeRegisterKind first, NativeRegisterKind second,
    NativeRegisterKind third, std::size_t slot) {
    switch (first) {
    case NativeRegisterKind::Float32:
        return selectSecondNativeArgument<Return, float>(second, third, slot);
    case NativeRegisterKind::Float64:
        return selectSecondNativeArgument<Return, double>(second, third, slot);
    default:
        return selectSecondNativeArgument<Return, NativeGp>(second, third, slot);
    }
}

void* selectNativeHook(const NativeTarget& target) {
    if (target.HasReturn && target.Return.Indirect)
        return target.Inputs.empty()
            ? nativeIndirectReturnHookForSlot(target.Slot)
            : nullptr;
    std::array<NativeRegisterKind, 3> arguments{
        NativeRegisterKind::Gp, NativeRegisterKind::Gp, NativeRegisterKind::Gp};
    for (std::size_t index = 0; index < target.Inputs.size(); ++index)
        arguments[index] = target.Inputs[index].Register;
    if (!target.HasReturn)
        return selectFirstNativeArgument<void>(arguments[0], arguments[1], arguments[2], target.Slot);
    switch (target.Return.Register) {
    case NativeRegisterKind::Float32:
        return selectFirstNativeArgument<float>(arguments[0], arguments[1], arguments[2], target.Slot);
    case NativeRegisterKind::Float64:
        return selectFirstNativeArgument<double>(arguments[0], arguments[1], arguments[2], target.Slot);
    default:
        return selectFirstNativeArgument<NativeGp>(arguments[0], arguments[1], arguments[2], target.Slot);
    }
}

// Keep SEH in a leaf function containing no objects with destructors. An engine
// access violation is converted into an ABI error instead of crossing into CLR.
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

struct TextConversionRuntime {
    UObject* Library{};
    const UFunction* StringToText{};
    const FProperty* StringToTextReturn{};
    const UFunction* TextToString{};
    const FProperty* TextToStringReturn{};
};

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

struct OwnedTextValue {
    std::array<std::byte, 40> Parameters{};
    const FProperty* Property{};
    bool Initialized{};
};

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

BriefcasePropertyKind propertyKind(const FProperty* property) {
    if (!property || !readable(property->ClassPrivate, sizeof(FFieldClass))) return BRIEFCASE_PROPERTY_UNKNOWN;
    const auto* fieldClass = reinterpret_cast<const FFieldClass*>(property->ClassPrivate);
    const auto type = nameToString(fieldClass->Name, RuntimeNameConverter);
    if (type == L"IntProperty") return BRIEFCASE_PROPERTY_INT32;
    if (type == L"UInt32Property") return BRIEFCASE_PROPERTY_UINT32;
    if (type == L"Int64Property") return BRIEFCASE_PROPERTY_INT64;
    if (type == L"UInt64Property") return BRIEFCASE_PROPERTY_UINT64;
    if (type == L"FloatProperty") return BRIEFCASE_PROPERTY_FLOAT;
    if (type == L"DoubleProperty") return BRIEFCASE_PROPERTY_DOUBLE;
    if (type == L"BoolProperty") return BRIEFCASE_PROPERTY_BOOL;
    if (type == L"ByteProperty" || type == L"EnumProperty") return BRIEFCASE_PROPERTY_BYTE;
    if (type == L"ObjectProperty" || type == L"ClassProperty") return BRIEFCASE_PROPERTY_OBJECT;
    if (type == L"StructProperty") return BRIEFCASE_PROPERTY_STRUCT;
    if (type == L"StrProperty") return BRIEFCASE_PROPERTY_STRING;
    if (type == L"TextProperty") return BRIEFCASE_PROPERTY_TEXT;
    return BRIEFCASE_PROPERTY_UNKNOWN;
}

bool nativeValueLayout(
    const FProperty* property, bool /*isReturn*/, std::uint32_t parameterSize,
    NativeValueLayout& result) {
    if (!property || property->ArrayDim != 1 || property->OffsetInternal < 0 ||
        property->ElementSize <= 0 ||
        static_cast<std::uint64_t>(property->OffsetInternal) +
            static_cast<std::uint64_t>(property->ElementSize) > parameterSize ||
        !readable(property->ClassPrivate, sizeof(FFieldClass)))
        return false;
    const auto type = nameToString(
        reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
        RuntimeNameConverter);
    result.Offset = property->OffsetInternal;
    result.Size = property->ElementSize;
    result.Indirect = false;
    result.Object = false;
    if (type == L"FloatProperty" && result.Size == 4)
        result.Register = NativeRegisterKind::Float32;
    else if (type == L"DoubleProperty" && result.Size == 8)
        result.Register = NativeRegisterKind::Float64;
    else if (type == L"Int8Property" || type == L"Int16Property" ||
             type == L"IntProperty" || type == L"Int64Property" ||
             type == L"ByteProperty" || type == L"UInt16Property" ||
             type == L"UInt32Property" || type == L"UInt64Property" ||
             type == L"EnumProperty" || type == L"BoolProperty") {
        if (result.Size != 1 && result.Size != 2 && result.Size != 4 && result.Size != 8)
            return false;
        result.Register = NativeRegisterKind::Gp;
    } else if (type == L"StructProperty") {
        result.Register = NativeRegisterKind::Gp;
        const bool registerSized = result.Size == 1 || result.Size == 2 ||
                                   result.Size == 4 || result.Size == 8;
        // MSVC passes a C++ reference as a pointer even when the referenced
        // structure would otherwise fit in one register. Unreal marks those
        // parameters with ReferenceParm in the reflected property flags.
        constexpr std::uint64_t ReferenceParameterFlag = 0x08000000;
        result.Indirect = (property->PropertyFlags & ReferenceParameterFlag) != 0 ||
                          !registerSized;
    } else if ((type == L"ObjectProperty" || type == L"ClassProperty") &&
               result.Size == static_cast<std::int32_t>(sizeof(void*))) {
        // The native function receives a UObject pointer. Managed patches see
        // the serial-checked handle used everywhere else in Briefcase, and a
        // changed prefix value is resolved back to a pointer before the call.
        result.Register = NativeRegisterKind::Gp;
        result.Object = true;
    } else {
        return false;
    }
    return true;
}

bool buildNativeLayout(const UFunction* function, NativeTarget& target) {
    constexpr std::uint64_t ParameterFlag = 0x80;
    constexpr std::uint64_t OutParameterFlag = 0x100;
    constexpr std::uint64_t ReturnParameterFlag = 0x400;
    constexpr std::uint64_t ReferenceParameterFlag = 0x08000000;
    constexpr std::uint64_t ConstParameterFlag = 0x2;
    if (!function || function->ParmsSize > 4096) return false;
    target.ParameterSize = function->ParmsSize;
    auto* field = function->ChildProperties;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) return false;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if ((property->PropertyFlags & ParameterFlag) != 0) {
            const bool isReturn = (property->PropertyFlags & ReturnParameterFlag) != 0;
            // UHT represents `const FStruct&` as ConstParm | OutParm |
            // ReferenceParm. It is an immutable native input, unlike a real
            // writable out parameter, and is copied into our private buffer.
            const bool isConstReference =
                (property->PropertyFlags &
                    (ConstParameterFlag | ReferenceParameterFlag)) ==
                (ConstParameterFlag | ReferenceParameterFlag);
            if (!isReturn && (property->PropertyFlags & OutParameterFlag) != 0 &&
                !isConstReference)
                return false;
            NativeValueLayout layout{};
            if (!nativeValueLayout(property, isReturn, target.ParameterSize, layout))
                return false;
            if (isReturn) {
                if (target.HasReturn) return false;
                target.Return = layout;
                target.HasReturn = true;
            } else {
                if (target.Inputs.size() == 3) return false;
                target.Inputs.push_back(layout);
            }
        }
        field = field->Next;
    }
    // A hidden return buffer consumes RDX after the member-function `this`
    // pointer. Explicit inputs would therefore need a separate shifted-register
    // dispatcher; reject that shape until it is implemented deliberately.
    if (target.HasReturn && target.Return.Indirect && !target.Inputs.empty())
        return false;
    return true;
}

bool sameNativeLayout(const NativeTarget& left, const NativeTarget& right) {
    if (left.ParameterSize != right.ParameterSize || left.HasReturn != right.HasReturn ||
        left.Inputs.size() != right.Inputs.size()) return false;
    const auto equal = [](const NativeValueLayout& a, const NativeValueLayout& b) {
        return a.Offset == b.Offset && a.Size == b.Size && a.Register == b.Register &&
               a.Indirect == b.Indirect && a.Object == b.Object;
    };
    if (left.HasReturn && !equal(left.Return, right.Return)) return false;
    for (std::size_t index = 0; index < left.Inputs.size(); ++index)
        if (!equal(left.Inputs[index], right.Inputs[index])) return false;
    return true;
}

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

std::string utf8(std::wstring_view value) {
    if (value.empty()) return {};
    const auto required = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
        nullptr, 0, nullptr, nullptr);
    if (required <= 0) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    if (WideCharToMultiByte(
            CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()),
            result.data(), required, nullptr, nullptr) != required)
        return {};
    return result;
}

void writeJsonString(std::ostream& output, std::wstring_view value) {
    output.put('"');
    for (const auto character : utf8(value)) {
        const auto byte = static_cast<unsigned char>(character);
        switch (byte) {
        case '"': output << "\\\""; break;
        case '\\': output << "\\\\"; break;
        case '\b': output << "\\b"; break;
        case '\f': output << "\\f"; break;
        case '\n': output << "\\n"; break;
        case '\r': output << "\\r"; break;
        case '\t': output << "\\t"; break;
        default:
            if (byte < 0x20) {
                constexpr char digits[] = "0123456789ABCDEF";
                output << "\\u00" << digits[byte >> 4] << digits[byte & 0x0F];
            } else {
                output.put(static_cast<char>(byte));
            }
            break;
        }
    }
    output.put('"');
}

void writePropertySnapshot(std::ostream& output, const FProperty* property) {
    const auto* fieldClass = reinterpret_cast<const FFieldClass*>(property->ClassPrivate);
    const auto typeName = readable(fieldClass, sizeof(FFieldClass))
        ? nameToString(fieldClass->Name, RuntimeNameConverter) : L"UnknownProperty";
    output << "{\"name\":";
    writeJsonString(output, nameToString(property->NamePrivate, RuntimeNameConverter));
    output << ",\"unrealType\":";
    writeJsonString(output, typeName);
    output << ",\"offset\":" << property->OffsetInternal
           << ",\"elementSize\":" << property->ElementSize
           << ",\"arrayDimension\":" << property->ArrayDim
           << ",\"flags\":" << property->PropertyFlags;
    if (typeName == L"StructProperty" && readable(property, sizeof(FStructProperty))) {
        const auto* typed = reinterpret_cast<const FStructProperty*>(property);
        if (typed->Struct && readable(typed->Struct, sizeof(UStruct))) {
            output << ",\"referencedTypePath\":";
            writeJsonString(output, objectPath(typed->Struct, RuntimeNameConverter));
        }
    }
    if (typeName == L"ArrayProperty" && readable(property, sizeof(FArrayProperty))) {
        const auto* typed = reinterpret_cast<const FArrayProperty*>(property);
        if (typed->Inner && readable(typed->Inner, sizeof(FProperty))) {
            const auto* innerClass = reinterpret_cast<const FFieldClass*>(typed->Inner->ClassPrivate);
            if (readable(innerClass, sizeof(FFieldClass))) {
                output << ",\"innerUnrealType\":";
                writeJsonString(output, nameToString(innerClass->Name, RuntimeNameConverter));
            }
        }
    }
    output << '}';
}

void writeProperties(std::ostream& output, const FField* first, bool parametersOnly) {
    constexpr std::uint64_t ParameterFlag = 0x80;
    bool needsComma = false;
    auto* field = first;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) break;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if (!parametersOnly || (property->PropertyFlags & ParameterFlag) != 0) {
            if (needsComma) output.put(',');
            writePropertySnapshot(output, property);
            needsComma = true;
        }
        field = field->Next;
    }
}

void writeFunctions(std::ostream& output, const UField* first) {
    bool needsComma = false;
    auto* child = first;
    for (unsigned visited = 0; child && visited < 4096; ++visited) {
        if (!readable(child, sizeof(UField)) ||
            !readable(child->ClassPrivate, sizeof(UObject)))
            break;
        if (nameToString(child->ClassPrivate->NamePrivate, RuntimeNameConverter) == L"Function" &&
            readable(child, sizeof(UFunction))) {
            const auto* function = reinterpret_cast<const UFunction*>(child);
            if (needsComma) output.put(',');
            output << "{\"name\":";
            writeJsonString(output, nameToString(function->NamePrivate, RuntimeNameConverter));
            output << ",\"flags\":" << function->FunctionFlags
                   << ",\"parameterSize\":" << function->ParmsSize
                   << ",\"parameterCount\":" << static_cast<unsigned>(function->NumParms)
                   << ",\"parameters\":[";
            writeProperties(output, function->ChildProperties, true);
            output << "]}";
            needsComma = true;
        }
        child = child->Next;
    }
}

struct ReflectedType {
    const UObject* Object;
    std::wstring Path;
    std::wstring Kind;
};

void writeSdkSnapshot(const std::filesystem::path& root,
                      const briefcase::profile::RuntimeProfile& runtimeProfile) {
    if (!RuntimeObjects || !RuntimeNameConverter) return;

    std::vector<ReflectedType> types;
    const auto objectCount = RuntimeObjects->ObjObjects.NumElements;
    types.reserve(4096);
    for (std::int32_t index = 0; index < objectCount; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UStruct)) ||
            item->Object->InternalIndex != index ||
            !readable(item->Object->ClassPrivate, sizeof(UObject)))
            continue;
        const auto kind = nameToString(
            item->Object->ClassPrivate->NamePrivate, RuntimeNameConverter);
        if (kind != L"Class" && kind != L"ScriptStruct") continue;
        auto path = objectPath(item->Object, RuntimeNameConverter);
        if (!path.starts_with(L"/Script/")) continue;
        types.push_back({item->Object, std::move(path), kind});
    }
    std::sort(types.begin(), types.end(), [](const auto& left, const auto& right) {
        return left.Path < right.Path;
    });

    const auto directory = root / L"Briefcase" / L"Core" / L"Sdk" / L"Metadata";
    std::filesystem::create_directories(directory);
    std::wostringstream fileName;
    fileName << L"DeceiveInc." << runtimeProfile.TargetName << L'.'
             << std::uppercase << std::hex << std::setw(8) << std::setfill(L'0')
             << runtimeProfile.PeTimestamp << L'-' << std::setw(8)
             << runtimeProfile.ImageSize << L".json";
    const auto destination = directory / fileName.str();
    const auto temporary = destination.wstring() + L".tmp-" +
                           std::to_wstring(GetCurrentProcessId());

    std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
    if (!output) {
        briefcase::log(L"sdk snapshot: unable to create " + temporary);
        return;
    }
    output << "{\"schemaVersion\":2,\"target\":";
    writeJsonString(output, runtimeProfile.TargetName);
    output << ",\"sdkAssemblyName\":";
    writeJsonString(output, runtimeProfile.SdkAssemblyName);
    output << ",\"gameBuild\":{\"peTimestamp\":" << runtimeProfile.PeTimestamp
           << ",\"imageSize\":" << runtimeProfile.ImageSize
           << "},\"capturedObjectCount\":" << objectCount << ",\"types\":[";

    bool needsTypeComma = false;
    for (const auto& type : types) {
        const auto* structure = reinterpret_cast<const UStruct*>(type.Object);
        if (!readable(structure, sizeof(UStruct))) continue;
        if (needsTypeComma) output.put(',');
        output << "{\"path\":";
        writeJsonString(output, type.Path);
        output << ",\"name\":";
        writeJsonString(output, nameToString(type.Object->NamePrivate, RuntimeNameConverter));
        output << ",\"kind\":";
        writeJsonString(output, type.Kind);
        output << ",\"superPath\":";
        if (structure->SuperStruct && readable(structure->SuperStruct, sizeof(UStruct)))
            writeJsonString(output, objectPath(structure->SuperStruct, RuntimeNameConverter));
        else
            output << "null";
        output << ",\"size\":" << structure->PropertiesSize << ",\"properties\":[";
        writeProperties(output, structure->ChildProperties, false);
        output << "],\"functions\":[";
        writeFunctions(output, structure->Children);
        output << "]}";
        needsTypeComma = true;
    }
    output << "]}";
    output.close();
    if (!output) {
        DeleteFileW(temporary.c_str());
        briefcase::log(L"sdk snapshot: write failed for " + temporary);
        return;
    }
    if (!MoveFileExW(temporary.c_str(), destination.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const auto error = GetLastError();
        DeleteFileW(temporary.c_str());
        briefcase::log(L"sdk snapshot: atomic replace failed error=" + std::to_wstring(error));
        return;
    }
    briefcase::log(L"sdk snapshot: wrote " + std::to_wstring(types.size()) +
             L" reflected types to " + destination.wstring());
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

std::int32_t canonicalSize(BriefcasePropertyKind kind) {
    switch (kind) {
    case BRIEFCASE_PROPERTY_INT32:
    case BRIEFCASE_PROPERTY_UINT32:
    case BRIEFCASE_PROPERTY_FLOAT: return 4;
    case BRIEFCASE_PROPERTY_INT64:
    case BRIEFCASE_PROPERTY_UINT64:
    case BRIEFCASE_PROPERTY_DOUBLE:
    case BRIEFCASE_PROPERTY_OBJECT: return 8;
    case BRIEFCASE_PROPERTY_BOOL: return 1;
    case BRIEFCASE_PROPERTY_BYTE: return 1;
    default: return 0;
    }
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
        const UObject* referenced{};
        if (!safeCopy(&referenced, source, sizeof(referenced))) return BRIEFCASE_UNREAL_UNREADABLE;
        BriefcaseObjectHandle referencedHandle{};
        if (!makeHandle(referenced, referencedHandle)) return BRIEFCASE_UNREAL_STALE_HANDLE;
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

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeFunction(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength,
    std::uint32_t expectedParameterSize, void* parameters,
    std::uint32_t parameterSize) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (expectedParameterSize != parameterSize || parameterSize > 65'535u ||
        (parameterSize && (!parameters || !writable(parameters, parameterSize))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;

    auto* object = const_cast<UObject*>(resolveObject(objectHandle));
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!object || !ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass) || !objectIsA(object, ownerClass))
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* function = findFunction(ownerClass, name);
    if (!function) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (function->ParmsSize != expectedParameterSize)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    // Managed code represents UObject parameters as BriefcaseObjectHandle. Before
    // ProcessEvent sees the buffer, resolve every reflected ObjectProperty to a
    // pointer whose object-array slot and serial number still match. Object
    // outputs are translated back after the call, so no game address crosses
    // the public ABI in either direction.
    struct ObjectArgument {
        std::int32_t Offset{};
        BriefcaseObjectHandle Original{};
        bool Output{};
    };
    std::vector<ObjectArgument> objectArguments;
    constexpr std::uint64_t ParameterFlag = 0x80;
    constexpr std::uint64_t OutParameterFlag = 0x100;
    constexpr std::uint64_t ReturnParameterFlag = 0x400;
    auto* field = function->ChildProperties;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) return BRIEFCASE_UNREAL_UNREADABLE;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        const auto* fieldClass = reinterpret_cast<const FFieldClass*>(property->ClassPrivate);
        const auto typeName = readable(fieldClass, sizeof(FFieldClass))
            ? nameToString(fieldClass->Name, RuntimeNameConverter) : L"";
        if ((property->PropertyFlags & ParameterFlag) != 0 &&
            (typeName == L"ObjectProperty" || typeName == L"ClassProperty")) {
            if (property->ElementSize != sizeof(void*) || property->ArrayDim != 1 ||
                property->OffsetInternal < 0 ||
                static_cast<std::uint64_t>(property->OffsetInternal) + sizeof(void*) > parameterSize)
                return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
            auto* slot = reinterpret_cast<std::byte*>(parameters) + property->OffsetInternal;
            const bool output = (property->PropertyFlags &
                (OutParameterFlag | ReturnParameterFlag)) != 0;
            BriefcaseObjectHandle supplied{std::numeric_limits<std::uint32_t>::max(), 0};
            UObject* resolved{};
            if (!output) {
                if (!safeCopy(&supplied, slot, sizeof(supplied))) return BRIEFCASE_UNREAL_UNREADABLE;
                if (supplied.Index != std::numeric_limits<std::uint32_t>::max()) {
                    resolved = const_cast<UObject*>(resolveObject(supplied));
                    if (!resolved) return BRIEFCASE_UNREAL_STALE_HANDLE;
                }
            }
            if (!safeCopy(slot, &resolved, sizeof(resolved))) return BRIEFCASE_UNREAL_UNREADABLE;
            objectArguments.push_back({property->OffsetInternal, supplied, output});
        }
        field = field->Next;
    }

    auto** vtable = reinterpret_cast<void**>(object->VTable);
    if (!readable(vtable + briefcase::profile::ProcessEventVTableIndex, sizeof(void*)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    const auto processEvent = reinterpret_cast<ProcessEventFn>(
        vtable[briefcase::profile::ProcessEventVTableIndex]);
    if (!executable(reinterpret_cast<const void*>(processEvent)))
        return BRIEFCASE_UNREAL_UNREADABLE;

    if (!safeProcessEvent(processEvent, object, function, parameters)) {
        for (const auto& argument : objectArguments) {
            if (argument.Output) continue;
            auto* slot = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
            safeCopy(slot, &argument.Original, sizeof(argument.Original));
        }
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    for (const auto& argument : objectArguments) {
        auto* slot = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
        if (!argument.Output) {
            if (!safeCopy(slot, &argument.Original, sizeof(argument.Original)))
                return BRIEFCASE_UNREAL_UNREADABLE;
            continue;
        }
        UObject* returned{};
        if (!safeCopy(&returned, slot, sizeof(returned))) return BRIEFCASE_UNREAL_UNREADABLE;
        BriefcaseObjectHandle returnedHandle{};
        if (!makeHandle(returned, returnedHandle)) return BRIEFCASE_UNREAL_STALE_HANDLE;
        if (!safeCopy(slot, &returnedHandle, sizeof(returnedHandle)))
            return BRIEFCASE_UNREAL_UNREADABLE;
    }
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeFunctionText(
    void* context, BriefcaseObjectHandle objectHandle,
    BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::uint32_t expectedParameterSize,
    void* parameters, std::uint32_t parameterSize,
    const BriefcaseTextArgument* textArguments,
    std::uint32_t textArgumentCount) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!textArguments || textArgumentCount == 0 || textArgumentCount > 64 ||
        !readable(textArguments,
            static_cast<std::size_t>(textArgumentCount) * sizeof(BriefcaseTextArgument)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (expectedParameterSize != parameterSize || parameterSize > 65'535u ||
        (parameterSize && (!parameters || !writable(parameters, parameterSize))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;

    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!ownerClass || !isClassObject(ownerClass)) return BRIEFCASE_UNREAL_STALE_HANDLE;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* function = findFunction(ownerClass, name);
    if (!function) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (function->ParmsSize != expectedParameterSize)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    struct TextArgumentState {
        BriefcaseTextArgument Descriptor{};
        const FProperty* Property{};
        bool Output{};
        bool OutputInitialized{};
        OwnedTextValue Input;
    };
    std::vector<TextArgumentState> states;
    states.reserve(textArgumentCount);
    for (std::uint32_t index = 0; index < textArgumentCount; ++index) {
        BriefcaseTextArgument descriptor{};
        if (!safeCopy(&descriptor, textArguments + index, sizeof(descriptor)) ||
            descriptor.StructSize < sizeof(BriefcaseTextArgument) ||
            (descriptor.Flags != BRIEFCASE_TEXT_INPUT &&
             descriptor.Flags != BRIEFCASE_TEXT_OUTPUT))
            return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
        if (std::any_of(states.begin(), states.end(), [&](const auto& state) {
                return state.Descriptor.ParameterOffset == descriptor.ParameterOffset;
            }))
            return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
        states.push_back({descriptor});
    }

    constexpr std::uint64_t ParameterFlag = 0x80;
    constexpr std::uint64_t OutParameterFlag = 0x100;
    constexpr std::uint64_t ReturnParameterFlag = 0x400;
    std::uint32_t reflectedTextCount = 0;
    auto* field = function->ChildProperties;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty)) ||
            !readable(field->ClassPrivate, sizeof(FFieldClass)))
            return BRIEFCASE_UNREAL_UNREADABLE;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        const auto typeName = nameToString(
            reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
            RuntimeNameConverter);
        if ((property->PropertyFlags & ParameterFlag) != 0 &&
            typeName == L"TextProperty") {
            ++reflectedTextCount;
            if (property->ElementSize != 24 || property->ArrayDim != 1 ||
                property->OffsetInternal < 0 ||
                static_cast<std::uint64_t>(property->OffsetInternal) + 24 > parameterSize)
                return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
            auto match = std::find_if(states.begin(), states.end(), [&](const auto& state) {
                return state.Descriptor.ParameterOffset == property->OffsetInternal;
            });
            if (match == states.end()) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
            const bool output = (property->PropertyFlags &
                (OutParameterFlag | ReturnParameterFlag)) != 0;
            const auto expectedFlag = output ? BRIEFCASE_TEXT_OUTPUT : BRIEFCASE_TEXT_INPUT;
            if (match->Descriptor.Flags != expectedFlag)
                return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
            match->Property = property;
            match->Output = output;
        }
        field = field->Next;
    }
    if (reflectedTextCount != textArgumentCount ||
        std::any_of(states.begin(), states.end(),
            [](const auto& state) { return state.Property == nullptr; }))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    TextConversionRuntime textRuntime{};
    if (!resolveTextConversionRuntime(textRuntime)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    auto cleanup = [&]() noexcept {
        for (auto& state : states) {
            auto* slot = reinterpret_cast<std::byte*>(parameters) +
                         state.Descriptor.ParameterOffset;
            if (state.OutputInitialized) {
                destroyPropertyValue(state.Property, slot);
                state.OutputInitialized = false;
            }
            destroyText(state.Input);
            std::array<std::byte, 24> empty{};
            safeCopy(slot, empty.data(), empty.size());
        }
    };

    for (auto& state : states) {
        auto* slot = reinterpret_cast<std::byte*>(parameters) +
                     state.Descriptor.ParameterOffset;
        if (state.Output) {
            if (!state.Descriptor.RequiredCharacters ||
                !writable(state.Descriptor.RequiredCharacters,
                          sizeof(*state.Descriptor.RequiredCharacters)) ||
                (state.Descriptor.OutputCapacityCharacters &&
                 (!state.Descriptor.Output ||
                  !writable(state.Descriptor.Output,
                      static_cast<std::size_t>(state.Descriptor.OutputCapacityCharacters) *
                          sizeof(std::uint16_t))))) {
                cleanup();
                return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
            }
            *state.Descriptor.RequiredCharacters = 0;
            if (!initializePropertyValue(state.Property, slot)) {
                cleanup();
                return BRIEFCASE_UNREAL_UNREADABLE;
            }
            state.OutputInitialized = true;
        } else {
            if (!constructText(textRuntime, state.Descriptor.Input,
                               state.Descriptor.InputCharacters, state.Input) ||
                !safeCopy(slot, state.Input.Parameters.data() + 16, 24)) {
                cleanup();
                return BRIEFCASE_UNREAL_UNREADABLE;
            }
        }
    }

    auto status = apiInvokeFunction(
        context, objectHandle, ownerClassHandle, utf8Name, nameLength,
        expectedParameterSize, parameters, parameterSize);
    if (status == BRIEFCASE_UNREAL_OK) {
        for (auto& state : states) {
            if (!state.Output) continue;
            auto* slot = reinterpret_cast<std::byte*>(parameters) +
                         state.Descriptor.ParameterOffset;
            status = textToUtf16(
                textRuntime, slot, state.Descriptor.Output,
                state.Descriptor.OutputCapacityCharacters,
                state.Descriptor.RequiredCharacters);
            if (status != BRIEFCASE_UNREAL_OK) break;
        }
    }
    cleanup();
    return status;
}

bool classIsChildOf(const UObject* candidate, const UObject* requestedBase) {
    if (!isClassObject(candidate) || !isClassObject(requestedBase)) return false;
    auto* current = reinterpret_cast<const UStruct*>(candidate);
    for (unsigned depth = 0; current && depth < 256; ++depth) {
        if (!readable(current, sizeof(UStruct))) return false;
        if (current == requestedBase) return true;
        current = current->SuperStruct;
    }
    return false;
}

bool installProcessEventDetour(void** vtable) {
    if (!vtable ||
        !readable(vtable + briefcase::profile::ProcessEventVTableIndex, sizeof(void*))) return false;
    const std::scoped_lock lock(PatchStateMutex);
    auto* target = vtable[briefcase::profile::ProcessEventVTableIndex];
    if (!target || !executable(target)) return false;
    // UObject::ProcessEvent is shared by ordinary native and Blueprint object
    // hierarchies. Once its implementation is detoured, registrations for a
    // newly instantiated class only add dispatch metadata; they do not require
    // a second MinHook target. Some cooked subsystem vtables expose a thunk at
    // this slot even though calls ultimately reach the already hooked function.
    if (OriginalProcessEvent.load(std::memory_order_acquire)) return true;

    const auto initialize = MH_Initialize();
    if (initialize != MH_OK && initialize != MH_ERROR_ALREADY_INITIALIZED) return false;
    void* trampoline{};
    if (MH_CreateHook(target, reinterpret_cast<void*>(&processEventHook), &trampoline) != MH_OK ||
        !trampoline || !executable(trampoline)) return false;
    OriginalProcessEvent.store(
        reinterpret_cast<ProcessEventFn>(trampoline), std::memory_order_release);
    ProcessEventTarget = target;
    if (MH_EnableHook(target) != MH_OK) {
        OriginalProcessEvent.store(nullptr, std::memory_order_release);
        ProcessEventTarget = nullptr;
        MH_RemoveHook(target);
        return false;
    }
    briefcase::log(L"patching host: global UObject::ProcessEvent detour installed");
    return true;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiRegisterPatch(
    void*, BriefcaseObjectHandle targetClassHandle, BriefcaseObjectHandle functionOwnerClassHandle,
    const char* utf8FunctionName, std::uint32_t functionNameLength, BriefcasePatchPhase phase,
    BriefcasePatchCallbackFn callback, void* userContext, std::uint64_t* registrationId) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!callback || !registrationId || !writable(registrationId, sizeof(*registrationId)) ||
        (phase != BRIEFCASE_PATCH_PREFIX && phase != BRIEFCASE_PATCH_POSTFIX))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;

    const auto* targetClass = resolveObject(targetClassHandle);
    const auto* functionOwnerClass = resolveObject(functionOwnerClassHandle);
    if (!targetClass || !functionOwnerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!classIsChildOf(targetClass, functionOwnerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    std::wstring functionName;
    if (!decodeUtf8(utf8FunctionName, functionNameLength, functionName))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* function = findFunction(functionOwnerClass, functionName);
    if (!function) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (function->ParmsSize > 65'535) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    std::vector<void**> vtables;
    const auto objectCount = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < objectCount; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        auto* object = item ? item->Object : nullptr;
        if (!object || !readable(object, sizeof(UObject)) ||
            !objectIsA(object, targetClass)) continue;
        auto** vtable = reinterpret_cast<void**>(object->VTable);
        if (vtable && std::find(vtables.begin(), vtables.end(), vtable) == vtables.end())
            vtables.push_back(vtable);
    }
    if (vtables.empty()) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (vtables.empty() || !installProcessEventDetour(vtables.front()))
        return BRIEFCASE_UNREAL_UNSUPPORTED;

    try {
        auto registration = std::make_shared<PatchRegistration>();
        {
            const std::scoped_lock lock(PatchStateMutex);
            registration->Id = NextPatchRegistrationId++;
            registration->TargetClass = targetClass;
            // Match the reflected function name rather than this exact UFunction
            // address. Blueprint subclasses may own another UFunction entry for
            // the same override; its parameter layout must still match.
            registration->FunctionName = function->NamePrivate;
            registration->ParameterSize = function->ParmsSize;
            registration->Phase = phase;
            registration->Callback = callback;
            registration->UserContext = userContext;
            PatchRegistrations.push_back(registration);
        }
        *registrationId = registration->Id;
        briefcase::log(L"patching host: registered " + objectPath(targetClass, RuntimeNameConverter) +
                 L"." + functionName +
                 (phase == BRIEFCASE_PATCH_PREFIX ? L" prefix" : L" postfix"));
        return BRIEFCASE_UNREAL_OK;
    } catch (...) {
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    }
}

BriefcaseBool BRIEFCASE_MOD_CALL apiUnregisterPatch(void*, std::uint64_t registrationId) {
    std::shared_ptr<PatchRegistration> removed;
    std::size_t remaining{};
    {
        const std::scoped_lock lock(PatchStateMutex);
        const auto registration = std::find_if(
            PatchRegistrations.begin(), PatchRegistrations.end(),
            [registrationId](const auto& item) { return item->Id == registrationId; });
        if (registration == PatchRegistrations.end()) return 0;
        removed = *registration;
        removed->Active = false;
        PatchRegistrations.erase(registration);
        remaining = PatchRegistrations.size();
    }
    // Wait for an already-running callback before managed code frees its GCHandle
    // and unloads the collectible AssemblyLoadContext.
    const std::scoped_lock invocationLock(removed->InvocationMutex);
    briefcase::log(L"patching host: unregistered callback " +
             std::to_wstring(registrationId) + L"; remaining=" +
             std::to_wstring(remaining));
    return 1;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiRegisterNativePatch(
    void*, BriefcaseObjectHandle targetClassHandle, BriefcaseObjectHandle functionOwnerClassHandle,
    const char* utf8FunctionName, std::uint32_t functionNameLength, BriefcasePatchPhase phase,
    BriefcasePatchCallbackFn callback, void* userContext, std::uint64_t* registrationId) {
    if (!RuntimeObjects || !RuntimeNameConverter || !ActiveProfile || !RuntimeImageBase)
        return BRIEFCASE_UNREAL_NOT_READY;
    if (!callback || !registrationId || !writable(registrationId, sizeof(*registrationId)) ||
        (phase != BRIEFCASE_PATCH_PREFIX && phase != BRIEFCASE_PATCH_POSTFIX))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;

    const auto* targetClass = resolveObject(targetClassHandle);
    const auto* functionOwnerClass = resolveObject(functionOwnerClassHandle);
    if (!targetClass || !functionOwnerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!classIsChildOf(targetClass, functionOwnerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    std::wstring functionName;
    if (!decodeUtf8(utf8FunctionName, functionNameLength, functionName))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* function = findFunction(functionOwnerClass, functionName);
    if (!function) return BRIEFCASE_UNREAL_NOT_FOUND;

    constexpr std::uint32_t FunctionNative = 0x00000400u;
    NativeTarget prototype{};
    if ((function->FunctionFlags & FunctionNative) == 0 ||
        !buildNativeLayout(function, prototype))
        return BRIEFCASE_UNREAL_UNSUPPORTED;

    auto* targetAddress = resolveNativeImplementation(function, targetClass);
    if (!targetAddress) {
        const auto execRva = reinterpret_cast<std::uintptr_t>(function->Func) -
                             reinterpret_cast<std::uintptr_t>(RuntimeImageBase);
        briefcase::log(L"native patching host: could not resolve the unique implementation for " +
                 objectPath(functionOwnerClass, RuntimeNameConverter) + L"." + functionName +
                 L" execRva=" + hexadecimal(execRva));
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    }

    try {
        NativeTarget* target{};
        {
            const std::scoped_lock lock(NativePatchStateMutex);
            const auto existing = std::find_if(
                NativeTargets.begin(), NativeTargets.end(),
                [targetAddress](const auto& item) { return item->Address == targetAddress; });
            if (existing != NativeTargets.end()) {
                if (!sameNativeLayout(**existing, prototype)) return BRIEFCASE_UNREAL_UNSUPPORTED;
                target = existing->get();
            } else {
                if (NativeTargets.size() >= MaxNativeTargets)
                    return BRIEFCASE_UNREAL_UNSUPPORTED;
                const auto initialize = MH_Initialize();
                if (initialize != MH_OK && initialize != MH_ERROR_ALREADY_INITIALIZED)
                    return BRIEFCASE_UNREAL_UNSUPPORTED;

                auto created = std::make_unique<NativeTarget>(std::move(prototype));
                created->Address = targetAddress;
                created->Slot = NativeTargets.size();
                auto* hook = selectNativeHook(*created);
                if (!hook) return BRIEFCASE_UNREAL_UNSUPPORTED;
                void* trampoline{};
                if (MH_CreateHook(targetAddress, hook,
                                  &trampoline) != MH_OK || !trampoline || !executable(trampoline))
                    return BRIEFCASE_UNREAL_UNSUPPORTED;
                created->Original = trampoline;
                target = created.get();
                NativeSlots[created->Slot].store(target, std::memory_order_release);
                if (MH_EnableHook(targetAddress) != MH_OK) {
                    NativeSlots[created->Slot].store(nullptr, std::memory_order_release);
                    MH_RemoveHook(targetAddress);
                    return BRIEFCASE_UNREAL_UNSUPPORTED;
                }
                NativeTargets.push_back(std::move(created));

                const auto implementationRva =
                    reinterpret_cast<std::uintptr_t>(targetAddress) -
                    reinterpret_cast<std::uintptr_t>(RuntimeImageBase);
                briefcase::log(L"native patching host: installed native detour for " +
                         objectPath(functionOwnerClass, RuntimeNameConverter) + L"." + functionName +
                         L" rva=" + hexadecimal(implementationRva) +
                         L" inputs=" + std::to_wstring(target->Inputs.size()) +
                         (target->HasReturn ? L" return=yes" : L" return=no"));
            }

            auto registration = std::make_shared<NativePatchRegistration>();
            registration->Id = NextNativePatchRegistrationId++;
            registration->Target = target;
            registration->TargetClass = targetClass;
            registration->FunctionName = function->NamePrivate;
            registration->Phase = phase;
            registration->Callback = callback;
            registration->UserContext = userContext;
            NativePatchRegistrations.push_back(registration);
            *registrationId = registration->Id;
        }
        briefcase::log(L"native patching host: registered " +
                 objectPath(targetClass, RuntimeNameConverter) + L"." + functionName +
                 (phase == BRIEFCASE_PATCH_PREFIX ? L" prefix" : L" postfix"));
        return BRIEFCASE_UNREAL_OK;
    } catch (...) {
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    }
}

BriefcaseBool BRIEFCASE_MOD_CALL apiUnregisterNativePatch(void*, std::uint64_t registrationId) {
    std::shared_ptr<NativePatchRegistration> removed;
    std::size_t remaining{};
    {
        const std::scoped_lock lock(NativePatchStateMutex);
        const auto registration = std::find_if(
            NativePatchRegistrations.begin(), NativePatchRegistrations.end(),
            [registrationId](const auto& item) { return item->Id == registrationId; });
        if (registration == NativePatchRegistrations.end()) return 0;
        removed = *registration;
        removed->Active = false;
        NativePatchRegistrations.erase(registration);
        remaining = NativePatchRegistrations.size();
    }
    // The machine-code detour remains as a forwarding stub for process lifetime.
    // Waiting here makes the collectible managed AssemblyLoadContext safe to free.
    const std::scoped_lock invocationLock(removed->InvocationMutex);
    briefcase::log(L"native patching host: unregistered callback " +
             std::to_wstring(registrationId) + L"; remaining=" +
             std::to_wstring(remaining));
    return 1;
}

struct ScriptArrayView {
    const void* Data;
    std::int32_t Num;
    std::int32_t Max;
};
static_assert(sizeof(ScriptArrayView) == 16);

const ScriptArrayView* patchContainer(
    const BriefcasePatchCall* call, std::uint32_t parameterOffset) {
    if (!call || call->StructSize < sizeof(BriefcasePatchCall) || !call->Parameters ||
        call->ParameterSize < sizeof(ScriptArrayView) ||
        parameterOffset > call->ParameterSize - sizeof(ScriptArrayView))
        return nullptr;
    const auto* descriptor = reinterpret_cast<const ScriptArrayView*>(
        static_cast<const std::byte*>(call->Parameters) + parameterOffset);
    return readable(descriptor, sizeof(*descriptor)) ? descriptor : nullptr;
}

const void* patchValue(
    const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    std::uint32_t valueSize) {
    if (!call || call->StructSize < sizeof(BriefcasePatchCall) || !call->Parameters ||
        call->ParameterSize < valueSize ||
        parameterOffset > call->ParameterSize - valueSize)
        return nullptr;
    const auto* value = static_cast<const std::byte*>(call->Parameters) + parameterOffset;
    return readable(value, valueSize) ? value : nullptr;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchByteArray(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    std::uint8_t* destination, std::uint32_t capacity, std::uint32_t* requiredBytes) {
    constexpr std::uint32_t MaximumBytes = 32u * 1024u * 1024u;
    if (!requiredBytes || !writable(requiredBytes, sizeof(*requiredBytes)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* value = patchContainer(call, parameterOffset);
    if (!value || value->Num < 0 || value->Max < value->Num ||
        static_cast<std::uint32_t>(value->Num) > MaximumBytes)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    *requiredBytes = static_cast<std::uint32_t>(value->Num);
    if (value->Num == 0) return BRIEFCASE_UNREAL_OK;
    if (!value->Data || !readable(value->Data, *requiredBytes))
        return BRIEFCASE_UNREAL_UNREADABLE;
    if (!destination || capacity < *requiredBytes)
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (!writable(destination, capacity)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    return safeCopy(destination, value->Data, *requiredBytes)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchString(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    std::uint16_t* destination, std::uint32_t capacityCharacters,
    std::uint32_t* requiredCharacters) {
    constexpr std::uint32_t MaximumCharacters = 1024u * 1024u;
    if (!requiredCharacters || !writable(requiredCharacters, sizeof(*requiredCharacters)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* value = patchContainer(call, parameterOffset);
    if (!value || value->Num < 0 || value->Max < value->Num ||
        static_cast<std::uint32_t>(value->Num) > MaximumCharacters)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    *requiredCharacters = static_cast<std::uint32_t>(value->Num);
    if (value->Num == 0) return BRIEFCASE_UNREAL_OK;
    const auto byteCount = static_cast<std::size_t>(*requiredCharacters) * sizeof(std::uint16_t);
    if (!value->Data || !readable(value->Data, byteCount))
        return BRIEFCASE_UNREAL_UNREADABLE;
    if (!destination || capacityCharacters < *requiredCharacters)
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (!writable(destination,
                  static_cast<std::size_t>(capacityCharacters) * sizeof(std::uint16_t)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    return safeCopy(destination, value->Data, byteCount)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchText(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    std::uint16_t* destination, std::uint32_t capacityCharacters,
    std::uint32_t* requiredCharacters) {
    const auto* value = patchValue(call, parameterOffset, 24);
    if (!value) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    TextConversionRuntime runtime{};
    if (!resolveTextConversionRuntime(runtime)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    return textToUtf16(
        runtime, value, destination, capacityCharacters, requiredCharacters);
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeNativeBoolean(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseGameBuild expectedBuild,
    std::uint64_t functionRva, BriefcaseBool* result) {
    if (!result || !RuntimeImageBase || !RuntimeImageSize || !ActiveProfile ||
        functionRva == 0 || functionRva >= RuntimeImageSize)
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *result = 0;
    if (expectedBuild.PeTimestamp != ActiveProfile->PeTimestamp ||
        expectedBuild.ImageSize != ActiveProfile->ImageSize)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    auto* object = const_cast<UObject*>(resolveObject(objectHandle));
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    auto* target = RuntimeImageBase + static_cast<std::size_t>(functionRva);
    if (!insideRuntimeImage(target) || !executable(target))
        return BRIEFCASE_UNREAL_UNREADABLE;

    using NativeBooleanMethod = bool (*)(UObject*);
    bool nativeResult{};
    __try {
        nativeResult = reinterpret_cast<NativeBooleanMethod>(target)(object);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    *result = nativeResult ? 1u : 0u;
    return BRIEFCASE_UNREAL_OK;
}

const BriefcaseUnrealApi UnrealApi{
    sizeof(BriefcaseUnrealApi), BRIEFCASE_UNREAL_API_VERSION, nullptr,
    apiFindObject, apiFindObjectsOfClass, apiGetObjectName, apiGetObjectPath,
    apiGetObjectClass, apiIsObjectA, apiGetPropertyInfo, apiReadProperty,
    apiInvokeFunction, apiReadStringProperty,
    apiInvokeFunctionText, apiReadTextProperty, apiInvokeNativeBoolean, {}};
const BriefcasePatchingApi PatchingApi{
    sizeof(BriefcasePatchingApi), BRIEFCASE_PATCHING_API_VERSION, nullptr,
    apiRegisterPatch, apiUnregisterPatch,
    apiRegisterNativePatch, apiUnregisterNativePatch,
    apiCopyPatchByteArray, apiCopyPatchString, apiCopyPatchText, {}};

} // namespace

namespace briefcase {

const BriefcaseUnrealApi* getUnrealApi() { return isUnrealApiReady() ? &UnrealApi : nullptr; }
const BriefcasePatchingApi* getPatchingApi() { return isUnrealApiReady() ? &PatchingApi : nullptr; }
bool isUnrealApiReady() {
    return RuntimeObjects && RuntimeNameConverter && saneObjectArray(RuntimeObjects);
}
const profile::RuntimeProfile* getRuntimeProfile() { return ActiveProfile; }

void runUnrealProbe(HMODULE self) {
    wchar_t modulePath[32768]{};
    if (!GetModuleFileNameW(self, modulePath, static_cast<DWORD>(std::size(modulePath)))) return;
    openLog(modulePath);
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
        return;
    }

    auto* nameFunctionAddress = base + runtimeProfile->FNameToStringRva;
    if (!readable(nameFunctionAddress, runtimeProfile->FNameToStringPrefix.size()) ||
        !std::equal(runtimeProfile->FNameToStringPrefix.begin(),
                    runtimeProfile->FNameToStringPrefix.end(), nameFunctionAddress)) {
        log(L"FName::ToString fingerprint mismatch; profile refused");
        return;
    }
    const auto convert = reinterpret_cast<FNameToString>(nameFunctionAddress);
    const auto* objects = reinterpret_cast<const FUObjectArray*>(
        base + runtimeProfile->GUObjectArrayRva);

    for (unsigned attempt = 0; attempt < 120 && !saneObjectArray(objects); ++attempt) Sleep(250);
    if (!saneObjectArray(objects)) {
        log(L"GUObjectArray did not become structurally valid");
        return;
    }

    // Native UClass objects appear very early, before packages have finished
    // linking their FProperty/FFunction chains. The object array being valid is
    // therefore necessary but not sufficient. Wait for normal asset loading to
    // grow the registry; this remains bounded and falls through after 30 seconds.
    const auto initialObjectCount = objects->ObjObjects.NumElements;
    for (unsigned attempt = 0; attempt < 120 &&
         saneObjectArray(objects) && objects->ObjObjects.NumElements < 50'000; ++attempt) {
        Sleep(250);
    }
    log(L"object registry initialization: " + std::to_wstring(initialObjectCount) + L" -> " +
        std::to_wstring(objects->ObjObjects.NumElements));

    const auto objectCountSnapshot = objects->ObjObjects.NumElements;
    // Publish the runtime state only after the executable profile, function
    // fingerprint and object-array invariants have all succeeded.
    RuntimeNameConverter = convert;
    RuntimeObjects = objects;
    ActiveProfile = runtimeProfile;
    RuntimeImageBase = base;
    RuntimeImageSize = nt->OptionalHeader.SizeOfImage;
    log(L"runtime target=" + std::wstring(runtimeProfile->TargetName) +
        L" sdk=" + std::wstring(runtimeProfile->SdkAssemblyName));
    log(L"imageBase=" + hexadecimal(reinterpret_cast<std::uintptr_t>(base)));
    log(L"GUObjectArray=" + hexadecimal(reinterpret_cast<std::uintptr_t>(objects)) +
        L" objects=" + std::to_wstring(objectCountSnapshot) +
        L" chunks=" + std::to_wstring(objects->ObjObjects.NumChunks));
    log(L"FName::ToString=" + hexadecimal(reinterpret_cast<std::uintptr_t>(nameFunctionAddress)));
    // SDK extraction is automatic for every supported executable profile. The
    // snapshot is build-specific and address-free, so client and server output
    // can coexist and be consumed by the same managed generator.
    writeSdkSnapshot(std::filesystem::path(modulePath).parent_path(), *runtimeProfile);

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
