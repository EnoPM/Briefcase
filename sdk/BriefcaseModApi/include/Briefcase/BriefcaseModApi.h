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
#define BRIEFCASE_UNREAL_API_VERSION 16u
#define BRIEFCASE_INPUT_API_VERSION 1u
#define BRIEFCASE_PATCHING_API_VERSION 7u
#define BRIEFCASE_GAME_THREAD_API_VERSION 1u
#define BRIEFCASE_RENDERING_API_VERSION 1u
#define BRIEFCASE_OVERLAY_MAX_COMMANDS 4096u
#define BRIEFCASE_OVERLAY_MAX_TEXT_BYTES (1024u * 1024u)
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
    BRIEFCASE_CAPABILITY_PATCHING = 1ull << 5,
    // Bit 6 is reserved by the managed-only mod-management facade.
    BRIEFCASE_CAPABILITY_GAME_THREAD = 1ull << 7
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
    BRIEFCASE_UNREAL_UNSUPPORTED = 9,
    BRIEFCASE_UNREAL_WRONG_THREAD = 10
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
    BRIEFCASE_PROPERTY_TEXT = 12,
    BRIEFCASE_PROPERTY_INT8 = 13,
    BRIEFCASE_PROPERTY_INT16 = 14,
    BRIEFCASE_PROPERTY_UINT16 = 15,
    BRIEFCASE_PROPERTY_NAME = 16,
    BRIEFCASE_PROPERTY_ARRAY = 17,
    BRIEFCASE_PROPERTY_SET = 18,
    BRIEFCASE_PROPERTY_MAP = 19,
    BRIEFCASE_PROPERTY_INTERFACE = 20,
    BRIEFCASE_PROPERTY_LAZY_OBJECT = 21,
    BRIEFCASE_PROPERTY_SOFT_OBJECT = 22,
    BRIEFCASE_PROPERTY_SOFT_CLASS = 23,
    BRIEFCASE_PROPERTY_DELEGATE = 24,
    BRIEFCASE_PROPERTY_MULTICAST_DELEGATE = 25,
    BRIEFCASE_PROPERTY_FIELD_PATH = 26
};

enum BriefcaseTextArgumentFlags : uint32_t {
    BRIEFCASE_TEXT_INPUT = 1,
    BRIEFCASE_TEXT_OUTPUT = 2
};

enum BriefcasePreparedParameterFlags : uint32_t {
    BRIEFCASE_PREPARED_INPUT = 1,
    BRIEFCASE_PREPARED_OUTPUT = 2,
    BRIEFCASE_PREPARED_RETURN = 4,
    BRIEFCASE_PREPARED_REFERENCE = 8
};

// Describes the generated ProcessEvent layout once. PrepareFunction validates
// every entry against live reflection before returning an opaque runtime token.
struct BriefcasePreparedParameter {
    uint32_t StructSize;
    int32_t Offset;
    int32_t ElementSize;
    uint32_t Kind;
    uint32_t Flags;
    uint32_t Reserved0;
    uint64_t Reserved[1];
};

// Temporary address-free result owned by Briefcase.UnrealRuntime. Managed code
// copies Data immediately and releases it through ReleaseValueBuffer. Data is a
// BVO1 envelope containing BVC1 nodes; it never contains an Unreal address.
struct BriefcaseOwnedValueBuffer {
    uint8_t* Data;
    uint32_t Size;
    uint32_t Reserved0;
    uint64_t Reserved[2];
};

// One BVC1-encoded input for an owning prepared parameter. The descriptor and
// byte buffer only have to remain alive for InvokePreparedValueFunctionV2.
// ParameterOffset binds the value to the already validated prepared layout.
struct BriefcaseValueInput {
    uint32_t StructSize;
    int32_t ParameterOffset;
    const uint8_t* Data;
    uint32_t Size;
    uint32_t Reserved0;
    uint64_t Reserved[2];
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

// ProcessEvent calls are shared by attributed patches and delegate event
// subscriptions. Parameters are valid only for the duration of the callback;
// copy helpers in BriefcasePatchingApi create address-free managed values.
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
// Copies an owning or aggregate property into Briefcase's recursive, pointer-free
// value wire format. The first sizing call may use a null destination.
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseReadValuePropertyFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, uint8_t* destination, uint32_t capacity,
    uint32_t* requiredBytes);
// Writes pointer-free scalar, object-handle, bool, FName or reflected struct
// storage. The runtime validates the reflected layout before touching the object.
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWritePropertyFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, const void* input, uint32_t inputSize);
// FString and FText writes cross the ABI as UTF-16. version.dll constructs and
// destroys the owning Unreal value with FProperty lifecycle functions.
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWriteTextPropertyFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    BriefcasePropertyKind expectedKind, const uint16_t* characters,
    uint32_t characterCount);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcasePrepareFunctionFn)(
    void* context, BriefcaseObjectHandle ownerClass, const char* utf8Name,
    uint32_t nameLength, uint32_t expectedParameterSize,
    const BriefcasePreparedParameter* parameters, uint32_t parameterCount,
    uint64_t* preparedFunction);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseInvokePreparedFunctionFn)(
    void* context, uint64_t preparedFunction, BriefcaseObjectHandle object,
    void* parameters, uint32_t parameterSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseInvokePreparedValueFunctionFn)(
    void* context, uint64_t preparedFunction, BriefcaseObjectHandle object,
    void* parameters, uint32_t parameterSize, BriefcaseOwnedValueBuffer* outputs);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseInvokePreparedValueFunctionV2Fn)(
    void* context, uint64_t preparedFunction, BriefcaseObjectHandle object,
    void* parameters, uint32_t parameterSize,
    const BriefcaseValueInput* inputs, uint32_t inputCount,
    BriefcaseOwnedValueBuffer* outputs);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseReleaseValueBufferFn)(
    void* context, BriefcaseOwnedValueBuffer* buffer);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcasePreparePropertyFn)(
    void* context, BriefcaseObjectHandle ownerClass, const char* utf8Name,
    uint32_t nameLength, int32_t expectedOffset, int32_t expectedElementSize,
    int32_t expectedArrayDimension, BriefcasePropertyKind expectedKind,
    uint64_t* preparedProperty);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseReadPreparedPropertyFn)(
    void* context, uint64_t preparedProperty, BriefcaseObjectHandle object,
    void* output, uint32_t outputSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWritePreparedPropertyFn)(
    void* context, uint64_t preparedProperty, BriefcaseObjectHandle object,
    const void* input, uint32_t inputSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWritePreparedValuePropertyFn)(
    void* context, uint64_t preparedProperty, BriefcaseObjectHandle object,
    const uint8_t* input, uint32_t inputSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseGetClassDefaultObjectFn)(
    void* context, BriefcaseObjectHandle classHandle, BriefcaseObjectHandle* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseGetObjectOuterFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseGetObjectFlagsFn)(
    void* context, BriefcaseObjectHandle object, uint32_t* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseLoadObjectFn)(
    void* context, BriefcaseObjectHandle expectedClass, const char* utf8Path,
    uint32_t pathLength, BriefcaseObjectHandle* result);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseAcquireObjectRootFn)(
    void* context, BriefcaseObjectHandle object);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseReleaseObjectRootFn)(
    void* context, BriefcaseObjectHandle object);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseSubscribeMulticastDelegateFn)(
    void* context, BriefcaseObjectHandle object, BriefcaseObjectHandle ownerClass,
    const char* utf8Name, uint32_t nameLength, int32_t expectedOffset,
    int32_t expectedElementSize, int32_t expectedArrayDimension,
    BriefcasePatchCallbackFn callback, void* userContext, uint64_t* registrationId);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseUnsubscribeDelegateFn)(
    void* context, uint64_t registrationId);
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
    // API v8 consumes all three v7 reserved slots without moving older fields.
    BriefcaseReadValuePropertyFn ReadValueProperty;
    BriefcaseWritePropertyFn WriteProperty;
    BriefcaseWriteTextPropertyFn WriteTextProperty;
    // API v10 resolves and validates generated SDK descriptors once, including
    // recursive canonical conversion for fixed structs containing UObject handles.
    // Tokens are process-local identifiers; Unreal addresses never cross this boundary.
    BriefcasePrepareFunctionFn PrepareFunction;
    BriefcaseInvokePreparedFunctionFn InvokePreparedFunction;
    BriefcasePreparePropertyFn PrepareProperty;
    BriefcaseReadPreparedPropertyFn ReadPreparedProperty;
    BriefcaseWritePreparedPropertyFn WritePreparedProperty;
    // API v11 returns recursively encoded owning out/return parameters while
    // their FProperty-managed native storage is still alive.
    BriefcaseInvokePreparedValueFunctionFn InvokePreparedValueFunction;
    BriefcaseReleaseValueBufferFn ReleaseValueBuffer;
    // API v13 reconstructs owning inputs and allocator-backed containers in
    // FProperty-initialized storage. The same bounded BVC1 format powers
    // generated property setters and writable patch values.
    BriefcaseInvokePreparedValueFunctionV2Fn InvokePreparedValueFunctionV2;
    BriefcaseWritePreparedValuePropertyFn WritePreparedValueProperty;
    // API v14 exposes identity/lifecycle metadata as serial-checked handles.
    // It never publishes UObject or UClass addresses to mods.
    BriefcaseGetClassDefaultObjectFn GetClassDefaultObject;
    BriefcaseGetObjectOuterFn GetObjectOuter;
    BriefcaseGetObjectFlagsFn GetObjectFlags;
    // API v15 loads a typed soft-object path through reflected vanilla
    // KismetSystemLibrary calls. Root leases keep the object alive without
    // exposing an address; every acquire must have one matching release.
    BriefcaseLoadObjectFn LoadObject;
    BriefcaseAcquireObjectRootFn AcquireObjectRoot;
    BriefcaseReleaseObjectRootFn ReleaseObjectRoot;
    // API v16 subscribes to reflected inline dynamic multicast delegates.
    // The runtime validates the generated property layout, owns the physical
    // binding, and removes it when the mod-scoped subscription is disposed.
    BriefcaseSubscribeMulticastDelegateFn SubscribeMulticastDelegate;
    BriefcaseUnsubscribeDelegateFn UnsubscribeDelegate;
};

// This host-table slot is reserved for binary layout compatibility.

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
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseCopyPatchValueFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, uint8_t* destination, uint32_t capacity,
    uint32_t* requiredBytes);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWritePatchValueFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, const void* input, uint32_t inputSize);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWritePatchTextFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, const uint16_t* characters,
    uint32_t characterCount);
typedef BriefcaseUnrealResult(BRIEFCASE_MOD_CALL* BriefcaseWritePatchEncodedValueFn)(
    void* context, const BriefcasePatchCall* call, uint32_t parameterOffset,
    BriefcasePropertyKind expectedKind, const uint8_t* input, uint32_t inputSize);

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
    // API v5 consumes all three v4 reserved slots.
    BriefcaseCopyPatchValueFn CopyValue;
    BriefcaseWritePatchValueFn WriteValue;
    BriefcaseWritePatchTextFn WriteText;
    // API v6 reconstructs an owning patch value from bounded BVC1 bytes. The
    // destination keeps Unreal ownership; no managed pointer is retained.
    BriefcaseWritePatchEncodedValueFn WriteEncodedValue;
};

// Future services are separate versioned tables. Adding a field to reflection,
// rendering or input will not move the existing Core pointer.
// The proxy is imported while Windows is starting the executable. It records
// that initial thread and only dispatches this callback when ProcessEvent later
// runs on the same thread. Managed work consequently enters Unreal from its
// game thread without exposing a native address to a mod.
struct BriefcaseGameThreadFrame {
    uint32_t StructSize;
    uint32_t ThreadId;
    uint64_t Sequence;
    float DeltaSeconds;
    uint32_t Reserved0;
    BriefcaseObjectHandle CurrentWorld;
    uint64_t Reserved[4];
};

typedef void(BRIEFCASE_MOD_CALL* BriefcaseGameThreadCallbackFn)(
    void* userContext, const BriefcaseGameThreadFrame* frame);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseRegisterGameThreadCallbackFn)(
    void* context, BriefcaseGameThreadCallbackFn callback, void* userContext,
    uint64_t* registrationId);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseUnregisterGameThreadCallbackFn)(
    void* context, uint64_t registrationId);
typedef void(BRIEFCASE_MOD_CALL* BriefcaseRequestGameThreadPumpFn)(void* context);
typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseIsGameThreadFn)(void* context);

struct BriefcaseGameThreadApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseRegisterGameThreadCallbackFn RegisterCallback;
    BriefcaseUnregisterGameThreadCallbackFn UnregisterCallback;
    BriefcaseRequestGameThreadPumpFn RequestPump;
    BriefcaseIsGameThreadFn IsGameThread;
    void* Reserved[8];
};


enum BriefcaseRenderingResult : uint32_t {
    BRIEFCASE_RENDERING_OK = 0,
    BRIEFCASE_RENDERING_INVALID_ARGUMENT = 1,
    BRIEFCASE_RENDERING_NOT_READY = 2,
    BRIEFCASE_RENDERING_UNSUPPORTED = 3
};

enum BriefcaseRenderingStatus : uint32_t {
    BRIEFCASE_RENDERING_UNAVAILABLE = 0,
    BRIEFCASE_RENDERING_STARTING = 1,
    BRIEFCASE_RENDERING_READY = 2,
    BRIEFCASE_RENDERING_FAILED = 3
};

enum BriefcaseOverlayCommandKind : uint32_t {
    BRIEFCASE_OVERLAY_CIRCLE = 0,
    BRIEFCASE_OVERLAY_LINE = 1,
    BRIEFCASE_OVERLAY_FILLED_RECTANGLE = 2,
    BRIEFCASE_OVERLAY_TEXT = 3
};

enum BriefcaseOverlayCommandFlags : uint32_t {
    BRIEFCASE_OVERLAY_FILLED = 1u << 0
};

// A pointer-free description of one primitive. TextOffset/TextLength refer to
// the UTF-8 blob in the containing frame and are copied during SubmitFrame.
struct BriefcaseOverlayCommand {
    uint32_t StructSize;
    uint32_t Kind;
    float X1;
    float Y1;
    float X2;
    float Y2;
    float Radius;
    float Thickness;
    float Rounding;
    uint32_t Color;
    uint32_t TextOffset;
    uint32_t TextLength;
    uint32_t Flags;
    int32_t Segments;
    uint32_t Reserved0;
    uint64_t Reserved[2];
};

// Managed code owns these pointers only for the duration of SubmitFrame. The
// native renderer validates and copies the complete snapshot before returning.
struct BriefcaseOverlayFrame {
    uint32_t StructSize;
    uint32_t Version;
    uint32_t Width;
    uint32_t Height;
    uint32_t CommandCount;
    uint32_t TextBytes;
    const BriefcaseOverlayCommand* Commands;
    const char* Utf8Text;
    uint64_t FrameNumber;
    uint64_t Reserved[3];
};

typedef BriefcaseBool(BRIEFCASE_MOD_CALL* BriefcaseStartRenderingFn)(void* context);
typedef BriefcaseRenderingResult(BRIEFCASE_MOD_CALL* BriefcaseSubmitOverlayFrameFn)(
    void* context, const BriefcaseOverlayFrame* frame);
typedef BriefcaseRenderingStatus(BRIEFCASE_MOD_CALL* BriefcaseGetRenderingStatusFn)(
    void* context, uint32_t* nativeError);

struct BriefcaseRenderingApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    void* Context;
    BriefcaseStartRenderingFn Start;
    BriefcaseSubmitOverlayFrameFn SubmitFrame;
    BriefcaseGetRenderingStatusFn GetStatus;
    void* Reserved[8];
};

struct BriefcaseHostApi {
    uint32_t StructSize;
    uint32_t ApiVersion;
    uint64_t Capabilities;
    const BriefcaseCoreApi* Core;
    const BriefcaseUnrealApi* Unreal;
    const BriefcaseRenderingApi* Rendering;
    const BriefcaseInputApi* Input;
    const BriefcasePatchingApi* Patching;
    // GameThread consumes the first v1 reserved slot.
    const BriefcaseGameThreadApi* GameThread;
    void* Reserved[6];
};

#if defined(__cplusplus)
static_assert(sizeof(BriefcaseGameThreadFrame) == 64);
static_assert(sizeof(BriefcasePreparedParameter) == 32);
static_assert(sizeof(BriefcaseOwnedValueBuffer) == 32);
static_assert(sizeof(BriefcaseValueInput) == 40);
static_assert(sizeof(BriefcaseUnrealApi) == 280);
static_assert(sizeof(BriefcasePatchingApi) == 104);
static_assert(sizeof(BriefcaseGameThreadApi) == 112);
static_assert(sizeof(BriefcaseOverlayCommand) == 80);
static_assert(sizeof(BriefcaseOverlayFrame) == 72);
static_assert(sizeof(BriefcaseRenderingApi) == 104);
static_assert(sizeof(BriefcaseHostApi) == 112);
#endif

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
