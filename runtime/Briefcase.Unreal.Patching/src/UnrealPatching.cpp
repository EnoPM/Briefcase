#include "UnrealPatching.h"

#include "Log.h"
#include "UnrealMarshalling.h"
#include "UnrealReflection.h"
#include "UnrealValueCodec.h"

#include <MinHook.h>
#include <hde/hde64.h>
#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <string_view>
#include <tuple>
#include <type_traits>
#include <utility>
#include <vector>

namespace briefcase::unreal {
namespace {
const profile::RuntimeProfile* PatchingProfile{};
std::byte* RuntimeImageBase{};
std::size_t RuntimeImageSize{};
struct PatchRegistration {
    std::uint64_t Id{};
    const UObject* TargetClass{};
    // Delegate subscriptions target one concrete sink instance. Ordinary
    // attributed patches leave this null and continue matching subclasses.
    const UObject* ExactObject{};
    // A delegate is invoked on an internal sink UObject. Managed callbacks must
    // still observe the publisher that owns the multicast property.
    BriefcaseObjectHandle CallbackInstance{UINT32_MAX, 0};
    bool HasCallbackInstance{};
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
constexpr std::size_t MaxProcessEventDetours = 64;
struct ProcessEventDetour {
    void* Address{};
    ProcessEventFn Original{};
    std::size_t Slot{};
};
std::vector<std::unique_ptr<ProcessEventDetour>> ProcessEventDetours;
std::array<std::atomic<ProcessEventDetour*>, MaxProcessEventDetours>
    ProcessEventSlots{};
std::atomic_bool FirstProcessEventObserved{false};
std::atomic_bool ManagedStartupPending{false};
std::atomic_bool ManagedStartupWaitLogged{false};

// UE can execute a native UFunction either through UObject::ProcessEvent or by
// calling the function's reflected Func thunk directly. ProcessEvent covers
// Blueprint dispatch, while the Func transport below covers native delegates
// such as Deceive Inc.'s room and spawn initialization callbacks.
struct FFrame427View {
    std::byte OutputDevice[0x10];
    UFunction* Node;
    UObject* Object;
    std::byte* Code;
    std::byte* Locals;
    FProperty* MostRecentProperty;
    std::byte* MostRecentPropertyAddress;
    std::byte FlowStack[0x30];
    void* PreviousFrame;
    void* OutParms;
    FField* PropertyChainForCompiledIn;
    UFunction* CurrentNativeFunction;
    bool ArrayContextFailed;
};
static_assert(offsetof(FFrame427View, Node) == 0x10);
static_assert(offsetof(FFrame427View, Code) == 0x20);
static_assert(offsetof(FFrame427View, Locals) == 0x28);
static_assert(offsetof(FFrame427View, CurrentNativeFunction) == 0x88);

using UFunctionThunkFn = void(__fastcall*)(UObject*, FFrame427View&, void*);
constexpr std::size_t MaxUFunctionThunkTargets = 64;
struct UFunctionThunkTarget {
    UFunction* Function{};
    UFunctionThunkFn Original{};
    std::size_t Slot{};
    std::atomic_uint64_t InvocationCount{};
};
std::mutex UFunctionThunkStateMutex;
std::vector<std::unique_ptr<UFunctionThunkTarget>> UFunctionThunkTargets;
std::array<std::atomic<UFunctionThunkTarget*>, MaxUFunctionThunkTargets>
    UFunctionThunkSlots{};
// ProcessEvent ultimately calls the same UFunction::Func for the function it
// dispatches. Remember that exact function so its thunk does not dispatch the
// same managed patch twice. Direct calls to other UFunctions made by the
// original body remain visible to their own thunk transports.
thread_local const UFunction* ProcessEventFunctionInOriginal{};

using ActorBeginPlayFn = void(__fastcall*)(UObject*);
std::atomic<ActorBeginPlayFn> OriginalActorBeginPlay{};
std::mutex ActorBeginPlayStateMutex;
const UFunction* ActorReceiveBeginPlayFunction{};
thread_local bool InsideSyntheticActorBeginPlay{};

struct DelegateBindingGroup {
    BriefcaseObjectHandle Publisher{};
    std::int32_t PropertyOffset{};
    BriefcaseObjectHandle Sink{};
    const UObject* SinkClass{};
    FName SinkFunctionName{};
    FScriptDelegate Binding{};
    std::uint32_t SubscriberCount{};
};

struct DelegateSubscription {
    std::uint64_t Id{};
    std::shared_ptr<DelegateBindingGroup> Group;
};

std::mutex DelegateStateMutex;
std::vector<std::shared_ptr<DelegateBindingGroup>> DelegateBindings;
std::vector<DelegateSubscription> DelegateSubscriptions;
std::vector<std::shared_ptr<DelegateBindingGroup>> PendingDelegateRemovals;
std::atomic_uint32_t PendingDelegateRemovalCount{};

bool sameObjectHandle(BriefcaseObjectHandle left, BriefcaseObjectHandle right) {
    return left.Index == right.Index && left.SerialNumber == right.SerialNumber;
}

bool sameName(const FName& left, const FName& right) {
    return left.ComparisonIndex == right.ComparisonIndex && left.Number == right.Number;
}

bool sameDelegate(const FScriptDelegate& left, const FScriptDelegate& right) {
    return left.Object.ObjectIndex == right.Object.ObjectIndex &&
           left.Object.ObjectSerialNumber == right.Object.ObjectSerialNumber &&
           sameName(left.FunctionName, right.FunctionName);
}

bool readDelegateArray(void* address, FScriptArray& result, bool requireWritableElements) {
    constexpr std::int32_t MaximumDelegateBindings = 100'000;
    if (!address || !readable(address, sizeof(FScriptArray)) ||
        !safeCopy(&result, address, sizeof(result)) || result.Num < 0 ||
        result.Max < result.Num || result.Max > MaximumDelegateBindings)
        return false;
    if (result.Num == 0) return result.Data != nullptr || result.Max == 0;
    const auto bytes = static_cast<std::size_t>(result.Num) * sizeof(FScriptDelegate);
    return result.Data && readableRange(result.Data, bytes) &&
           (!requireWritableElements || writable(result.Data, bytes));
}

bool removeDelegateBinding(const DelegateBindingGroup& group) noexcept {
    auto* object = const_cast<UObject*>(resolveObject(group.Publisher));
    if (!object) return true; // The publisher was destroyed with its delegate storage.
    if (group.PropertyOffset < 0 || !readable(object->ClassPrivate, sizeof(UStruct)))
        return false;
    const auto* actualClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    if (actualClass->PropertiesSize < 0 ||
        static_cast<std::uint64_t>(group.PropertyOffset) + sizeof(FScriptArray) >
            static_cast<std::uint64_t>(actualClass->PropertiesSize))
        return false;
    auto* address = reinterpret_cast<std::byte*>(object) + group.PropertyOffset;
    if (!writable(address, sizeof(FScriptArray))) return false;
    FScriptArray array{};
    if (!readDelegateArray(address, array, true)) return false;
    if (array.Num == 0) return true;

    auto* entries = static_cast<FScriptDelegate*>(array.Data);
    std::int32_t index = -1;
    for (std::int32_t candidate = 0; candidate < array.Num; ++candidate) {
        FScriptDelegate current{};
        if (!safeCopy(&current, entries + candidate, sizeof(current))) return false;
        if (sameDelegate(current, group.Binding)) {
            index = candidate;
            break;
        }
    }
    if (index < 0) return true;
    __try {
        if (index + 1 < array.Num) {
            std::memmove(
                entries + index, entries + index + 1,
                static_cast<std::size_t>(array.Num - index - 1) * sizeof(FScriptDelegate));
        }
        std::memset(entries + array.Num - 1, 0, sizeof(FScriptDelegate));
        --array.Num;
        std::memcpy(address, &array, sizeof(array));
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

void processPendingDelegateRemovals() {
    std::vector<std::shared_ptr<DelegateBindingGroup>> pending;
    {
        const std::scoped_lock lock(DelegateStateMutex);
        pending.swap(PendingDelegateRemovals);
        PendingDelegateRemovalCount.store(0, std::memory_order_release);
    }
    for (const auto& group : pending) {
        if (!removeDelegateBinding(*group)) {
            briefcase::log(L"events: could not remove a stale delegate binding safely");
        } else {
            briefcase::log(L"events: released deferred delegate binding");
        }
    }
}

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
        GetCurrentThreadId() != capturedGameThreadId()) return;

    if (PendingDelegateRemovalCount.load(std::memory_order_acquire) != 0)
        processPendingDelegateRemovals();

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

void waitForManagedStartup() {
    if (GetCurrentThreadId() != capturedGameThreadId() ||
        !ManagedStartupPending.load(std::memory_order_acquire)) return;

    if (!ManagedStartupWaitLogged.exchange(true, std::memory_order_acq_rel))
        briefcase::log(
            L"managed startup barrier: first game-thread ProcessEvent is waiting for mods");

    constexpr DWORD MaximumWaitMilliseconds = 15'000;
    const auto startedAt = GetTickCount64();
    while (ManagedStartupPending.load(std::memory_order_acquire)) {
        if (GetTickCount64() - startedAt >= MaximumWaitMilliseconds) {
            const auto wasPending =
                ManagedStartupPending.exchange(false, std::memory_order_acq_rel);
            if (wasPending)
                briefcase::log(
                    L"managed startup barrier: timed out after 15 seconds; game startup resumed");
            return;
        }
        Sleep(1);
    }

    briefcase::log(
        L"managed startup barrier: mods are ready; game startup resumed");
}

void dispatchProcessEvent(
    std::size_t slot, UObject* object,
    const UFunction* function, void* parameters);

template<std::size_t Slot>
void __fastcall processEventHook(
    UObject* object, const UFunction* function, void* parameters) {
    dispatchProcessEvent(Slot, object, function, parameters);
}

template<std::size_t... Slots>
constexpr auto makeProcessEventHooks(std::index_sequence<Slots...>) {
    return std::array<ProcessEventFn, sizeof...(Slots)>{
        &processEventHook<Slots>...};
}

ProcessEventFn processEventHookForSlot(std::size_t slot) {
    static constexpr auto hooks = makeProcessEventHooks(
        std::make_index_sequence<MaxProcessEventDetours>{});
    return slot < hooks.size() ? hooks[slot] : nullptr;
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
            (registration->ExactObject && registration->ExactObject != object) ||
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
        const auto actualInstance = call.Instance;
        if (registration->HasCallbackInstance)
            call.Instance = registration->CallbackInstance;
        const auto invoked = safePatchCallback(registration, &call, &runOriginal);
        call.Instance = actualInstance;
        if (!invoked)
            briefcase::log(L"patching host: managed callback raised a native exception");
    }
}

void dispatchActorBeginPlay(UObject* actor) {
    const auto original = OriginalActorBeginPlay.load(std::memory_order_acquire);
    if (!original) return;
    const auto* function = ActorReceiveBeginPlayFunction;
    if (!actor || !function) {
        original(actor);
        return;
    }

    const auto registrations = registrationsFor(actor, function);
    if (registrations.Prefixes.empty() && registrations.Postfixes.empty()) {
        original(actor);
        return;
    }
    BriefcaseObjectHandle instance{};
    if (!makeHandle(actor, instance)) {
        original(actor);
        return;
    }

    BriefcaseBool runOriginal = 1;
    BriefcasePatchCall call{
        sizeof(BriefcasePatchCall), BRIEFCASE_PATCH_PREFIX, instance, 0,
        nullptr, 0, 0, {}};
    call.Reserved[0] = reinterpret_cast<std::uint64_t>(function);
    invokePatchCallbacks(registrations.Prefixes, call, runOriginal);
    if (runOriginal != 0) {
        InsideSyntheticActorBeginPlay = true;
        original(actor);
        InsideSyntheticActorBeginPlay = false;
        call.OriginalRan = 1;
    }
    call.Phase = BRIEFCASE_PATCH_POSTFIX;
    invokePatchCallbacks(registrations.Postfixes, call, runOriginal);
}

void __fastcall actorBeginPlayHook(UObject* actor) {
    dispatchActorBeginPlay(actor);
}

void dispatchUFunctionThunk(
    std::size_t slot, UObject* object, FFrame427View& frame, void* result) noexcept {
    auto* target = slot < UFunctionThunkSlots.size()
        ? UFunctionThunkSlots[slot].load(std::memory_order_acquire) : nullptr;
    if (!target || !target->Original) return;
    const auto original = target->Original;

    // AActor::BeginPlay exposes ReceiveBeginPlay as one lifecycle patch. If its
    // native body dispatches the reflected event too, suppress that nested copy.
    if (InsideSyntheticActorBeginPlay && ActorReceiveBeginPlayFunction &&
        sameName(target->Function->NamePrivate,
                 ActorReceiveBeginPlayFunction->NamePrivate)) {
        original(object, frame, result);
        return;
    }

    // ProcessEvent already dispatches the same registration around its original
    // call. Suppress the Func transport on that nested path to avoid duplicates.
    if (ProcessEventFunctionInOriginal == target->Function) {
        original(object, frame, result);
        return;
    }

    bool originalRan{};
    try {
        const auto* function = target->Function;
        const auto registrations = registrationsFor(object, function);
        if (registrations.Prefixes.empty() && registrations.Postfixes.empty()) {
            original(object, frame, result);
            return;
        }

        if (target->InvocationCount.fetch_add(1, std::memory_order_relaxed) == 0)
            briefcase::log(L"patching host: first UFunction thunk callback for " +
                objectPath(function, RuntimeNameConverter));

        BriefcaseObjectHandle instance{};
        if (!makeHandle(object, instance)) {
            original(object, frame, result);
            return;
        }

        // A null Code pointer means UE has already materialized the reflected
        // parameter struct in Locals. Compiled bytecode frames require VM
        // stepping; until that transport is added, callbacks may still use
        // __instance but parameter access fails explicitly on a null buffer.
        void* parameters{};
        if (function->ParmsSize == 0) {
            parameters = nullptr;
        } else if (frame.Code == nullptr && frame.Locals &&
                   readable(frame.Locals, function->ParmsSize)) {
            parameters = frame.Locals;
        }

        BriefcaseBool runOriginal = 1;
        BriefcasePatchCall call{
            sizeof(BriefcasePatchCall), BRIEFCASE_PATCH_PREFIX, instance, 0,
            parameters, function->ParmsSize, 0, {}};
        call.Reserved[0] = reinterpret_cast<std::uint64_t>(function);
        invokePatchCallbacks(registrations.Prefixes, call, runOriginal);

        // A bytecode frame must be advanced by the original exec thunk. Skipping
        // it without evaluating parameters would corrupt the caller's VM frame.
        if (frame.Code != nullptr) runOriginal = 1;
        if (runOriginal != 0) {
            original(object, frame, result);
            originalRan = true;
            call.OriginalRan = 1;
        }
        call.Phase = BRIEFCASE_PATCH_POSTFIX;
        invokePatchCallbacks(registrations.Postfixes, call, runOriginal);
    } catch (...) {
        briefcase::log(L"patching host: UFunction thunk dispatch failed; original preserved");
        if (!originalRan) original(object, frame, result);
    }
}

template<std::size_t Slot>
void __fastcall uFunctionThunk(
    UObject* object, FFrame427View& frame, void* result) noexcept {
    dispatchUFunctionThunk(Slot, object, frame, result);
}

template<std::size_t... Slots>
constexpr auto makeUFunctionThunks(std::index_sequence<Slots...>) {
    return std::array<UFunctionThunkFn, sizeof...(Slots)>{
        &uFunctionThunk<Slots>...};
}

UFunctionThunkFn uFunctionThunkForSlot(std::size_t slot) {
    static constexpr auto thunks = makeUFunctionThunks(
        std::make_index_sequence<MaxUFunctionThunkTargets>{});
    return slot < thunks.size() ? thunks[slot] : nullptr;
}

bool installUFunctionThunk(UFunction* function) {
    constexpr std::uint16_t NoReturnValue = 0xFFFF;
    if (!function || !readable(function, sizeof(UFunction)) ||
        function->ReturnValueOffset != NoReturnValue || !function->Func ||
        !executable(function->Func)) return false;

    const std::scoped_lock lock(UFunctionThunkStateMutex);
    const auto existing = std::find_if(
        UFunctionThunkTargets.begin(), UFunctionThunkTargets.end(),
        [function](const auto& item) { return item->Function == function; });
    if (existing != UFunctionThunkTargets.end()) return true;
    if (UFunctionThunkTargets.size() >= MaxUFunctionThunkTargets) return false;

    auto target = std::make_unique<UFunctionThunkTarget>();
    target->Function = function;
    target->Original = reinterpret_cast<UFunctionThunkFn>(function->Func);
    target->Slot = UFunctionThunkTargets.size();
    const auto replacement = uFunctionThunkForSlot(target->Slot);
    if (!replacement) return false;

    DWORD previousProtection{};
    if (!VirtualProtect(
            &function->Func, sizeof(function->Func), PAGE_READWRITE,
            &previousProtection)) return false;
    // Publish the slot before swapping Func: another game thread may call the
    // thunk as soon as the pointer changes, and must always be able to reach
    // the original function.
    auto* published = target.get();
    UFunctionThunkSlots[target->Slot].store(published, std::memory_order_release);
    const auto previous = InterlockedExchangePointer(
        &function->Func, reinterpret_cast<void*>(replacement));
    DWORD ignored{};
    const auto protectionRestored = VirtualProtect(
        &function->Func, sizeof(function->Func), previousProtection, &ignored);
    if (!protectionRestored || previous != reinterpret_cast<void*>(target->Original)) {
        DWORD writableProtection{};
        if (VirtualProtect(
                &function->Func, sizeof(function->Func), PAGE_READWRITE,
                &writableProtection)) {
            InterlockedExchangePointer(&function->Func, previous);
            VirtualProtect(
                &function->Func, sizeof(function->Func), writableProtection, &ignored);
        }
        UFunctionThunkSlots[target->Slot].store(nullptr, std::memory_order_release);
        return false;
    }

    UFunctionThunkTargets.push_back(std::move(target));
    briefcase::log(L"patching host: installed UFunction thunk transport for " +
        objectPath(function, RuntimeNameConverter));
    return true;
}

void dispatchProcessEvent(
    std::size_t slot, UObject* object,
    const UFunction* function, void* parameters) {
    auto* detour = slot < ProcessEventSlots.size()
        ? ProcessEventSlots[slot].load(std::memory_order_acquire) : nullptr;
    const auto original = detour ? detour->Original : nullptr;
    if (!original) return;

    if (InsideSyntheticActorBeginPlay && function &&
        ActorReceiveBeginPlayFunction &&
        sameName(function->NamePrivate, ActorReceiveBeginPlayFunction->NamePrivate)) {
        original(object, function, parameters);
        return;
    }

    waitForManagedStartup();

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
        const auto* previousProcessEventFunction = ProcessEventFunctionInOriginal;
        ProcessEventFunctionInOriginal = function;
        original(object, function, parameters);
        ProcessEventFunctionInOriginal = previousProcessEventFunction;
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
    std::uint64_t DetourInvocationCount{};
};

struct NativePatchRegistration {
    std::uint64_t Id{};
    NativeTarget* Target{};
    const UObject* TargetClass{};
    const UFunction* Function{};
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
        // Native detours marshal arguments into a private parameter buffer.
        // CopyValue still needs the matching UFunction to recover the exact
        // FProperty graph for structs, arrays and other aggregate values.
        call.Reserved[0] = reinterpret_cast<std::uint64_t>(registration->Function);
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
    if (std::atomic_ref(target->DetourInvocationCount).fetch_add(1, std::memory_order_relaxed) == 0)
        briefcase::log(L"native patching host: first detour entry slot=" +
            std::to_wstring(slot) + L" rva=" + hexadecimal(
                reinterpret_cast<std::uintptr_t>(target->Address) -
                reinterpret_cast<std::uintptr_t>(RuntimeImageBase)));

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
} // namespace

void configurePatchingRuntime(
    const profile::RuntimeProfile* runtimeProfile,
    std::byte* imageBase,
    std::size_t imageSize) noexcept {
    PatchingProfile = runtimeProfile;
    RuntimeImageBase = imageBase;
    RuntimeImageSize = imageSize;
}

void beginManagedStartupBarrier() noexcept {
    ManagedStartupWaitLogged.store(false, std::memory_order_release);
    ManagedStartupPending.store(true, std::memory_order_release);
    briefcase::log(L"managed startup barrier: armed");
}

void completeManagedStartupBarrier() noexcept {
    if (ManagedStartupPending.exchange(false, std::memory_order_acq_rel))
        briefcase::log(L"managed startup barrier: managed initialization completed");
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

bool installActorBeginPlayDetour(const UFunction* receiveBeginPlay) {
    if (!receiveBeginPlay || !RuntimeObjects) return false;
    const std::scoped_lock lock(ActorBeginPlayStateMutex);
    if (OriginalActorBeginPlay.load(std::memory_order_acquire)) return true;

    auto* actor = findObjectByPath(L"/Script/Engine.Default__Actor");
    if (!actor || !readable(actor, sizeof(UObject))) return false;
    auto** vtable = reinterpret_cast<void**>(actor->VTable);
    const auto slot = briefcase::profile::ActorBeginPlayVTableIndex;
    if (!vtable || !readable(vtable + slot, sizeof(void*))) return false;
    auto* target = vtable[slot];
    if (!target || !executable(target)) return false;

    const auto initialize = MH_Initialize();
    if (initialize != MH_OK && initialize != MH_ERROR_ALREADY_INITIALIZED) return false;
    void* trampoline{};
    if (MH_CreateHook(target, reinterpret_cast<void*>(&actorBeginPlayHook), &trampoline) != MH_OK ||
        !trampoline || !executable(trampoline)) return false;
    ActorReceiveBeginPlayFunction = receiveBeginPlay;
    OriginalActorBeginPlay.store(
        reinterpret_cast<ActorBeginPlayFn>(trampoline), std::memory_order_release);
    if (MH_EnableHook(target) != MH_OK) {
        OriginalActorBeginPlay.store(nullptr, std::memory_order_release);
        ActorReceiveBeginPlayFunction = nullptr;
        MH_RemoveHook(target);
        return false;
    }
    briefcase::log(L"patching host: AActor::BeginPlay lifecycle transport installed");
    return true;
}

// PatchStateMutex must already be held. Keeping the lock-free implementation
// separate lets composite operations, such as delegate registration, update
// their patch and delegate state atomically without recursively locking the
// non-recursive mutex.
bool installProcessEventDetourLocked(void** vtable) {
    if (!vtable ||
        !readable(vtable + briefcase::profile::ProcessEventVTableIndex, sizeof(void*))) return false;
    auto* target = vtable[briefcase::profile::ProcessEventVTableIndex];
    if (!target || !executable(target)) return false;
    if (std::any_of(
            ProcessEventDetours.begin(), ProcessEventDetours.end(),
            [target](const auto& candidate) { return candidate->Address == target; }))
        return true;
    if (ProcessEventDetours.size() >= MaxProcessEventDetours) return false;

    const auto replacement = processEventHookForSlot(ProcessEventDetours.size());
    if (!replacement) return false;
    const auto initialize = MH_Initialize();
    if (initialize != MH_OK && initialize != MH_ERROR_ALREADY_INITIALIZED) return false;
    void* trampoline{};
    if (MH_CreateHook(target, reinterpret_cast<void*>(replacement), &trampoline) != MH_OK ||
        !trampoline || !executable(trampoline)) return false;

    auto detour = std::make_unique<ProcessEventDetour>();
    detour->Address = target;
    detour->Original = reinterpret_cast<ProcessEventFn>(trampoline);
    detour->Slot = ProcessEventDetours.size();
    auto* published = detour.get();
    ProcessEventSlots[detour->Slot].store(published, std::memory_order_release);
    if (MH_EnableHook(target) != MH_OK) {
        ProcessEventSlots[detour->Slot].store(nullptr, std::memory_order_release);
        MH_RemoveHook(target);
        return false;
    }
    ProcessEventDetours.push_back(std::move(detour));
    briefcase::log(
        L"patching host: UObject::ProcessEvent detour installed; implementations=" +
        std::to_wstring(ProcessEventDetours.size()));
    return true;
}

bool installProcessEventDetour(void** vtable) {
    const std::scoped_lock lock(PatchStateMutex);
    return installProcessEventDetourLocked(vtable);
}

BriefcaseBool BRIEFCASE_MOD_CALL apiRegisterGameThreadCallback(
    void*, BriefcaseGameThreadCallbackFn callback, void* userContext,
    std::uint64_t* registrationId) {
    if (!RuntimeObjects || !callback || !registrationId ||
        !writable(registrationId, sizeof(*registrationId)) ||
        capturedGameThreadId() == 0) return 0;

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
                       std::to_wstring(capturedGameThreadId()));
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
    const auto captured = capturedGameThreadId();
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
    if (functionName == L"ReceiveBeginPlay" &&
        !installActorBeginPlayDetour(function))
        briefcase::log(L"patching host: AActor::BeginPlay lifecycle transport unavailable");

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
    std::size_t installedProcessEventTargets{};
    for (auto** vtable : vtables)
        if (installProcessEventDetour(vtable)) ++installedProcessEventTargets;
    if (installedProcessEventTargets == 0) return BRIEFCASE_UNREAL_UNSUPPORTED;

    // Keep ProcessEvent as the universal transport and additionally replace
    // void UFunction thunks so native delegate calls reach the same callbacks.
    // Failure is non-fatal because some functions are ProcessEvent-only or
    // return values that the current thunk transport deliberately refuses.
    installUFunctionThunk(const_cast<UFunction*>(function));

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

// A dynamic script delegate stores only {UObject, function name}. Its function
// must already belong to the bound object's UClass; a standalone delegate
// signature UFunction cannot be called on the publisher directly. Briefcase
// therefore binds an otherwise idle class-default object method whose reflected
// parameter buffer has exactly the same layout. The ProcessEvent detour consumes
// that call and skips the method body, making the CDO a side-effect-free sink.
bool sameDelegatePropertyShape(
    const FProperty* expected, const FProperty* candidate, unsigned depth = 0) {
    constexpr unsigned MaximumDepth = 8;
    if (!expected || !candidate || depth > MaximumDepth ||
        !readable(expected, sizeof(FProperty)) ||
        !readable(candidate, sizeof(FProperty)) ||
        (depth == 0 && expected->OffsetInternal != candidate->OffsetInternal) ||
        expected->ElementSize != candidate->ElementSize ||
        expected->ArrayDim != candidate->ArrayDim)
        return false;

    const auto expectedType = propertyTypeName(expected);
    if (expectedType.empty() || expectedType != propertyTypeName(candidate)) return false;
    if (expectedType == L"BoolProperty") {
        if (!readable(expected, sizeof(FBoolProperty)) ||
            !readable(candidate, sizeof(FBoolProperty))) return false;
        const auto* left = reinterpret_cast<const FBoolProperty*>(expected);
        const auto* right = reinterpret_cast<const FBoolProperty*>(candidate);
        return left->FieldSize == right->FieldSize &&
               left->ByteOffset == right->ByteOffset &&
               left->ByteMask == right->ByteMask &&
               left->FieldMask == right->FieldMask;
    }
    if (expectedType == L"StructProperty") {
        if (!readable(expected, sizeof(FStructProperty)) ||
            !readable(candidate, sizeof(FStructProperty))) return false;
        return reinterpret_cast<const FStructProperty*>(expected)->Struct ==
               reinterpret_cast<const FStructProperty*>(candidate)->Struct;
    }
    if (expectedType == L"ObjectProperty" || expectedType == L"WeakObjectProperty" ||
        expectedType == L"LazyObjectProperty" || expectedType == L"SoftObjectProperty") {
        if (!readable(expected, sizeof(FObjectPropertyBase)) ||
            !readable(candidate, sizeof(FObjectPropertyBase))) return false;
        return reinterpret_cast<const FObjectPropertyBase*>(expected)->PropertyClass ==
               reinterpret_cast<const FObjectPropertyBase*>(candidate)->PropertyClass;
    }
    if (expectedType == L"ClassProperty" || expectedType == L"SoftClassProperty") {
        if (!readable(expected, sizeof(FClassProperty)) ||
            !readable(candidate, sizeof(FClassProperty))) return false;
        const auto* left = reinterpret_cast<const FClassProperty*>(expected);
        const auto* right = reinterpret_cast<const FClassProperty*>(candidate);
        return left->PropertyClass == right->PropertyClass &&
               left->MetaClass == right->MetaClass;
    }
    if (expectedType == L"InterfaceProperty") {
        if (!readable(expected, sizeof(FInterfaceProperty)) ||
            !readable(candidate, sizeof(FInterfaceProperty))) return false;
        return reinterpret_cast<const FInterfaceProperty*>(expected)->InterfaceClass ==
               reinterpret_cast<const FInterfaceProperty*>(candidate)->InterfaceClass;
    }
    if (expectedType == L"EnumProperty") {
        if (!readable(expected, sizeof(FEnumProperty)) ||
            !readable(candidate, sizeof(FEnumProperty))) return false;
        const auto* left = reinterpret_cast<const FEnumProperty*>(expected);
        const auto* right = reinterpret_cast<const FEnumProperty*>(candidate);
        return left->Enum == right->Enum &&
               sameDelegatePropertyShape(
                   left->UnderlyingProperty, right->UnderlyingProperty, depth + 1);
    }
    if (expectedType == L"ByteProperty") {
        if (!readable(expected, sizeof(FByteProperty)) ||
            !readable(candidate, sizeof(FByteProperty))) return false;
        return reinterpret_cast<const FByteProperty*>(expected)->Enum ==
               reinterpret_cast<const FByteProperty*>(candidate)->Enum;
    }
    if (expectedType == L"ArrayProperty") {
        if (!readable(expected, sizeof(FArrayProperty)) ||
            !readable(candidate, sizeof(FArrayProperty))) return false;
        return sameDelegatePropertyShape(
            reinterpret_cast<const FArrayProperty*>(expected)->Inner,
            reinterpret_cast<const FArrayProperty*>(candidate)->Inner, depth + 1);
    }
    if (expectedType == L"SetProperty") {
        if (!readable(expected, sizeof(FSetProperty)) ||
            !readable(candidate, sizeof(FSetProperty))) return false;
        return sameDelegatePropertyShape(
            reinterpret_cast<const FSetProperty*>(expected)->ElementProperty,
            reinterpret_cast<const FSetProperty*>(candidate)->ElementProperty, depth + 1);
    }
    if (expectedType == L"MapProperty") {
        if (!readable(expected, sizeof(FMapProperty)) ||
            !readable(candidate, sizeof(FMapProperty))) return false;
        const auto* left = reinterpret_cast<const FMapProperty*>(expected);
        const auto* right = reinterpret_cast<const FMapProperty*>(candidate);
        return sameDelegatePropertyShape(left->KeyProperty, right->KeyProperty, depth + 1) &&
               sameDelegatePropertyShape(left->ValueProperty, right->ValueProperty, depth + 1);
    }
    if (expectedType == L"DelegateProperty" ||
        expectedType == L"MulticastDelegateProperty" ||
        expectedType == L"MulticastInlineDelegateProperty") {
        if (!readable(expected, sizeof(FDelegateProperty)) ||
            !readable(candidate, sizeof(FDelegateProperty))) return false;
        return reinterpret_cast<const FDelegateProperty*>(expected)->SignatureFunction ==
               reinterpret_cast<const FDelegateProperty*>(candidate)->SignatureFunction;
    }
    if (expectedType == L"FieldPathProperty") {
        if (!readable(expected, sizeof(FFieldPathProperty)) ||
            !readable(candidate, sizeof(FFieldPathProperty))) return false;
        return reinterpret_cast<const FFieldPathProperty*>(expected)->PropertyClass ==
               reinterpret_cast<const FFieldPathProperty*>(candidate)->PropertyClass;
    }
    return true;
}

bool collectFunctionParameters(
    const UFunction* function, std::vector<const FProperty*>& result) {
    constexpr std::uint64_t ParameterFlag = 0x80;
    if (!function || !readable(function, sizeof(UFunction))) return false;
    auto* field = function->ChildProperties;
    unsigned visited{};
    for (; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) return false;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if ((property->PropertyFlags & ParameterFlag) != 0) result.push_back(property);
        field = field->Next;
    }
    return !field && result.size() == function->NumParms;
}

bool sameDelegateFunctionShape(
    const UFunction* signature, const UFunction* candidate) {
    if (!signature || !candidate || signature == candidate ||
        !readable(signature, sizeof(UFunction)) ||
        !readable(candidate, sizeof(UFunction)) ||
        signature->ParmsSize != candidate->ParmsSize ||
        signature->NumParms != candidate->NumParms)
        return false;
    std::vector<const FProperty*> expected;
    std::vector<const FProperty*> actual;
    expected.reserve(signature->NumParms);
    actual.reserve(candidate->NumParms);
    if (!collectFunctionParameters(signature, expected) ||
        !collectFunctionParameters(candidate, actual)) return false;
    for (std::size_t index = 0; index < expected.size(); ++index)
        if (!sameDelegatePropertyShape(expected[index], actual[index])) return false;
    return true;
}

struct DelegateSink {
    BriefcaseObjectHandle Object{UINT32_MAX, 0};
    const UObject* Class{};
    const UFunction* Function{};
};

bool delegateSinkInUse(BriefcaseObjectHandle object, FName functionName) {
    return std::any_of(
        DelegateBindings.begin(), DelegateBindings.end(),
        [&](const auto& group) {
            return sameObjectHandle(group->Sink, object) &&
                   sameName(group->SinkFunctionName, functionName);
        });
}

bool findDelegateSinkOnClass(
    const UObject* classObject, const UFunction* signature, DelegateSink& result) {
    constexpr std::uint32_t ClassDefaultObjectFlag = 0x00000010u;
    if (!classObject || !isClassObject(classObject) ||
        !readable(classObject, sizeof(UClass))) return false;
    const auto* unrealClass = reinterpret_cast<const UClass*>(classObject);
    const auto* defaultObject = unrealClass->ClassDefaultObject;
    BriefcaseObjectHandle defaultHandle{};
    if (!defaultObject || !isRegisteredObject(defaultObject) ||
        (defaultObject->ObjectFlags & ClassDefaultObjectFlag) == 0 ||
        !objectIsA(defaultObject, classObject) ||
        !makeHandle(defaultObject, defaultHandle)) return false;

    for (auto* current = reinterpret_cast<const UStruct*>(classObject);
         current; current = current->SuperStruct) {
        if (!readable(current, sizeof(UStruct))) return false;
        auto* child = current->Children;
        for (unsigned visited = 0; child && visited < 4096; ++visited) {
            if (!readable(child, sizeof(UField)) ||
                !readable(child->ClassPrivate, sizeof(UObject))) return false;
            if (nameToString(child->ClassPrivate->NamePrivate, RuntimeNameConverter) ==
                    L"Function") {
                const auto* function = reinterpret_cast<const UFunction*>(child);
                if (sameDelegateFunctionShape(signature, function) &&
                    !delegateSinkInUse(defaultHandle, function->NamePrivate)) {
                    result = {defaultHandle, classObject, function};
                    return true;
                }
            }
            child = child->Next;
        }
    }
    return false;
}

// Called while DelegateStateMutex is held so a sink pair cannot be handed to
// two publishers. Prefer the publisher class to keep the search deterministic,
// then fall back to every reflected UClass CDO in GUObjectArray.
bool findDelegateSink(
    const UObject* preferredClass, const UFunction* signature, DelegateSink& result) {
    if (findDelegateSinkOnClass(preferredClass, signature, result)) return true;
    briefcase::log(
        L"events: publisher class has no compatible unused delegate sink; "
        L"searching reflected classes");
    const auto count = RuntimeObjects->ObjObjects.NumElements;
    for (std::int32_t index = 0; index < count; ++index) {
        const auto* item = itemAt(RuntimeObjects->ObjObjects, index);
        const auto* candidate = item ? item->Object : nullptr;
        if (!candidate || candidate == preferredClass) continue;
        if (findDelegateSinkOnClass(candidate, signature, result)) return true;
    }
    return false;
}

bool appendDelegateBinding(void* address, const FScriptDelegate& binding) {
    constexpr std::int32_t MaximumDelegateBindings = 100'000;
    if (!writable(address, sizeof(FScriptArray))) return false;
    FScriptArray array{};
    if (!readDelegateArray(address, array, false) ||
        array.Num >= MaximumDelegateBindings) return false;

    auto* entries = static_cast<FScriptDelegate*>(array.Data);
    for (std::int32_t index = 0; index < array.Num; ++index) {
        FScriptDelegate current{};
        if (!safeCopy(&current, entries + index, sizeof(current))) return false;
        // Never adopt a binding created by the game or another framework.
        if (sameDelegate(current, binding)) return false;
    }

    if (array.Num < array.Max) {
        auto* destination = entries + array.Num;
        if (!writable(destination, sizeof(binding)) ||
            !safeCopy(destination, &binding, sizeof(binding))) return false;
        ++array.Num;
        return safeCopy(address, &array, sizeof(array));
    }

    const auto grown = std::max<std::int32_t>(
        4, array.Max + std::max<std::int32_t>(1, array.Max / 2));
    const auto capacity = std::min(grown, MaximumDelegateBindings);
    if (capacity <= array.Num) return false;
    void* allocation{};
    if (!safeUnrealMalloc(
            static_cast<std::size_t>(capacity) * sizeof(FScriptDelegate),
            alignof(FScriptDelegate), allocation)) return false;
    if ((array.Num != 0 && !safeCopy(
            allocation, array.Data,
            static_cast<std::size_t>(array.Num) * sizeof(FScriptDelegate))) ||
        !safeCopy(
            static_cast<FScriptDelegate*>(allocation) + array.Num,
            &binding, sizeof(binding))) {
        safeUnrealFree(allocation);
        return false;
    }

    const auto oldAllocation = array.Data;
    const FScriptArray replacement{allocation, array.Num + 1, capacity};
    if (!safeCopy(address, &replacement, sizeof(replacement))) {
        safeUnrealFree(allocation);
        return false;
    }
    if (oldAllocation && !safeUnrealFree(oldAllocation))
        briefcase::log(L"events: delegate array grew but its old allocation could not be freed");
    return true;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiSubscribeMulticastDelegate(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePatchCallbackFn callback, void* userContext,
    std::uint64_t* registrationId) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    if (!callback || !registrationId || !writable(registrationId, sizeof(*registrationId)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *registrationId = 0;

    const UObject* object{};
    const FProperty* property{};
    std::byte* value{};
    const auto resolved = resolvePropertyAccess(
        objectHandle, ownerClassHandle, utf8Name, nameLength, expectedOffset,
        expectedElementSize, expectedArrayDimension,
        BRIEFCASE_PROPERTY_MULTICAST_DELEGATE, object, property, value);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;
    const auto typeName = propertyTypeName(property);
    if ((typeName != L"MulticastDelegateProperty" &&
         typeName != L"MulticastInlineDelegateProperty") ||
        expectedElementSize != static_cast<std::int32_t>(sizeof(FScriptArray)) ||
        !readable(property, sizeof(FDelegateProperty))) {
        briefcase::log(
            L"events: unsupported property layout type=" + typeName +
            L" size=" + std::to_wstring(expectedElementSize));
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    }

    const auto* signature =
        reinterpret_cast<const FDelegateProperty*>(property)->SignatureFunction;
    if (!isRegisteredObject(signature) || !readable(signature, sizeof(UFunction)) ||
        signature->ParmsSize > 65'535)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const auto signatureName = nameToString(signature->NamePrivate, RuntimeNameConverter);
    if (signatureName.empty()) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const wchar_t* stage = L"create registration";
    try {
        auto patch = std::make_shared<PatchRegistration>();
        std::shared_ptr<DelegateBindingGroup> group;
        bool createdGroup = false;
        {
            const std::scoped_lock lock(DelegateStateMutex, PatchStateMutex);
            const auto existing = std::find_if(
                DelegateBindings.begin(), DelegateBindings.end(),
                [&](const auto& candidate) {
                    return sameObjectHandle(candidate->Publisher, objectHandle) &&
                           candidate->PropertyOffset == expectedOffset;
                });
            if (existing != DelegateBindings.end()) {
                group = *existing;
            } else {
                stage = L"find delegate sink";
                DelegateSink sink{};
                if (!findDelegateSink(object->ClassPrivate, signature, sink)) {
                    briefcase::log(
                        L"events: no unused CDO function matches " + signatureName);
                    return BRIEFCASE_UNREAL_UNSUPPORTED;
                }
                auto* sinkObject = resolveObject(sink.Object);
                auto** vtable = sinkObject
                    ? reinterpret_cast<void**>(sinkObject->VTable) : nullptr;
                stage = L"install ProcessEvent detour";
                if (!sinkObject || !vtable || !installProcessEventDetourLocked(vtable)) {
                    briefcase::log(L"events: the selected delegate sink cannot be detoured");
                    return BRIEFCASE_UNREAL_UNSUPPORTED;
                }
                const FScriptDelegate binding{
                    {static_cast<std::int32_t>(sink.Object.Index),
                     static_cast<std::int32_t>(sink.Object.SerialNumber)},
                    sink.Function->NamePrivate};
                group = std::make_shared<DelegateBindingGroup>();
                group->Publisher = objectHandle;
                group->PropertyOffset = expectedOffset;
                group->Sink = sink.Object;
                group->SinkClass = sink.Class;
                group->SinkFunctionName = sink.Function->NamePrivate;
                group->Binding = binding;
                stage = L"format selected sink log";
                briefcase::log(
                    L"events: selected sink " +
                    objectPath(sink.Class, RuntimeNameConverter) + L"." +
                    nameToString(sink.Function->NamePrivate, RuntimeNameConverter));
                DelegateBindings.reserve(DelegateBindings.size() + 1);
                createdGroup = true;
            }

            DelegateSubscriptions.reserve(DelegateSubscriptions.size() + 1);
            PatchRegistrations.reserve(PatchRegistrations.size() + 1);
            stage = L"append multicast binding";
            if (createdGroup && !appendDelegateBinding(value, group->Binding)) {
                briefcase::log(L"events: failed to append the sink to the multicast array");
                return BRIEFCASE_UNREAL_UNSUPPORTED;
            }

            patch->Id = NextPatchRegistrationId++;
            patch->TargetClass = group->SinkClass;
            patch->ExactObject = resolveObject(group->Sink);
            patch->CallbackInstance = objectHandle;
            patch->HasCallbackInstance = true;
            patch->FunctionName = group->SinkFunctionName;
            patch->ParameterSize = signature->ParmsSize;
            patch->Phase = BRIEFCASE_PATCH_PREFIX;
            patch->Callback = callback;
            patch->UserContext = userContext;
            stage = L"publish subscription state";
            if (createdGroup) DelegateBindings.push_back(group);
            ++group->SubscriberCount;
            PatchRegistrations.push_back(patch);
            DelegateSubscriptions.push_back({patch->Id, group});
        }

        *registrationId = patch->Id;
        std::wstring propertyName;
        decodeUtf8(utf8Name, nameLength, propertyName);
        briefcase::log(
            L"events: subscribed " + objectPath(object, RuntimeNameConverter) + L"." +
            propertyName + L" -> " + signatureName + L" through " +
            objectPath(group->SinkClass, RuntimeNameConverter) + L"." +
            nameToString(group->SinkFunctionName, RuntimeNameConverter));
        return BRIEFCASE_UNREAL_OK;
    } catch (...) {
        briefcase::log(
            L"events: native subscription raised an exception during " +
            std::wstring(stage));
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    }
}

BriefcaseBool BRIEFCASE_MOD_CALL apiUnsubscribeDelegate(
    void*, std::uint64_t registrationId) {
    try {
        std::shared_ptr<PatchRegistration> removed;
        std::shared_ptr<DelegateBindingGroup> group;
        {
            const std::scoped_lock lock(DelegateStateMutex, PatchStateMutex);
            const auto subscription = std::find_if(
                DelegateSubscriptions.begin(), DelegateSubscriptions.end(),
                [registrationId](const auto& item) { return item.Id == registrationId; });
            if (subscription == DelegateSubscriptions.end()) return 0;
            group = subscription->Group;
            DelegateSubscriptions.erase(subscription);

            const auto registration = std::find_if(
                PatchRegistrations.begin(), PatchRegistrations.end(),
                [registrationId](const auto& item) { return item->Id == registrationId; });
            if (registration != PatchRegistrations.end()) {
                removed = *registration;
                removed->Active = false;
                PatchRegistrations.erase(registration);
            }

            if (group->SubscriberCount != 0) --group->SubscriberCount;
            if (group->SubscriberCount == 0) {
                const auto binding = std::find(
                    DelegateBindings.begin(), DelegateBindings.end(), group);
                if (binding != DelegateBindings.end()) DelegateBindings.erase(binding);
                // Always defer physical removal. Dispose may be called by the
                // handler itself while Unreal is iterating this very array.
                PendingDelegateRemovals.push_back(group);
                PendingDelegateRemovalCount.store(
                    static_cast<std::uint32_t>(PendingDelegateRemovals.size()),
                    std::memory_order_release);
                GameThreadPumpRequested.store(true, std::memory_order_release);
            }
        }

        // No managed GCHandle may be freed while its callback is returning.
        if (removed) {
            const std::scoped_lock invocationLock(removed->InvocationMutex);
        }
        briefcase::log(
            L"events: unsubscribed registration " + std::to_wstring(registrationId));
        return 1;
    } catch (...) {
        return 0;
    }
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiRegisterNativePatch(
    void*, BriefcaseObjectHandle targetClassHandle, BriefcaseObjectHandle functionOwnerClassHandle,
    const char* utf8FunctionName, std::uint32_t functionNameLength, BriefcasePatchPhase phase,
    BriefcasePatchCallbackFn callback, void* userContext, std::uint64_t* registrationId) {
    if (!RuntimeObjects || !RuntimeNameConverter || !PatchingProfile || !RuntimeImageBase)
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
                const auto execRva =
                    reinterpret_cast<std::uintptr_t>(function->Func) -
                    reinterpret_cast<std::uintptr_t>(RuntimeImageBase);
                briefcase::log(L"native patching host: installed native detour for " +
                         objectPath(functionOwnerClass, RuntimeNameConverter) + L"." + functionName +
                         L" execRva=" + hexadecimal(execRva) +
                         L" implementationRva=" + hexadecimal(implementationRva) +
                         L" inputs=" + std::to_wstring(target->Inputs.size()) +
                         (target->HasReturn ? L" return=yes" : L" return=no"));
            }

            auto registration = std::make_shared<NativePatchRegistration>();
            registration->Id = NextNativePatchRegistrationId++;
            registration->Target = target;
            registration->TargetClass = targetClass;
            registration->Function = function;
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

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePatchEncodedValue(
    void*, const BriefcasePatchCall* call, std::uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, const std::uint8_t* input,
    std::uint32_t inputSize) {
    if (!input || inputSize < 12 || inputSize > MaximumValueWireBytes ||
        !readableRange(input, inputSize)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = patchProperty(call, parameterOffset, expectedKind);
    if (!property) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    auto* destination = static_cast<std::byte*>(call->Parameters) + parameterOffset;
    return replacePropertyValueFromWire(property, destination, input, inputSize);
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeNativeBoolean(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseGameBuild expectedBuild,
    std::uint64_t functionRva, BriefcaseBool* result) {
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    if (!result || !RuntimeImageBase || !RuntimeImageSize || !PatchingProfile ||
        functionRva == 0 || functionRva >= RuntimeImageSize)
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *result = 0;
    if (expectedBuild.PeTimestamp != PatchingProfile->PeTimestamp ||
        expectedBuild.ImageSize != PatchingProfile->ImageSize)
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
} // namespace briefcase::unreal
