#pragma once

#include "UnrealReflection.h"

#include <Briefcase/BriefcaseModApi.h>

#include <array>
#include <cstddef>
#include <cstdint>

namespace briefcase::unreal {

using ProcessEventFn = void(__fastcall*)(UObject*, const UFunction*, void*);

// Resolved from the engine's GMalloc global after profile and signature
// validation. It is non-owning and remains stable for the process lifetime.
extern void* RuntimeMalloc;

[[nodiscard]] bool safeProcessEvent(
    ProcessEventFn processEvent,
    UObject* object,
    const UFunction* function,
    void* parameters) noexcept;

[[nodiscard]] bool initializePropertyValue(
    const FProperty* property, void* value) noexcept;
bool destroyPropertyValue(
    const FProperty* property, void* value) noexcept;
[[nodiscard]] bool validUnrealAllocator(void* allocator);
[[nodiscard]] bool safePropertyHash(
    const FProperty* property,
    const void* value,
    std::uint32_t& result) noexcept;
[[nodiscard]] bool safePropertyIdentical(
    const FProperty* property,
    const void* left,
    const void* right,
    bool& result) noexcept;
[[nodiscard]] bool safePropertyAlignment(
    const FProperty* property, std::uint32_t& result) noexcept;
[[nodiscard]] bool safeUnrealMalloc(
    std::size_t size,
    std::uint32_t alignment,
    void*& result) noexcept;
bool safeUnrealFree(void* allocation) noexcept;

struct TextConversionRuntime {
    UObject* Library{};
    const UFunction* StringToText{};
    const FProperty* StringToTextReturn{};
    const UFunction* TextToString{};
    const FProperty* TextToStringReturn{};
};

[[nodiscard]] bool resolveTextConversionRuntime(
    TextConversionRuntime& result);
[[nodiscard]] ProcessEventFn processEventFor(const UObject* object);

struct OwnedTextValue {
    std::array<std::byte, 40> Parameters{};
    const FProperty* Property{};
    bool Initialized{};
};

[[nodiscard]] bool constructText(
    const TextConversionRuntime& runtime,
    const std::uint16_t* characters,
    std::uint32_t characterCount,
    OwnedTextValue& result);
void destroyText(OwnedTextValue& value) noexcept;

struct OwnedStringValue {
    std::array<std::byte, 40> Parameters{};
    const FProperty* Property{};
    bool Initialized{};
};

[[nodiscard]] bool constructString(
    const TextConversionRuntime& runtime,
    const std::uint16_t* characters,
    std::uint32_t characterCount,
    OwnedStringValue& result);
[[nodiscard]] BriefcaseUnrealResult textToUtf16(
    const TextConversionRuntime& runtime,
    const void* textValue,
    std::uint16_t* destination,
    std::uint32_t capacityCharacters,
    std::uint32_t* requiredCharacters);

[[nodiscard]] BriefcasePropertyKind propertyKind(const FProperty* property);
[[nodiscard]] bool isPreparedPlainProperty(
    const FProperty* property, unsigned depth);
[[nodiscard]] bool isDirectPreparedObject(const FProperty* property);

BriefcaseUnrealResult resolvePropertyAccess(
    BriefcaseObjectHandle objectHandle, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, const UObject*& object,
    const FProperty*& property, std::byte*& value);

} // namespace briefcase::unreal
