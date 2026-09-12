#include "UnrealInvocation.h"

#include <Windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <new>
#include <string>
#include <vector>

namespace briefcase::unreal {

namespace {

std::atomic_uint32_t CapturedGameThreadId{};
struct PreparedObjectArgument {
    std::int32_t Offset{};
    bool Output{};
    bool HasInput{};
};

struct PreparedValueArgument {
    const FProperty* Property{};
    std::int32_t Offset{};
    std::int32_t ElementSize{};
    bool Output{};
    bool HasInput{};
    bool WireInput{};
    bool WireOutput{};
};

struct PreparedFunctionEntry {
    const UObject* OwnerClass{};
    const UFunction* Function{};
    std::uint32_t ParameterSize{};
    bool UsesCanonicalMarshalling{};
    bool UsesWireInputs{};
    bool UsesWireOutputs{};
    std::vector<PreparedObjectArgument> ObjectArguments;
    std::vector<PreparedValueArgument> ValueArguments;
};

struct PreparedPropertyEntry {
    const UObject* OwnerClass{};
    const FProperty* Property{};
    std::int32_t Offset{};
    std::int32_t ElementSize{};
    BriefcasePropertyKind Kind{};
    bool WireValue{};
};

constexpr std::size_t MaximumPreparedEntries = 16'384;
constexpr std::uint64_t PreparedTokenMask = 0xffff000000000000ull;
constexpr std::uint64_t PreparedFunctionTag = 0x4bcf000000000000ull;
constexpr std::uint64_t PreparedPropertyTag = 0x4bce000000000000ull;
std::array<std::atomic<const PreparedFunctionEntry*>, MaximumPreparedEntries>
    PreparedFunctions{};
std::array<std::atomic<const PreparedPropertyEntry*>, MaximumPreparedEntries>
    PreparedProperties{};
std::atomic_uint32_t NextPreparedFunction{};
std::atomic_uint32_t NextPreparedProperty{};

template <typename T>
const T* preparedEntry(
    std::uint64_t token, std::uint64_t expectedTag,
    const std::array<std::atomic<const T*>, MaximumPreparedEntries>& entries) {
    if ((token & PreparedTokenMask) != expectedTag) return nullptr;
    const auto ordinal = token & ~PreparedTokenMask;
    if (ordinal == 0 || ordinal > MaximumPreparedEntries) return nullptr;
    return entries[static_cast<std::size_t>(ordinal - 1)].load(std::memory_order_acquire);
}

template <typename T>
bool publishPrepared(
    T* entry, std::uint64_t tag, std::atomic_uint32_t& next,
    std::array<std::atomic<const T*>, MaximumPreparedEntries>& entries,
    std::uint64_t& token) {
    const auto index = next.fetch_add(1, std::memory_order_relaxed);
    if (index >= MaximumPreparedEntries) {
        delete entry;
        return false;
    }
    entries[index].store(entry, std::memory_order_release);
    token = tag | (static_cast<std::uint64_t>(index) + 1);
    return true;
}

} // namespace

void setCapturedGameThreadId(std::uint32_t threadId) noexcept {
    CapturedGameThreadId.store(threadId, std::memory_order_release);
}

std::uint32_t capturedGameThreadId() noexcept {
    return CapturedGameThreadId.load(std::memory_order_acquire);
}

bool onCapturedGameThread() noexcept {
    const auto captured = capturedGameThreadId();
    return captured != 0 && GetCurrentThreadId() == captured;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeFunction(
    void*, BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength,
    std::uint32_t expectedParameterSize, void* parameters,
    std::uint32_t parameterSize) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
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

    const auto processEvent = processEventFor(object);
    if (!processEvent) return BRIEFCASE_UNREAL_UNREADABLE;

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
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
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

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiPrepareFunction(
    void*, BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::uint32_t expectedParameterSize,
    const BriefcasePreparedParameter* parameters, std::uint32_t parameterCount,
    std::uint64_t* preparedFunction) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!preparedFunction || !writable(preparedFunction, sizeof(*preparedFunction)) ||
        parameterCount > 1024 ||
        (parameterCount && (!parameters || !readable(
            parameters, sizeof(BriefcasePreparedParameter) * parameterCount))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *preparedFunction = 0;
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* function = findFunction(ownerClass, name);
    if (!function) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (function->ParmsSize != expectedParameterSize || expectedParameterSize > 65'535u)
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    constexpr std::uint64_t ConstParameterFlag = 0x2;
    constexpr std::uint64_t ParameterFlag = 0x80;
    constexpr std::uint64_t OutParameterFlag = 0x100;
    constexpr std::uint64_t ReturnParameterFlag = 0x400;
    constexpr std::uint64_t ReferenceParameterFlag = 0x08000000;
    std::vector<const FProperty*> reflected;
    auto* field = function->ChildProperties;
    for (unsigned visited = 0; field && visited < 4096; ++visited) {
        if (!readable(field, sizeof(FProperty))) return BRIEFCASE_UNREAL_UNREADABLE;
        const auto* property = reinterpret_cast<const FProperty*>(field);
        if ((property->PropertyFlags & ParameterFlag) != 0) reflected.push_back(property);
        field = field->Next;
    }
    if (reflected.size() != parameterCount) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;

    auto* entry = new (std::nothrow) PreparedFunctionEntry{};
    if (!entry) return BRIEFCASE_UNREAL_UNREADABLE;
    entry->OwnerClass = ownerClass;
    entry->Function = function;
    entry->ParameterSize = expectedParameterSize;
    entry->ObjectArguments.reserve(parameterCount);
    entry->ValueArguments.reserve(parameterCount);
    std::vector<bool> matched(reflected.size());

    for (std::uint32_t index = 0; index < parameterCount; ++index) {
        BriefcasePreparedParameter descriptor{};
        if (!safeCopy(&descriptor, parameters + index, sizeof(descriptor)) ||
            descriptor.StructSize < sizeof(BriefcasePreparedParameter)) {
            delete entry;
            return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
        }
        const auto allowedFlags = BRIEFCASE_PREPARED_INPUT | BRIEFCASE_PREPARED_OUTPUT |
                                  BRIEFCASE_PREPARED_RETURN | BRIEFCASE_PREPARED_REFERENCE;
        if ((descriptor.Flags & ~allowedFlags) != 0 || descriptor.Offset < 0 ||
            descriptor.ElementSize <= 0 ||
            static_cast<std::uint64_t>(descriptor.Offset) +
                static_cast<std::uint64_t>(descriptor.ElementSize) > expectedParameterSize) {
            delete entry;
            return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
        }

        std::size_t matchIndex = reflected.size();
        for (std::size_t candidate = 0; candidate < reflected.size(); ++candidate) {
            const auto* property = reflected[candidate];
            if (!matched[candidate] && property->OffsetInternal == descriptor.Offset &&
                property->ElementSize == descriptor.ElementSize && property->ArrayDim == 1 &&
                propertyKind(property) == static_cast<BriefcasePropertyKind>(descriptor.Kind)) {
                matchIndex = candidate;
                break;
            }
        }
        if (matchIndex == reflected.size()) {
            delete entry;
            return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
        }
        matched[matchIndex] = true;
        const auto* property = reflected[matchIndex];
        std::uint32_t actualFlags{};
        const bool constReference =
            (property->PropertyFlags &
                (ReferenceParameterFlag | ConstParameterFlag)) ==
                (ReferenceParameterFlag | ConstParameterFlag);
        const bool output = !constReference && (property->PropertyFlags &
            (OutParameterFlag | ReturnParameterFlag)) != 0;
        const bool returned = (property->PropertyFlags & ReturnParameterFlag) != 0;
        const bool reference =
            (property->PropertyFlags & ReferenceParameterFlag) != 0 &&
            !constReference;
        if (!output || reference) actualFlags |= BRIEFCASE_PREPARED_INPUT;
        if (output) actualFlags |= BRIEFCASE_PREPARED_OUTPUT;
        if (returned) actualFlags |= BRIEFCASE_PREPARED_RETURN;
        if (reference) actualFlags |= BRIEFCASE_PREPARED_REFERENCE;
        if (descriptor.Flags != actualFlags) {
            delete entry;
            return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
        }

        const auto kind = static_cast<BriefcasePropertyKind>(descriptor.Kind);
        const bool hasInput = !output || reference;
        const bool directObject = kind == BRIEFCASE_PROPERTY_OBJECT &&
                                  isDirectPreparedObject(property) &&
                                  property->ElementSize == sizeof(void*);
        const bool plain = isPreparedPlainProperty(property, 0);
        bool wireInput = false;
        bool wireOutput = false;
        if (directObject) {
            entry->ObjectArguments.push_back({descriptor.Offset, output, hasInput});
        } else if (!plain) {
            if (isPreparedCanonicalProperty(property, 0)) {
                entry->UsesCanonicalMarshalling = true;
            } else {
                if (hasInput) {
                    if (!isWireWritableProperty(property, 0)) {
                        delete entry;
                        return BRIEFCASE_UNREAL_UNSUPPORTED;
                    }
                    wireInput = true;
                    entry->UsesWireInputs = true;
                }
                if (output) {
                    if (!isWireReadableProperty(property, 0)) {
                        delete entry;
                        return BRIEFCASE_UNREAL_UNSUPPORTED;
                    }
                    wireOutput = true;
                    entry->UsesWireOutputs = true;
                }
                if (!wireInput && !wireOutput) {
                    delete entry;
                    return BRIEFCASE_UNREAL_UNSUPPORTED;
                }
            }
        }
        entry->ValueArguments.push_back({
            property, descriptor.Offset, descriptor.ElementSize,
            output, hasInput, wireInput, wireOutput});
    }

    std::uint64_t token{};
    if (!publishPrepared(
            entry, PreparedFunctionTag, NextPreparedFunction,
            PreparedFunctions, token)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    *preparedFunction = token;
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokePreparedFunction(
    void*, std::uint64_t preparedFunction, BriefcaseObjectHandle objectHandle,
    void* parameters, std::uint32_t parameterSize) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    const auto* entry = preparedEntry(
        preparedFunction, PreparedFunctionTag, PreparedFunctions);
    if (!entry) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (entry->UsesWireInputs || entry->UsesWireOutputs)
        return BRIEFCASE_UNREAL_UNSUPPORTED;
    if (entry->ParameterSize != parameterSize || parameterSize > 65'535u ||
        (parameterSize && (!parameters || !writable(parameters, parameterSize))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (!readable(entry->OwnerClass, sizeof(UStruct)) ||
        !readable(entry->Function, sizeof(UFunction)) ||
        entry->Function->ParmsSize != entry->ParameterSize)
        return BRIEFCASE_UNREAL_NOT_READY;
    auto* object = const_cast<UObject*>(resolveObject(objectHandle));
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!objectIsA(object, entry->OwnerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    if (entry->UsesCanonicalMarshalling) {
        // The managed buffer is an address-free canonical image. Build a native
        // ProcessEvent buffer for this call, resolve every nested handle, then
        // copy only declared outputs back as canonical values. This branch is
        // used only for fixed-layout values, so no Unreal-owned lifetime exists
        // in the temporary storage.
        std::vector<std::byte> nativeParameters(parameterSize);
        auto* canonical = reinterpret_cast<std::byte*>(parameters);
        for (const auto& argument : entry->ValueArguments) {
            if (!argument.HasInput) continue;
            if (!writeCanonicalFixedValue(
                    argument.Property,
                    nativeParameters.data() + argument.Offset,
                    canonical + argument.Offset,
                    static_cast<std::size_t>(argument.ElementSize), 0))
                return BRIEFCASE_UNREAL_STALE_HANDLE;
        }

        const auto processEvent = processEventFor(object);
        if (!processEvent || !safeProcessEvent(
                processEvent, object, entry->Function, nativeParameters.data()))
            return BRIEFCASE_UNREAL_UNREADABLE;

        for (const auto& argument : entry->ValueArguments) {
            if (!argument.Output) continue;
            auto* output = canonical + argument.Offset;
            std::memset(output, 0, static_cast<std::size_t>(argument.ElementSize));
            if (!copyCanonicalFixedValue(
                    argument.Property,
                    nativeParameters.data() + argument.Offset,
                    output, static_cast<std::size_t>(argument.ElementSize), 0))
                return BRIEFCASE_UNREAL_UNREADABLE;
        }
        return BRIEFCASE_UNREAL_OK;
    }

    if (entry->ObjectArguments.size() > 64) return BRIEFCASE_UNREAL_UNSUPPORTED;
    std::array<BriefcaseObjectHandle, 64> originals{};
    for (std::size_t index = 0; index < entry->ObjectArguments.size(); ++index) {
        const auto& argument = entry->ObjectArguments[index];
        auto* slot = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
        BriefcaseObjectHandle supplied{std::numeric_limits<std::uint32_t>::max(), 0};
        UObject* resolved{};
        if (argument.HasInput) {
            if (!safeCopy(&supplied, slot, sizeof(supplied))) return BRIEFCASE_UNREAL_UNREADABLE;
            if (supplied.Index != std::numeric_limits<std::uint32_t>::max()) {
                resolved = const_cast<UObject*>(resolveObject(supplied));
                if (!resolved) return BRIEFCASE_UNREAL_STALE_HANDLE;
            }
        }
        originals[index] = supplied;
        if (!safeCopy(slot, &resolved, sizeof(resolved))) return BRIEFCASE_UNREAL_UNREADABLE;
    }

    const auto restoreInputs = [&]() noexcept {
        for (std::size_t index = 0; index < entry->ObjectArguments.size(); ++index) {
            const auto& argument = entry->ObjectArguments[index];
            if (!argument.HasInput) continue;
            auto* slot = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
            safeCopy(slot, &originals[index], sizeof(originals[index]));
        }
    };
    const auto processEvent = processEventFor(object);
    if (!processEvent || !safeProcessEvent(
            processEvent, object, entry->Function, parameters)) {
        restoreInputs();
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    for (std::size_t index = 0; index < entry->ObjectArguments.size(); ++index) {
        const auto& argument = entry->ObjectArguments[index];
        auto* slot = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
        if (!argument.Output) {
            if (!safeCopy(slot, &originals[index], sizeof(originals[index])))
                return BRIEFCASE_UNREAL_UNREADABLE;
            continue;
        }
        UObject* returned{};
        if (!safeCopy(&returned, slot, sizeof(returned))) return BRIEFCASE_UNREAL_UNREADABLE;
        BriefcaseObjectHandle handle{};
        if (!makeHandle(returned, handle) || !safeCopy(slot, &handle, sizeof(handle)))
            return BRIEFCASE_UNREAL_STALE_HANDLE;
    }
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokePreparedValueFunction(
    void*, std::uint64_t preparedFunction, BriefcaseObjectHandle objectHandle,
    void* parameters, std::uint32_t parameterSize,
    BriefcaseOwnedValueBuffer* outputs) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    if (!outputs || !writable(outputs, sizeof(*outputs)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *outputs = {};
    const auto* entry = preparedEntry(
        preparedFunction, PreparedFunctionTag, PreparedFunctions);
    if (!entry || !entry->UsesWireOutputs) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (entry->UsesWireInputs) return BRIEFCASE_UNREAL_UNSUPPORTED;
    if (entry->ParameterSize != parameterSize || parameterSize > 65'535u ||
        (parameterSize && (!parameters || !writable(parameters, parameterSize))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (!readable(entry->OwnerClass, sizeof(UStruct)) ||
        !readable(entry->Function, sizeof(UFunction)) ||
        entry->Function->ParmsSize != entry->ParameterSize)
        return BRIEFCASE_UNREAL_NOT_READY;
    auto* object = const_cast<UObject*>(resolveObject(objectHandle));
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!objectIsA(object, entry->OwnerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    std::vector<std::byte> nativeParameters(parameterSize);
    std::vector<const PreparedValueArgument*> initialized;
    const auto cleanup = [&]() noexcept {
        for (auto iterator = initialized.rbegin(); iterator != initialized.rend(); ++iterator)
            destroyPropertyValue(
                (*iterator)->Property, nativeParameters.data() + (*iterator)->Offset);
    };
    for (const auto& argument : entry->ValueArguments) {
        if (!argument.WireOutput) continue;
        if (!initializePropertyValue(
                argument.Property, nativeParameters.data() + argument.Offset)) {
            cleanup();
            return BRIEFCASE_UNREAL_UNREADABLE;
        }
        initialized.push_back(&argument);
    }

    const auto* canonical = reinterpret_cast<const std::byte*>(parameters);
    for (const auto& argument : entry->ValueArguments) {
        if (!argument.HasInput) continue;
        if (!writeCanonicalFixedValue(
                argument.Property, nativeParameters.data() + argument.Offset,
                canonical + argument.Offset,
                static_cast<std::size_t>(argument.ElementSize), 0)) {
            cleanup();
            return BRIEFCASE_UNREAL_STALE_HANDLE;
        }
    }

    const auto processEvent = processEventFor(object);
    if (!processEvent || !safeProcessEvent(
            processEvent, object, entry->Function, nativeParameters.data())) {
        cleanup();
        return BRIEFCASE_UNREAL_UNREADABLE;
    }

    ValueWireBuilder wire;
    std::uint32_t wireCount{};
    for (const auto& argument : entry->ValueArguments)
        if (argument.WireOutput) ++wireCount;
    if (!wire.append(ValueOutputsWireMagic) || !wire.append(wireCount)) {
        cleanup();
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    for (const auto& argument : entry->ValueArguments) {
        if (argument.WireOutput) {
            if (!wire.append(argument.Offset) || !appendValueNode(
                    argument.Property, nativeParameters.data() + argument.Offset,
                    wire, 0)) {
                cleanup();
                return BRIEFCASE_UNREAL_UNREADABLE;
            }
        } else if (argument.Output) {
            auto* destination = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
            std::memset(destination, 0, static_cast<std::size_t>(argument.ElementSize));
            if (!copyCanonicalFixedValue(
                    argument.Property, nativeParameters.data() + argument.Offset,
                    destination, static_cast<std::size_t>(argument.ElementSize), 0)) {
                cleanup();
                return BRIEFCASE_UNREAL_UNREADABLE;
            }
        }
    }
    cleanup();

    if (wire.Bytes.size() > MaximumValueWireBytes ||
        wire.Bytes.size() > std::numeric_limits<std::uint32_t>::max())
        return BRIEFCASE_UNREAL_UNREADABLE;
    auto* copy = new (std::nothrow) std::uint8_t[wire.Bytes.size()];
    if (!copy) return BRIEFCASE_UNREAL_UNREADABLE;
    std::memcpy(copy, wire.Bytes.data(), wire.Bytes.size());
    outputs->Data = copy;
    outputs->Size = static_cast<std::uint32_t>(wire.Bytes.size());
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokePreparedValueFunctionV2(
    void*, std::uint64_t preparedFunction, BriefcaseObjectHandle objectHandle,
    void* parameters, std::uint32_t parameterSize,
    const BriefcaseValueInput* inputs, std::uint32_t inputCount,
    BriefcaseOwnedValueBuffer* outputs) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!onCapturedGameThread()) return BRIEFCASE_UNREAL_WRONG_THREAD;
    if (!outputs || !writable(outputs, sizeof(*outputs)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *outputs = {};
    const auto* entry = preparedEntry(
        preparedFunction, PreparedFunctionTag, PreparedFunctions);
    if (!entry || (!entry->UsesWireInputs && !entry->UsesWireOutputs))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (entry->ParameterSize != parameterSize || parameterSize > 65'535u ||
        (parameterSize && (!parameters || !writable(parameters, parameterSize))) ||
        inputCount > 256 || (inputCount &&
            (!inputs || !readableRange(inputs,
                static_cast<std::size_t>(inputCount) * sizeof(BriefcaseValueInput)))))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (!readable(entry->OwnerClass, sizeof(UStruct)) ||
        !readable(entry->Function, sizeof(UFunction)) ||
        entry->Function->ParmsSize != entry->ParameterSize)
        return BRIEFCASE_UNREAL_NOT_READY;
    auto* object = const_cast<UObject*>(resolveObject(objectHandle));
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!objectIsA(object, entry->OwnerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;

    struct SuppliedInput {
        BriefcaseValueInput Descriptor{};
        bool Matched{};
    };
    std::vector<SuppliedInput> supplied(inputCount);
    for (std::uint32_t index = 0; index < inputCount; ++index) {
        if (!safeCopy(&supplied[index].Descriptor, inputs + index,
                      sizeof(BriefcaseValueInput)) ||
            supplied[index].Descriptor.StructSize < sizeof(BriefcaseValueInput) ||
            supplied[index].Descriptor.ParameterOffset < 0 ||
            supplied[index].Descriptor.Size < 12 ||
            supplied[index].Descriptor.Size > MaximumValueWireBytes ||
            !supplied[index].Descriptor.Data ||
            !readableRange(
                supplied[index].Descriptor.Data, supplied[index].Descriptor.Size))
            return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    }

    std::vector<std::byte> nativeParameters(parameterSize);
    std::vector<const PreparedValueArgument*> initialized;
    const auto cleanup = [&]() noexcept {
        for (auto iterator = initialized.rbegin(); iterator != initialized.rend(); ++iterator)
            destroyPropertyValue(
                (*iterator)->Property, nativeParameters.data() + (*iterator)->Offset);
    };
    for (const auto& argument : entry->ValueArguments) {
        if (!argument.WireInput && !argument.WireOutput) continue;
        if (!initializePropertyValue(
                argument.Property, nativeParameters.data() + argument.Offset)) {
            cleanup();
            return BRIEFCASE_UNREAL_UNREADABLE;
        }
        initialized.push_back(&argument);
    }

    const auto* canonical = reinterpret_cast<const std::byte*>(parameters);
    for (const auto& argument : entry->ValueArguments) {
        if (!argument.HasInput) continue;
        if (argument.WireInput) {
            auto suppliedValue = supplied.end();
            for (auto iterator = supplied.begin(); iterator != supplied.end(); ++iterator) {
                if (!iterator->Matched && iterator->Descriptor.ParameterOffset == argument.Offset) {
                    suppliedValue = iterator;
                    break;
                }
            }
            if (suppliedValue == supplied.end() || !decodeValueEnvelope(
                    argument.Property, nativeParameters.data() + argument.Offset,
                    suppliedValue->Descriptor.Data, suppliedValue->Descriptor.Size)) {
                cleanup();
                return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
            }
            suppliedValue->Matched = true;
        } else if (!writeCanonicalFixedValue(
                argument.Property, nativeParameters.data() + argument.Offset,
                canonical + argument.Offset,
                static_cast<std::size_t>(argument.ElementSize), 0)) {
            cleanup();
            return BRIEFCASE_UNREAL_STALE_HANDLE;
        }
    }
    if (std::any_of(supplied.begin(), supplied.end(),
            [](const auto& value) { return !value.Matched; })) {
        cleanup();
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    }

    const auto processEvent = processEventFor(object);
    if (!processEvent || !safeProcessEvent(
            processEvent, object, entry->Function, nativeParameters.data())) {
        cleanup();
        return BRIEFCASE_UNREAL_UNREADABLE;
    }

    ValueWireBuilder wire;
    std::uint32_t wireCount{};
    for (const auto& argument : entry->ValueArguments)
        if (argument.WireOutput) ++wireCount;
    if (!wire.append(ValueOutputsWireMagic) || !wire.append(wireCount)) {
        cleanup();
        return BRIEFCASE_UNREAL_UNREADABLE;
    }
    for (const auto& argument : entry->ValueArguments) {
        if (argument.WireOutput) {
            if (!wire.append(argument.Offset) || !appendValueNode(
                    argument.Property, nativeParameters.data() + argument.Offset,
                    wire, 0)) {
                cleanup();
                return BRIEFCASE_UNREAL_UNREADABLE;
            }
        } else if (argument.Output) {
            auto* destination = reinterpret_cast<std::byte*>(parameters) + argument.Offset;
            std::memset(destination, 0, static_cast<std::size_t>(argument.ElementSize));
            if (!copyCanonicalFixedValue(
                    argument.Property, nativeParameters.data() + argument.Offset,
                    destination, static_cast<std::size_t>(argument.ElementSize), 0)) {
                cleanup();
                return BRIEFCASE_UNREAL_UNREADABLE;
            }
        }
    }
    cleanup();

    if (wire.Bytes.size() > MaximumValueWireBytes ||
        wire.Bytes.size() > std::numeric_limits<std::uint32_t>::max())
        return BRIEFCASE_UNREAL_UNREADABLE;
    auto* copy = new (std::nothrow) std::uint8_t[wire.Bytes.size()];
    if (!copy) return BRIEFCASE_UNREAL_UNREADABLE;
    std::memcpy(copy, wire.Bytes.data(), wire.Bytes.size());
    outputs->Data = copy;
    outputs->Size = static_cast<std::uint32_t>(wire.Bytes.size());
    return BRIEFCASE_UNREAL_OK;
}

void BRIEFCASE_MOD_CALL apiReleaseValueBuffer(
    void*, BriefcaseOwnedValueBuffer* buffer) {
    if (!buffer || !writable(buffer, sizeof(*buffer))) return;
    delete[] buffer->Data;
    *buffer = {};
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiPrepareProperty(
    void*, BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, std::uint64_t* preparedProperty) {
    if (!RuntimeObjects || !RuntimeNameConverter) return BRIEFCASE_UNREAL_NOT_READY;
    if (!preparedProperty || !writable(preparedProperty, sizeof(*preparedProperty)))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    *preparedProperty = 0;
    const auto* ownerClass = resolveObject(ownerClassHandle);
    if (!ownerClass) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!isClassObject(ownerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    std::wstring name;
    if (!decodeUtf8(utf8Name, nameLength, name)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const auto* property = findProperty(ownerClass, name);
    if (!property) return BRIEFCASE_UNREAL_NOT_FOUND;
    if (expectedOffset < 0 || expectedElementSize <= 0 || expectedArrayDimension != 1 ||
        property->OffsetInternal != expectedOffset ||
        property->ElementSize != expectedElementSize || property->ArrayDim != 1 ||
        propertyKind(property) != expectedKind ||
        (!isPreparedAggregateKind(expectedKind) &&
         canonicalSize(expectedKind) != expectedElementSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    const bool directObject = expectedKind == BRIEFCASE_PROPERTY_OBJECT &&
                              isDirectPreparedObject(property);
    const bool canonical = directObject || isPreparedPlainProperty(property, 0) ||
                           isPreparedCanonicalProperty(property, 0);
    const bool wireValue = !canonical && isWireWritableProperty(property, 0);
    if (!canonical && !wireValue)
        return BRIEFCASE_UNREAL_UNSUPPORTED;

    auto* entry = new (std::nothrow) PreparedPropertyEntry{
        ownerClass, property, expectedOffset, expectedElementSize, expectedKind, wireValue};
    if (!entry) return BRIEFCASE_UNREAL_UNREADABLE;
    std::uint64_t token{};
    if (!publishPrepared(
            entry, PreparedPropertyTag, NextPreparedProperty,
            PreparedProperties, token)) return BRIEFCASE_UNREAL_UNSUPPORTED;
    *preparedProperty = token;
    return BRIEFCASE_UNREAL_OK;
}

BriefcaseUnrealResult resolvePreparedProperty(
    std::uint64_t token, BriefcaseObjectHandle objectHandle,
    const PreparedPropertyEntry*& entry, const UObject*& object,
    std::byte*& value) {
    entry = preparedEntry(token, PreparedPropertyTag, PreparedProperties);
    if (!entry) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    if (!readable(entry->OwnerClass, sizeof(UStruct)) ||
        !readable(entry->Property, sizeof(FProperty)))
        return BRIEFCASE_UNREAL_NOT_READY;
    object = resolveObject(objectHandle);
    if (!object) return BRIEFCASE_UNREAL_STALE_HANDLE;
    if (!objectIsA(object, entry->OwnerClass)) return BRIEFCASE_UNREAL_TYPE_MISMATCH;
    const auto* actualClass = reinterpret_cast<const UStruct*>(object->ClassPrivate);
    if (!readable(actualClass, sizeof(UStruct)) || actualClass->PropertiesSize < 0 ||
        static_cast<std::uint64_t>(entry->Offset) +
            static_cast<std::uint64_t>(entry->ElementSize) >
                static_cast<std::uint64_t>(actualClass->PropertiesSize))
        return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    value = reinterpret_cast<std::byte*>(const_cast<UObject*>(object)) + entry->Offset;
    return readable(value, static_cast<std::size_t>(entry->ElementSize))
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReadPreparedProperty(
    void*, std::uint64_t preparedProperty, BriefcaseObjectHandle objectHandle,
    void* output, std::uint32_t outputSize) {
    const PreparedPropertyEntry* entry{};
    const UObject* object{};
    std::byte* source{};
    const auto resolved = resolvePreparedProperty(
        preparedProperty, objectHandle, entry, object, source);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;
    if (entry->WireValue) return BRIEFCASE_UNREAL_UNSUPPORTED;
    const auto required = entry->Kind == BRIEFCASE_PROPERTY_BOOL
        ? static_cast<std::uint32_t>(sizeof(BriefcaseBool))
        : entry->Kind == BRIEFCASE_PROPERTY_OBJECT
            ? static_cast<std::uint32_t>(sizeof(BriefcaseObjectHandle))
            : static_cast<std::uint32_t>(entry->ElementSize);
    if (!output || outputSize < required || !writable(output, required))
        return BRIEFCASE_UNREAL_BUFFER_TOO_SMALL;
    if (entry->Kind == BRIEFCASE_PROPERTY_BOOL) {
        const auto* boolean = reinterpret_cast<const FBoolProperty*>(entry->Property);
        if (!readable(boolean, sizeof(FBoolProperty)) || boolean->FieldSize != 1 ||
            boolean->ByteOffset >= entry->ElementSize || boolean->FieldMask == 0)
            return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
        std::uint8_t storage{};
        if (!safeCopy(&storage, source + boolean->ByteOffset, 1))
            return BRIEFCASE_UNREAL_UNREADABLE;
        const BriefcaseBool value = (storage & boolean->FieldMask) != 0 ? 1u : 0u;
        return safeCopy(output, &value, sizeof(value))
            ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
    }
    if (entry->Kind == BRIEFCASE_PROPERTY_OBJECT ||
        entry->Kind == BRIEFCASE_PROPERTY_STRUCT) {
        std::memset(output, 0, required);
        return copyCanonicalFixedValue(
            entry->Property, source, reinterpret_cast<std::byte*>(output), required, 0)
            ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
    }
    return safeCopy(output, source, required)
        ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePreparedProperty(
    void*, std::uint64_t preparedProperty, BriefcaseObjectHandle objectHandle,
    const void* input, std::uint32_t inputSize) {
    if (!input || inputSize == 0 || !readable(input, inputSize))
        return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const PreparedPropertyEntry* entry{};
    const UObject* object{};
    std::byte* destination{};
    const auto resolved = resolvePreparedProperty(
        preparedProperty, objectHandle, entry, object, destination);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;
    if (entry->WireValue) return BRIEFCASE_UNREAL_UNSUPPORTED;
    const auto required = entry->Kind == BRIEFCASE_PROPERTY_BOOL ? 1u :
        entry->Kind == BRIEFCASE_PROPERTY_OBJECT
            ? static_cast<std::uint32_t>(sizeof(BriefcaseObjectHandle))
            : static_cast<std::uint32_t>(entry->ElementSize);
    if (inputSize != required) return BRIEFCASE_UNREAL_LAYOUT_MISMATCH;
    if (!writable(destination, static_cast<std::size_t>(entry->ElementSize)))
        return BRIEFCASE_UNREAL_UNREADABLE;
    return writeCanonicalFixedValue(
        entry->Property, destination, reinterpret_cast<const std::byte*>(input),
        inputSize, 0) ? BRIEFCASE_UNREAL_OK : BRIEFCASE_UNREAL_UNREADABLE;
}

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePreparedValueProperty(
    void*, std::uint64_t preparedProperty, BriefcaseObjectHandle objectHandle,
    const std::uint8_t* input, std::uint32_t inputSize) {
    if (!input || inputSize < 12 || inputSize > MaximumValueWireBytes ||
        !readableRange(input, inputSize)) return BRIEFCASE_UNREAL_INVALID_ARGUMENT;
    const PreparedPropertyEntry* entry{};
    const UObject* object{};
    std::byte* destination{};
    const auto resolved = resolvePreparedProperty(
        preparedProperty, objectHandle, entry, object, destination);
    if (resolved != BRIEFCASE_UNREAL_OK) return resolved;
    if (!entry->WireValue) return BRIEFCASE_UNREAL_UNSUPPORTED;
    return replacePropertyValueFromWire(
        entry->Property, destination, input, inputSize);
}

} // namespace briefcase::unreal
