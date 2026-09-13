#pragma once

#include <Windows.h>
#include <filesystem>

struct BriefcaseHostApi;

namespace briefcase {
void loadOptionalRenderingHost(const std::filesystem::path& frameworkDirectory);
const BriefcaseHostApi* getHostApi();
void runModHost(HMODULE frameworkModule);
}
