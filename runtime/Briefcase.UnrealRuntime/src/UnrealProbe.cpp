#include "UnrealProbe.h"

#include "BinarySnapshotWriter.h"
#include "Log.h"
#include "PeImageView.h"
#include "RuntimeProfile.h"
#include "RuntimeSymbolResolver.h"
#include "UnrealLayout.h"

#include <Briefcase/BriefcaseModApi.h>
#include <MinHook.h>
#include <hde/hde64.h>
#include <Windows.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
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
#include <span>
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

struct GameThreadRegistration {
    std::uint64_t Id{};
    BriefcaseGameThreadCallbackFn Callback{};
    void* UserContext{};
    bool Active{true};
    std::recursive_mutex InvocationMutex;
};

std::mutex GameThreadStateMutex;
std::vector<std::shared_ptr<GameThreadRegistration>> GameThreadRegistrations;
std::atomic_uint32_t GameThreadRegistrationCount{};
std::uint64_t NextGameThreadRegistrationId{1};
std::atomic_uint32_t CapturedGameThreadId{};
std::atomic_bool GameThreadPumpRequested{true};
std::atomic_uint64_t GameThreadSequence{};
std::atomic_int64_t LastGameThreadPumpCounter{};
std::atomic_int64_t GameThreadPerformanceFrequency{};
const UObject* GameThreadWorldClass{};
BriefcaseObjectHandle LastObservedWorld{UINT32_MAX, 0};
thread_local bool InsideGameThreadPump{};

bool safeGameThreadCallback(
    const std::shared_ptr<GameThreadRegistration>& registration,
    const BriefcaseGameThreadFrame* frame) noexcept {
    __try {
        registration->Callback(registration->UserContext, frame);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

const UObject* worldFor(const UObject* object) {
    if (!GameThreadWorldClass)
        GameThreadWorldClass = findObjectByPath(L"/Script/Engine.World");
    if (!GameThreadWorldClass) return nullptr;
    auto* current = object;
    for (unsigned depth = 0; current && depth < 64; ++depth) {
        if (!readable(current, sizeof(UObject))) return nullptr;
        if (objectIsA(current, GameThreadWorldClass)) return current;
        current = current->OuterPrivate;
    }
    return nullptr;
}

void pumpGameThread(UObject* eventObject) {
    if (InsideGameThreadPump ||
        GetCurrentThreadId() != CapturedGameThreadId.load(std::memory_order_acquire)) return;

    // ProcessEvent is one of Unreal's hottest paths. Perform only atomic
    // checks and the clock throttle until a pulse is actually due.
    if (GameThreadRegistrationCount.load(std::memory_order_relaxed) == 0) return;

    LARGE_INTEGER counter{};
    if (!QueryPerformanceCounter(&counter)) return;
    auto frequency = GameThreadPerformanceFrequency.load(std::memory_order_acquire);
    if (frequency == 0) {
        LARGE_INTEGER queried{};
        if (!QueryPerformanceFrequency(&queried) || queried.QuadPart <= 0) return;
        frequency = queried.QuadPart;
        GameThreadPerformanceFrequency.store(frequency, std::memory_order_release);
    }
    auto previous = LastGameThreadPumpCounter.load(std::memory_order_relaxed);
    const auto minimumInterval = std::max<LONGLONG>(1, frequency / 120);
    const bool requested = GameThreadPumpRequested.exchange(false, std::memory_order_acq_rel);
    if (!requested && previous != 0 && counter.QuadPart - previous < minimumInterval) return;
    if (!LastGameThreadPumpCounter.compare_exchange_strong(
            previous, counter.QuadPart,
            std::memory_order_acq_rel, std::memory_order_relaxed)) return;

    std::vector<std::shared_ptr<GameThreadRegistration>> registrations;
    {
        const std::scoped_lock lock(GameThreadStateMutex);
        if (GameThreadRegistrations.empty()) return;
        registrations = GameThreadRegistrations;
    }

    if (const auto* world = worldFor(eventObject)) {
        BriefcaseObjectHandle handle{};
        if (makeHandle(world, handle)) LastObservedWorld = handle;
    } else if (!resolveObject(LastObservedWorld)) {
        LastObservedWorld = {UINT32_MAX, 0};
    }

    const float delta = previous == 0 ? 0.0f : static_cast<float>(
        static_cast<double>(counter.QuadPart - previous) /
        static_cast<double>(frequency));
    BriefcaseGameThreadFrame frame{
        sizeof(BriefcaseGameThreadFrame), GetCurrentThreadId(),
        GameThreadSequence.fetch_add(1, std::memory_order_relaxed) + 1,
        delta, 0, LastObservedWorld, {}};

    InsideGameThreadPump = true;
    for (const auto& registration : registrations) {
        const std::scoped_lock invocationLock(registration->InvocationMutex);
        if (!registration->Active) continue;
        if (!safeGameThreadCallback(registration, &frame))
            briefcase::log(L"game thread: managed callback raised a native exception");
    }
    InsideGameThreadPump = false;
}

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

    pumpGameThread(object);

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
    // Reserved0 remains opaque to mods. Native copy/write helpers use it only
    // during this callback to recover the reflected FProperty safely.
    call.Reserved[0] = reinterpret_cast<std::uint64_t>(function);
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

constexpr std::uint32_t ValueWireMagic = 0x31435642u; // "BVC1"
constexpr std::size_t MaximumValueWireBytes = 32u * 1024u * 1024u;
constexpr std::int32_t MaximumContainerElements = 100'000;
constexpr unsigned MaximumValueDepth = 8;

struct ValueWireBuilder {
    std::vector<std::byte> Bytes;

    bool append(const void* source, std::size_t size) {
        if (size > MaximumValueWireBytes || Bytes.size() > MaximumValueWireBytes - size)
            return false;
        const auto start = Bytes.size();
        Bytes.resize(start + size);
        return size == 0 || safeCopy(Bytes.data() + start, source, size);
    }

    template <typename TValue>
    bool append(const TValue& value) { return append(&value, sizeof(value)); }

    std::size_t beginNode(BriefcasePropertyKind kind) {
        const auto start = Bytes.size();
        const auto numericKind = static_cast<std::uint32_t>(kind);
        const std::uint32_t payloadSize = 0;
        return append(numericKind) && append(payloadSize) ? start : SIZE_MAX;
    }

    bool endNode(std::size_t start) {
        if (start == SIZE_MAX || start > Bytes.size() || Bytes.size() - start < 8)
            return false;
        const auto payload = Bytes.size() - start - 8;
        if (payload > std::numeric_limits<std::uint32_t>::max()) return false;
        const auto size = static_cast<std::uint32_t>(payload);
        std::memcpy(Bytes.data() + start + 4, &size, sizeof(size));
        return true;
    }
};

bool reflectedTypeName(const FProperty* property, std::wstring& result) {
    if (!property || !readable(property, sizeof(FProperty)) ||
        !readable(property->ClassPrivate, sizeof(FFieldClass))) return false;
    result = nameToString(
        reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
        RuntimeNameConverter);
    return !result.empty();
}

bool appendWideString(ValueWireBuilder& output, const std::wstring& value) {
    if (value.size() > static_cast<std::size_t>(MaximumContainerElements) ||
        value.size() > std::numeric_limits<std::uint32_t>::max()) return false;
    const auto count = static_cast<std::uint32_t>(value.size());
    return output.append(count) &&
           output.append(value.data(), value.size() * sizeof(wchar_t));
}

bool appendStringBuffer(ValueWireBuilder& output, const FStringBuffer& value) {
    if (value.Num < 0 || value.Max < value.Num ||
        value.Num > MaximumContainerElements) return false;
    const auto count = static_cast<std::uint32_t>(value.Num > 0 ? value.Num - 1 : 0);
    return output.append(count) &&
           (count == 0 || (value.Data && output.append(
               value.Data, static_cast<std::size_t>(count) * sizeof(wchar_t))));
}

bool appendWeakHandle(ValueWireBuilder& output, const FWeakObjectPtr& weak) {
    BriefcaseObjectHandle handle{
        std::numeric_limits<std::uint32_t>::max(), 0};
    if (weak.ObjectIndex >= 0) {
        BriefcaseObjectHandle candidate{
            static_cast<std::uint32_t>(weak.ObjectIndex),
            static_cast<std::uint32_t>(weak.ObjectSerialNumber)};
        if (resolveObject(candidate)) handle = candidate;
    }
    return output.append(handle);
}

bool copyCanonicalFixedValue(
    const FProperty* property, const std::byte* source,
    std::byte* destination, std::size_t destinationSize, unsigned depth);

bool copyCanonicalStruct(
    const UStruct* structure, const std::byte* source,
    std::byte* destination, std::size_t destinationSize, unsigned depth) {
    if (!structure || depth > MaximumValueDepth ||
        !readable(structure, sizeof(UStruct))) return false;
    for (auto* current = structure; current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return false;
        auto* field = current->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return false;
            const auto* property = reinterpret_cast<const FProperty*>(field);
            if (property->ArrayDim != 1 || property->OffsetInternal < 0 ||
                property->ElementSize <= 0 ||
                static_cast<std::size_t>(property->OffsetInternal) > destinationSize ||
                static_cast<std::size_t>(property->ElementSize) >
                    destinationSize - static_cast<std::size_t>(property->OffsetInternal)) {
                field = field->Next;
                continue;
            }
            if (!copyCanonicalFixedValue(
                    property, source + property->OffsetInternal,
                    destination + property->OffsetInternal,
                    static_cast<std::size_t>(property->ElementSize), depth + 1))
                return false;
            field = field->Next;
        }
    }
    return true;
}

bool copyCanonicalFixedValue(
    const FProperty* property, const std::byte* source,
    std::byte* destination, std::size_t destinationSize, unsigned depth) {
    if (!property || !source || !destination || depth > MaximumValueDepth) return false;
    std::wstring type;
    if (!reflectedTypeName(property, type)) return false;
    if (type == L"BoolProperty") {
        if (destinationSize < 1 || !readable(property, sizeof(FBoolProperty))) return false;
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        std::uint8_t storage{};
        if (boolean->ByteOffset >= static_cast<std::uint8_t>(property->ElementSize) ||
            !safeCopy(&storage, source + boolean->ByteOffset, sizeof(storage))) return false;
        destination[0] = static_cast<std::byte>((storage & boolean->ByteMask) != 0 ? 1 : 0);
        return true;
    }
    if (type == L"ObjectProperty" || type == L"ClassProperty") {
        if (destinationSize != sizeof(BriefcaseObjectHandle)) return false;
        UObject* object{};
        BriefcaseObjectHandle handle{};
        return safeCopy(&object, source, sizeof(object)) && makeHandle(object, handle) &&
               safeCopy(destination, &handle, sizeof(handle));
    }
    if (type == L"WeakObjectProperty") {
        if (destinationSize != sizeof(BriefcaseObjectHandle)) return false;
        BriefcaseObjectHandle weak{};
        if (!safeCopy(&weak, source, sizeof(weak))) return false;
        if (weak.Index != std::numeric_limits<std::uint32_t>::max() && !resolveObject(weak))
            weak = {std::numeric_limits<std::uint32_t>::max(), 0};
        return safeCopy(destination, &weak, sizeof(weak));
    }
    if (type == L"StructProperty") {
        if (!readable(property, sizeof(FStructProperty))) return false;
        const auto* structure = reinterpret_cast<const FStructProperty*>(property)->Struct;
        return copyCanonicalStruct(
            structure, source, destination, destinationSize, depth + 1);
    }
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_UNKNOWN || kind == BRIEFCASE_PROPERTY_STRING ||
        kind == BRIEFCASE_PROPERTY_TEXT || kind == BRIEFCASE_PROPERTY_ARRAY ||
        kind == BRIEFCASE_PROPERTY_SET || kind == BRIEFCASE_PROPERTY_MAP ||
        kind == BRIEFCASE_PROPERTY_INTERFACE ||
        kind == BRIEFCASE_PROPERTY_LAZY_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_CLASS ||
        kind == BRIEFCASE_PROPERTY_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_MULTICAST_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_FIELD_PATH)
        return true; // Owning fields remain zero in the address-free struct image.
    return destinationSize == static_cast<std::size_t>(property->ElementSize) &&
           safeCopy(destination, source, destinationSize);
}

bool appendValueNode(
    const FProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth);

bool validScriptArray(const FScriptArray& value, std::int32_t elementSize) {
    if (value.Num < 0 || value.Max < value.Num || value.Num > MaximumContainerElements ||
        elementSize <= 0) return false;
    if (value.Num == 0) return true;
    const auto bytes = static_cast<std::uint64_t>(value.Num) *
                       static_cast<std::uint64_t>(elementSize);
    return bytes <= MaximumValueWireBytes && value.Data &&
           readable(value.Data, static_cast<std::size_t>(bytes));
}

const std::uint32_t* bitArrayData(const FScriptBitArray& flags) {
    return flags.Allocator.SecondaryData
        ? flags.Allocator.SecondaryData
        : flags.Allocator.InlineData;
}

bool appendArrayNode(
    const FArrayProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !readable(property, sizeof(FArrayProperty)) ||
        !readable(property->Inner, sizeof(FProperty))) return false;
    FScriptArray value{};
    if (!safeCopy(&value, source, sizeof(value)) ||
        !validScriptArray(value, property->Inner->ElementSize)) return false;
    const auto start = output.beginNode(BRIEFCASE_PROPERTY_ARRAY);
    const auto count = static_cast<std::uint32_t>(value.Num);
    if (start == SIZE_MAX || !output.append(count)) return false;
    const auto* data = reinterpret_cast<const std::byte*>(value.Data);
    for (std::int32_t index = 0; index < value.Num; ++index) {
        if (!appendValueNode(
                property->Inner,
                data + static_cast<std::size_t>(index) * property->Inner->ElementSize,
                output, depth + 1)) return false;
    }
    return output.endNode(start);
}

bool validSparseSet(
    const FScriptSet& set, const FScriptSetLayout& layout,
    std::int32_t& validCount, const std::uint32_t*& flags) {
    const auto& elements = set.Elements;
    if (layout.Size <= 0 || layout.Size > 65'535 ||
        layout.SparseArrayLayout.Size != layout.Size ||
        elements.Data.Num < 0 || elements.Data.Max < elements.Data.Num ||
        elements.Data.Num > MaximumContainerElements ||
        elements.NumFreeIndices < 0 || elements.NumFreeIndices > elements.Data.Num ||
        elements.AllocationFlags.NumBits < elements.Data.Num ||
        elements.AllocationFlags.MaxBits < elements.AllocationFlags.NumBits)
        return false;
    validCount = elements.Data.Num - elements.NumFreeIndices;
    if (!validScriptArray(elements.Data, layout.Size)) return false;
    flags = bitArrayData(elements.AllocationFlags);
    const auto words = (static_cast<std::size_t>(elements.Data.Num) + 31) / 32;
    return words == 0 || (flags && readable(flags, words * sizeof(std::uint32_t)));
}

bool appendSetNode(
    const FSetProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !readable(property, sizeof(FSetProperty)) ||
        !readable(property->ElementProperty, sizeof(FProperty))) return false;
    FScriptSet set{};
    if (!safeCopy(&set, source, sizeof(set))) return false;
    std::int32_t count{};
    const std::uint32_t* flags{};
    if (!validSparseSet(set, property->SetLayout, count, flags)) return false;
    const auto start = output.beginNode(BRIEFCASE_PROPERTY_SET);
    const auto encodedCount = static_cast<std::uint32_t>(count);
    if (start == SIZE_MAX || !output.append(encodedCount)) return false;
    const auto* data = reinterpret_cast<const std::byte*>(set.Elements.Data.Data);
    for (std::int32_t index = 0; index < set.Elements.Data.Num; ++index) {
        if ((flags[index / 32] & (1u << (index & 31))) == 0) continue;
        if (!appendValueNode(
                property->ElementProperty,
                data + static_cast<std::size_t>(index) * property->SetLayout.Size,
                output, depth + 1)) return false;
    }
    return output.endNode(start);
}

bool appendMapNode(
    const FMapProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !readable(property, sizeof(FMapProperty)) ||
        !readable(property->KeyProperty, sizeof(FProperty)) ||
        !readable(property->ValueProperty, sizeof(FProperty)) ||
        property->MapLayout.ValueOffset < 0 ||
        property->MapLayout.ValueOffset > property->MapLayout.SetLayout.Size -
            property->ValueProperty->ElementSize)
        return false;
    FScriptSet set{};
    if (!safeCopy(&set, source, sizeof(set))) return false;
    std::int32_t count{};
    const std::uint32_t* flags{};
    if (!validSparseSet(set, property->MapLayout.SetLayout, count, flags)) return false;
    const auto start = output.beginNode(BRIEFCASE_PROPERTY_MAP);
    const auto encodedCount = static_cast<std::uint32_t>(count);
    if (start == SIZE_MAX || !output.append(encodedCount)) return false;
    const auto* data = reinterpret_cast<const std::byte*>(set.Elements.Data.Data);
    for (std::int32_t index = 0; index < set.Elements.Data.Num; ++index) {
        if ((flags[index / 32] & (1u << (index & 31))) == 0) continue;
        const auto* pair = data + static_cast<std::size_t>(index) *
                                   property->MapLayout.SetLayout.Size;
        if (!appendValueNode(property->KeyProperty, pair, output, depth + 1) ||
            !appendValueNode(
                property->ValueProperty, pair + property->MapLayout.ValueOffset,
                output, depth + 1)) return false;
    }
    return output.endNode(start);
}

bool appendValueNode(
    const FProperty* property, const std::byte* source,
    ValueWireBuilder& output, unsigned depth) {
    if (!property || !source || depth > MaximumValueDepth ||
        !readable(property, sizeof(FProperty))) return false;
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_ARRAY)
        return appendArrayNode(reinterpret_cast<const FArrayProperty*>(property), source, output, depth);
    if (kind == BRIEFCASE_PROPERTY_SET)
        return appendSetNode(reinterpret_cast<const FSetProperty*>(property), source, output, depth);
    if (kind == BRIEFCASE_PROPERTY_MAP)
        return appendMapNode(reinterpret_cast<const FMapProperty*>(property), source, output, depth);

    const auto start = output.beginNode(kind);
    if (start == SIZE_MAX || kind == BRIEFCASE_PROPERTY_UNKNOWN) return false;
    if (kind == BRIEFCASE_PROPERTY_STRING) {
        FStringBuffer value{};
        if (!safeCopy(&value, source, sizeof(value)) || value.Num < 0 ||
            value.Max < value.Num || value.Num > MaximumContainerElements) return false;
        auto count = value.Num > 0 ? value.Num - 1 : 0;
        if (!output.append(static_cast<std::uint32_t>(count))) return false;
        if (count && (!value.Data ||
            !output.append(value.Data, static_cast<std::size_t>(count) * sizeof(std::uint16_t))))
            return false;
    } else if (kind == BRIEFCASE_PROPERTY_TEXT) {
        TextConversionRuntime runtime{};
        if (!resolveTextConversionRuntime(runtime)) return false;
        std::uint32_t required{};
        auto status = textToUtf16(runtime, source, nullptr, 0, &required);
        if (status == BRIEFCASE_UNREAL_OK && required == 0) {
            if (!output.append(required)) return false;
        } else {
            if (status != BRIEFCASE_UNREAL_BUFFER_TOO_SMALL || required > 65'536) return false;
            std::vector<std::uint16_t> text(required);
            status = textToUtf16(runtime, source, text.data(), required, &required);
            if (status != BRIEFCASE_UNREAL_OK) return false;
            auto count = required > 0 && text[required - 1] == 0 ? required - 1 : required;
            if (!output.append(count) ||
                !output.append(text.data(), static_cast<std::size_t>(count) * sizeof(std::uint16_t)))
                return false;
        }
    } else if (kind == BRIEFCASE_PROPERTY_BOOL) {
        std::byte value{};
        if (!copyCanonicalFixedValue(property, source, &value, 1, depth) ||
            !output.append(value)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_OBJECT) {
        BriefcaseObjectHandle handle{};
        if (!copyCanonicalFixedValue(property, source,
                reinterpret_cast<std::byte*>(&handle), sizeof(handle), depth) ||
             !output.append(handle)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_INTERFACE) {
        FScriptInterface value{};
        BriefcaseObjectHandle handle{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !makeHandle(value.ObjectPointer, handle) || !output.append(handle)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_LAZY_OBJECT) {
        FLazyObjectPtrView value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !appendWeakHandle(output, value.WeakObject) ||
            !output.append(value.Guid, sizeof(value.Guid))) return false;
    } else if (kind == BRIEFCASE_PROPERTY_SOFT_OBJECT ||
               kind == BRIEFCASE_PROPERTY_SOFT_CLASS) {
        FSoftObjectPtrView value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !appendWeakHandle(output, value.WeakObject) ||
            !appendWideString(output, nameToString(value.AssetPathName, RuntimeNameConverter)) ||
            !appendStringBuffer(output, value.SubPathString)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_DELEGATE) {
        FScriptDelegate value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !appendWeakHandle(output, value.Object) ||
            !output.append(value.FunctionName)) return false;
    } else if (kind == BRIEFCASE_PROPERTY_MULTICAST_DELEGATE) {
        FScriptArray value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !validScriptArray(value, sizeof(FScriptDelegate))) return false;
        const auto count = static_cast<std::uint32_t>(value.Num);
        if (!output.append(count)) return false;
        const auto* bindings = reinterpret_cast<const FScriptDelegate*>(value.Data);
        for (std::int32_t index = 0; index < value.Num; ++index) {
            FScriptDelegate binding{};
            if (!safeCopy(&binding, bindings + index, sizeof(binding)) ||
                !appendWeakHandle(output, binding.Object) ||
                !output.append(binding.FunctionName)) return false;
        }
    } else if (kind == BRIEFCASE_PROPERTY_FIELD_PATH) {
        FFieldPathView value{};
        if (property->ElementSize != sizeof(value) ||
            !safeCopy(&value, source, sizeof(value)) ||
            !validScriptArray(value.Path, sizeof(FName))) return false;
        const auto count = static_cast<std::uint32_t>(value.Path.Num);
        if (!output.append(count) ||
            (count != 0 && !output.append(
                value.Path.Data, static_cast<std::size_t>(count) * sizeof(FName))))
            return false;
    } else if (kind == BRIEFCASE_PROPERTY_STRUCT) {
        const auto size = static_cast<std::uint32_t>(property->ElementSize);
        std::vector<std::byte> canonical(size);
        if (!copyCanonicalFixedValue(property, source, canonical.data(), canonical.size(), depth) ||
            !output.append(size) || !output.append(canonical.data(), canonical.size())) return false;
    } else {
        if (property->ElementSize <= 0 ||
            !output.append(source, static_cast<std::size_t>(property->ElementSize))) return false;
    }
    return output.endNode(start);
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

std::wstring propertyTypeName(const FProperty* property) {
    if (!property || !readable(property->ClassPrivate, sizeof(FFieldClass)))
        return L"UnknownProperty";
    return nameToString(
        reinterpret_cast<const FFieldClass*>(property->ClassPrivate)->Name,
        RuntimeNameConverter);
}

void writeTypeReference(std::ostream& output, const UObject* object) {
    if (!isRegisteredObject(object)) return;
    const auto path = objectPath(object, RuntimeNameConverter);
    if (path.empty()) return;
    output << ",\"referencedTypePath\":";
    writeJsonString(output, path);
}

void writePropertyTypeSnapshot(
    std::ostream& output,
    const FProperty* property,
    unsigned depth = 0) {
    constexpr unsigned MaximumTypeDepth = 8;
    const auto typeName = propertyTypeName(property);
    output << "{\"unrealType\":";
    writeJsonString(output, typeName);
    output << ",\"elementSize\":" << (property ? property->ElementSize : 0);
    if (!property || depth >= MaximumTypeDepth) {
        output << '}';
        return;
    }

    if (typeName == L"BoolProperty" && readable(property, sizeof(FBoolProperty))) {
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        output << ",\"booleanLayout\":{\"fieldSize\":"
               << static_cast<unsigned>(boolean->FieldSize)
               << ",\"byteOffset\":" << static_cast<unsigned>(boolean->ByteOffset)
               << ",\"byteMask\":" << static_cast<unsigned>(boolean->ByteMask)
               << ",\"fieldMask\":" << static_cast<unsigned>(boolean->FieldMask)
               << '}';
    } else if (typeName == L"StructProperty" &&
               readable(property, sizeof(FStructProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FStructProperty*>(property)->Struct);
    } else if ((typeName == L"ObjectProperty" || typeName == L"WeakObjectProperty" ||
                typeName == L"LazyObjectProperty" || typeName == L"SoftObjectProperty") &&
               readable(property, sizeof(FObjectPropertyBase))) {
        writeTypeReference(
            output, reinterpret_cast<const FObjectPropertyBase*>(property)->PropertyClass);
    } else if ((typeName == L"ClassProperty" || typeName == L"SoftClassProperty") &&
               readable(property, sizeof(FClassProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FClassProperty*>(property)->MetaClass);
    } else if (typeName == L"InterfaceProperty" &&
               readable(property, sizeof(FInterfaceProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FInterfaceProperty*>(property)->InterfaceClass);
    } else if (typeName == L"EnumProperty" && readable(property, sizeof(FEnumProperty))) {
        const auto* enumeration = reinterpret_cast<const FEnumProperty*>(property);
        writeTypeReference(output, enumeration->Enum);
        if (enumeration->UnderlyingProperty &&
            readable(enumeration->UnderlyingProperty, sizeof(FProperty))) {
            output << ",\"underlyingType\":";
            writePropertyTypeSnapshot(output, enumeration->UnderlyingProperty, depth + 1);
        }
    } else if (typeName == L"ByteProperty" && readable(property, sizeof(FByteProperty))) {
        writeTypeReference(output, reinterpret_cast<const FByteProperty*>(property)->Enum);
    } else if ((typeName == L"DelegateProperty" ||
                typeName == L"MulticastDelegateProperty" ||
                typeName == L"MulticastInlineDelegateProperty" ||
                typeName == L"MulticastSparseDelegateProperty") &&
               readable(property, sizeof(FDelegateProperty))) {
        writeTypeReference(
            output, reinterpret_cast<const FDelegateProperty*>(property)->SignatureFunction);
    } else if (typeName == L"FieldPathProperty" &&
               readable(property, sizeof(FFieldPathProperty))) {
        const auto* fieldClass =
            reinterpret_cast<const FFieldPathProperty*>(property)->PropertyClass;
        if (fieldClass && readable(fieldClass, sizeof(FFieldClass))) {
            output << ",\"referencedTypePath\":";
            writeJsonString(output, L"FFieldClass:" +
                nameToString(fieldClass->Name, RuntimeNameConverter));
        }
    }

    const auto writeNested = [&](const char* name, const FProperty* nested) {
        if (!nested || !readable(nested, sizeof(FProperty))) return;
        output << ",\"" << name << "\":";
        writePropertyTypeSnapshot(output, nested, depth + 1);
    };
    if (typeName == L"ArrayProperty" && readable(property, sizeof(FArrayProperty))) {
        writeNested("innerType", reinterpret_cast<const FArrayProperty*>(property)->Inner);
    } else if (typeName == L"SetProperty" && readable(property, sizeof(FSetProperty))) {
        writeNested(
            "innerType", reinterpret_cast<const FSetProperty*>(property)->ElementProperty);
    } else if (typeName == L"MapProperty" && readable(property, sizeof(FMapProperty))) {
        const auto* map = reinterpret_cast<const FMapProperty*>(property);
        writeNested("keyType", map->KeyProperty);
        writeNested("valueType", map->ValueProperty);
    }
    output << '}';
}

void writePropertySnapshot(std::ostream& output, const FProperty* property) {
    const auto typeName = propertyTypeName(property);
    output << "{\"name\":";
    writeJsonString(output, nameToString(property->NamePrivate, RuntimeNameConverter));
    output << ",\"unrealType\":";
    writeJsonString(output, typeName);
    output << ",\"offset\":" << property->OffsetInternal
           << ",\"elementSize\":" << property->ElementSize
           << ",\"arrayDimension\":" << property->ArrayDim
           << ",\"flags\":" << property->PropertyFlags
           << ",\"type\":";
    writePropertyTypeSnapshot(output, property);
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

void writeEnumValues(std::ostream& output, const UEnum* enumeration) {
    if (!enumeration || !readable(enumeration, sizeof(UEnum)) ||
        enumeration->NamesNum < 0 || enumeration->NamesMax < enumeration->NamesNum ||
        enumeration->NamesNum > 65'536 ||
        (enumeration->NamesNum > 0 &&
         (!enumeration->Names ||
          !readable(enumeration->Names,
              static_cast<std::size_t>(enumeration->NamesNum) * sizeof(FEnumNameValue)))))
        return;

    for (std::int32_t index = 0; index < enumeration->NamesNum; ++index) {
        if (index != 0) output.put(',');
        output << "{\"name\":";
        writeJsonString(
            output, nameToString(enumeration->Names[index].Name, RuntimeNameConverter));
        output << ",\"value\":" << enumeration->Names[index].Value << '}';
    }
}
struct ReflectedType {
    const UObject* Object;
    std::wstring Path;
    std::wstring Kind;
};

enum class SdkSnapshotFormat {
    Binary,
    Json
};

SdkSnapshotFormat configuredSnapshotFormat(const std::filesystem::path& root) {
    constexpr std::uintmax_t MaximumConfigurationBytes = 1024 * 1024;
    const auto path = root / L"Briefcase" / L"loader.json";
    std::error_code error;
    const auto size = std::filesystem::file_size(path, error);
    if (error || size > MaximumConfigurationBytes) return SdkSnapshotFormat::Binary;

    std::ifstream input(path, std::ios::binary);
    if (!input) return SdkSnapshotFormat::Binary;
    std::string contents(static_cast<std::size_t>(size), '\0');
    input.read(contents.data(), static_cast<std::streamsize>(contents.size()));
    if (!input && !input.eof()) return SdkSnapshotFormat::Binary;

    constexpr std::string_view Key = "\"sdkSnapshotFormat\"";
    auto position = contents.find(Key);
    if (position == std::string::npos) return SdkSnapshotFormat::Binary;
    position = contents.find(':', position + Key.size());
    if (position == std::string::npos) return SdkSnapshotFormat::Binary;
    position = contents.find('"', position + 1);
    if (position == std::string::npos) return SdkSnapshotFormat::Binary;
    const auto end = contents.find('"', position + 1);
    if (end == std::string::npos) return SdkSnapshotFormat::Binary;

    auto value = contents.substr(position + 1, end - position - 1);
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    if (value == "json") return SdkSnapshotFormat::Json;
    if (value != "binary")
        briefcase::log(L"sdk snapshot: unknown sdkSnapshotFormat; using binary");
    return SdkSnapshotFormat::Binary;
}

std::vector<const FProperty*> snapshotProperties(
    const FField* first, bool parametersOnly) {
    constexpr std::uint64_t ParameterFlag = 0x80;
    std::vector<const FProperty*> properties;
    auto* field = first;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) break;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if (!parametersOnly || (property->PropertyFlags & ParameterFlag) != 0)
            properties.push_back(property);
        field = field->Next;
    }
    return properties;
}

std::vector<const UFunction*> snapshotFunctions(const UField* first) {
    std::vector<const UFunction*> functions;
    auto* child = first;
    for (unsigned visited = 0; child && visited < 4096; ++visited) {
        if (!readable(child, sizeof(UField)) ||
            !isRegisteredObject(child->ClassPrivate))
            break;
        if (nameToString(child->ClassPrivate->NamePrivate, RuntimeNameConverter) == L"Function" &&
            readable(child, sizeof(UFunction)))
            functions.push_back(reinterpret_cast<const UFunction*>(child));
        child = child->Next;
    }
    return functions;
}

void writeBinaryString(
    briefcase::snapshot::BinarySnapshotWriter& output, std::wstring_view value) {
    output.writeString(utf8(value));
}

void writeBinaryNullableString(
    briefcase::snapshot::BinarySnapshotWriter& output, std::wstring_view value) {
    output.writeBoolean(!value.empty());
    if (!value.empty()) writeBinaryString(output, value);
}

void writeBinaryPropertyType(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const FProperty* property,
    unsigned depth = 0) {
    constexpr unsigned MaximumTypeDepth = 8;
    const auto typeName = propertyTypeName(property);
    writeBinaryString(output, typeName);
    output.writeInt32(property ? property->ElementSize : 0);

    std::wstring referencedTypePath;
    const FProperty* innerType{};
    const FProperty* keyType{};
    const FProperty* valueType{};
    const FProperty* underlyingType{};
    const FBoolProperty* booleanLayout{};

    if (property && depth < MaximumTypeDepth) {
        const auto reference = [&](const UObject* object) {
            return isRegisteredObject(object)
                ? objectPath(object, RuntimeNameConverter)
                : std::wstring{};
        };
        const auto nested = [](const FProperty* candidate) {
            return candidate && readable(candidate, sizeof(FProperty)) ? candidate : nullptr;
        };

        if (typeName == L"BoolProperty" && readable(property, sizeof(FBoolProperty))) {
            booleanLayout = reinterpret_cast<const FBoolProperty*>(property);
        } else if (typeName == L"StructProperty" &&
                   readable(property, sizeof(FStructProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FStructProperty*>(property)->Struct);
        } else if ((typeName == L"ObjectProperty" || typeName == L"WeakObjectProperty" ||
                    typeName == L"LazyObjectProperty" || typeName == L"SoftObjectProperty") &&
                   readable(property, sizeof(FObjectPropertyBase))) {
            referencedTypePath = reference(
                reinterpret_cast<const FObjectPropertyBase*>(property)->PropertyClass);
        } else if ((typeName == L"ClassProperty" || typeName == L"SoftClassProperty") &&
                   readable(property, sizeof(FClassProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FClassProperty*>(property)->MetaClass);
        } else if (typeName == L"InterfaceProperty" &&
                   readable(property, sizeof(FInterfaceProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FInterfaceProperty*>(property)->InterfaceClass);
        } else if (typeName == L"EnumProperty" &&
                   readable(property, sizeof(FEnumProperty))) {
            const auto* enumeration = reinterpret_cast<const FEnumProperty*>(property);
            referencedTypePath = reference(enumeration->Enum);
            underlyingType = nested(enumeration->UnderlyingProperty);
        } else if (typeName == L"ByteProperty" &&
                   readable(property, sizeof(FByteProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FByteProperty*>(property)->Enum);
        } else if ((typeName == L"DelegateProperty" ||
                    typeName == L"MulticastDelegateProperty" ||
                    typeName == L"MulticastInlineDelegateProperty" ||
                    typeName == L"MulticastSparseDelegateProperty") &&
                   readable(property, sizeof(FDelegateProperty))) {
            referencedTypePath = reference(
                reinterpret_cast<const FDelegateProperty*>(property)->SignatureFunction);
        } else if (typeName == L"FieldPathProperty" &&
                   readable(property, sizeof(FFieldPathProperty))) {
            const auto* fieldClass =
                reinterpret_cast<const FFieldPathProperty*>(property)->PropertyClass;
            if (fieldClass && readable(fieldClass, sizeof(FFieldClass))) {
                const auto name = nameToString(fieldClass->Name, RuntimeNameConverter);
                if (!name.empty()) referencedTypePath = L"FFieldClass:" + name;
            }
        }

        if (typeName == L"ArrayProperty" && readable(property, sizeof(FArrayProperty))) {
            innerType = nested(reinterpret_cast<const FArrayProperty*>(property)->Inner);
        } else if (typeName == L"SetProperty" &&
                   readable(property, sizeof(FSetProperty))) {
            innerType = nested(
                reinterpret_cast<const FSetProperty*>(property)->ElementProperty);
        } else if (typeName == L"MapProperty" &&
                   readable(property, sizeof(FMapProperty))) {
            const auto* map = reinterpret_cast<const FMapProperty*>(property);
            keyType = nested(map->KeyProperty);
            valueType = nested(map->ValueProperty);
        }
    }

    writeBinaryNullableString(output, referencedTypePath);
    const auto writeNested = [&](const FProperty* nested) {
        output.writeBoolean(nested != nullptr);
        if (nested) writeBinaryPropertyType(output, nested, depth + 1);
    };
    writeNested(innerType);
    writeNested(keyType);
    writeNested(valueType);
    writeNested(underlyingType);
    output.writeBoolean(booleanLayout != nullptr);
    if (booleanLayout) {
        output.writeByte(booleanLayout->FieldSize);
        output.writeByte(booleanLayout->ByteOffset);
        output.writeByte(booleanLayout->ByteMask);
        output.writeByte(booleanLayout->FieldMask);
    }
}

void writeBinaryProperty(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const FProperty* property) {
    writeBinaryString(output, nameToString(property->NamePrivate, RuntimeNameConverter));
    writeBinaryString(output, propertyTypeName(property));
    output.writeInt32(property->OffsetInternal);
    output.writeInt32(property->ElementSize);
    output.writeInt32(property->ArrayDim);
    output.writeUInt64(property->PropertyFlags);
    // Legacy schema-2 fields are absent from schema-3 snapshots.
    output.writeBoolean(false);
    output.writeBoolean(false);
    output.writeBoolean(true);
    writeBinaryPropertyType(output, property);
}

void writeBinaryProperties(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const FField* first,
    bool parametersOnly) {
    const auto properties = snapshotProperties(first, parametersOnly);
    output.writeInt32(static_cast<std::int32_t>(properties.size()));
    for (const auto* property : properties) writeBinaryProperty(output, property);
}

void writeBinaryFunctions(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const UField* first) {
    const auto functions = snapshotFunctions(first);
    output.writeInt32(static_cast<std::int32_t>(functions.size()));
    for (const auto* function : functions) {
        writeBinaryString(
            output, nameToString(function->NamePrivate, RuntimeNameConverter));
        output.writeUInt32(function->FunctionFlags);
        output.writeInt32(function->ParmsSize);
        output.writeInt32(function->NumParms);
        writeBinaryProperties(output, function->ChildProperties, true);
    }
}

void writeBinaryEnumValues(
    briefcase::snapshot::BinarySnapshotWriter& output,
    const UEnum* enumeration) {
    if (!enumeration || !readable(enumeration, sizeof(UEnum)) ||
        enumeration->NamesNum < 0 || enumeration->NamesMax < enumeration->NamesNum ||
        enumeration->NamesNum > 65'536 ||
        (enumeration->NamesNum > 0 &&
         (!enumeration->Names ||
          !readable(enumeration->Names,
              static_cast<std::size_t>(enumeration->NamesNum) * sizeof(FEnumNameValue))))) {
        output.writeInt32(0);
        return;
    }
    output.writeInt32(enumeration->NamesNum);
    for (std::int32_t index = 0; index < enumeration->NamesNum; ++index) {
        writeBinaryString(
            output, nameToString(enumeration->Names[index].Name, RuntimeNameConverter));
        output.writeInt64(enumeration->Names[index].Value);
    }
}

void writeBinarySdkSnapshot(
    std::ostream& stream,
    const std::vector<ReflectedType>& types,
    std::int32_t objectCount,
    const briefcase::profile::RuntimeProfile& runtimeProfile) {
    briefcase::snapshot::BinarySnapshotWriter output(stream);
    output.writeHeader();
    output.writeInt32(3);
    writeBinaryString(output, runtimeProfile.TargetName);
    writeBinaryString(output, runtimeProfile.SdkAssemblyName);
    output.writeUInt32(runtimeProfile.PeTimestamp);
    output.writeUInt32(runtimeProfile.ImageSize);
    output.writeInt32(objectCount);
    output.writeInt32(static_cast<std::int32_t>(types.size()));

    for (const auto& type : types) {
        const bool enumeration = type.Kind == L"Enum";
        const auto* structure = reinterpret_cast<const UStruct*>(type.Object);
        const auto* enumObject = reinterpret_cast<const UEnum*>(type.Object);
        writeBinaryString(output, type.Path);
        writeBinaryString(
            output, nameToString(type.Object->NamePrivate, RuntimeNameConverter));
        writeBinaryString(output, type.Kind);

        std::wstring superPath;
        if (!enumeration && structure->SuperStruct)
            superPath = objectPath(structure->SuperStruct, RuntimeNameConverter);
        writeBinaryNullableString(output, superPath);
        output.writeInt32(enumeration ? 0 : structure->PropertiesSize);
        if (enumeration) {
            output.writeInt32(0);
            output.writeInt32(0);
            writeBinaryEnumValues(output, enumObject);
        } else {
            writeBinaryProperties(output, structure->ChildProperties, false);
            writeBinaryFunctions(output, structure->Children);
            output.writeInt32(0);
        }
    }
}

void writeSdkSnapshot(const std::filesystem::path& root,
                      const briefcase::profile::RuntimeProfile& runtimeProfile) {
    if (!RuntimeObjects || !RuntimeNameConverter) return;

    std::vector<ReflectedType> types;
    const auto objectCount = RuntimeObjects->ObjObjects.NumElements;
    types.reserve(4096);
    for (std::int32_t index = 0; index < objectCount; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        if (!item || !readable(item->Object, sizeof(UObject)) ||
            item->Object->InternalIndex != index ||
            !readable(item->Object->ClassPrivate, sizeof(UObject)))
            continue;
        const auto kind = nameToString(
            item->Object->ClassPrivate->NamePrivate, RuntimeNameConverter);
        if (kind != L"Class" && kind != L"ScriptStruct" && kind != L"Enum") continue;
        if ((kind == L"Enum" && !readable(item->Object, sizeof(UEnum))) ||
            (kind != L"Enum" && !readable(item->Object, sizeof(UStruct))))
            continue;
        auto path = objectPath(item->Object, RuntimeNameConverter);
        if (!path.starts_with(L"/Script/")) continue;
        types.push_back({item->Object, std::move(path), kind});
    }
    std::sort(types.begin(), types.end(), [](const auto& left, const auto& right) {
        return left.Path < right.Path;
    });

    const auto directory = root / L"Briefcase" / L"Core" / L"Sdk" / L"Metadata";
    std::filesystem::create_directories(directory);
    const auto format = configuredSnapshotFormat(root);
    std::wostringstream fileStem;
    fileStem << L"DeceiveInc." << runtimeProfile.TargetName << L'.'
             << std::uppercase << std::hex << std::setw(8) << std::setfill(L'0')
             << runtimeProfile.PeTimestamp << L'-' << std::setw(8)
             << runtimeProfile.ImageSize;
    const auto extension = format == SdkSnapshotFormat::Json
        ? std::wstring_view(L".json") : std::wstring_view(L".bsnap");
    const auto destination = directory / (fileStem.str() + extension.data());
    const auto temporary = destination.wstring() + L".tmp-" +
                           std::to_wstring(GetCurrentProcessId());

    std::ofstream output(temporary, std::ios::binary | std::ios::trunc);
    if (!output) {
        briefcase::log(L"sdk snapshot: unable to create " + temporary);
        return;
    }
    if (format == SdkSnapshotFormat::Binary) {
        writeBinarySdkSnapshot(output, types, objectCount, runtimeProfile);
    } else {
        output << "{\"schemaVersion\":3,\"target\":";
        writeJsonString(output, runtimeProfile.TargetName);
        output << ",\"sdkAssemblyName\":";
        writeJsonString(output, runtimeProfile.SdkAssemblyName);
        output << ",\"gameBuild\":{\"peTimestamp\":" << runtimeProfile.PeTimestamp
               << ",\"imageSize\":" << runtimeProfile.ImageSize
               << "},\"capturedObjectCount\":" << objectCount << ",\"types\":[";

        bool needsTypeComma = false;
        for (const auto& type : types) {
            const bool enumeration = type.Kind == L"Enum";
            const auto* structure = reinterpret_cast<const UStruct*>(type.Object);
            const auto* enumObject = reinterpret_cast<const UEnum*>(type.Object);
            if ((enumeration && !readable(enumObject, sizeof(UEnum))) ||
                (!enumeration && !readable(structure, sizeof(UStruct))))
                continue;
            if (needsTypeComma) output.put(',');
            output << "{\"path\":";
            writeJsonString(output, type.Path);
            output << ",\"name\":";
            writeJsonString(output, nameToString(type.Object->NamePrivate, RuntimeNameConverter));
            output << ",\"kind\":";
            writeJsonString(output, type.Kind);

            if (enumeration) {
                output << ",\"superPath\":null,\"size\":0,\"properties\":[],"
                          "\"functions\":[],\"values\":[";
                writeEnumValues(output, enumObject);
                output << "]}";
            } else {
                output << ",\"superPath\":";
                if (structure->SuperStruct && readable(structure->SuperStruct, sizeof(UStruct)))
                    writeJsonString(output, objectPath(structure->SuperStruct, RuntimeNameConverter));
                else
                    output << "null";
                output << ",\"size\":" << structure->PropertiesSize << ",\"properties\":[";
                writeProperties(output, structure->ChildProperties, false);
                output << "],\"functions\":[";
                writeFunctions(output, structure->Children);
                output << "],\"values\":[]}";
            }
            needsTypeComma = true;
        }
        output << "]}";
    }
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
    const auto alternativeExtension = format == SdkSnapshotFormat::Json
        ? std::wstring_view(L".bsnap") : std::wstring_view(L".json");
    const auto alternative = directory / (fileStem.str() + alternativeExtension.data());
    DeleteFileW(alternative.c_str());
    briefcase::log(L"sdk snapshot: wrote " + std::to_wstring(types.size()) +
             L" reflected types as " +
             (format == SdkSnapshotFormat::Json ? L"json" : L"binary") +
             L" to " + destination.wstring());
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
    case BRIEFCASE_PROPERTY_INT8:
    case BRIEFCASE_PROPERTY_BYTE:
    case BRIEFCASE_PROPERTY_BOOL: return 1;
    case BRIEFCASE_PROPERTY_INT16:
    case BRIEFCASE_PROPERTY_UINT16: return 2;
    case BRIEFCASE_PROPERTY_INT32:
    case BRIEFCASE_PROPERTY_UINT32:
    case BRIEFCASE_PROPERTY_FLOAT: return 4;
    case BRIEFCASE_PROPERTY_INT64:
    case BRIEFCASE_PROPERTY_UINT64:
    case BRIEFCASE_PROPERTY_DOUBLE:
    case BRIEFCASE_PROPERTY_OBJECT:
    case BRIEFCASE_PROPERTY_NAME: return 8;
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

bool writeCanonicalFixedValue(
    const FProperty* property, std::byte* destination,
    const std::byte* source, std::size_t sourceSize, unsigned depth);

bool writeCanonicalStruct(
    const UStruct* structure, std::byte* destination,
    const std::byte* source, std::size_t sourceSize, unsigned depth) {
    if (!structure || depth > MaximumValueDepth ||
        !readable(structure, sizeof(UStruct))) return false;
    for (auto* current = structure; current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return false;
        auto* field = current->ChildProperties;
        for (unsigned visited = 0; field && visited < 4096; ++visited) {
            if (!readable(field, sizeof(FProperty))) return false;
            const auto* property = reinterpret_cast<const FProperty*>(field);
            if (property->ArrayDim == 1 && property->OffsetInternal >= 0 &&
                property->ElementSize > 0 &&
                static_cast<std::size_t>(property->OffsetInternal) <= sourceSize &&
                static_cast<std::size_t>(property->ElementSize) <=
                    sourceSize - static_cast<std::size_t>(property->OffsetInternal) &&
                !writeCanonicalFixedValue(
                    property, destination + property->OffsetInternal,
                    source + property->OffsetInternal,
                    static_cast<std::size_t>(property->ElementSize), depth + 1))
                return false;
            field = field->Next;
        }
    }

    return true;
}

bool writeCanonicalFixedValue(
    const FProperty* property, std::byte* destination,
    const std::byte* source, std::size_t sourceSize, unsigned depth) {
    if (!property || !destination || !source || depth > MaximumValueDepth) return false;
    std::wstring type;
    if (!reflectedTypeName(property, type)) return false;
    if (type == L"BoolProperty") {
        if (sourceSize < 1 || !readable(property, sizeof(FBoolProperty))) return false;
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(property);
        std::uint8_t storage{};
        if (boolean->ByteOffset >= static_cast<std::uint8_t>(property->ElementSize) ||
            !safeCopy(&storage, destination + boolean->ByteOffset, 1)) return false;
        const bool enabled = source[0] != std::byte{};
        storage = enabled ? static_cast<std::uint8_t>(storage | boolean->ByteMask)
                          : static_cast<std::uint8_t>(storage & ~boolean->ByteMask);
        return safeCopy(destination + boolean->ByteOffset, &storage, 1);
    }
    if (type == L"ObjectProperty" || type == L"ClassProperty") {
        if (sourceSize != sizeof(BriefcaseObjectHandle)) return false;
        BriefcaseObjectHandle handle{};
        if (!safeCopy(&handle, source, sizeof(handle))) return false;
        UObject* object{};
        if (handle.Index != std::numeric_limits<std::uint32_t>::max()) {
            object = const_cast<UObject*>(resolveObject(handle));
            if (!object) return false;
        }
        return safeCopy(destination, &object, sizeof(object));
    }
    if (type == L"WeakObjectProperty") {
        if (sourceSize != sizeof(BriefcaseObjectHandle)) return false;
        BriefcaseObjectHandle handle{};
        if (!safeCopy(&handle, source, sizeof(handle)) ||
            (handle.Index != std::numeric_limits<std::uint32_t>::max() &&
             !resolveObject(handle))) return false;
        return safeCopy(destination, &handle, sizeof(handle));
    }
    if (type == L"StructProperty") {
        if (!readable(property, sizeof(FStructProperty))) return false;
        return writeCanonicalStruct(
            reinterpret_cast<const FStructProperty*>(property)->Struct,
            destination, source, sourceSize, depth + 1);
    }
    const auto kind = propertyKind(property);
    if (kind == BRIEFCASE_PROPERTY_UNKNOWN || kind == BRIEFCASE_PROPERTY_STRING ||
        kind == BRIEFCASE_PROPERTY_TEXT || kind == BRIEFCASE_PROPERTY_ARRAY ||
        kind == BRIEFCASE_PROPERTY_SET || kind == BRIEFCASE_PROPERTY_MAP ||
        kind == BRIEFCASE_PROPERTY_INTERFACE ||
        kind == BRIEFCASE_PROPERTY_LAZY_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_OBJECT ||
        kind == BRIEFCASE_PROPERTY_SOFT_CLASS ||
        kind == BRIEFCASE_PROPERTY_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_MULTICAST_DELEGATE ||
        kind == BRIEFCASE_PROPERTY_FIELD_PATH)
        return true; // Owning nested fields are mutated through UFunctions.
    return sourceSize == static_cast<std::size_t>(property->ElementSize) &&
           safeCopy(destination, source, sourceSize);
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

struct OwnedStringValue {
    std::array<std::byte, 40> Parameters{};
    const FProperty* Property{};
    bool Initialized{};
};

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
        bool HasInput{};
    };
    std::vector<ObjectArgument> objectArguments;
    constexpr std::uint64_t ParameterFlag = 0x80;
    constexpr std::uint64_t OutParameterFlag = 0x100;
    constexpr std::uint64_t ReturnParameterFlag = 0x400;
    constexpr std::uint64_t ReferenceParameterFlag = 0x08000000;
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
            const bool hasInput = !output ||
                (property->PropertyFlags & ReferenceParameterFlag) != 0;
            BriefcaseObjectHandle supplied{std::numeric_limits<std::uint32_t>::max(), 0};
            UObject* resolved{};
            if (hasInput) {
                if (!safeCopy(&supplied, slot, sizeof(supplied))) return BRIEFCASE_UNREAL_UNREADABLE;
                if (supplied.Index != std::numeric_limits<std::uint32_t>::max()) {
                    resolved = const_cast<UObject*>(resolveObject(supplied));
                    if (!resolved) return BRIEFCASE_UNREAL_STALE_HANDLE;
                }
            }
            if (!safeCopy(slot, &resolved, sizeof(resolved))) return BRIEFCASE_UNREAL_UNREADABLE;
            objectArguments.push_back({property->OffsetInternal, supplied, output, hasInput});
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

BriefcaseBool BRIEFCASE_MOD_CALL apiRegisterGameThreadCallback(
    void*, BriefcaseGameThreadCallbackFn callback, void* userContext,
    std::uint64_t* registrationId) {
    if (!RuntimeObjects || !callback || !registrationId ||
        !writable(registrationId, sizeof(*registrationId)) ||
        CapturedGameThreadId.load(std::memory_order_acquire) == 0) return 0;

    auto* anchor = findObjectByPath(L"/Script/Engine.Default__KismetSystemLibrary");
    if (!anchor)
        anchor = findObjectByPath(L"/Script/Engine.Default__KismetTextLibrary");
    if (!anchor || !readable(anchor, sizeof(UObject))) {
        briefcase::log(L"game thread: no stable ProcessEvent anchor was found");
        return 0;
    }
    if (!installProcessEventDetour(reinterpret_cast<void**>(anchor->VTable))) {
        briefcase::log(L"game thread: ProcessEvent detour installation failed");
        return 0;
    }

    try {
        auto registration = std::make_shared<GameThreadRegistration>();
        {
            const std::scoped_lock lock(GameThreadStateMutex);
            registration->Id = NextGameThreadRegistrationId++;
            registration->Callback = callback;
            registration->UserContext = userContext;
            GameThreadRegistrations.push_back(registration);
            GameThreadRegistrationCount.store(
                static_cast<std::uint32_t>(GameThreadRegistrations.size()),
                std::memory_order_release);
        }
        *registrationId = registration->Id;
        GameThreadPumpRequested.store(true, std::memory_order_release);
        briefcase::log(L"game thread: registered managed scheduler on thread " +
                       std::to_wstring(CapturedGameThreadId.load(std::memory_order_relaxed)));
        return 1;
    } catch (...) {
        return 0;
    }
}

BriefcaseBool BRIEFCASE_MOD_CALL apiUnregisterGameThreadCallback(
    void*, std::uint64_t registrationId) {
    std::shared_ptr<GameThreadRegistration> removed;
    {
        const std::scoped_lock lock(GameThreadStateMutex);
        const auto registration = std::find_if(
            GameThreadRegistrations.begin(), GameThreadRegistrations.end(),
            [registrationId](const auto& item) { return item->Id == registrationId; });
        if (registration == GameThreadRegistrations.end()) return 0;
        removed = *registration;
        removed->Active = false;
        GameThreadRegistrations.erase(registration);
        GameThreadRegistrationCount.store(
            static_cast<std::uint32_t>(GameThreadRegistrations.size()),
            std::memory_order_release);
    }
    const std::scoped_lock invocationLock(removed->InvocationMutex);
    return 1;
}

void BRIEFCASE_MOD_CALL apiRequestGameThreadPump(void*) {
    GameThreadPumpRequested.store(true, std::memory_order_release);
}

BriefcaseBool BRIEFCASE_MOD_CALL apiIsGameThread(void*) {
    const auto captured = CapturedGameThreadId.load(std::memory_order_acquire);
    return captured != 0 && GetCurrentThreadId() == captured ? 1u : 0u;
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

const FProperty* patchProperty(
    const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind) {
    if (!call || call->StructSize < sizeof(BriefcasePatchCall) ||
        call->Reserved[0] == 0 || !call->Parameters) return nullptr;
    const auto* function = reinterpret_cast<const UFunction*>(call->Reserved[0]);
    if (!readable(function, sizeof(UFunction)) || function->ParmsSize != call->ParameterSize)
        return nullptr;
    auto* field = function->ChildProperties;
    constexpr std::uint64_t ParameterFlag = 0x80;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) return nullptr;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if ((property->PropertyFlags & ParameterFlag) != 0 &&
            property->OffsetInternal == static_cast<std::int32_t>(parameterOffset) &&
            property->ArrayDim == 1 && property->ElementSize > 0 &&
            static_cast<std::uint64_t>(property->OffsetInternal) +
                static_cast<std::uint64_t>(property->ElementSize) <= call->ParameterSize &&
            propertyKind(property) == expectedKind)
            return property;
        field = field->Next;
    }
    return nullptr;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchValue(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, std::uint8_t* destination,
    std::uint32_t capacity, std::uint32_t* requiredBytes) {
    if (!requiredBytes || !writable(requiredBytes, sizeof(*requiredBytes)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *requiredBytes = 0;
    const auto* property = patchProperty(call, parameterOffset, expectedKind);
    if (!property) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const auto* source = static_cast<const std::byte*>(call->Parameters) + parameterOffset;
    ValueWireBuilder wire;
    if (!wire.append(ValueWireMagic) || !appendValueNode(property, source, wire, 0))
        return BRIEFCASE_UNREAL_UNREADABLE;
    *requiredBytes = static_cast<std::uint32_t>(wire.Bytes.size());
    if (!destination || capacity < *requiredBytes)
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (!writable(destination, capacity)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    return safeCopy(destination, wire.Bytes.data(), wire.Bytes.size())
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePatchValue(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, const void* input, std::uint32_t inputSize) {
    if (!input || inputSize == 0 || !readable(input, inputSize))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = patchProperty(call, parameterOffset, expectedKind);
    if (!property) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    if (expectedKind == BRIEFCASE_PROPERTY_STRING || expectedKind == BRIEFCASE_PROPERTY_TEXT ||
        expectedKind == BRIEFCASE_PROPERTY_ARRAY || expectedKind == BRIEFCASE_PROPERTY_SET ||
        expectedKind == BRIEFCASE_PROPERTY_MAP)
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    const auto canonicalSize = expectedKind == BRIEFCASE_PROPERTY_BOOL ? 1u :
        expectedKind == BRIEFCASE_PROPERTY_OBJECT
            ? static_cast<std::uint32_t>(sizeof(BriefcaseObjectHandle))
            : static_cast<std::uint32_t>(property->ElementSize);
    if (inputSize != canonicalSize) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    auto* destination = static_cast<std::byte*>(call->Parameters) + parameterOffset;
    if (!writable(destination, static_cast<std::size_t>(property->ElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    return writeCanonicalFixedValue(
        property, destination, reinterpret_cast<const std::byte*>(input), inputSize, 0)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePatchText(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, const std::uint16_t* characters,
    std::uint32_t characterCount) {
    if (expectedKind != BRIEFCASE_PROPERTY_STRING && expectedKind != BRIEFCASE_PROPERTY_TEXT)
        return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    if (characterCount >= 65'536 ||
        (characterCount && (!characters || !readable(
            characters, static_cast<std::size_t>(characterCount) * sizeof(std::uint16_t)))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = patchProperty(call, parameterOffset, expectedKind);
    if (!property) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    auto* destination = static_cast<std::byte*>(call->Parameters) + parameterOffset;
    if (!writable(destination, static_cast<std::size_t>(property->ElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    std::vector<std::uint16_t> terminated(characterCount + 1);
    if (characterCount && !safeCopy(
            terminated.data(), characters,
            static_cast<std::size_t>(characterCount) * sizeof(std::uint16_t)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    TextConversionRuntime runtime{};
    if (!resolveTextConversionRuntime(runtime)) return BRIEFCASE_UNREAL_UNSUPPORTED;

    if (expectedKind == BRIEFCASE_PROPERTY_TEXT) {
        if (property->ElementSize != 24) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
        OwnedTextValue temporary;
        if (!constructText(runtime, terminated.data(), characterCount, temporary))
            return BRIEFCASE_UNREAL_UNREADABLE;
        std::array<std::byte, 24> bytes{};
        if (!safeCopy(bytes.data(), temporary.Parameters.data() + 16, bytes.size()) ||
            !destroyPropertyValue(property, destination) ||
            !safeCopy(destination, bytes.data(), bytes.size())) {
            destroyText(temporary);
            return BRIEFCASE_UNREAL_UNREADABLE;
        }
        temporary.Initialized = false;
        return BRIEFCASE_UNREAL_OK;
    }

    if (property->ElementSize != sizeof(FStringBuffer))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    OwnedStringValue temporary;
    if (!constructString(runtime, terminated.data(), characterCount, temporary))
        return BRIEFCASE_UNREAL_UNREADABLE;
    std::array<std::byte, sizeof(FStringBuffer)> bytes{};
    if (!safeCopy(bytes.data(), temporary.Parameters.data() + 24, bytes.size()) ||
        !destroyPropertyValue(property, destination) ||
        !safeCopy(destination, bytes.data(), bytes.size())) {
        if (temporary.Initialized)
            destroyPropertyValue(temporary.Property, temporary.Parameters.data() + 24);
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    temporary.Initialized = false;
    return BRIEFCASE_UNREAL_OK;
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
    apiInvokeFunctionText, apiReadTextProperty, apiInvokeNativeBoolean,
    apiReadValueProperty, apiWriteProperty, apiWriteTextProperty};
const BriefcaseGameThreadApi GameThreadApi{
    sizeof(BriefcaseGameThreadApi), BRIEFCASE_GAME_THREAD_API_VERSION, nullptr,
    apiRegisterGameThreadCallback, apiUnregisterGameThreadCallback,
    apiRequestGameThreadPump, apiIsGameThread, {}};
const BriefcasePatchingApi PatchingApi{
    sizeof(BriefcasePatchingApi), BRIEFCASE_PATCHING_API_VERSION, nullptr,
    apiRegisterPatch, apiUnregisterPatch,
    apiRegisterNativePatch, apiUnregisterNativePatch,
    apiCopyPatchByteArray, apiCopyPatchString, apiCopyPatchText,
    apiCopyPatchValue, apiWritePatchValue, apiWritePatchText};

} // namespace

namespace briefcase {

const BriefcaseUnrealApi* getUnrealApi() { return isUnrealApiReady() ? &UnrealApi : nullptr; }
const BriefcasePatchingApi* getPatchingApi() { return isUnrealApiReady() ? &PatchingApi : nullptr; }
const BriefcaseGameThreadApi* getGameThreadApi() {
    return isUnrealApiReady() ? &GameThreadApi : nullptr;
}
void captureGameThreadId(DWORD threadId) {
    CapturedGameThreadId.store(threadId, std::memory_order_release);
}
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
    if (!executable(nameFunctionAddress) || !readable(objects, sizeof(FUObjectArray))) {
        log(L"resolved Unreal symbols have incompatible memory protection");
        return;
    }
    const auto convert = reinterpret_cast<FNameToString>(nameFunctionAddress);

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
    log(L"FName::ToString=" + hexadecimal(reinterpret_cast<std::uintptr_t>(nameFunctionAddress)) +
        L" signatureMatches=" + std::to_wstring(symbolResolution.Symbols.FNameMatchCount));
    log(L"GUObjectArray signature references=" +
        std::to_wstring(symbolResolution.Symbols.GUObjectReferenceCount) +
        L" unanimousTarget=true");
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
