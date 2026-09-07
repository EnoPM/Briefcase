#include <Windows.h>
#include <cstdint>
#include <cwchar>
#include <iterator>

#include <Briefcase/BriefcaseRuntimeBootstrap.h>

extern "C" { std::uintptr_t mProcs[17] = {}; }
static HMODULE OriginalVersion;
static DWORD InitialThreadId;

namespace {
void reportProxyFailure(const wchar_t* message, DWORD error = 0) {
    wchar_t line[512]{};
    if (error)
        swprintf_s(line, L"Briefcase proxy: %ls (error=%lu)\n", message, error);
    else
        swprintf_s(line, L"Briefcase proxy: %ls\n", message);
    OutputDebugStringW(line);
}

DWORD WINAPI loadRuntime(void* parameter) {
    const auto self = static_cast<HMODULE>(parameter);
    wchar_t runtimePath[32768]{};
    const DWORD length = GetModuleFileNameW(
        self, runtimePath, static_cast<DWORD>(std::size(runtimePath)));
    if (!length || length >= std::size(runtimePath)) {
        reportProxyFailure(L"could not resolve the proxy path", GetLastError());
        return 1;
    }

    auto* separator = wcsrchr(runtimePath, L'\\');
    if (!separator) {
        reportProxyFailure(L"proxy path has no parent directory");
        return 2;
    }
    separator[1] = L'\0';
    if (wcscat_s(
            runtimePath,
            L"Briefcase\\Core\\Native\\Briefcase.UnrealRuntime.dll")) {
        reportProxyFailure(L"native runtime path is too long");
        return 3;
    }

    const HMODULE runtime = LoadLibraryExW(
        runtimePath, nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!runtime) {
        reportProxyFailure(L"could not load Briefcase.UnrealRuntime.dll", GetLastError());
        return 4;
    }

    const auto initialize = reinterpret_cast<BriefcaseRuntimeInitializeFn>(
        GetProcAddress(runtime, BRIEFCASE_RUNTIME_INITIALIZE_EXPORT));
    if (!initialize) {
        reportProxyFailure(L"native runtime bootstrap export is missing", GetLastError());
        return 5;
    }

    const BriefcaseRuntimeBootstrap bootstrap{
        sizeof(BriefcaseRuntimeBootstrap),
        BRIEFCASE_RUNTIME_BOOTSTRAP_VERSION,
        self,
        InitialThreadId,
        0};
    if (!initialize(&bootstrap)) {
        reportProxyFailure(L"native runtime initialization failed");
        return 6;
    }
    return 0;
}
} // namespace

BOOL WINAPI DllMain(HMODULE self, DWORD reason, void*) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    // A statically imported proxy is attached on Unreal's initial process
    // thread. Save the ID before creating our bootstrap worker.
    InitialThreadId = GetCurrentThreadId();
    static wchar_t path[32768]{};
    const UINT length = GetSystemDirectoryW(path, static_cast<UINT>(std::size(path)));
    if (!length || length >= std::size(path) || wcscat_s(path, L"\\version.dll")) return FALSE;
    OriginalVersion = LoadLibraryW(path);
    if (!OriginalVersion) return FALSE;

    const char* names[] = {
        "GetFileVersionInfoA", "GetFileVersionInfoByHandle", "GetFileVersionInfoExA", "GetFileVersionInfoExW",
        "GetFileVersionInfoSizeA", "GetFileVersionInfoSizeExA", "GetFileVersionInfoSizeExW", "GetFileVersionInfoSizeW",
        "GetFileVersionInfoW", "VerFindFileA", "VerFindFileW", "VerInstallFileA", "VerInstallFileW",
        "VerLanguageNameA", "VerLanguageNameW", "VerQueryValueA", "VerQueryValueW"
    };
    for (std::size_t index = 0; index < std::size(names); ++index) {
        mProcs[index] = reinterpret_cast<std::uintptr_t>(GetProcAddress(OriginalVersion, names[index]));
        if (!mProcs[index]) return FALSE;
    }

    // Load the separate native runtime on a worker. Windows does not execute the
    // thread entry point until DLL_PROCESS_ATTACH has released the loader lock.
    if (HANDLE thread = CreateThread(nullptr, 0, loadRuntime, self, 0, nullptr))
        CloseHandle(thread);
    else
        reportProxyFailure(L"could not create the runtime bootstrap thread", GetLastError());
    return TRUE;
}
