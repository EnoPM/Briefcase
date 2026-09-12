#include <Windows.h>

#include <array>
#include <filesystem>
#include <string>

#include <Briefcase/BriefcaseLauncherBootstrap.h>
#include <Briefcase/BriefcaseRuntimeBootstrap.h>

namespace {
HMODULE BootstrapModule{};

void reportFailure(const wchar_t* message, DWORD error = ERROR_SUCCESS) {
    std::wstring line = L"Briefcase bootstrap: ";
    line += message;
    if (error != ERROR_SUCCESS) {
        line += L" (error=";
        line += std::to_wstring(error);
        line += L")";
    }
    line += L"\n";
    OutputDebugStringW(line.c_str());
}

std::filesystem::path moduleDirectory(HMODULE module) {
    std::array<wchar_t, 32768> path{};
    const DWORD length = GetModuleFileNameW(
        module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    return std::filesystem::path(path.data()).parent_path();
}
} // namespace

// This function runs after LoadLibraryW has returned from DllMain, so the
// Windows loader lock is no longer held while Briefcase loads CoreCLR and
// discovers Unreal metadata.
extern "C" __declspec(dllexport) DWORD WINAPI BriefcaseBootstrapRuntime(
    void* parameter) {
    const auto* request =
        static_cast<const BriefcaseLauncherBootstrapRequest*>(parameter);
    if (!request ||
        request->StructSize < sizeof(BriefcaseLauncherBootstrapRequest) ||
        request->Version != BRIEFCASE_LAUNCHER_BOOTSTRAP_VERSION ||
        request->InitialThreadId == 0) {
        reportFailure(L"the launcher request is invalid");
        return ERROR_INVALID_PARAMETER;
    }

    const auto directory = moduleDirectory(BootstrapModule);
    if (directory.empty()) {
        reportFailure(L"could not resolve the bootstrap directory", GetLastError());
        return ERROR_PATH_NOT_FOUND;
    }

    const auto runtimePath = directory / L"Briefcase.UnrealRuntime.dll";
    const HMODULE runtime = LoadLibraryExW(
        runtimePath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!runtime) {
        reportFailure(L"could not load Briefcase.UnrealRuntime.dll", GetLastError());
        return ERROR_MOD_NOT_FOUND;
    }

    const auto initialize = reinterpret_cast<BriefcaseRuntimeInitializeFn>(
        GetProcAddress(runtime, BRIEFCASE_RUNTIME_INITIALIZE_EXPORT));
    if (!initialize) {
        reportFailure(L"the runtime initialization export is missing", GetLastError());
        return ERROR_PROC_NOT_FOUND;
    }

    // The executable and the former version.dll proxy both live in Win64.
    // Passing the executable module preserves the existing path contract:
    // Briefcase data is resolved below Win64/Briefcase.
    const HMODULE executable = GetModuleHandleW(nullptr);
    const BriefcaseRuntimeBootstrap bootstrap{
        sizeof(BriefcaseRuntimeBootstrap),
        BRIEFCASE_RUNTIME_BOOTSTRAP_VERSION,
        executable,
        request->InitialThreadId,
        0};
    if (!initialize(&bootstrap)) {
        reportFailure(L"Briefcase.UnrealRuntime rejected initialization");
        return ERROR_DLL_INIT_FAILED;
    }

    return ERROR_SUCCESS;
}

BOOL WINAPI DllMain(HMODULE self, DWORD reason, void*) {
    if (reason == DLL_PROCESS_ATTACH) {
        BootstrapModule = self;
        DisableThreadLibraryCalls(self);
    }
    return TRUE;
}