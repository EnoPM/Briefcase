#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace briefcase::discovery {

struct PatternByte {
    std::uint8_t Value{};
    bool Exact{};
};

// Patterns are written in the same form used by reverse-engineering tools:
// two hexadecimal digits mean an exact byte and ?? means a wildcard. Parsing
// at compile time keeps malformed runtime profiles from compiling.
template <std::size_t Length>
consteval auto makePattern(const char (&text)[Length]) {
    static_assert(Length % 3 == 0,
                  "A signature must contain two-character tokens separated by spaces.");
    constexpr auto count = Length / 3;
    std::array<PatternByte, count> result{};

    const auto nibble = [](char value) consteval -> std::uint8_t {
        if (value >= '0' && value <= '9') return static_cast<std::uint8_t>(value - '0');
        if (value >= 'A' && value <= 'F') return static_cast<std::uint8_t>(value - 'A' + 10);
        if (value >= 'a' && value <= 'f') return static_cast<std::uint8_t>(value - 'a' + 10);
        throw "A signature contains a non-hexadecimal digit.";
    };

    for (std::size_t index = 0; index < count; ++index) {
        const auto offset = index * 3;
        if (index != 0 && text[offset - 1] != ' ')
            throw "Signature tokens must be separated by one space.";
        if (text[offset] == '?' && text[offset + 1] == '?') {
            result[index] = PatternByte{0, false};
            continue;
        }
        result[index] = PatternByte{
            static_cast<std::uint8_t>((nibble(text[offset]) << 4) |
                                      nibble(text[offset + 1])),
            true};
    }
    return result;
}

struct PatternView {
    std::span<const PatternByte> Bytes;

    template <std::size_t Size>
    constexpr explicit PatternView(const std::array<PatternByte, Size>& bytes) noexcept
        : Bytes(bytes) {}
};

struct PatternMatches {
    std::vector<std::size_t> Offsets;
    bool Truncated{};
};

enum class ResolutionStatus {
    Resolved,
    NotFound,
    Ambiguous,
    InvalidEncoding,
    OutsideImage
};

struct SignatureResolution {
    ResolutionStatus Status{ResolutionStatus::NotFound};
    std::uint32_t Rva{};
    std::size_t MatchCount{};
    std::size_t CandidateCount{};
};

PatternMatches findPatternMatches(
    std::span<const std::byte> bytes,
    PatternView pattern,
    std::size_t maximumMatches = 4096);

SignatureResolution resolveUniqueMatch(
    std::span<const std::byte> section,
    std::uint32_t sectionRva,
    PatternView pattern,
    std::uint32_t imageSize);

// Every matching instruction contributes one RIP-relative target. Several
// call sites are accepted only when all of them resolve to the same RVA.
SignatureResolution resolveRipRelativeConsensus(
    std::span<const std::byte> section,
    std::uint32_t sectionRva,
    PatternView pattern,
    std::size_t displacementOffset,
    std::size_t instructionEndOffset,
    std::int64_t targetAdjustment,
    std::uint32_t imageSize);

} // namespace briefcase::discovery
