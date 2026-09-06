#pragma once

// Public binary contract between version.dll and native mods.
// Keep this header free of STL and Unreal types: both sides may use different
// compilers and runtime-library settings while still sharing this C ABI.

#include <stdint.h>

#if defined(_WIN32)
#define BRIEFCASE_MOD_CALL __cdecl
#define BRIEFCASE_MOD_EXPORT extern "C" __declspec(dllexport)
#else
#define BRIEFCASE_MOD_CALL
#define BRIEFCASE_MOD_EXPORT extern "C"
#endif

#define BRIEFCASE_HOST_API_VERSION 1u
#define BRIEFCASE_UNREAL_API_VERSION 6u
#define BRIEFCASE_RENDERING_API_VERSION 4u
#define BRIEFCASE_INPUT_API_VERSION 1u
#define BRIEFCASE_PATCHING_API_VERSION 4u
#define BRIEFCASE_MOD_ID_CAPACITY 64u
#define BRIEFCASE_MOD_NAME_CAPACITY 96u
#define BRIEFCASE_MOD_AUTHOR_CAPACITY 64u
#define BRIEFCASE_MOD_VERSION_CAPACITY 32u
#define BRIEFCASE_MOD_DESCRIPTION_CAPACITY 192u

typedef uint32_t BriefcaseBool;

enum BriefcaseCapability : uint64_t {
    BRIEFCASE_CAPABILITY_CORE = 1ull << 0,
    // Reserved now; exposed only after handles and game-thread dispatch exist.
    BRIEFCASE_CAPABILITY_UNREAL_REFLECTION = 1ull << 1,
    BRIEFCASE_CAPABILITY_UNREAL_INVOCATION = 1ull << 2,
    BRIEFCASE_CAPABILITY_RENDERING = 1ull << 3,
    BRIEFCASE_CAPABILITY_INPUT = 1ull << 4,
    BRIEFCASE_CAPABILITY_PATCHING = 1ull << 5
};

enum BriefcaseLogLevel : uint32_t {
    BRIEFCASE_LOG_TRACE = 0,
    BRIEFCASE_LOG_INFO = 1,
    BRIEFCASE_LOG_WARNING = 2,
    BRIEFCASE_LOG_ERROR = 3
};

struct BriefcaseVersion {
    uint16_t Major;
    uint16_t Minor;
    uint16_t Patch;
    uint16_t Reserved;
};

struct BriefcaseGameBuild {
    uint32_t PeTimestamp;
    uint32_t ImageSize;
};

typedef void(BRIEFCASE_MOD_CALL* BriefcaseLogFn)(
    void* context,
    BriefcaseLogLevel level,
    const char* utf8Message,
    uint32_t messageLength);
typedef BriefcaseVersion(BRIEFCASE_MOD_CALL* BriefcaseGetFrameworkVersionFn)(void* context);
typedef BriefcaseGameBuild(BRIEFCASE_MOD_CALL* BriefcaseGetGameBuildFn)(void* context);

struct BriefcaseCoreApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseLogFn Log;
    BriefcaseGetFrameworkVersionFn GetFrameworkVersion;
    BriefcaseGetGameBuildFn GetGameBuild;
    void* Reserved[8];
};

enum BriefcaseUnrealResult : uint32_t {
    BRIEFCASE_UNREAL_OK = 0,
    BRIEFCASE_UNREAL_INVALID_ARGUMENT = 1,
    BRIEFCASE_UNREAL_NOT_READY = 2,
    BRIEFCASE_UNREAL_NOT_FOUND = 3,
    BRIEFCASE_UNREAL_STALE_HANDLE = 4,
    BRIEFCASE_UNREAL_BUFFER_TOO_SMALL = 5,
    BRIEFCASE_UNREAL_TYPE_MISMATCH = 6,
    BRIEFCASE_UNREAL_LAYOUT_MISMATCH = 7,
    BRIEFCASE_UNREAL_UNREADABLE = 8,
    BRIEFCASE_UNREAL_UNSUPPORTED = 9
};

enum BriefcasePropertyKind : uint32_t {
    BRIEFCASE_PROPERTY_UNKNOWN = 0,
    BRIEFCASE_PROPERTY_INT32 = 1,
    BRIEFCASE_PROPERTY_UINT32 = 2,
    BRIEFCASE_PROPERTY_INT64 = 3,
    BRIEFCASE_PROPERTY_UINT64 = 4,
    BRIEFCASE_PROPERTY_FLOAT = 5,
    BRIEFCASE_PROPERTY_DOUBLE = 6,
    BRIEFCASE_PROPERTY_BOOL = 7,
    BRIEFCASE_PROPERTY_OBJECT = 8,
    BRIEFCASE_PROPERTY_STRUCT = 9,
    BRIEFCASE_PROPERTY_STRING = 10,
    BRIEFCASE_PROPERTY_BYTE = 11,
    BRIEFCASE_PROPERTY_TEXT = 12
};

enum BriefcaseTextArgumentFlags : uint32_t {
    BRIEFCASE_TEXT_INPUT = 1,
    BRIEFCASE_TEXT_OUTPUT = 2
};

// FText is an owning Unreal type and must never be copied across the managed
// ABI as 24 opaque bytes. This descriptor transports UTF-16 text while the
// runtime constructs and destroys the real FText inside ProcessEvent storage.
struct BriefcaseTextArgument {
    uint32_t StructSize;
    int32_t ParameterOffset;
    uint32_t Flags;
    uint32_t InputCharacters;
    const uint16_t* Input;
    uint16_t* Output;
    uint32_t* RequiredCharacters;
    uint32_t OutputCapacityCharacters;
    uint32_t Reserved0;
    uint64_t Reserved[2];
};

// A UObject address is never exposed. Unreal increments SerialNumber when an
// object-array slot is reused, making a stale {Index, SerialNumber} fail closed.
struct BriefcaseObjectHandle {
    uint32_t Index;
    uint32_t SerialNumber;
};

struct BriefcasePropertyInfo {
    uint32_t StructSize;
    uint32_t Kind;
    int32_t Offset;
    int32_t ElementSize;
    int32_t ArrayDimension;
    uint32_t Reserved0;
    uint64_t Flags;
    uint64_t Reserved[4];
};

typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseFindObjectFn)(
    void* context, const char* utf8Path, uint32_t pathLength, BriefcaseObjectHandle* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseFindObjectsOfClassFn)(
    void* context, BriefcaseObjectHandle classHandle, BriefcaseObjectHandle* results,
    uint32_t capacity, uint32_t* written, uint32_t* total);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseGetObjectTextFn)(
    void* context, BriefcaseObjectHandle object, char* utf8Buffer,
    uint32_t capacity, uint32_t* requiredBytes);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseGetObjectClassFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle* classHandle);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseIsObjectAFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle classHandle, BriefcaseBool* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseGetPropertyInfoFn)(
    void* context, BriefcaseObjectHandle ownerClass, const char* utf8Name,
    uint32_t nameLength, BriefcasePropertyInfo* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseReadPropertyFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, void* output, uint32_t outputSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseInvokeFunctionFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, uint32_t expectedParameterSize,
    void* parameters, uint32_t parameterSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseReadStringPropertyFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    uint16_t* destination, uint32_t capacityCharacters, uint32_t* requiredCharacters);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseInvokeFunctionTextFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, uint32_t expectedParameterSize,
    void* parameters, uint32_t parameterSize,
    const BriefcaseTextArgument* textArguments, uint32_t textArgumentCount);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseReadTextPropertyFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    uint16_t* destination, uint32_t capacityCharacters, uint32_t* requiredCharacters);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseInvokeNativeBooleanFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseGameBuild expectedBuild,
    uint64_t functionRva, BriefcaseBool* result);
struct BriefcaseUnrealApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseFindObjectFn FindObject;
    BriefcaseFindObjectsOfClassFn FindObjectsOfClass;
    BriefcaseGetObjectTextFn GetObjectName;
    BriefcaseGetObjectTextFn GetObjectPath;
    BriefcaseGetObjectClassFn GetObjectClass;
    BriefcaseIsObjectAFn IsObjectA;
    BriefcaseGetPropertyInfoFn GetPropertyInfo;
    BriefcaseReadPropertyFn ReadProperty;
    // API v2 consumes the first v1 reserved slot without moving earlier fields.
    BriefcaseInvokeFunctionFn InvokeFunction;
    // API v3 copies FString properties into caller-owned UTF-16 storage.
    BriefcaseReadStringPropertyFn ReadStringProperty;
    // API v5 owns every native FText lifetime and only exchanges UTF-16.
    BriefcaseInvokeFunctionTextFn InvokeFunctionText;
    BriefcaseReadTextPropertyFn ReadTextProperty;
    // API v6 invokes a build-pinned native `bool Method()` without exposing a
    // UObject address across the ABI. The runtime validates the object handle,
    // executable image range, and exact PE identity before making the call.
    BriefcaseInvokeNativeBooleanFn InvokeNativeBoolean;
    void* Reserved[3];
};

struct BriefcaseRenderFrame {
    uint32_t StructSize;
    uint32_t Width;
    uint32_t Height;
    BriefcaseBool MenuVisible;
    float DeltaSeconds;
    uint32_t Reserved0;
    uint64_t FrameNumber;
    uint64_t Reserved[4];
};

typedef void(BRIEFCASE_MOD_CALL* BriefcaseRenderCallbackFn)(
    void* userContext, const BriefcaseRenderFrame* frame);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseRegisterRenderCallbackFn)(
    void* context, BriefcaseRenderCallbackFn callback, void* userContext, uint64_t* registrationId);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseUnregisterRenderCallbackFn)(
    void* context, uint64_t registrationId);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseSetRenderCallbackActiveFn)(
    void* context, uint64_t registrationId, BriefcaseBool activeWhenMenuHidden);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseSetMenuVisibleFn)(void* context, BriefcaseBool visible);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseGetMenuVisibleFn)(void* context);

// The first rendering ABI deliberately exposes only the ImGui calls needed by
// the managed smoke-test menu. All calls must happen inside a render callback.
// Text is UTF-8 and length-delimited; no variadic C functions cross the ABI.
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiBeginFn)(
    void* context, const char* utf8Name, uint32_t nameLength, BriefcaseBool* open, uint32_t flags);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiEndFn)(void* context);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiTextFn)(
    void* context, const char* utf8Text, uint32_t textLength);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiCheckboxFn)(
    void* context, const char* utf8Label, uint32_t labelLength, BriefcaseBool* value);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiSliderFloatFn)(
    void* context, const char* utf8Label, uint32_t labelLength, float* value,
    float minimum, float maximum, const char* utf8Format, uint32_t formatLength);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiButtonFn)(
    void* context, const char* utf8Label, uint32_t labelLength, float width, float height);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSameLineFn)(void* context);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSeparatorTextFn)(
    void* context, const char* utf8Label, uint32_t labelLength);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSetNextWindowPositionFn)(
    void* context, float x, float y, uint32_t condition, float pivotX, float pivotY);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSetNextWindowSizeFn)(
    void* context, float width, float height, uint32_t condition);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiBeginChildFn)(
    void* context, const char* utf8Id, uint32_t idLength, float width, float height,
    uint32_t childFlags, uint32_t windowFlags);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiEndChildFn)(void* context);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiSelectableFn)(
    void* context, const char* utf8Label, uint32_t labelLength, BriefcaseBool selected,
    uint32_t flags, float width, float height);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSeparatorFn)(void* context);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSpacingFn)(void* context);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiSliderIntFn)(
    void* context, const char* utf8Label, uint32_t labelLength, int32_t* value,
    int32_t minimum, int32_t maximum, const char* utf8Format, uint32_t formatLength);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiSetNextItemWidthFn)(void* context, float width);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiTextColoredFn)(
    void* context, uint32_t rgba, const char* utf8Text, uint32_t textLength);
typedef float(BRIEFCASE_MOD_CALL* BriefcaseImGuiGetFramerateFn)(void* context);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiDrawCircleFn)(
    void* context, float x, float y, float radius, uint32_t rgba,
    int32_t segments, float thickness, BriefcaseBool filled);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiDrawLineFn)(
    void* context, float x1, float y1, float x2, float y2,
    uint32_t rgba, float thickness);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiDrawRectFilledFn)(
    void* context, float minimumX, float minimumY, float maximumX, float maximumY,
    uint32_t rgba, float rounding);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiDrawTextFn)(
    void* context, float x, float y, uint32_t rgba,
    const char* utf8Text, uint32_t textLength);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiBeginComboFn)(
    void* context, const char* utf8Label, uint32_t labelLength,
    const char* utf8Preview, uint32_t previewLength, uint32_t flags);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseImGuiEndComboFn)(void* context);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseImGuiInputTextFn)(
    void* context, const char* utf8Label, uint32_t labelLength,
    char* utf8Buffer, uint32_t bufferCapacity, uint32_t flags);

struct BriefcaseRenderingApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseRegisterRenderCallbackFn RegisterCallback;
    BriefcaseUnregisterRenderCallbackFn UnregisterCallback;
    BriefcaseSetRenderCallbackActiveFn SetCallbackActive;
    BriefcaseSetMenuVisibleFn SetMenuVisible;
    BriefcaseGetMenuVisibleFn GetMenuVisible;
    BriefcaseImGuiBeginFn Begin;
    BriefcaseImGuiEndFn End;
    BriefcaseImGuiTextFn Text;
    BriefcaseImGuiCheckboxFn Checkbox;
    BriefcaseImGuiSliderFloatFn SliderFloat;
    BriefcaseImGuiButtonFn Button;
    BriefcaseImGuiSameLineFn SameLine;
    BriefcaseImGuiSeparatorTextFn SeparatorText;
    BriefcaseImGuiSetNextWindowPositionFn SetNextWindowPosition;
    BriefcaseImGuiSetNextWindowSizeFn SetNextWindowSize;
    BriefcaseImGuiBeginChildFn BeginChild;
    BriefcaseImGuiEndChildFn EndChild;
    BriefcaseImGuiSelectableFn Selectable;
    BriefcaseImGuiSeparatorFn Separator;
    BriefcaseImGuiSpacingFn Spacing;
    BriefcaseImGuiSliderIntFn SliderInt;
    BriefcaseImGuiSetNextItemWidthFn SetNextItemWidth;
    BriefcaseImGuiTextColoredFn TextColored;
    BriefcaseImGuiGetFramerateFn GetFramerate;
    BriefcaseImGuiDrawCircleFn DrawCircle;
    BriefcaseImGuiDrawLineFn DrawLine;
    BriefcaseImGuiDrawRectFilledFn DrawRectFilled;
    BriefcaseImGuiDrawTextFn DrawText;
    // API v3 consumes the first two v2 reserved slots without moving any
    // earlier member. BeginCombo returning true must be paired with EndCombo.
    BriefcaseImGuiBeginComboFn BeginCombo;
    BriefcaseImGuiEndComboFn EndCombo;
    // API v4 adds bounded, mutable UTF-8 text input.
    BriefcaseImGuiInputTextFn InputText;
    void* Reserved[5];
};

typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseIsKeyDownFn)(void* context, uint32_t virtualKey);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseGameHasFocusFn)(void* context);

// Read-only operating-system input state. A mod cannot synthesize keys or
// mouse events through this table.
struct BriefcaseInputApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseIsKeyDownFn IsKeyDown;
    BriefcaseGameHasFocusFn GameHasFocus;
    void* Reserved[8];
};

enum BriefcasePatchPhase : uint32_t {
    BRIEFCASE_PATCH_PREFIX = 0,
    BRIEFCASE_PATCH_POSTFIX = 1
};

struct BriefcasePatchCall {
    uint32_t StructSize;
    BriefcasePatchPhase Phase;
    BriefcaseObjectHandle Instance;
    BriefcaseBool OriginalRan;
    void* Parameters;
    uint32_t ParameterSize;
    uint32_t Reserved0;
    uint64_t Reserved[4];
};

typedef void(BRIEFCASE_MOD_CALL* BriefcasePatchCallbackFn)(
    void* userContext, BriefcasePatchCall* call, BriefcaseBool* runOriginal);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseRegisterPatchFn)(
    void* context, BriefcaseObjectHandle targetClass, BriefcaseObjectHandle functionOwnerClass,
    const char* utf8FunctionName, uint32_t functionNameLength, BriefcasePatchPhase phase,
    BriefcasePatchCallbackFn callback, void* userContext, uint64_t* registrationId);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseUnregisterPatchFn)(
    void* context, uint64_t registrationId);
// The initial native contract accepts reflected void Method() instance functions. The host uses
// the type and method identity to select a build-validated machine-code target.
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseRegisterNativePatchFn)(
    void* context, BriefcaseObjectHandle targetClass, BriefcaseObjectHandle functionOwnerClass,
    const char* utf8FunctionName, uint32_t functionNameLength, BriefcasePatchPhase phase,
    BriefcasePatchCallbackFn callback, void* userContext, uint64_t* registrationId);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseUnregisterNativePatchFn)(
    void* context, uint64_t registrationId);
// Copies values owned by an active ProcessEvent parameter buffer. These
// helpers validate Unreal's container descriptor and copy the data before the
// managed callback returns, so no game pointer escapes into a mod.
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseCopyPatchByteArrayFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    uint8_t* destination, uint32_t capacity, uint32_t* requiredBytes);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseCopyPatchStringFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    uint16_t* destination, uint32_t capacityCharacters, uint32_t* requiredCharacters);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseCopyPatchTextFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    uint16_t* destination, uint32_t capacityCharacters, uint32_t* requiredCharacters);

struct BriefcasePatchingApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseRegisterPatchFn RegisterPatch;
    BriefcaseUnregisterPatchFn UnregisterPatch;
    BriefcaseRegisterNativePatchFn RegisterNativePatch;
    BriefcaseUnregisterNativePatchFn UnregisterNativePatch;
    // API v4 consumes three reserved slots without moving earlier members.
    BriefcaseCopyPatchByteArrayFn CopyByteArray;
    BriefcaseCopyPatchStringFn CopyString;
    BriefcaseCopyPatchTextFn CopyText;
    void* Reserved[3];
};

// Future services are separate versioned tables. Adding a field to reflection,
// rendering or input will not move the existing Core pointer.
struct BriefcaseHostApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    uint64_t Capabilities;
    const BriefcaseCoreApi* Core;
    const BriefcaseUnrealApi* Unreal;
    const BriefcaseRenderingApi* Rendering;
    const BriefcaseInputApi* Input;
    const BriefcasePatchingApi* Patching;
    void* Reserved[7];
};

// All text is UTF-8 and stored inline. The host never frees mod-owned memory.
struct BriefcaseModInfo {
    uint32_t StructSize;
    uint32_t MinimumHostApiVersion;
    uint32_t MaximumHostApiVersion;
    uint32_t Reserved0;
    uint64_t RequiredCapabilities;
    char Id[BRIEFCASE_MOD_ID_CAPACITY];
    char Name[BRIEFCASE_MOD_NAME_CAPACITY];
    char Author[BRIEFCASE_MOD_AUTHOR_CAPACITY];
    char Version[BRIEFCASE_MOD_VERSION_CAPACITY];
    char Description[BRIEFCASE_MOD_DESCRIPTION_CAPACITY];
    uint64_t Reserved[8];
};

typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseModQueryFn)(uint32_t hostApiVersion, BriefcaseModInfo* info);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseModLoadFn)(const BriefcaseHostApi* host);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseModUnloadFn)(void);

#define BRIEFCASE_MOD_QUERY_EXPORT "BriefcaseMod_Query"
#define BRIEFCASE_MOD_LOAD_EXPORT "BriefcaseMod_Load"
#define BRIEFCASE_MOD_UNLOAD_EXPORT "BriefcaseMod_Unload"
