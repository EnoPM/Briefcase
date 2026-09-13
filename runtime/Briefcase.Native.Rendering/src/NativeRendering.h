#pragma once

#include <Briefcase/BriefcaseModApi.h>

#if defined(BRIEFCASE_NATIVE_RENDERING_EXPORTS)
#define BRIEFCASE_RENDERING_EXPORT extern "C" __declspec(dllexport)
#else
#define BRIEFCASE_RENDERING_EXPORT extern "C" __declspec(dllimport)
#endif

#define BRIEFCASE_RENDERING_API_EXPORT "BriefcaseGetRenderingApi"

using BriefcaseGetRenderingApiFn = const BriefcaseRenderingApi*(BRIEFCASE_MOD_CALL*)();

BRIEFCASE_RENDERING_EXPORT const BriefcaseRenderingApi* BRIEFCASE_MOD_CALL
BriefcaseGetRenderingApi();
