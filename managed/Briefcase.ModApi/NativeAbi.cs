using System.Runtime.InteropServices;

namespace Briefcase.ModApi.Interop;

public static class BriefcaseAbi
{
    public const uint HostApiVersion = 1;
    public const uint RenderingApiVersion = 4;
    public const uint InputApiVersion = 1;
    public const uint PatchingApiVersion = 4;
    public const ulong CoreCapability = 1UL << 0;
    public const ulong UnrealReflectionCapability = 1UL << 1;
    public const ulong UnrealInvocationCapability = 1UL << 2;
    public const ulong RenderingCapability = 1UL << 3;
    public const ulong InputCapability = 1UL << 4;
    public const ulong PatchingCapability = 1UL << 5;
    public const ulong ModManagementCapability = 1UL << 6;
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
    Unsupported
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
    Text
}

[Flags]
public enum NativeTextArgumentFlags : uint
{
    Input = 1,
    Output = 2
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
    public NativeRenderingApi* Rendering;
    public NativeInputApi* Input;
    public NativePatchingApi* Patching;
    public fixed ulong Reserved[7];
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
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeRenderFrame
{
    public uint StructSize;
    public uint Width;
    public uint Height;
    public uint MenuVisible;
    public float DeltaSeconds;
    public uint Reserved0;
    public ulong FrameNumber;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeRenderingApi
{
    public uint StructSize;
    public uint ApiVersion;
    public void* Context;
    public delegate* unmanaged[Cdecl]<void*, delegate* unmanaged[Cdecl]<void*, NativeRenderFrame*, void>, void*, ulong*, uint> RegisterCallback;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint> UnregisterCallback;
    public delegate* unmanaged[Cdecl]<void*, ulong, uint, uint> SetCallbackActive;
    public delegate* unmanaged[Cdecl]<void*, uint, void> SetMenuVisible;
    public delegate* unmanaged[Cdecl]<void*, uint> GetMenuVisible;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, uint*, uint, uint> Begin;
    public delegate* unmanaged[Cdecl]<void*, void> End;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, void> Text;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, uint*, uint> Checkbox;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, float*, float, float, byte*, uint, uint> SliderFloat;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, float, float, uint> Button;
    public delegate* unmanaged[Cdecl]<void*, void> SameLine;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, void> SeparatorText;
    public delegate* unmanaged[Cdecl]<void*, float, float, uint, float, float, void> SetNextWindowPosition;
    public delegate* unmanaged[Cdecl]<void*, float, float, uint, void> SetNextWindowSize;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, float, float, uint, uint, uint> BeginChild;
    public delegate* unmanaged[Cdecl]<void*, void> EndChild;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, uint, uint, float, float, uint> Selectable;
    public delegate* unmanaged[Cdecl]<void*, void> Separator;
    public delegate* unmanaged[Cdecl]<void*, void> Spacing;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, int*, int, int, byte*, uint, uint> SliderInt;
    public delegate* unmanaged[Cdecl]<void*, float, void> SetNextItemWidth;
    public delegate* unmanaged[Cdecl]<void*, uint, byte*, uint, void> TextColored;
    public delegate* unmanaged[Cdecl]<void*, float> GetFramerate;
    public delegate* unmanaged[Cdecl]<void*, float, float, float, uint, int, float, uint, void> DrawCircle;
    public delegate* unmanaged[Cdecl]<void*, float, float, float, float, uint, float, void> DrawLine;
    public delegate* unmanaged[Cdecl]<void*, float, float, float, float, uint, float, void> DrawRectFilled;
    public delegate* unmanaged[Cdecl]<void*, float, float, uint, byte*, uint, void> DrawText;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, byte*, uint, uint, uint> BeginCombo;
    public delegate* unmanaged[Cdecl]<void*, void> EndCombo;
    public delegate* unmanaged[Cdecl]<void*, byte*, uint, byte*, uint, uint, uint> InputText;
    public fixed ulong Reserved[5];
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
    public fixed ulong Reserved[3];
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
