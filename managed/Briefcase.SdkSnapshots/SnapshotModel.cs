using System.Text.Json.Serialization;

namespace Briefcase.SdkSnapshots;

public sealed class SdkSnapshot
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("target")] public string Target { get; init; } = "";
    [JsonPropertyName("sdkAssemblyName")] public string SdkAssemblyName { get; init; } = "";
    [JsonPropertyName("gameBuild")] public GameBuildSnapshot GameBuild { get; init; } = new();
    [JsonPropertyName("capturedObjectCount")] public int CapturedObjectCount { get; init; }
    [JsonPropertyName("types")] public List<TypeSnapshot> Types { get; init; } = [];
}

public sealed class GameBuildSnapshot
{
    [JsonPropertyName("peTimestamp")] public uint PeTimestamp { get; init; }
    [JsonPropertyName("imageSize")] public uint ImageSize { get; init; }
}

public sealed class TypeSnapshot
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("superPath")] public string? SuperPath { get; init; }
    [JsonPropertyName("size")] public int Size { get; init; }
    [JsonPropertyName("properties")] public List<PropertySnapshot> Properties { get; init; } = [];
    [JsonPropertyName("functions")] public List<FunctionSnapshot> Functions { get; init; } = [];
    [JsonPropertyName("values")] public List<EnumValueSnapshot> Values { get; init; } = [];
}

public sealed class PropertySnapshot
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("unrealType")] public string UnrealType { get; init; } = "";
    [JsonPropertyName("offset")] public int Offset { get; init; }
    [JsonPropertyName("elementSize")] public int ElementSize { get; init; }
    [JsonPropertyName("arrayDimension")] public int ArrayDimension { get; init; }
    [JsonPropertyName("flags")] public ulong Flags { get; init; }

    // Schema 2 compatibility. Schema 3 writes the complete recursive Type node.
    [JsonPropertyName("referencedTypePath")] public string? ReferencedTypePath { get; init; }
    [JsonPropertyName("innerUnrealType")] public string? InnerUnrealType { get; init; }
    [JsonPropertyName("type")] public UnrealTypeSnapshot? Type { get; init; }

    [JsonIgnore]
    public UnrealTypeSnapshot EffectiveType => Type ?? UnrealTypeSnapshot.FromLegacy(this);
}

public sealed class UnrealTypeSnapshot
{
    [JsonPropertyName("unrealType")] public string UnrealType { get; init; } = "UnknownProperty";
    [JsonPropertyName("elementSize")] public int ElementSize { get; init; }
    [JsonPropertyName("referencedTypePath")] public string? ReferencedTypePath { get; init; }
    [JsonPropertyName("innerType")] public UnrealTypeSnapshot? InnerType { get; init; }
    [JsonPropertyName("keyType")] public UnrealTypeSnapshot? KeyType { get; init; }
    [JsonPropertyName("valueType")] public UnrealTypeSnapshot? ValueType { get; init; }
    [JsonPropertyName("underlyingType")] public UnrealTypeSnapshot? UnderlyingType { get; init; }
    [JsonPropertyName("booleanLayout")] public BooleanLayoutSnapshot? BooleanLayout { get; init; }

    /// <summary>
    /// Complete UFunction signature for a reflected delegate property (schema 4+).
    /// Older snapshots leave this null and remain readable.
    /// </summary>
    [JsonPropertyName("delegateSignature")] public FunctionSnapshot? DelegateSignature { get; init; }

    public static UnrealTypeSnapshot FromLegacy(PropertySnapshot property) => new()
    {
        UnrealType = string.IsNullOrEmpty(property.UnrealType)
            ? "UnknownProperty"
            : property.UnrealType,
        ElementSize = property.ElementSize,
        ReferencedTypePath = property.ReferencedTypePath,
        InnerType = property.InnerUnrealType is { Length: > 0 } inner
            ? new UnrealTypeSnapshot { UnrealType = inner }
            : null
    };
}

public sealed class BooleanLayoutSnapshot
{
    [JsonPropertyName("fieldSize")] public byte FieldSize { get; init; }
    [JsonPropertyName("byteOffset")] public byte ByteOffset { get; init; }
    [JsonPropertyName("byteMask")] public byte ByteMask { get; init; }
    [JsonPropertyName("fieldMask")] public byte FieldMask { get; init; }
}

public sealed class EnumValueSnapshot
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("value")] public long Value { get; init; }
}

public sealed class FunctionSnapshot
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("flags")] public uint Flags { get; init; }
    [JsonPropertyName("parameterSize")] public int ParameterSize { get; init; }
    [JsonPropertyName("parameterCount")] public int ParameterCount { get; init; }
    [JsonPropertyName("parameters")] public List<PropertySnapshot> Parameters { get; init; } = [];
}

[JsonSerializable(typeof(SdkSnapshot))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
public sealed partial class SnapshotJsonContext : JsonSerializerContext;
