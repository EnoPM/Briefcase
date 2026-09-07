#pragma once

#include <Windows.h>
#include "RuntimeProfile.h"

struct BriefcaseUnrealApi;
struct BriefcasePatchingApi;
struct BriefcaseGameThreadApi;

namespace briefcase {
const BriefcaseUnrealApi* getUnrealApi();
const BriefcasePatchingApi* getPatchingApi();
const BriefcaseGameThreadApi* getGameThreadApi();
void captureGameThreadId(DWORD threadId);
bool isUnrealApiReady();
const profile::RuntimeProfile* getRuntimeProfile();
void runUnrealProbe(HMODULE self);
}
