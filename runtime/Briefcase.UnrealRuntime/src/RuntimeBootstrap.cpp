#include <Briefcase/BriefcaseRuntimeBootstrap.h>

#include "DotNetHost.h"
#include "Log.h"
#include "StartupModuleHost.h"
#include "UnrealProbe.h"

#include <atomic>
#include <array>
#include <filesystem>

namespace {
std::atomic_bool RuntimeStarted{};
}

extern "C" __declspec(dllexport) BOOL WINAPI BriefcaseInitializeRuntime(
    const BriefcaseRuntimeBootstrap* bootstrap) {
    if (!bootstrap ||
        bootstrap->StructSize < sizeof(BriefcaseRuntimeBootstrap) ||
        bootstrap->Version != BRIEFCASE_RUNTIME_BOOTSTRAP_VERSION ||
        !bootstrap->RootModule ||
        bootstrap->InitialThreadId == 0) {
        return FALSE;
    }

    bool expected = false;
    if (!RuntimeStarted.compare_exchange_strong(expected, true)) return TRUE;

    try {
        // Paths remain relative to the loader root in Win64. The root is the
        // proxy module or, for launcher injection, the game executable.
        std::array<wchar_t, 32768> modulePath{};
        const DWORD pathLength = GetModuleFileNameW(
            bootstrap->RootModule,
            modulePath.data(),
            static_cast<DWORD>(modulePath.size()));
        if (!pathLength || pathLength >= modulePath.size()) return FALSE;

        const std::filesystem::path rootPath(modulePath.data());
        briefcase::openLog(rootPath);
        briefcase::log(L"native runtime initialization started");
        briefcase::loadStartupModules(rootPath.parent_path() / L"Briefcase");

        briefcase::captureGameThreadId(bootstrap->InitialThreadId);
        briefcase::runUnrealProbe(bootstrap->RootModule, &briefcase::runDotNetHost);
        return TRUE;
    } catch (...) {
        briefcase::log(L"runtime bootstrap: unhandled native exception");
        return FALSE;
    }
}
