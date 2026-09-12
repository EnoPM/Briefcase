#include <Windows.h>
#include <TlHelp32.h>

#include <array>
#include <chrono>
#include <cstddef>
#include <filesystem>
#include <iostream>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include <Briefcase/BriefcaseLauncherBootstrap.h>

namespace {
constexpr DWORD RemoteOperationTimeoutMs = 60'000;
constexpr DWORD TargetProcessAccess = PROCESS_CREATE_THREAD |
    PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION |
    PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE;
constexpr auto ModuleDiscoveryTimeout = std::chrono::seconds(10);
constexpr auto TargetProcessTimeout = std::chrono::minutes(5);

class UniqueHandle {
public:
    UniqueHandle() = default;
    explicit UniqueHandle(HANDLE value) : Value(value) {}
    ~UniqueHandle() { reset(); }
    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;
    UniqueHandle(UniqueHandle&& other) noexcept : Value(other.release()) {}
    UniqueHandle& operator=(UniqueHandle&& other) noexcept {
        if (this != &other) reset(other.release());
        return *this;
    }
    HANDLE get() const { return Value; }
    explicit operator bool() const {
        return Value && Value != INVALID_HANDLE_VALUE;
    }
    HANDLE release() {
        const HANDLE value = Value;
        Value = nullptr;
        return value;
    }
    void reset(HANDLE value = nullptr) {
        if (*this) CloseHandle(Value);
        Value = value;
    }
private:
    HANDLE Value{};
};

class RemoteAllocation {
public:
    RemoteAllocation(HANDLE process, SIZE_T size) : Process(process) {
        Address = VirtualAllocEx(
            process, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!Address) Error = GetLastError();
    }
    ~RemoteAllocation() {
        if (Address) VirtualFreeEx(Process, Address, 0, MEM_RELEASE);
    }
    RemoteAllocation(const RemoteAllocation&) = delete;
    RemoteAllocation& operator=(const RemoteAllocation&) = delete;
    void* get() const { return Address; }
    DWORD error() const { return Error; }
private:
    HANDLE Process{};
    void* Address{};
    DWORD Error{ERROR_SUCCESS};
};

struct Options {
    std::filesystem::path Executable;
    std::filesystem::path LauncherExecutable;
    std::filesystem::path WaitForExecutable;
    std::vector<std::wstring> GameArguments;
    bool AllowLocalVersion{};
    bool ShowHelp{};
};

struct ObservedProcess {
    UniqueHandle Process;
    DWORD ProcessId{};
    DWORD InitialThreadId{};
};

std::filesystem::path currentExecutableDirectory() {
    std::array<wchar_t, 32768> path{};
    const DWORD length = GetModuleFileNameW(
        nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return {};
    return std::filesystem::path(path.data()).parent_path();
}

std::wstring quoteArgument(std::wstring_view value) {
    if (!value.empty() && value.find_first_of(L" \t\n\v\"") == std::wstring_view::npos)
        return std::wstring(value);

    std::wstring result{L"\""};
    std::size_t slashes{};
    for (const wchar_t character : value) {
        if (character == L'\\') {
            ++slashes;
            continue;
        }
        if (character == L'\"') {
            result.append(slashes * 2 + 1, L'\\');
            result.push_back(character);
            slashes = 0;
            continue;
        }
        result.append(slashes, L'\\');
        slashes = 0;
        result.push_back(character);
    }
    result.append(slashes * 2, L'\\');
    result.push_back(L'\"');
    return result;
}

void printUsage() {
    std::wcout
        << L"Usage:\n"
        << L"  Briefcase.Launcher.exe [--executable <shipping-path>] [-- <game arguments>]\n"
        << L"  Briefcase.Launcher.exe --launcher <launcher-path> --wait-for <shipping-path>\n"
        << L"                           [-- <launcher arguments>]\n\n"
        << L"If the exact Shipping executable is already running, both modes attach to it\n"
        << L"without starting another process. Otherwise, direct mode starts the Shipping\n"
        << L"executable and launcher mode starts the external launcher before injecting.\n"
        << L"A local version.dll is rejected by default.\n";
}

std::optional<Options> parseOptions(int argc, wchar_t** argv) {
    Options options;
    bool gameArguments{};
    for (int index = 1; index < argc; ++index) {
        const std::wstring_view argument{argv[index]};
        if (gameArguments) {
            options.GameArguments.emplace_back(argument);
        } else if (argument == L"--") {
            gameArguments = true;
        } else if (argument == L"--help" || argument == L"-h") {
            options.ShowHelp = true;
        } else if (argument == L"--allow-local-version") {
            options.AllowLocalVersion = true;
        } else if (argument == L"--executable" ||
                   argument == L"--launcher" ||
                   argument == L"--wait-for") {
            if (++index >= argc) {
                std::wcerr << L"Missing path after " << argument << L".\n";
                return std::nullopt;
            }
            if (argument == L"--executable") options.Executable = argv[index];
            if (argument == L"--launcher") options.LauncherExecutable = argv[index];
            if (argument == L"--wait-for") options.WaitForExecutable = argv[index];
        } else {
            std::wcerr << L"Unknown launcher option: " << argument << L"\n";
            return std::nullopt;
        }
    }

    const bool usesExternalLauncher =
        !options.LauncherExecutable.empty() || !options.WaitForExecutable.empty();
    if (usesExternalLauncher &&
        (options.LauncherExecutable.empty() || options.WaitForExecutable.empty())) {
        std::wcerr << L"--launcher and --wait-for must be provided together.\n";
        return std::nullopt;
    }
    if (usesExternalLauncher && !options.Executable.empty()) {
        std::wcerr << L"--executable cannot be combined with --launcher.\n";
        return std::nullopt;
    }
    return options;
}
std::optional<std::filesystem::path> findDefaultTarget(
    const std::filesystem::path& directory) {
    const std::array names{
        L"DeceiveInc-Win64-Shipping.exe",
        L"DeceiveIncServer-Win64-Shipping.exe"};
    std::optional<std::filesystem::path> result;
    for (const auto* name : names) {
        const auto candidate = directory / name;
        if (!std::filesystem::is_regular_file(candidate)) continue;
        if (result) return std::nullopt;
        result = candidate;
    }
    return result;
}

std::optional<std::filesystem::path> processImagePath(HANDLE process) {
    std::array<wchar_t, 32768> path{};
    DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &length) || !length)
        return std::nullopt;
    return std::filesystem::path(std::wstring(path.data(), length)).lexically_normal();
}

bool equalPath(const std::filesystem::path& left, const std::filesystem::path& right) {
    return _wcsicmp(left.c_str(), right.c_str()) == 0;
}

std::wstring win32ErrorText(DWORD error) {
    wchar_t* buffer{};
    DWORD length = FormatMessageW(
        FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
            FORMAT_MESSAGE_IGNORE_INSERTS,
        nullptr, error, MAKELANGID(LANG_ENGLISH, SUBLANG_ENGLISH_US),
        reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    if (!length) {
        length = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr, error, 0, reinterpret_cast<wchar_t*>(&buffer), 0, nullptr);
    }
    std::wstring result = length && buffer
        ? std::wstring(buffer, length)
        : L"unknown Windows error";
    if (buffer) LocalFree(buffer);
    while (!result.empty() &&
           (result.back() == L'\r' || result.back() == L'\n' ||
            result.back() == L' ')) {
        result.pop_back();
    }
    return result;
}

void explainRemoteAccessDenied(DWORD error) {
    if (error != ERROR_ACCESS_DENIED) return;
    std::wcerr
        << L"  Diagnostic: OpenProcess succeeded, but the target denied a remote "
        << L"memory operation.\n"
        << L"  If the launcher is elevated and the Windows protection level is none, "
        << L"process-protection software may have removed PROCESS_VM_OPERATION or "
        << L"PROCESS_VM_WRITE from the handle. Standard late attachment is then "
        << L"unavailable.\n";
}

std::optional<bool> processIsElevated(HANDLE process, DWORD* error = nullptr) {
    HANDLE tokenValue{};
    if (!OpenProcessToken(process, TOKEN_QUERY, &tokenValue)) {
        if (error) *error = GetLastError();
        return std::nullopt;
    }
    UniqueHandle token{tokenValue};
    TOKEN_ELEVATION elevation{};
    DWORD returned{};
    if (!GetTokenInformation(
            token.get(), TokenElevation, &elevation, sizeof(elevation), &returned)) {
        if (error) *error = GetLastError();
        return std::nullopt;
    }
    return elevation.TokenIsElevated != FALSE;
}

std::wstring_view protectionLevelName(DWORD level) {
    switch (level) {
    case 0x00000000: return L"WinTcb light";
    case 0x00000001: return L"Windows";
    case 0x00000002: return L"Windows light";
    case 0x00000003: return L"Antimalware light";
    case 0x00000004: return L"LSA light";
    case 0x00000005: return L"WinTcb";
    case 0x00000006: return L"Code generation light";
    case 0x00000007: return L"Authenticode";
    case 0x00000008: return L"PPL application";
    case 0xFFFFFFFE: return L"none";
    case 0xFFFFFFFF: return L"same as caller";
    default: return L"unknown";
    }
}

std::wstring_view machineName(USHORT machine) {
    switch (machine) {
    case IMAGE_FILE_MACHINE_UNKNOWN: return L"native";
    case IMAGE_FILE_MACHINE_I386: return L"x86";
    case IMAGE_FILE_MACHINE_AMD64: return L"x64";
    case IMAGE_FILE_MACHINE_ARM64: return L"ARM64";
    default: return L"unknown";
    }
}

void printProcessDiagnostics(HANDLE process) {
    std::wcout << L"Injection diagnostics:\n";

    DWORD error{};
    const auto launcherElevated = processIsElevated(GetCurrentProcess(), &error);
    if (launcherElevated) {
        std::wcout << L"  Launcher elevated: "
                   << (*launcherElevated ? L"yes" : L"no") << L"\n";
    } else {
        std::wcout << L"  Launcher elevation: unavailable (error=" << error
                   << L", " << win32ErrorText(error) << L")\n";
    }

    error = ERROR_SUCCESS;
    const auto targetElevated = processIsElevated(process, &error);
    if (targetElevated) {
        std::wcout << L"  Target elevated: "
                   << (*targetElevated ? L"yes" : L"no") << L"\n";
    } else {
        std::wcout << L"  Target elevation: unavailable (error=" << error
                   << L", " << win32ErrorText(error) << L")\n";
    }

    USHORT processMachine{};
    USHORT nativeMachine{};
    if (IsWow64Process2(process, &processMachine, &nativeMachine)) {
        std::wcout << L"  Target architecture: " << machineName(processMachine)
                   << L" (native machine: " << machineName(nativeMachine) << L")\n";
    } else {
        error = GetLastError();
        std::wcout << L"  Target architecture: unavailable (error=" << error
                   << L", " << win32ErrorText(error) << L")\n";
    }

    PROCESS_PROTECTION_LEVEL_INFORMATION protection{};
    if (GetProcessInformation(
            process, ProcessProtectionLevelInfo, &protection,
            sizeof(protection))) {
        std::wcout << L"  Windows protection level: "
                   << protectionLevelName(protection.ProtectionLevel)
                   << L" (" << protection.ProtectionLevel << L")\n";
    } else {
        error = GetLastError();
        std::wcout << L"  Windows protection level: unavailable (error=" << error
                   << L", " << win32ErrorText(error) << L")\n";
    }

    PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dynamicCode{};
    if (GetProcessMitigationPolicy(
            process, ProcessDynamicCodePolicy, &dynamicCode,
            sizeof(dynamicCode))) {
        std::wcout << L"  Dynamic-code policy: prohibit="
                   << (dynamicCode.ProhibitDynamicCode ? L"yes" : L"no")
                   << L", allow-thread-opt-out="
                   << (dynamicCode.AllowThreadOptOut ? L"yes" : L"no") << L"\n";
    } else {
        error = GetLastError();
        std::wcout << L"  Dynamic-code policy: unavailable (error=" << error
                   << L", " << win32ErrorText(error) << L")\n";
    }

    PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY signature{};
    if (GetProcessMitigationPolicy(
            process, ProcessSignaturePolicy, &signature, sizeof(signature))) {
        std::wcout << L"  Signature policy: Microsoft-only="
                   << (signature.MicrosoftSignedOnly ? L"yes" : L"no")
                   << L", Store-only="
                   << (signature.StoreSignedOnly ? L"yes" : L"no")
                   << L", opt-in="
                   << (signature.MitigationOptIn ? L"yes" : L"no") << L"\n";
    } else {
        error = GetLastError();
        std::wcout << L"  Signature policy: unavailable (error=" << error
                   << L", " << win32ErrorText(error) << L")\n";
    }
}

std::optional<DWORD> findProcessIdByPath(const std::filesystem::path& expectedPath) {
    UniqueHandle snapshot{CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)};
    if (!snapshot) return std::nullopt;

    const auto expectedName = expectedPath.filename().wstring();
    PROCESSENTRY32W entry{sizeof(entry)};
    if (!Process32FirstW(snapshot.get(), &entry)) return std::nullopt;
    do {
        if (_wcsicmp(entry.szExeFile, expectedName.c_str()) != 0) continue;
        UniqueHandle process{OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
            FALSE, entry.th32ProcessID)};
        if (!process) continue;
        const auto imagePath = processImagePath(process.get());
        if (imagePath && equalPath(*imagePath, expectedPath))
            return entry.th32ProcessID;
    } while (Process32NextW(snapshot.get(), &entry));
    return std::nullopt;
}

std::optional<DWORD> oldestThreadId(DWORD processId) {
    UniqueHandle snapshot{CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0)};
    if (!snapshot) return std::nullopt;

    std::optional<DWORD> result;
    FILETIME oldestCreation{};
    THREADENTRY32 entry{sizeof(entry)};
    if (!Thread32First(snapshot.get(), &entry)) return std::nullopt;
    do {
        if (entry.th32OwnerProcessID != processId) continue;
        UniqueHandle thread{OpenThread(
            THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID)};
        if (!thread) continue;
        FILETIME creation{}, exit{}, kernel{}, user{};
        if (!GetThreadTimes(thread.get(), &creation, &exit, &kernel, &user)) continue;
        if (!result || CompareFileTime(&creation, &oldestCreation) < 0) {
            result = entry.th32ThreadID;
            oldestCreation = creation;
        }
    } while (Thread32Next(snapshot.get(), &entry));
    return result;
}

std::optional<ObservedProcess> openProcessForInjection(
    DWORD processId, DWORD* openError = nullptr) {
    UniqueHandle process{OpenProcess(TargetProcessAccess, FALSE, processId)};
    if (!process) {
        if (openError) *openError = GetLastError();
        return std::nullopt;
    }

    const auto initialThreadId = oldestThreadId(processId);
    if (!initialThreadId) {
        if (openError) *openError = ERROR_NOT_FOUND;
        return std::nullopt;
    }
    return ObservedProcess{std::move(process), processId, *initialThreadId};
}

std::optional<ObservedProcess> waitForProcess(
    const std::filesystem::path& expectedPath) {
    const auto deadline = std::chrono::steady_clock::now() + TargetProcessTimeout;
    DWORD lastOpenError = ERROR_SUCCESS;
    do {
        const auto processId = findProcessIdByPath(expectedPath);
        if (processId) {
            auto observed = openProcessForInjection(*processId, &lastOpenError);
            if (observed) return observed;
        }
        Sleep(25);
    } while (std::chrono::steady_clock::now() < deadline);

    if (lastOpenError != ERROR_SUCCESS) {
        std::wcerr << L"The Shipping process appeared but could not be opened "
                   << L"for injection (error=" << lastOpenError << L").\n";
    }
    return std::nullopt;
}

std::wstring makeCommandLine(
    const std::filesystem::path& executable,
    const std::vector<std::wstring>& arguments) {
    std::wstring commandLine = quoteArgument(executable.wstring());
    for (const auto& argument : arguments) {
        commandLine.push_back(L' ');
        commandLine += quoteArgument(argument);
    }
    return commandLine;
}

bool createProcess(
    const std::filesystem::path& executable,
    const std::vector<std::wstring>& arguments,
    DWORD flags,
    PROCESS_INFORMATION& processInfo) {
    auto commandLine = makeCommandLine(executable, arguments);
    std::vector<wchar_t> mutableCommandLine(commandLine.begin(), commandLine.end());
    mutableCommandLine.push_back(L'\0');
    STARTUPINFOW startup{sizeof(startup)};
    return CreateProcessW(
        executable.c_str(), mutableCommandLine.data(), nullptr, nullptr, FALSE,
        flags | CREATE_UNICODE_ENVIRONMENT, nullptr,
        executable.parent_path().c_str(), &startup, &processInfo) != FALSE;
}
std::optional<std::uintptr_t> remoteModuleBaseOnce(
    DWORD processId, std::wstring_view moduleName) {
    const std::wstring expectedName{moduleName};
    UniqueHandle snapshot{CreateToolhelp32Snapshot(
        TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, processId)};
    if (!snapshot) return std::nullopt;

    MODULEENTRY32W entry{sizeof(entry)};
    if (!Module32FirstW(snapshot.get(), &entry)) return std::nullopt;
    do {
        if (_wcsicmp(entry.szModule, expectedName.c_str()) == 0)
            return reinterpret_cast<std::uintptr_t>(entry.modBaseAddr);
    } while (Module32NextW(snapshot.get(), &entry));
    return std::nullopt;
}

std::optional<std::uintptr_t> remoteModuleBase(
    DWORD processId, std::wstring_view moduleName) {
    const auto deadline = std::chrono::steady_clock::now() + ModuleDiscoveryTimeout;
    do {
        if (const auto module = remoteModuleBaseOnce(processId, moduleName)) {
            return module;
        }

        // A newly resumed process can temporarily expose an empty or partial
        // module snapshot while the Windows loader maps its initial imports.
        Sleep(25);
    } while (std::chrono::steady_clock::now() < deadline);
    return std::nullopt;
}

std::optional<std::wstring> owningModuleName(const void* address) {
    MEMORY_BASIC_INFORMATION memory{};
    if (!VirtualQuery(address, &memory, sizeof(memory)) || !memory.AllocationBase)
        return std::nullopt;
    std::array<wchar_t, 32768> path{};
    const DWORD length = GetModuleFileNameW(
        static_cast<HMODULE>(memory.AllocationBase), path.data(),
        static_cast<DWORD>(path.size()));
    if (!length || length >= path.size()) return std::nullopt;
    return std::filesystem::path(path.data()).filename().wstring();
}

bool runRemoteThread(
    HANDLE process,
    LPTHREAD_START_ROUTINE start,
    void* parameter,
    DWORD& exitCode) {
    UniqueHandle thread{CreateRemoteThread(
        process, nullptr, 0, start, parameter, 0, nullptr)};
    if (!thread) return false;
    if (WaitForSingleObject(thread.get(), RemoteOperationTimeoutMs) != WAIT_OBJECT_0) {
        SetLastError(ERROR_TIMEOUT);
        return false;
    }
    return GetExitCodeThread(thread.get(), &exitCode) != FALSE;
}

bool writeRemote(HANDLE process, void* destination, const void* source, SIZE_T size) {
    SIZE_T written{};
    return WriteProcessMemory(process, destination, source, size, &written) != FALSE &&
           written == size;
}

bool injectBootstrap(
    HANDLE process,
    DWORD processId,
    DWORD initialThreadId,
    const std::filesystem::path& bootstrapPath) {
    auto remoteBootstrap = remoteModuleBaseOnce(
        processId, bootstrapPath.filename().wstring());
    if (remoteBootstrap) {
        std::wcout << L"Briefcase.Bootstrap.dll is already loaded; "
                   << L"retrying runtime initialization.\n";
    } else {
        const auto path = bootstrapPath.wstring();
        RemoteAllocation remotePath{process, (path.size() + 1) * sizeof(wchar_t)};
        if (!remotePath.get()) {
            std::wcerr << L"VirtualAllocEx could not allocate the bootstrap path "
                       << L"in the target process (error=" << remotePath.error()
                       << L", " << win32ErrorText(remotePath.error()) << L").\n";
            explainRemoteAccessDenied(remotePath.error());
            return false;
        }
        if (!writeRemote(process, remotePath.get(), path.c_str(),
                         (path.size() + 1) * sizeof(wchar_t))) {
            const DWORD error = GetLastError();
            std::wcerr << L"WriteProcessMemory could not copy the bootstrap path "
                       << L"into the target process (error=" << error
                       << L", " << win32ErrorText(error) << L").\n";
            explainRemoteAccessDenied(error);
            return false;
        }

        const HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
        const auto loadLibrary = kernel32
            ? reinterpret_cast<const void*>(GetProcAddress(kernel32, "LoadLibraryW"))
            : nullptr;
        const auto ownerName = owningModuleName(loadLibrary);
        if (!loadLibrary || !ownerName) {
            std::wcerr << L"Could not resolve LoadLibraryW locally.\n";
            return false;
        }

        MEMORY_BASIC_INFORMATION ownerMemory{};
        if (!VirtualQuery(loadLibrary, &ownerMemory, sizeof(ownerMemory))) return false;
        const auto localOwner = reinterpret_cast<std::uintptr_t>(
            ownerMemory.AllocationBase);
        const auto remoteOwner = remoteModuleBase(processId, *ownerName);
        if (!remoteOwner) {
            std::wcerr << L"Could not find " << *ownerName
                       << L" in the child process.\n";
            return false;
        }
        const auto loadLibraryRva =
            reinterpret_cast<std::uintptr_t>(loadLibrary) - localOwner;
        const auto remoteLoadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
            *remoteOwner + loadLibraryRva);

        DWORD loadStatus{};
        if (!runRemoteThread(
                process, remoteLoadLibrary, remotePath.get(), loadStatus)) {
            const DWORD error = GetLastError();
            std::wcerr << L"Loading Briefcase.Bootstrap.dll was blocked or failed (error="
                       << error << L", " << win32ErrorText(error) << L").\n";
            explainRemoteAccessDenied(error);
            return false;
        }
        if (loadStatus == 0) {
            std::wcerr << L"LoadLibraryW rejected Briefcase.Bootstrap.dll.\n";
            return false;
        }

        remoteBootstrap = remoteModuleBase(
            processId, bootstrapPath.filename().wstring());
        if (!remoteBootstrap) {
            std::wcerr << L"The bootstrap DLL was not visible in the child process.\n";
            return false;
        }
    }

    const HMODULE localBootstrap = LoadLibraryExW(
        bootstrapPath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!localBootstrap) {
        const DWORD error = GetLastError();
        std::wcerr << L"Could not inspect the bootstrap export (error="
                   << error << L", " << win32ErrorText(error) << L").\n";
        return false;
    }
    const auto localEntry = reinterpret_cast<std::uintptr_t>(
        GetProcAddress(localBootstrap, BRIEFCASE_LAUNCHER_BOOTSTRAP_EXPORT));
    const auto localBase = reinterpret_cast<std::uintptr_t>(localBootstrap);
    if (!localEntry || localEntry < localBase) {
        std::wcerr << L"BriefcaseBootstrapRuntime is not exported.\n";
        FreeLibrary(localBootstrap);
        return false;
    }
    const auto entryRva = localEntry - localBase;
    FreeLibrary(localBootstrap);

    const BriefcaseLauncherBootstrapRequest request{
        sizeof(BriefcaseLauncherBootstrapRequest),
        BRIEFCASE_LAUNCHER_BOOTSTRAP_VERSION,
        initialThreadId,
        0};
    RemoteAllocation remoteRequest{process, sizeof(request)};
    if (!remoteRequest.get()) {
        std::wcerr << L"VirtualAllocEx could not allocate the bootstrap request "
                   << L"in the target process (error=" << remoteRequest.error()
                   << L", " << win32ErrorText(remoteRequest.error()) << L").\n";
        explainRemoteAccessDenied(remoteRequest.error());
        return false;
    }
    if (!writeRemote(
            process, remoteRequest.get(), &request, sizeof(request))) {
        const DWORD error = GetLastError();
        std::wcerr << L"WriteProcessMemory could not copy the bootstrap request "
                   << L"into the target process (error=" << error
                   << L", " << win32ErrorText(error) << L").\n";
        explainRemoteAccessDenied(error);
        return false;
    }

    const auto remoteEntry = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        *remoteBootstrap + entryRva);
    DWORD bootstrapStatus{};
    if (!runRemoteThread(
            process, remoteEntry, remoteRequest.get(), bootstrapStatus)) {
        const DWORD error = GetLastError();
        std::wcerr << L"The Briefcase bootstrap call failed (error="
                   << error << L", " << win32ErrorText(error) << L").\n";
        explainRemoteAccessDenied(error);
        return false;
    }
    if (bootstrapStatus != ERROR_SUCCESS) {
        std::wcerr << L"Briefcase initialization returned error="
                   << bootstrapStatus << L".\n";
        return false;
    }
    return true;
}
} // namespace

int wmain(int argc, wchar_t** argv) {
    std::wcout << L"Briefcase Launcher "
               << BRIEFCASE_VERSION_MAJOR << L"."
               << BRIEFCASE_VERSION_MINOR << L"."
               << BRIEFCASE_VERSION_BUILD << L"\n";

    const auto parsed = parseOptions(argc, argv);
    if (!parsed) {
        printUsage();
        return 2;
    }
    Options options = *parsed;
    if (options.ShowHelp) {
        printUsage();
        return 0;
    }

    try {
        const bool usesExternalLauncher = !options.LauncherExecutable.empty();
        if (!usesExternalLauncher && options.Executable.empty()) {
            const auto target = findDefaultTarget(currentExecutableDirectory());
            if (!target) {
                std::wcerr << L"Place Briefcase.Launcher.exe beside exactly one supported Shipping executable,\n"
                           << L"or pass --executable <path>.\n";
                return 2;
            }
            options.Executable = *target;
        }

        auto targetExecutable = usesExternalLauncher
            ? options.WaitForExecutable
            : options.Executable;
        targetExecutable =
            std::filesystem::absolute(targetExecutable).lexically_normal();
        if (!std::filesystem::is_regular_file(targetExecutable)) {
            std::wcerr << L"Target executable was not found: "
                       << targetExecutable.wstring() << L"\n";
            return 2;
        }
        if (usesExternalLauncher) {
            options.LauncherExecutable = std::filesystem::absolute(
                options.LauncherExecutable).lexically_normal();
            if (!std::filesystem::is_regular_file(options.LauncherExecutable)) {
                std::wcerr << L"External launcher was not found: "
                           << options.LauncherExecutable.wstring() << L"\n";
                return 2;
            }
        }

        const auto targetDirectory = targetExecutable.parent_path();
        const auto localVersion = targetDirectory / L"version.dll";
        if (!options.AllowLocalVersion && std::filesystem::is_regular_file(localVersion)) {
            std::wcerr
                << L"Injection-only test refused: a local version.dll is present.\n"
                << L"Temporarily rename it, then run the launcher again.\n"
                << L"Use --allow-local-version only when intentionally testing both loaders.\n";
            return 3;
        }

        const auto bootstrapPath =
            targetDirectory / L"Briefcase" / L"Core" / L"Native" /
            L"Briefcase.Bootstrap.dll";
        const auto runtimePath = bootstrapPath.parent_path() /
            L"Briefcase.UnrealRuntime.dll";
        if (!std::filesystem::is_regular_file(bootstrapPath) ||
            !std::filesystem::is_regular_file(runtimePath)) {
            std::wcerr << L"Briefcase native files are incomplete below: "
                       << bootstrapPath.parent_path().wstring() << L"\n";
            return 4;
        }

        UniqueHandle process;
        UniqueHandle initialThread;
        UniqueHandle externalLauncherProcess;
        UniqueHandle externalLauncherThread;
        DWORD processId{};
        DWORD initialThreadId{};
        bool attachedToExisting{};
        bool mayTerminateTarget{};

        if (const auto existingProcessId = findProcessIdByPath(targetExecutable)) {
            DWORD openError{};
            auto observed = openProcessForInjection(*existingProcessId, &openError);
            if (!observed) {
                std::wcerr << L"The existing Shipping process could not be opened "
                           << L"for injection (error=" << openError << L", "
                           << win32ErrorText(openError) << L").\n";
                return 6;
            }
            process = std::move(observed->Process);
            processId = observed->ProcessId;
            initialThreadId = observed->InitialThreadId;
            attachedToExisting = true;
            std::wcout << L"Found running target: " << targetExecutable.wstring() << L"\n"
                       << L"Attaching to PID: " << processId << L"\n"
                       << L"Game thread candidate: " << initialThreadId << L"\n";
            if (!options.GameArguments.empty()) {
                std::wcout << L"The target is already running; command-line arguments "
                           << L"were not applied.\n";
            }
        } else if (usesExternalLauncher) {
            PROCESS_INFORMATION launcherInfo{};
            if (!createProcess(
                    options.LauncherExecutable, options.GameArguments,
                    0, launcherInfo)) {
                std::wcerr << L"Could not start the external launcher (error="
                           << GetLastError() << L").\n";
                return 5;
            }
            externalLauncherProcess.reset(launcherInfo.hProcess);
            externalLauncherThread.reset(launcherInfo.hThread);
            std::wcout << L"Launcher: " << options.LauncherExecutable.wstring() << L"\n"
                       << L"Waiting for: " << targetExecutable.wstring() << L"\n";

            auto observed = waitForProcess(targetExecutable);
            if (!observed) {
                std::wcerr << L"Timed out waiting for the Shipping process.\n";
                return 6;
            }
            process = std::move(observed->Process);
            processId = observed->ProcessId;
            initialThreadId = observed->InitialThreadId;
            mayTerminateTarget = true;
            std::wcout << L"Observed Shipping process PID: " << processId << L"\n"
                       << L"Observed initial thread: " << initialThreadId << L"\n";
        } else {
            PROCESS_INFORMATION processInfo{};
            if (!createProcess(
                    targetExecutable, options.GameArguments,
                    CREATE_SUSPENDED, processInfo)) {
                std::wcerr << L"CreateProcessW failed (error="
                           << GetLastError() << L").\n";
                return 5;
            }
            process.reset(processInfo.hProcess);
            initialThread.reset(processInfo.hThread);
            processId = processInfo.dwProcessId;
            initialThreadId = processInfo.dwThreadId;
            mayTerminateTarget = true;

            std::wcout << L"Target: " << targetExecutable.wstring() << L"\n"
                       << L"PID: " << processId << L"\n"
                       << L"Resuming process initialization before LoadLibraryW...\n";
            if (ResumeThread(initialThread.get()) == static_cast<DWORD>(-1)) {
                std::wcerr << L"ResumeThread failed (error="
                           << GetLastError() << L").\n";
                TerminateProcess(process.get(), 1);
                return 6;
            }
        }

        printProcessDiagnostics(process.get());

        const bool runtimeAlreadyLoaded = remoteModuleBaseOnce(
            processId, runtimePath.filename().wstring()).has_value();
        if (runtimeAlreadyLoaded) {
            std::wcout << L"Briefcase.UnrealRuntime.dll is already loaded; "
                       << L"no injection is needed.\n";
        } else if (!injectBootstrap(
                       process.get(), processId, initialThreadId, bootstrapPath)) {
            if (mayTerminateTarget) {
                std::wcerr << L"Briefcase was not initialized; terminating the Shipping process.\n";
                TerminateProcess(process.get(), 1);
                WaitForSingleObject(process.get(), 5'000);
            } else {
                std::wcerr << L"Briefcase was not initialized; the existing Shipping "
                           << L"process was left running.\n";
            }
            return 7;
        }

        std::wcout << (runtimeAlreadyLoaded
                           ? L"Briefcase is already initialized in the target process.\n"
                           : L"Briefcase was injected and initialized successfully.\n")
                   << L"Runtime log: "
                   << (targetDirectory / L"Briefcase" / L"Briefcase.log").wstring()
                   << L"\nWaiting for the Shipping process to exit...\n";
        WaitForSingleObject(process.get(), INFINITE);
        DWORD exitCode{};
        if (!GetExitCodeProcess(process.get(), &exitCode)) return 8;
        std::wcout << L"Shipping process exit code: " << exitCode << L"\n";
        if (attachedToExisting) {
            std::wcout << L"Attached launcher session ended with the target process.\n";
        }
        return static_cast<int>(exitCode);
    } catch (const std::exception& exception) {
        std::cerr << "Launcher error: " << exception.what() << "\n";
        return 9;
    }
}
