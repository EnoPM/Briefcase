#include "Log.h"

#include <Windows.h>
#include <fstream>
#include <mutex>

namespace {
std::mutex LogMutex;
std::wofstream LogFile;
}

namespace briefcase {

void openLog(const std::filesystem::path& modulePath) {
    std::scoped_lock lock(LogMutex);
    const auto directory = modulePath.parent_path() / L"Briefcase";
    std::filesystem::create_directories(directory);
    const auto path = directory / L"Briefcase.log";
    LogFile.open(path, std::ios::out | std::ios::trunc);
}

void log(std::wstring_view message) {
    std::scoped_lock lock(LogMutex);
    std::wstring line{L"Briefcase: "};
    line.append(message);
    line.push_back(L'\n');
    OutputDebugStringW(line.c_str());
    if (LogFile) {
        LogFile << line;
        LogFile.flush();
    }
}

void closeLog() {
    std::scoped_lock lock(LogMutex);
    LogFile.close();
}

} // namespace briefcase
