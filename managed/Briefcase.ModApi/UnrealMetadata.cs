namespace Briefcase.ModApi;

/// <summary>
/// Semantic category of a reflected Unreal property. The original FProperty
/// class name is retained by <see cref="UnrealTypeMetadata.UnrealType"/> so a
/// newer engine property can remain observable even before Briefcase assigns a
/// dedicated category to it.
/// </summary>
public enum UnrealTypeKind
{
    Unknown,
    Boolean,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    Enum,
    Name,
    String,
    Text,
    Object,
    Class,
    Interface,
    WeakObject,
    LazyObject,
    SoftObject,
    SoftClass,
    Struct,
    Array,
    Set,
    Map,
    Delegate,
    MulticastDelegate,
    FieldPath
}

public enum UnrealReflectedTypeKind
{
    Class,
    Struct,
    Enum
}

/// <summary>
/// Describes how one FBoolProperty selects its value from native storage.
/// Blueprint booleans may share a byte, so the masks are part of the type
/// contract rather than an implementation detail.
/// </summary>
public sealed record UnrealBooleanLayout(
    byte FieldSize,
    byte ByteOffset,
    byte ByteMask,
    byte FieldMask);

/// <summary>
/// Address-free recursive description of one Unreal property type.
/// Container nodes point to their element/key/value nodes and references keep
/// the reflected /Script path of their target type when Unreal provides it.
/// </summary>
public sealed record UnrealTypeMetadata(
    UnrealTypeKind Kind,
    string UnrealType,
    int ElementSize,
    string? ReferencedTypePath = null,
    UnrealTypeMetadata? InnerType = null,
    UnrealTypeMetadata? KeyType = null,
    UnrealTypeMetadata? ValueType = null,
    UnrealTypeMetadata? UnderlyingType = null,
    UnrealBooleanLayout? BooleanLayout = null)
{
    public override string ToString() => Kind switch
    {
        UnrealTypeKind.Array => $"TArray<{InnerType?.ToString() ?? "?"}>",
        UnrealTypeKind.Set => $"TSet<{InnerType?.ToString() ?? "?"}>",
        UnrealTypeKind.Map => $"TMap<{KeyType?.ToString() ?? "?"}, {ValueType?.ToString() ?? "?"}>",
        UnrealTypeKind.Enum => ReferencedName() ?? $"enum<{UnderlyingType?.ToString() ?? "?"}>",
        UnrealTypeKind.Struct or UnrealTypeKind.Object or UnrealTypeKind.Class or
        UnrealTypeKind.Interface or UnrealTypeKind.WeakObject or UnrealTypeKind.LazyObject or
        UnrealTypeKind.SoftObject or UnrealTypeKind.SoftClass or UnrealTypeKind.Delegate or
        UnrealTypeKind.MulticastDelegate => ReferencedName() ?? UnrealType,
        _ => PrimitiveName()
    };

    private string PrimitiveName() => Kind switch
    {
        UnrealTypeKind.Boolean => "bool",
        UnrealTypeKind.Int8 => "int8",
        UnrealTypeKind.UInt8 => "uint8",
        UnrealTypeKind.Int16 => "int16",
        UnrealTypeKind.UInt16 => "uint16",
        UnrealTypeKind.Int32 => "int32",
        UnrealTypeKind.UInt32 => "uint32",
        UnrealTypeKind.Int64 => "int64",
        UnrealTypeKind.UInt64 => "uint64",
        UnrealTypeKind.Float => "float",
        UnrealTypeKind.Double => "double",
        UnrealTypeKind.Name => "FName",
        UnrealTypeKind.String => "FString",
        UnrealTypeKind.Text => "FText",
        _ => UnrealType
    };

    private string? ReferencedName()
    {
        if (string.IsNullOrWhiteSpace(ReferencedTypePath)) return null;
        var separator = ReferencedTypePath.LastIndexOf('.');
        return separator >= 0 && separator + 1 < ReferencedTypePath.Length
            ? ReferencedTypePath[(separator + 1)..]
            : ReferencedTypePath;
    }
}

public sealed record UnrealReflectedProperty(
    string Name,
    int Offset,
    int ElementSize,
    int ArrayDimension,
    ulong Flags,
    UnrealTypeMetadata Type);

public sealed record UnrealReflectedParameter(
    string Name,
    int Offset,
    int ElementSize,
    int ArrayDimension,
    ulong Flags,
    UnrealTypeMetadata Type)
{
    private const ulong OutParameterFlag = 0x100;
    private const ulong ReturnParameterFlag = 0x400;
    private const ulong ReferenceParameterFlag = 0x08000000;
    private const ulong ConstParameterFlag = 0x2;

    public bool IsReturn => (Flags & ReturnParameterFlag) != 0;
    public bool IsOutput => (Flags & (OutParameterFlag | ReturnParameterFlag)) != 0;
    public bool IsReference => (Flags & ReferenceParameterFlag) != 0;
    public bool IsConst => (Flags & ConstParameterFlag) != 0;
    public bool IsInput => !IsOutput || IsReference;
}

public sealed record UnrealReflectedFunction(
    string Name,
    uint Flags,
    int ParameterBufferSize,
    int ParameterCount,
    IReadOnlyList<UnrealReflectedParameter> Parameters);

public sealed record UnrealEnumValue(string Name, long Value);

/// <summary>
/// Build-specific reflection description attached to every generated class and
/// structure. Member descriptors live in their generated Metadata nested type
/// so large Unreal classes do not require one oversized static initializer.
/// </summary>
public sealed record UnrealReflectedType(
    string Path,
    string Name,
    UnrealReflectedTypeKind Kind,
    string? SuperPath,
    int NativeSize);

/// <summary>Associates a generated CLR enum with its Unreal /Script path.</summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class UnrealTypePathAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}
