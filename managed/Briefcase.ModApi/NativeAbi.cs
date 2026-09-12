using System.Runtime.InteropServices;

namespace Briefcase.ModApi.Interop;

public static class BriefcaseAbi
{
    public const uint HostApiVersion = 1;
    public const uint InputApiVersion = 1;
    public const uint PatchingApiVersion = 7;
    public const uint GameThreadApiVersion = 1;
    public const uint UnrealApiVersion = 16;
    public const ulong CoreCapability = 1UL << 0;
    public const ulong UnrealReflectionCapability = 1UL << 1;
    public const ulong UnrealInvocationCapability = 1UL << 2;
    public const ulong RenderingCapability = 1UL << 3;
    public const ulong InputCapability = 1UL << 4;
    public const ulong PatchingCapability = 1UL << 5;
    public const ulong ModManagementCapability = 1UL << 6;
    public const ulong GameThreadCapability = 1UL << 7;
}

public enum NativeUnrealResult : uint
{
    Ok,
    InvalidArgument,
    NotReady,
    NotFound,
    StaleHandle,
    BufferTooSmall,
    TypeMismatch,
    LayoutMismatch,
    Unreadable,
    Unsupported,
    WrongThread
}

public enum UnrealPropertyKind : uint
{
    Unknown,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    Bool,
    Object,
    Struct,
    String,
    Byte,
    Text,
    Int8,
    Int16,
    UInt16,
    Name,
    Array,
    Set,
    Map,
    Interface,
    LazyObject,
    SoftObject,
    SoftClass,
    Delegate,
    MulticastDelegate,
    FieldPath
}

[Flags]
public enum NativeTextArgumentFlags : uint
{
    Input = 1,
    Output = 2
}

[Flags]
public enum NativePreparedParameterFlags : uint
{
    Input = 1,
    Output = 2,
    Return = 4,
    Reference = 8
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativePreparedParameter
{
    public uint StructSize;
    public int Offset;
    public int ElementSize;
    public UnrealPropertyKind Kind;
    public NativePreparedParameterFlags Flags;
    public uint Reserved0;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeOwnedValueBuffer
{
    public byte* Data;
    public uint Size;
    public uint Reserved0;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeValueInput
{
    public uint StructSize;
    public int ParameterOffset;
    public byte* Data;
    public uint Size;
    public uint Reserved0;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeTextArgument
{
    public uint StructSize;
    public int ParameterOffset;
    public NativeTextArgumentFlags Flags;
    public uint InputCharacters;
    public char* Input;
    public char* Output;
    public uint* RequiredCharacters;
    public uint OutputCapacityCharacters;
    public uint Reserved0;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct UnrealObjectHandle(uint Index, uint SerialNumber)
{
    // Index locates FUObjectItem; SerialNumber prevents an object destroyed by
    // Unreal GC from becoming a valid handle to a different object in that slot.
    public bool IsNull => Index == uint.MaxValue;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativePropertyInfo
{
    public uint StructSize;
    public UnrealPropertyKind Kind;
    public int Offset;
    public int ElementSize;
    public int ArrayDimension;
    public uint Reserved0;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeVersion
{
    public ushort Major;
    public ushort Minor;
    public ushort Patch;
    public ushort Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeGameBuild
{
    public uint PeTimestamp;
    public uint ImageSize;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeCoreApi
{
    public uint StructSize;
    public uint ApiVersion;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, void> Log;
    public delegate* unmanaged[Cdecl]<void*, NativeVersion> GetFrameworkVersion;
    public delegate* unmanaged[Cdecl]<void*, NativeGameBuild> GetGameBuild;
    public fixed ulong Reserved[8];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeHostApi
{
    public uint StructSize;
    public uint ApiVersion;
    public ulong Capabilities;
    public NativeCoreApi* Core;
    public NativeUnrealApi* Unreal;
    public void* ReservedRendering;
    public NativeInputApi* Input;
    public NativePatchingApi* Patching;
    public NativeGameThreadApi* GameThread;
    public fixed ulong Reserved[6];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeInputApi
{
    public uint StructSize;
    public uint ApiVersion;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, uint, uint> IsKeyDown;
    public delegate* unmanaged[Cdecl]<void*, uint> GameHasFocus;
    public fixed ulong Reserved[8];
}

public enum NativePatchPhase : uint
{
    Prefix,
    Postfix
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativePatchCall
{
    public uint StructSize;
    public NativePatchPhase Phase;
    public UnrealObjectHandle Instance;
    public uint OriginalRan;
    public void* Parameters;
    public uint ParameterSize;
    public uint Reserved0;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativePatchingApi
{
    public uint StructSize;
    public uint ApiVersion;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, NativePatchPhase, delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint*, void>, void*, ulong*, NativeUnrealResult> RegisterPatch;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint> UnregisterPatch;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, NativePatchPhase, delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint*, void>, void*, ulong*, NativeUnrealResult> RegisterNativePatch;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint> UnregisterNativePatch;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, byte*, uint, uint*, NativeUnrealResult> CopyByteArray;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, char*, uint, uint*, NativeUnrealResult> CopyString;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, char*, uint, uint*, NativeUnrealResult> CopyText;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, UnrealPropertyKind, byte*, uint, uint*, NativeUnrealResult> CopyValue;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, UnrealPropertyKind, void*, uint, NativeUnrealResult> WriteValue;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, UnrealPropertyKind, char*, uint, NativeUnrealResult> WriteText;
    public delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint, UnrealPropertyKind, byte*, uint, NativeUnrealResult> WriteEncodedValue;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeGameThreadFrame
{
    public uint StructSize;
    public uint ThreadId;
    public ulong Sequence;
    public float DeltaSeconds;
    public uint Reserved0;
    public UnrealObjectHandle CurrentWorld;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeGameThreadApi
{
    public uint StructSize;
    public uint ApiVersion;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, NativeGameThreadFrame*, void>, void*, ulong*, uint> RegisterCallback;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint> UnregisterCallback;
    public delegate* unmanaged[Cdecl]<void*, void> RequestPump;
    public delegate* unmanaged[Cdecl]<void*, uint> IsGameThread;
    public fixed ulong Reserved[8];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeUnrealApi
{
    public uint StructSize;
    public uint ApiVersion;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, UnrealObjectHandle*, NativeUnrealResult> FindObject;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle*, uint, uint*, uint*, NativeUnrealResult> FindObjectsOfClass;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, byte*, uint, uint*, NativeUnrealResult> GetObjectName;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, byte*, uint, uint*, NativeUnrealResult> GetObjectPath;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle*, NativeUnrealResult> GetObjectClass;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, uint*, NativeUnrealResult> IsObjectA;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, byte*, uint, NativePropertyInfo*, NativeUnrealResult> GetPropertyInfo;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, UnrealPropertyKind, void*, uint, NativeUnrealResult> ReadProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, uint, void*, uint, NativeUnrealResult> InvokeFunction;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, char*, uint, uint*, NativeUnrealResult> ReadStringProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, uint, void*, uint, NativeTextArgument*, uint, NativeUnrealResult> InvokeFunctionText;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, char*, uint, uint*, NativeUnrealResult> ReadTextProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, NativeGameBuild, ulong, uint*, NativeUnrealResult> InvokeNativeBoolean;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, UnrealPropertyKind, byte*, uint, uint*, NativeUnrealResult> ReadValueProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, UnrealPropertyKind, void*, uint, NativeUnrealResult> WriteProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, UnrealPropertyKind, char*, uint, NativeUnrealResult> WriteTextProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, byte*, uint, uint, NativePreparedParameter*, uint, ulong*, NativeUnrealResult> PrepareFunction;
    public delegate* unmanaged[Cdecl]<void*, ulong, UnrealObjectHandle, void*, uint, NativeUnrealResult> InvokePreparedFunction;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, byte*, uint, int, int, int, UnrealPropertyKind, ulong*, NativeUnrealResult> PrepareProperty;
    public delegate* unmanaged[Cdecl]<void*, ulong, UnrealObjectHandle, void*, uint, NativeUnrealResult> ReadPreparedProperty;
    public delegate* unmanaged[Cdecl]<void*, ulong, UnrealObjectHandle, void*, uint, NativeUnrealResult> WritePreparedProperty;
    public delegate* unmanaged[Cdecl]<void*, ulong, UnrealObjectHandle, void*, uint, NativeOwnedValueBuffer*, NativeUnrealResult> InvokePreparedValueFunction;
    public delegate* unmanaged[Cdecl]<void*, NativeOwnedValueBuffer*, void> ReleaseValueBuffer;
    public delegate* unmanaged[Cdecl]<void*, ulong, UnrealObjectHandle, void*, uint, NativeValueInput*, uint, NativeOwnedValueBuffer*, NativeUnrealResult> InvokePreparedValueFunctionV2;
    public delegate* unmanaged[Cdecl]<void*, ulong, UnrealObjectHandle, byte*, uint, NativeUnrealResult> WritePreparedValueProperty;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle*, NativeUnrealResult> GetClassDefaultObject;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle*, NativeUnrealResult> GetObjectOuter;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, uint*, NativeUnrealResult> GetObjectFlags;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, byte*, uint, UnrealObjectHandle*, NativeUnrealResult> LoadObject;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, NativeUnrealResult> AcquireObjectRoot;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, NativeUnrealResult> ReleaseObjectRoot;
    public delegate* unmanaged[Cdecl]<void*, UnrealObjectHandle, UnrealObjectHandle, byte*, uint, int, int, int, delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint*, void>, void*, ulong*, NativeUnrealResult> SubscribeMulticastDelegate;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint> UnsubscribeDelegate;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeModInfo
{
    public uint StructSize;
    public uint MinimumHostApiVersion;
    public uint MaximumHostApiVersion;
    public uint Reserved0;
    public ulong RequiredCapabilities;
    public fixed byte Id[64];
    public fixed byte Name[96];
    public fixed byte Author[64];
    public fixed byte Version[32];
    public fixed byte Description[192];
    public fixed ulong Reserved[8];
}
