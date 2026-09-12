#include "StartupModuleHost.h"

#include "Log.h"

#include <Briefcase/BriefcaseStartupModule.h>
#include <Windows.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <filesystem>
#include <string>
#include <vector>

namespace {

constexpr std::size_t MaximumStartupModules = 64;
std::vector<HMODULE> LoadedModules;

void WINAPI startupLog(
    BriefcaseStartupLogLevel level,
    const wchar_t* message) {
    if (!message) return;
    std::wstring prefix;
    switch (level) {
    case BriefcaseStartupLogLevel::Warning:
        prefix = L"[startup warning] ";
        break;
    case BriefcaseStartupLogLevel::Error:
        prefix = L"[startup error] ";
        break;
    default:
        prefix = L"[startup] ";
        break;
    }
    briefcase::log(prefix + message);
}

bool readGameBuild(
    HMODULE gameModule,
    std::uint32_t& timestamp,
    std::uint32_t& imageSize) noexcept {
    __try {
        const auto* base = reinterpret_cast<const std::byte*>(gameModule);
        const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
        if (!base || dos->e_magic != IMAGE_DOS_SIGNATURE ||
            dos->e_lfanew < static_cast<LONG>(sizeof(IMAGE_DOS_HEADER)) ||
            dos->e_lfanew > 1024 * 1024) {
            return false;
        }
        const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
            base + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE ||
            nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
            return false;
        }
        timestamp = nt->FileHeader.TimeDateStamp;
        imageSize = nt->OptionalHeader.SizeOfImage;
        return imageSize >= 0x1000;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

DWORD invokeStartupModule(
    BriefcaseStartupInitializeFn initialize,
    const BriefcaseStartupContext* context) noexcept {
    __try {
        return initialize(context);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return ERROR_UNHANDLED_EXCEPTION;
    }
}

bool isDll(const std::filesystem::path& path) {
    return _wcsicmp(path.extension().c_str(), L".dll") == 0;
}

} // namespace

namespace briefcase {

void loadStartupModules(const std::filesystem::path& briefcaseDirectory) {
    const auto startupDirectory = briefcaseDirectory / L"Mods" / L"Startup";
    std::error_code error;
    if (!std::filesystem::is_directory(startupDirectory, error)) {
        if (error) {
            log(L"startup module directory could not be inspected: " +
                std::to_wstring(error.value()));
        } else {
            log(L"no native startup module directory was found");
        }
        return;
    }

    std::vector<std::filesystem::path> candidates;
    for (std::filesystem::directory_iterator iterator(startupDirectory, error), end;
         !error && iterator != end;
         iterator.increment(error)) {
        std::error_code entryError;
        if (!iterator->is_regular_file(entryError) || entryError ||
            !isDll(iterator->path())) {
            continue;
        }
        candidates.push_back(iterator->path());
        if (candidates.size() >= MaximumStartupModules) break;
    }
    if (error) {
        log(L"startup module enumeration stopped: " +
            std::to_wstring(error.value()));
    }
    std::sort(candidates.begin(), candidates.end());

    const HMODULE gameModule = GetModuleHandleW(nullptr);
    std::uint32_t timestamp{};
    std::uint32_t imageSize{};
    if (!readGameBuild(gameModule, timestamp, imageSize)) {
        log(L"startup modules refused: invalid game PE headers");
        return;
    }

    const auto directoryText = briefcaseDirectory.native();
    const BriefcaseStartupContext context{
        sizeof(BriefcaseStartupContext),
        BRIEFCASE_STARTUP_MODULE_API_VERSION,
        gameModule,
        timestamp,
        imageSize,
        directoryText.c_str(),
        static_cast<std::uint32_t>(directoryText.size()),
        0,
        &startupLog,
        {0, 0, 0, 0}};

    for (const auto& path : candidates) {
        const auto fileName = path.filename().native();
        const HMODULE module = LoadLibraryExW(
            path.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) {
            log(L"startup module load failed: " + fileName + L" error=" +
                std::to_wstring(GetLastError()));
            continue;
        }

        const auto initialize = reinterpret_cast<BriefcaseStartupInitializeFn>(
            GetProcAddress(module, BRIEFCASE_STARTUP_INITIALIZE_EXPORT));
        if (!initialize) {
            log(L"startup module has no Briefcase entry point: " + fileName);
            FreeLibrary(module);
            continue;
        }

        const DWORD result = invokeStartupModule(initialize, &context);
        if (result != ERROR_SUCCESS) {
            log(L"startup module initialization failed: " + fileName +
                L" error=" + std::to_wstring(result));
            FreeLibrary(module);
            continue;
        }

        LoadedModules.push_back(module);
        log(L"startup module initialized: " + fileName);
    }
}

} // namespace briefcase
