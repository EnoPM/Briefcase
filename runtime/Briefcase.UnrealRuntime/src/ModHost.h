#pragma once

#include <Windows.h>

struct BriefcaseHostApi;

namespace briefcase {
const BriefcaseHostApi* getHostApi();
void runModHost(HMODULE frameworkModule);
}
