#include <Briefcase/BriefcaseRuntimeBootstrap.h>

#include "DotNetHost.h"
#include "Log.h"
#include "UnrealProbe.h"

#include <atomic>

namespace {
std::atomic_bool RuntimeStarted{};
}

extern "C" __declspec(dllexport) BOOL WINAPI BriefcaseInitializeRuntime(
    const BriefcaseRuntimeBootstrap* bootstrap) {
    if (!bootstrap ||
        bootstrap->StructSize < sizeof(BriefcaseRuntimeBootstrap) ||
        bootstrap->Version != BRIEFCASE_RUNTIME_BOOTSTRAP_VERSION ||
        !bootstrap->ProxyModule ||
        bootstrap->InitialThreadId == 0) {
        return FALSE;
    }

    bool expected = false;
    if (!RuntimeStarted.compare_exchange_strong(expected, true)) return TRUE;

    try {
        // Paths intentionally remain relative to the proxy in Win64. The
        // runtime itself lives below Briefcase/Core/Native.
        briefcase::captureGameThreadId(bootstrap->InitialThreadId);
        briefcase::runUnrealProbe(bootstrap->ProxyModule);
        briefcase::runDotNetHost(bootstrap->ProxyModule);
        return TRUE;
    } catch (...) {
        briefcase::log(L"runtime bootstrap: unhandled native exception");
        return FALSE;
    }
}
