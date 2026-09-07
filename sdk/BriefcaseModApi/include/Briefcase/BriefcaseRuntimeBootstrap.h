#pragma once

#include <Windows.h>
#include <cstdint>

inline constexpr std::uint32_t BRIEFCASE_RUNTIME_BOOTSTRAP_VERSION = 1;
inline constexpr char BRIEFCASE_RUNTIME_INITIALIZE_EXPORT[] =
    "BriefcaseInitializeRuntime";

// Private ABI between the tiny version.dll proxy and the native runtime DLL.
// StructSize and Version let a future proxy reject an incompatible runtime
// before either side reads fields that the other side does not understand.
struct BriefcaseRuntimeBootstrap {
    std::uint32_t StructSize;
    std::uint32_t Version;
    HMODULE ProxyModule;
    DWORD InitialThreadId;
    DWORD Reserved;
};

using BriefcaseRuntimeInitializeFn =
    BOOL(WINAPI*)(const BriefcaseRuntimeBootstrap* bootstrap);

static_assert(sizeof(BriefcaseRuntimeBootstrap) == 24);
