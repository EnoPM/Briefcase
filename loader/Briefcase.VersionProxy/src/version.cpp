#include <Windows.h>
#include <cstdint>
#include <cwchar>
#include <iterator>

#include "UnrealProbe.h"
#include "ModHost.h"
#include "DotNetHost.h"

extern "C" { std::uintptr_t mProcs[17] = {}; }
static HMODULE OriginalVersion;

DWORD WINAPI runEmbeddedRuntime(void* parameter) {
    const auto self = static_cast<HMODULE>(parameter);
    briefcase::runUnrealProbe(self);
    briefcase::runDotNetHost(self);
    return 0;
}

BOOL WINAPI DllMain(HMODULE self, DWORD reason, void*) {
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(self);
    // A statically imported proxy is attached on Unreal's initial process
    // thread. Save the ID before creating our bootstrap worker.
    briefcase::captureGameThreadId(GetCurrentThreadId());
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

    // The Unreal reader is linked into this DLL. Run it on a worker thread after
    // DllMain returns so reflection traversal and file IO happen without holding
    // the Windows loader lock.
    if (HANDLE thread = CreateThread(nullptr, 0, runEmbeddedRuntime, self, 0, nullptr)) CloseHandle(thread);
    return TRUE;
}
