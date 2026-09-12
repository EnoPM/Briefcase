#pragma once

#include <Windows.h>
#include <cstdint>

// Native startup modules are intentionally small. Briefcase loads them before
// it waits for Unreal reflection or starts CoreCLR, which makes this ABI useful
// for work that must finish during the first moments of process initialization.
inline constexpr std::uint32_t BRIEFCASE_STARTUP_MODULE_API_VERSION = 1;
inline constexpr char BRIEFCASE_STARTUP_INITIALIZE_EXPORT[] =
    "BriefcaseInitializeStartupModule";

enum class BriefcaseStartupLogLevel : std::uint32_t {
    Information = 0,
    Warning = 1,
    Error = 2
};

using BriefcaseStartupLogFn = void(WINAPI*)(
    BriefcaseStartupLogLevel level,
    const wchar_t* message);

struct BriefcaseStartupContext {
    std::uint32_t StructSize;
    std::uint32_t ApiVersion;
    HMODULE GameModule;
    std::uint32_t PeTimestamp;
    std::uint32_t ImageSize;
    const wchar_t* BriefcaseDirectory;
    std::uint32_t BriefcaseDirectoryLength;
    std::uint32_t Reserved;
    BriefcaseStartupLogFn Log;
    std::uint64_t ReservedFields[4];
};

using BriefcaseStartupInitializeFn = DWORD(WINAPI*)(
    const BriefcaseStartupContext* context);

static_assert(sizeof(BriefcaseStartupContext) == 80);
