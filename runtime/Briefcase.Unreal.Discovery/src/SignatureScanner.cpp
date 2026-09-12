#include "SignatureScanner.h"

#include <algorithm>
#include <cstring>
#include <limits>

namespace briefcase::discovery {
namespace {

bool matchesAt(
    std::span<const std::byte> bytes,
    std::size_t offset,
    std::span<const PatternByte> pattern) noexcept {
    for (std::size_t index = 0; index < pattern.size(); ++index) {
        if (pattern[index].Exact &&
            std::to_integer<std::uint8_t>(bytes[offset + index]) != pattern[index].Value)
            return false;
    }
    return true;
}

bool addRva(
    std::uint32_t sectionRva,
    std::size_t sectionOffset,
    std::uint32_t imageSize,
    std::uint32_t& result) noexcept {
    const auto value = static_cast<std::uint64_t>(sectionRva) + sectionOffset;
    if (value >= imageSize || value > std::numeric_limits<std::uint32_t>::max()) return false;
    result = static_cast<std::uint32_t>(value);
    return true;
}

} // namespace

PatternMatches findPatternMatches(
    std::span<const std::byte> bytes,
    PatternView pattern,
    std::size_t maximumMatches) {
    PatternMatches result{};
    if (pattern.Bytes.empty() || pattern.Bytes.size() > bytes.size() || maximumMatches == 0)
        return result;

    const auto anchor = std::find_if(
        pattern.Bytes.begin(), pattern.Bytes.end(),
        [](const PatternByte& value) { return value.Exact; });
    if (anchor == pattern.Bytes.end()) return result;
    const auto anchorOffset = static_cast<std::size_t>(anchor - pattern.Bytes.begin());
    const auto last = bytes.size() - pattern.Bytes.size();

    for (std::size_t offset = 0; offset <= last; ++offset) {
        if (std::to_integer<std::uint8_t>(bytes[offset + anchorOffset]) != anchor->Value ||
            !matchesAt(bytes, offset, pattern.Bytes))
            continue;
        if (result.Offsets.size() == maximumMatches) {
            result.Truncated = true;
            break;
        }
        result.Offsets.push_back(offset);
    }
    return result;
}

SignatureResolution resolveUniqueMatch(
    std::span<const std::byte> section,
    std::uint32_t sectionRva,
    PatternView pattern,
    std::uint32_t imageSize) {
    const auto matches = findPatternMatches(section, pattern, 2);
    if (matches.Offsets.empty()) return {};
    if (matches.Truncated || matches.Offsets.size() != 1)
        return {ResolutionStatus::Ambiguous, 0, matches.Offsets.size(), matches.Offsets.size()};

    std::uint32_t rva{};
    if (!addRva(sectionRva, matches.Offsets.front(), imageSize, rva))
        return {ResolutionStatus::OutsideImage, 0, 1, 1};
    return {ResolutionStatus::Resolved, rva, 1, 1};
}

SignatureResolution resolveRipRelativeConsensus(
    std::span<const std::byte> section,
    std::uint32_t sectionRva,
    PatternView pattern,
    std::size_t displacementOffset,
    std::size_t instructionEndOffset,
    std::int64_t targetAdjustment,
    std::uint32_t imageSize) {
    const auto matches = findPatternMatches(section, pattern);
    if (matches.Offsets.empty()) return {};
    if (matches.Truncated)
        return {ResolutionStatus::Ambiguous, 0, matches.Offsets.size(), 0};
    if (displacementOffset > pattern.Bytes.size() ||
        sizeof(std::int32_t) > pattern.Bytes.size() - displacementOffset ||
        instructionEndOffset > pattern.Bytes.size())
        return {ResolutionStatus::InvalidEncoding, 0, matches.Offsets.size(), 0};

    std::vector<std::uint32_t> candidates;
    candidates.reserve(matches.Offsets.size());
    for (const auto match : matches.Offsets) {
        if (match > section.size() - pattern.Bytes.size() ||
            displacementOffset > section.size() - match ||
            sizeof(std::int32_t) > section.size() - match - displacementOffset)
            return {ResolutionStatus::InvalidEncoding, 0, matches.Offsets.size(), candidates.size()};

        std::int32_t displacement{};
        std::memcpy(
            &displacement,
            section.data() + match + displacementOffset,
            sizeof(displacement));
        const auto instructionEnd = static_cast<std::uint64_t>(sectionRva) +
                                    match + instructionEndOffset;
        if (instructionEnd > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
            return {ResolutionStatus::InvalidEncoding, 0, matches.Offsets.size(), candidates.size()};
        const auto target = static_cast<std::int64_t>(instructionEnd) +
                            displacement + targetAdjustment;
        if (target < 0 || target >= imageSize)
            return {ResolutionStatus::OutsideImage, 0, matches.Offsets.size(), candidates.size()};
        const auto rva = static_cast<std::uint32_t>(target);
        if (std::find(candidates.begin(), candidates.end(), rva) == candidates.end())
            candidates.push_back(rva);
    }

    if (candidates.size() != 1)
        return {ResolutionStatus::Ambiguous, 0, matches.Offsets.size(), candidates.size()};
    return {
        ResolutionStatus::Resolved,
        candidates.front(),
        matches.Offsets.size(),
        candidates.size()};
}

} // namespace briefcase::discovery
