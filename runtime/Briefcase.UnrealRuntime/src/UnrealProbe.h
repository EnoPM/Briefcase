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
using UnrealRuntimeReadyCallback = void (*)(HMODULE);
void runUnrealProbe(HMODULE self, UnrealRuntimeReadyCallback onRuntimeReady = nullptr);
}
