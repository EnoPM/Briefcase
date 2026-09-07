#include "RuntimeSymbolResolver.h"

namespace briefcase::discovery {
namespace {

inline constexpr auto FNameToStringPattern = makePattern(
    "48 89 5C 24 18 56 57 41 56 48 83 EC 30 83 79 04 00 48 8D 3D "
    "?? ?? ?? ?? 48 8B DA 4C 8B F1 75 63 8B 01 8B F0 44 0F B7 C0 "
    "C1 EE 10 44 89 44 24 54 44 0F B6 05 ?? ?? ?? ?? 89 74 24 50 "
    "45 84 C0 74 05");

// This is the chunked UObject-array lookup emitted throughout UE 4.27. The
// second RIP-relative load reads ObjObjects.Objects at GUObjectArray + 0x10.
// Dozens of independent call sites should therefore agree on one base RVA.
inline constexpr auto GUObjectArrayReferencePattern = makePattern(
    "8B 40 0C 3B 05 ?? ?? ?? ?? 7D 2A 99 0F B7 D2 03 C2 8B C8 "
    "0F B7 C0 2B C2 48 98 C1 F9 10 48 63 C9 48 8D 14 40 "
    "48 8B 05 ?? ?? ?? ?? 48 8B 0C C8 4C 8D 04 D1");

RuntimeSymbolFailure directFailure(ResolutionStatus status) noexcept {
    switch (status) {
    case ResolutionStatus::NotFound:
        return RuntimeSymbolFailure::FNameToStringNotFound;
    case ResolutionStatus::Ambiguous:
        return RuntimeSymbolFailure::FNameToStringAmbiguous;
    default:
        return RuntimeSymbolFailure::FNameToStringInvalid;
    }
}

RuntimeSymbolFailure relativeFailure(ResolutionStatus status) noexcept {
    switch (status) {
    case ResolutionStatus::NotFound:
        return RuntimeSymbolFailure::GUObjectArrayNotFound;
    case ResolutionStatus::Ambiguous:
        return RuntimeSymbolFailure::GUObjectArrayAmbiguous;
    default:
        return RuntimeSymbolFailure::GUObjectArrayInvalid;
    }
}

} // namespace

RuntimeSymbolResolution resolveRuntimeSymbols(const PeImageView& image) {
    const auto text = image.section(".text");
    if (!text) return {RuntimeSymbolFailure::TextSectionMissing, {}};

    const auto name = resolveUniqueMatch(
        text->Bytes,
        text->Rva,
        PatternView(FNameToStringPattern),
        image.imageSize());
    if (name.Status != ResolutionStatus::Resolved)
        return {directFailure(name.Status), {}};

    const auto objects = resolveRipRelativeConsensus(
        text->Bytes,
        text->Rva,
        PatternView(GUObjectArrayReferencePattern),
        39,
        43,
        -0x10,
        image.imageSize());
    if (objects.Status != ResolutionStatus::Resolved)
        return {relativeFailure(objects.Status), {}};

    const auto data = image.sectionContaining(objects.Rva, 0x30);
    if (!data || std::string_view(data->Name.data()) != ".data")
        return {RuntimeSymbolFailure::GUObjectArrayOutsideData, {}};

    return {
        RuntimeSymbolFailure::None,
        {
            name.Rva,
            objects.Rva,
            name.MatchCount,
            objects.MatchCount
        }};
}

std::string_view runtimeSymbolFailureName(RuntimeSymbolFailure failure) noexcept {
    switch (failure) {
    case RuntimeSymbolFailure::None: return "none";
    case RuntimeSymbolFailure::TextSectionMissing: return ".text section missing";
    case RuntimeSymbolFailure::FNameToStringNotFound: return "FName::ToString signature missing";
    case RuntimeSymbolFailure::FNameToStringAmbiguous: return "FName::ToString signature ambiguous";
    case RuntimeSymbolFailure::FNameToStringInvalid: return "FName::ToString signature invalid";
    case RuntimeSymbolFailure::GUObjectArrayNotFound: return "GUObjectArray references missing";
    case RuntimeSymbolFailure::GUObjectArrayAmbiguous: return "GUObjectArray target ambiguous";
    case RuntimeSymbolFailure::GUObjectArrayInvalid: return "GUObjectArray reference invalid";
    case RuntimeSymbolFailure::GUObjectArrayOutsideData: return "GUObjectArray target outside .data";
    }
    return "unknown signature resolution failure";
}

} // namespace briefcase::discovery
