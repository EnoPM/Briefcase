#pragma once

#include "RuntimeProfile.h"
#include "UnrealInvocation.h"

#include <cstddef>
#include <cstdint>

namespace briefcase::unreal {

// Supplies the validated executable identity and mapped image used by native
// hooks. Until this is called, every RVA-based entry point fails closed.
void configurePatchingRuntime(
    const profile::RuntimeProfile* runtimeProfile,
    std::byte* imageBase,
    std::size_t imageSize) noexcept;

// The runtime arms this short-lived gate before starting CoreCLR. Once the
// global ProcessEvent detour exists, the captured game thread waits at that
// safe boundary until managed mods have registered their early native hooks.
// The wait is bounded and fails open, so a broken managed host cannot prevent
// the game or dedicated server from starting.
void beginManagedStartupBarrier() noexcept;
void completeManagedStartupBarrier() noexcept;

BriefcaseBool BRIEFCASE_MOD_CALL apiRegisterGameThreadCallback(
    void* context, BriefcaseGameThreadCallbackFn callback, void* userContext,
    std::uint64_t* registrationId);
BriefcaseBool BRIEFCASE_MOD_CALL apiUnregisterGameThreadCallback(
    void* context, std::uint64_t registrationId);
void BRIEFCASE_MOD_CALL apiRequestGameThreadPump(void* context);
BriefcaseBool BRIEFCASE_MOD_CALL apiIsGameThread(void* context);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiRegisterPatch(
    void* context, BriefcaseObjectHandle targetClassHandle,
    BriefcaseObjectHandle functionOwnerClassHandle,
    const char* utf8FunctionName, std::uint32_t functionNameLength,
    BriefcasePatchPhase phase, BriefcasePatchCallbackFn callback,
    void* userContext, std::uint64_t* registrationId);
BriefcaseBool BRIEFCASE_MOD_CALL apiUnregisterPatch(
    void* context, std::uint64_t registrationId);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiSubscribeMulticastDelegate(
    void* context, BriefcaseObjectHandle objectHandle,
    BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::int32_t expectedOffset,
    std::int32_t expectedElementSize, std::int32_t expectedArrayDimension,
    BriefcasePatchCallbackFn callback, void* userContext,
    std::uint64_t* registrationId);
BriefcaseBool BRIEFCASE_MOD_CALL apiUnsubscribeDelegate(
    void* context, std::uint64_t registrationId);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiRegisterNativePatch(
    void* context, BriefcaseObjectHandle targetClassHandle,
    BriefcaseObjectHandle functionOwnerClassHandle,
    const char* utf8FunctionName, std::uint32_t functionNameLength,
    BriefcasePatchPhase phase, BriefcasePatchCallbackFn callback,
    void* userContext, std::uint64_t* registrationId);
BriefcaseBool BRIEFCASE_MOD_CALL apiUnregisterNativePatch(
    void* context, std::uint64_t registrationId);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchByteArray(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, std::uint8_t* destination,
    std::uint32_t capacity, std::uint32_t* requiredBytes);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchString(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, std::uint16_t* destination,
    std::uint32_t capacityCharacters, std::uint32_t* requiredCharacters);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchText(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, std::uint16_t* destination,
    std::uint32_t capacityCharacters, std::uint32_t* requiredCharacters);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiCopyPatchValue(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, BriefcasePropertyKind expectedKind,
    std::uint8_t* destination, std::uint32_t capacity,
    std::uint32_t* requiredBytes);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePatchValue(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, BriefcasePropertyKind expectedKind,
    const void* input, std::uint32_t inputSize);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePatchText(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, BriefcasePropertyKind expectedKind,
    const std::uint16_t* characters, std::uint32_t characterCount);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePatchEncodedValue(
    void* context, const BriefcasePatchCall* call,
    std::uint32_t parameterOffset, BriefcasePropertyKind expectedKind,
    const std::uint8_t* input, std::uint32_t inputSize);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeNativeBoolean(
    void* context, BriefcaseObjectHandle objectHandle,
    BriefcaseGameBuild expectedBuild, std::uint64_t functionRva,
    BriefcaseBool* result);

} // namespace briefcase::unreal
