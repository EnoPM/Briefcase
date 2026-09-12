#include "ModHost.h"

#include "Log.h"
#include "RuntimeProfile.h"
#include "UnrealProbe.h"

#include <Briefcase/BriefcaseModApi.h>
#include <Windows.h>
#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <string>
#include <string_view>
#include <unordered_set>
#include <vector>

namespace {
#if !defined(BRIEFCASE_VERSION_MAJOR) || !defined(BRIEFCASE_VERSION_MINOR) || \
    !defined(BRIEFCASE_VERSION_BUILD)
#error The Briefcase version must be supplied by Directory.Build.props.
#endif

// These macros are derived from the repository's plain-text VERSION file by
// MSBuild. The native ABI and managed assemblies therefore report the same
// framework version that is used for tags and release archive names.
constexpr BriefcaseVersion FrameworkVersion{
    BRIEFCASE_VERSION_MAJOR,
    BRIEFCASE_VERSION_MINOR,
    BRIEFCASE_VERSION_BUILD,
    0};
std::vector<HMODULE> LoadedMods;
std::vector<HMODULE> ResidentModules;

std::wstring utf8ToWide(const char* text, std::uint32_t length) {
    if (!text || length == 0 || length > 16 * 1024) return {};
    const int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text,
                                              static_cast<int>(length), nullptr, 0);
    if (required <= 0) return L"<invalid UTF-8>";
    std::wstring converted(static_cast<std::size_t>(required), L'\0');
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, static_cast<int>(length),
                        converted.data(), required);
    return converted;
}

void BRIEFCASE_MOD_CALL apiLog(void*, BriefcaseLogLevel level, const char* message,
                         std::uint32_t length) {
    std::wstring prefix;
    switch (level) {
    case BRIEFCASE_LOG_TRACE: prefix = L"[mod trace] "; break;
    case BRIEFCASE_LOG_WARNING: prefix = L"[mod warning] "; break;
    case BRIEFCASE_LOG_ERROR: prefix = L"[mod error] "; break;
    default: prefix = L"[mod] "; break;
    }
    briefcase::log(prefix + utf8ToWide(message, length));
}

BriefcaseVersion BRIEFCASE_MOD_CALL apiGetFrameworkVersion(void*) { return FrameworkVersion; }
BriefcaseGameBuild BRIEFCASE_MOD_CALL apiGetGameBuild(void*) {
    const auto* runtimeProfile = briefcase::getRuntimeProfile();
    return runtimeProfile
        ? BriefcaseGameBuild{runtimeProfile->PeTimestamp, runtimeProfile->ImageSize}
        : BriefcaseGameBuild{};
}

const BriefcaseCoreApi CoreApi{
    sizeof(BriefcaseCoreApi), BRIEFCASE_HOST_API_VERSION, nullptr, apiLog,
    apiGetFrameworkVersion, apiGetGameBuild, {}};

BriefcaseHostApi HostApi{
    sizeof(BriefcaseHostApi), BRIEFCASE_HOST_API_VERSION, BRIEFCASE_CAPABILITY_CORE, &CoreApi,
    nullptr, nullptr, nullptr, nullptr, nullptr, {}};

bool hasTerminator(const char* text, std::size_t capacity) {
    return text && std::memchr(text, '\0', capacity) != nullptr;
}

std::string normalizedId(const char* id) {
    std::string value{id};
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

// Exceptions and access violations must not cross the plug-in ABI. These leaf
// wrappers deliberately contain no C++ objects so MSVC can use SEH safely.
bool safeQuery(BriefcaseModQueryFn query, BriefcaseModInfo* info) noexcept {
    __try {
        return query(BRIEFCASE_HOST_API_VERSION, info) != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

bool safeLoad(BriefcaseModLoadFn load) noexcept {
    __try {
        return load(briefcase::getHostApi()) != 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

std::filesystem::path moduleDirectory(HMODULE module) {
    std::array<wchar_t, 32768> path{};
    const DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    return std::filesystem::path(path.data()).parent_path();
}

} // namespace

namespace briefcase {

const BriefcaseHostApi* getHostApi() {
    HostApi.Capabilities = BRIEFCASE_CAPABILITY_CORE;
    HostApi.Unreal = nullptr;
    HostApi.ReservedRendering = nullptr;
    HostApi.Input = nullptr;
    HostApi.Patching = nullptr;
    HostApi.GameThread = nullptr;
    if (isUnrealApiReady()) {
        HostApi.Capabilities |= BRIEFCASE_CAPABILITY_UNREAL_REFLECTION |
                                BRIEFCASE_CAPABILITY_UNREAL_INVOCATION;
        HostApi.Unreal = getUnrealApi();
        HostApi.Capabilities |= BRIEFCASE_CAPABILITY_PATCHING;
        HostApi.Patching = getPatchingApi();
        HostApi.Capabilities |= BRIEFCASE_CAPABILITY_GAME_THREAD;
        HostApi.GameThread = getGameThreadApi();
    }
    return &HostApi;
}

void runModHost(HMODULE frameworkModule) {
    try {
        const auto* hostApi = getHostApi();
        const auto root = moduleDirectory(frameworkModule);
        if (root.empty()) {
            log(L"mod host: unable to resolve framework directory");
            return;
        }
        const auto modsDirectory = root / L"Briefcase" / L"Mods";
        std::filesystem::create_directories(modsDirectory);

        std::vector<std::filesystem::path> candidates;
        for (const auto& entry : std::filesystem::directory_iterator(modsDirectory)) {
            if (!entry.is_regular_file() || _wcsicmp(entry.path().extension().c_str(), L".dll") != 0) continue;
            candidates.push_back(entry.path());
        }
        std::sort(candidates.begin(), candidates.end(), [](const auto& left, const auto& right) {
            return _wcsicmp(left.filename().c_str(), right.filename().c_str()) < 0;
        });

        log(L"mod host: scanning " + modsDirectory.wstring() + L"; candidates=" +
            std::to_wstring(candidates.size()));
        std::unordered_set<std::string> identifiers;

        for (const auto& path : candidates) {
            // The full path and restricted search flags prevent dependencies from
            // being resolved through the process current directory or PATH.
            HMODULE module = LoadLibraryExW(path.c_str(), nullptr,
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (!module) {
                log(L"mod host: load failed for " + path.filename().wstring() +
                    L" error=" + std::to_wstring(GetLastError()));
                continue;
            }
            // Native shared libraries cannot be unloaded safely. Keep every
            // successfully mapped candidate resident even when its ABI is later
            // rejected. The OS releases it when the game process terminates.
            ResidentModules.push_back(module);

            const auto query = reinterpret_cast<BriefcaseModQueryFn>(GetProcAddress(module, BRIEFCASE_MOD_QUERY_EXPORT));
            const auto load = reinterpret_cast<BriefcaseModLoadFn>(GetProcAddress(module, BRIEFCASE_MOD_LOAD_EXPORT));
            if (!query || !load) {
                log(L"mod host: rejected " + path.filename().wstring() + L" (missing required exports)");
                continue;
            }

            BriefcaseModInfo info{};
            info.StructSize = sizeof(info);
            if (!safeQuery(query, &info) || info.StructSize < sizeof(BriefcaseModInfo) ||
                !hasTerminator(info.Id, sizeof(info.Id)) || info.Id[0] == '\0' ||
                !hasTerminator(info.Name, sizeof(info.Name)) ||
                !hasTerminator(info.Version, sizeof(info.Version))) {
                log(L"mod host: rejected " + path.filename().wstring() + L" (invalid metadata or exception)");
                continue;
            }
            if (info.MinimumHostApiVersion > BRIEFCASE_HOST_API_VERSION ||
                info.MaximumHostApiVersion < BRIEFCASE_HOST_API_VERSION) {
                log(L"mod host: rejected " + path.filename().wstring() + L" (incompatible host API)");
                continue;
            }
            if ((info.RequiredCapabilities & ~hostApi->Capabilities) != 0) {
                log(L"mod host: rejected " + path.filename().wstring() + L" (missing capability)");
                continue;
            }
            if (!identifiers.insert(normalizedId(info.Id)).second) {
                                log(L"mod host: rejected duplicate id from " + path.filename().wstring());
                continue;
            }
            if (!safeLoad(load)) {
                log(L"mod host: load callback failed for " + path.filename().wstring());
                identifiers.erase(normalizedId(info.Id));
                continue;
            }

            LoadedMods.push_back(module);
            log(L"mod host: loaded " + utf8ToWide(info.Name, static_cast<std::uint32_t>(std::strlen(info.Name))) +
                L" " + utf8ToWide(info.Version, static_cast<std::uint32_t>(std::strlen(info.Version))) +
                L" [" + utf8ToWide(info.Id, static_cast<std::uint32_t>(std::strlen(info.Id))) + L"]");
        }
        log(L"mod host: active mods=" + std::to_wstring(LoadedMods.size()));
    } catch (const std::exception& exception) {
        const auto message = std::string_view{exception.what()};
        log(L"mod host: exception: " + utf8ToWide(message.data(), static_cast<std::uint32_t>(message.size())));
    } catch (...) {
        log(L"mod host: unknown exception");
    }
}

} // namespace briefcase
