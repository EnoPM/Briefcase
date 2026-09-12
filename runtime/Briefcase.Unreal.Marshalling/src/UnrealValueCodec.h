#pragma once

#include "UnrealMarshalling.h"

#include <cstddef>
#include <cstdint>
#include <vector>

namespace briefcase::unreal {

inline constexpr std::uint32_t ValueWireMagic = 0x31435642u; // "BVC1"
inline constexpr std::uint32_t ValueOutputsWireMagic = 0x314f5642u; // "BVO1"
inline constexpr std::size_t MaximumValueWireBytes = 32u * 1024u * 1024u;
inline constexpr std::int32_t MaximumContainerElements = 100'000;
inline constexpr unsigned MaximumValueDepth = 8;

// Builds a bounded little-endian BVC1/BVO1 payload. Node payload sizes are
// back-filled only after their complete, recursively validated value is present.
struct ValueWireBuilder {
    std::vector<std::byte> Bytes;

    [[nodiscard]] bool append(const void* source, std::size_t size);

    template <typename TValue>
    [[nodiscard]] bool append(const TValue& value) {
        return append(&value, sizeof(value));
    }

    [[nodiscard]] std::size_t beginNode(BriefcasePropertyKind kind);
    [[nodiscard]] bool endNode(std::size_t start);
};

[[nodiscard]] std::int32_t canonicalSize(BriefcasePropertyKind kind);
[[nodiscard]] bool isPreparedAggregateKind(BriefcasePropertyKind kind);
[[nodiscard]] bool isPreparedCanonicalProperty(
    const FProperty* property, unsigned depth);
[[nodiscard]] bool isWireReadableProperty(
    const FProperty* property, unsigned depth);
[[nodiscard]] bool isWireWritableProperty(
    const FProperty* property, unsigned depth);

[[nodiscard]] bool copyCanonicalFixedValue(
    const FProperty* property,
    const std::byte* source,
    std::byte* destination,
    std::size_t destinationSize,
    unsigned depth);
[[nodiscard]] bool writeCanonicalFixedValue(
    const FProperty* property,
    std::byte* destination,
    const std::byte* source,
    std::size_t sourceSize,
    unsigned depth);

[[nodiscard]] bool appendValueNode(
    const FProperty* property,
    const std::byte* source,
    ValueWireBuilder& output,
    unsigned depth);
[[nodiscard]] bool decodeValueEnvelope(
    const FProperty* property,
    std::byte* destination,
    const std::uint8_t* input,
    std::uint32_t inputSize);
[[nodiscard]] BriefcaseUnrealResult replacePropertyValueFromWire(
    const FProperty* property,
    std::byte* destination,
    const std::uint8_t* input,
    std::uint32_t inputSize);

} // namespace briefcase::unreal
