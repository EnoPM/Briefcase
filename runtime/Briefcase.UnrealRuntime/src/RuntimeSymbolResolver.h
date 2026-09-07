#pragma once

#include "PeImageView.h"
#include "SignatureScanner.h"

#include <cstdint>
#include <string_view>

namespace briefcase::discovery {

enum class RuntimeSymbolFailure {
    None,
    TextSectionMissing,
    FNameToStringNotFound,
    FNameToStringAmbiguous,
    FNameToStringInvalid,
    GUObjectArrayNotFound,
    GUObjectArrayAmbiguous,
    GUObjectArrayInvalid,
    GUObjectArrayOutsideData
};

struct RuntimeSymbols {
    std::uint32_t FNameToStringRva{};
    std::uint32_t GUObjectArrayRva{};
    std::size_t FNameMatchCount{};
    std::size_t GUObjectReferenceCount{};
};

struct RuntimeSymbolResolution {
    RuntimeSymbolFailure Failure{RuntimeSymbolFailure::None};
    RuntimeSymbols Symbols{};

    [[nodiscard]] bool succeeded() const noexcept {
        return Failure == RuntimeSymbolFailure::None;
    }
};

RuntimeSymbolResolution resolveRuntimeSymbols(const PeImageView& image);
std::string_view runtimeSymbolFailureName(RuntimeSymbolFailure failure) noexcept;

} // namespace briefcase::discovery
