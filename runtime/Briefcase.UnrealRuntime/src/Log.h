#pragma once

#include <filesystem>
#include <string_view>

namespace briefcase {
void openLog(const std::filesystem::path& modulePath);
void log(std::wstring_view message);
void closeLog();
}
