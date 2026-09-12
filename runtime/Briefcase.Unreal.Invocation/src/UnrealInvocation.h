#pragma once

#include "UnrealValueCodec.h"

#include <cstdint>

namespace briefcase::unreal {

// The runtime captures Unreal's game thread once during bootstrap. Invocation
// entry points fail closed when called from another thread.
void setCapturedGameThreadId(std::uint32_t threadId) noexcept;
[[nodiscard]] std::uint32_t capturedGameThreadId() noexcept;
[[nodiscard]] bool onCapturedGameThread() noexcept;

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeFunction(
    void* context, BriefcaseObjectHandle objectHandle,
    BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::uint32_t expectedParameterSize,
    void* parameters, std::uint32_t parameterSize);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokeFunctionText(
    void* context, BriefcaseObjectHandle objectHandle,
    BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::uint32_t expectedParameterSize,
    void* parameters, std::uint32_t parameterSize,
    const BriefcaseTextArgument* textArguments,
    std::uint32_t textArgumentCount);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiPrepareFunction(
    void* context, BriefcaseObjectHandle ownerClassHandle, const char* utf8Name,
    std::uint32_t nameLength, std::uint32_t expectedParameterSize,
    const BriefcasePreparedParameter* parameters, std::uint32_t parameterCount,
    std::uint64_t* preparedFunction);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokePreparedFunction(
    void* context, std::uint64_t preparedFunction,
    BriefcaseObjectHandle objectHandle, void* parameters,
    std::uint32_t parameterSize);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokePreparedValueFunction(
    void* context, std::uint64_t preparedFunction,
    BriefcaseObjectHandle objectHandle, void* parameters,
    std::uint32_t parameterSize, BriefcaseOwnedValueBuffer* outputs);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiInvokePreparedValueFunctionV2(
    void* context, std::uint64_t preparedFunction,
    BriefcaseObjectHandle objectHandle, void* parameters,
    std::uint32_t parameterSize, const BriefcaseValueInput* inputs,
    std::uint32_t inputCount, BriefcaseOwnedValueBuffer* outputs);
void BRIEFCASE_MOD_CALL apiReleaseValueBuffer(
    void* context, BriefcaseOwnedValueBuffer* buffer);

BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiPrepareProperty(
    void* context, BriefcaseObjectHandle ownerClassHandle,
    const char* utf8Name, std::uint32_t nameLength,
    std::int32_t expectedOffset, std::int32_t expectedElementSize,
    std::int32_t expectedArrayDimension, BriefcasePropertyKind expectedKind,
    std::uint64_t* preparedProperty);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiReadPreparedProperty(
    void* context, std::uint64_t preparedProperty,
    BriefcaseObjectHandle objectHandle, void* output,
    std::uint32_t outputSize);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePreparedProperty(
    void* context, std::uint64_t preparedProperty,
    BriefcaseObjectHandle objectHandle, const void* input,
    std::uint32_t inputSize);
BriefcaseUnrealResult BRIEFCASE_MOD_CALL apiWritePreparedValueProperty(
    void* context, std::uint64_t preparedProperty,
    BriefcaseObjectHandle objectHandle, const std::uint8_t* input,
    std::uint32_t inputSize);

} // namespace briefcase::unreal
