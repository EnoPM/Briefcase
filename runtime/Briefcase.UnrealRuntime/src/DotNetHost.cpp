#include "DotNetHost.h"

#include "Log.h"
#include "ModHost.h"

#include <Windows.h>
#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cwchar>
#include <exception>
#include <filesystem>
#include <functional>
#include <string>
#include <vector>

namespace {
using hostfxr_handle = void*;
using hostfxr_initialize_for_runtime_config_fn =
    std::int32_t(__cdecl*)(const wchar_t*, const void*, hostfxr_handle*);
using hostfxr_get_runtime_delegate_fn =
    std::int32_t(__cdecl*)(hostfxr_handle, std::int32_t, void**);
using hostfxr_close_fn = std::int32_t(__cdecl*)(hostfxr_handle);
using load_assembly_and_get_function_pointer_fn = std::int32_t(__cdecl*)(
    const wchar_t*, const wchar_t*, const wchar_t*, const wchar_t*, void*, void**);
using managed_initialize_fn = std::int32_t(__cdecl*)(const BriefcaseHostApi*);
struct hostfxr_initialize_parameters {
    std::size_t size;
    const wchar_t* host_path;
    const wchar_t* dotnet_root;
};

// hostfxr_delegate_type::hdt_load_assembly_and_get_function_pointer.
constexpr std::int32_t LoadAssemblyDelegate = 5;
// Reserved value documented by hostfxr for a method marked UnmanagedCallersOnly.
const auto UnmanagedCallersOnly = reinterpret_cast<const wchar_t*>(static_cast<std::intptr_t>(-1));
HMODULE HostFxrModule{};
struct HostFxrLocation {
    std::filesystem::path Library;
    std::filesystem::path DotNetRoot;
};

std::filesystem::path moduleDirectory(HMODULE module) {
    std::array<wchar_t, 32768> path{};
    const DWORD length = GetModuleFileNameW(module, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    return std::filesystem::path(path.data()).parent_path();
}

std::filesystem::path newestHostFxr(const std::filesystem::path& fxrRoot) {
    if (!std::filesystem::is_directory(fxrRoot)) return {};
    std::vector<std::filesystem::path> candidates;
    for (const auto& entry : std::filesystem::directory_iterator(fxrRoot)) {
        const auto candidate = entry.path() / L"hostfxr.dll";
        if (entry.is_directory() && std::filesystem::is_regular_file(candidate))
            candidates.push_back(candidate);
    }
    if (candidates.empty()) return {};
    std::sort(candidates.begin(), candidates.end(), std::greater<>());
    return candidates.front();
}

HostFxrLocation findHostFxr(const std::filesystem::path& runtimeDirectory) {
    const auto bundledAtRoot = runtimeDirectory / L"hostfxr.dll";
    if (std::filesystem::is_regular_file(bundledAtRoot))
        return {bundledAtRoot, runtimeDirectory};

    const auto bundledVersioned = newestHostFxr(runtimeDirectory / L"host" / L"fxr");
    if (!bundledVersioned.empty())
        return {bundledVersioned, runtimeDirectory};

    std::array<wchar_t, 32768> programFiles{};
    const DWORD length = GetEnvironmentVariableW(L"ProgramFiles", programFiles.data(),
                                                  static_cast<DWORD>(programFiles.size()));
    if (!length || length >= programFiles.size()) return {};
    const auto installedRoot = std::filesystem::path(programFiles.data()) / L"dotnet";
    return {newestHostFxr(installedRoot / L"host" / L"fxr"), installedRoot};
}

std::wstring statusText(const wchar_t* operation, std::int32_t status) {
    return std::wstring(operation) + L" failed with status 0x" +
           [] (std::uint32_t value) {
               wchar_t buffer[16]{};
               swprintf_s(buffer, L"%08X", value);
               return std::wstring(buffer);
           }(static_cast<std::uint32_t>(status));
}
} // namespace

namespace briefcase {

void runDotNetHost(HMODULE frameworkModule) {
    try {
        const auto root = moduleDirectory(frameworkModule);
        const auto coreDirectory = root / L"Briefcase" / L"Core";
        const auto dotNetDirectory = coreDirectory / L"DotNet";
        const auto runtimeConfig = coreDirectory / L"Briefcase.ManagedHost.runtimeconfig.json";
        const auto managedAssembly = coreDirectory / L"Briefcase.ManagedHost.dll";
        if (!std::filesystem::is_regular_file(runtimeConfig) ||
            !std::filesystem::is_regular_file(managedAssembly)) {
            log(L"managed host: development runtime not installed");
            return;
        }

        const auto hostFxr = findHostFxr(dotNetDirectory);
        if (hostFxr.Library.empty()) {
            log(L"managed host: hostfxr.dll was not found; install the matching .NET runtime");
            return;
        }
        HostFxrModule = LoadLibraryExW(hostFxr.Library.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!HostFxrModule) {
            log(L"managed host: could not load " + hostFxr.Library.wstring() +
                L" error=" + std::to_wstring(GetLastError()));
            return;
        }

        const auto initialize = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
            GetProcAddress(HostFxrModule, "hostfxr_initialize_for_runtime_config"));
        const auto getDelegate = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
            GetProcAddress(HostFxrModule, "hostfxr_get_runtime_delegate"));
        const auto close = reinterpret_cast<hostfxr_close_fn>(
            GetProcAddress(HostFxrModule, "hostfxr_close"));
        if (!initialize || !getDelegate || !close) {
            log(L"managed host: hostfxr exports are incomplete");
            return;
        }

        hostfxr_handle context{};
        // initialize_for_runtime_config accepts framework-dependent component
        // configs. The bundled runtime therefore uses the standard dotnet
        // host/fxr + shared/Microsoft.NETCore.App directory layout.
        std::array<wchar_t, 32768> hostPathBuffer{};
        const DWORD hostPathLength = GetModuleFileNameW(
            frameworkModule, hostPathBuffer.data(),
            static_cast<DWORD>(hostPathBuffer.size()));
        if (!hostPathLength || hostPathLength >= hostPathBuffer.size()) {
            log(L"managed host: could not resolve the native loader path");
            return;
        }
        const std::filesystem::path hostPath{hostPathBuffer.data()};
        const hostfxr_initialize_parameters parameters{
            sizeof(hostfxr_initialize_parameters), hostPath.c_str(), hostFxr.DotNetRoot.c_str()};
        const auto initializeStatus = initialize(runtimeConfig.c_str(), &parameters, &context);
        if (initializeStatus < 0 || !context) {
            log(statusText(L"managed host: runtime initialization", initializeStatus));
            return;
        }

        void* delegateAddress{};
        const auto delegateStatus = getDelegate(context, LoadAssemblyDelegate, &delegateAddress);
        close(context);
        if (delegateStatus < 0 || !delegateAddress) {
            log(statusText(L"managed host: delegate acquisition", delegateStatus));
            return;
        }

        const auto loadAssembly =
            reinterpret_cast<load_assembly_and_get_function_pointer_fn>(delegateAddress);
        void* initializeAddress{};
        const auto loadStatus = loadAssembly(
            managedAssembly.c_str(),
            L"Briefcase.ManagedHost.EntryPoint, Briefcase.ManagedHost",
            L"Initialize",
            UnmanagedCallersOnly,
            nullptr,
            &initializeAddress);
        if (loadStatus < 0 || !initializeAddress) {
            log(statusText(L"managed host: assembly load", loadStatus));
            return;
        }

        const auto managedInitialize = reinterpret_cast<managed_initialize_fn>(initializeAddress);
        const auto initializeResult = managedInitialize(getHostApi());
        if (initializeResult != 0) {
            log(L"managed host: Initialize returned " + std::to_wstring(initializeResult));
            return;
        }
        log(L"managed host: CoreCLR runtime started");
    } catch (const std::exception& exception) {
        log(L"managed host: exception while starting CoreCLR");
        (void)exception;
    } catch (...) {
        log(L"managed host: unknown exception while starting CoreCLR");
    }
}

} // namespace briefcase
