#pragma once

#include <Windows.h>
#include <cstdint>

inline constexpr std::uint32_t BRIEFCASE_LAUNCHER_BOOTSTRAP_VERSION = 1;
inline constexpr char BRIEFCASE_LAUNCHER_BOOTSTRAP_EXPORT[] =
    "BriefcaseBootstrapRuntime";

// Private launcher-to-bootstrap ABI. The launcher writes this value into the
// child process, then calls BriefcaseBootstrapRuntime on a remote thread.
struct BriefcaseLauncherBootstrapRequest {
    std::uint32_t StructSize;
    std::uint32_t Version;
    DWORD InitialThreadId;
    DWORD Reserved;
};

using BriefcaseLauncherBootstrapFn =
    DWORD(WINAPI*)(void* request);

static_assert(sizeof(BriefcaseLauncherBootstrapRequest) == 16);