#pragma once

#include <Windows.h>
#include "RuntimeProfile.h"

struct BriefcaseUnrealApi;
struct BriefcasePatchingApi;

namespace briefcase {
const BriefcaseUnrealApi* getUnrealApi();
const BriefcasePatchingApi* getPatchingApi();
bool isUnrealApiReady();
const profile::RuntimeProfile* getRuntimeProfile();
void runUnrealProbe(HMODULE self);
}
