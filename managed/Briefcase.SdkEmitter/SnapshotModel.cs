using System.Text.Json.Serialization;

namespace Briefcase.SdkEmitter;

internal sealed class SdkSnapshot
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; }
    [JsonPropertyName("target")] public string Target { get; init; } = "";
    [JsonPropertyName("sdkAssemblyName")] public string SdkAssemblyName { get; init; } = "";
    [JsonPropertyName("gameBuild")] public GameBuildSnapshot GameBuild { get; init; } = new();
    [JsonPropertyName("types")] public List<TypeSnapshot> Types { get; init; } = [];
}

internal sealed class GameBuildSnapshot
{
    [JsonPropertyName("peTimestamp")] public uint PeTimestamp { get; init; }
    [JsonPropertyName("imageSize")] public uint ImageSize { get; init; }
}

internal sealed class TypeSnapshot
{
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("superPath")] public string? SuperPath { get; init; }
    [JsonPropertyName("size")] public int Size { get; init; }
    [JsonPropertyName("properties")] public List<PropertySnapshot> Properties { get; init; } = [];
    [JsonPropertyName("functions")] public List<FunctionSnapshot> Functions { get; init; } = [];
}

internal sealed class PropertySnapshot
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("unrealType")] public string UnrealType { get; init; } = "";
    [JsonPropertyName("offset")] public int Offset { get; init; }
    [JsonPropertyName("elementSize")] public int ElementSize { get; init; }
    [JsonPropertyName("arrayDimension")] public int ArrayDimension { get; init; }
    [JsonPropertyName("flags")] public ulong Flags { get; init; }
    [JsonPropertyName("referencedTypePath")] public string? ReferencedTypePath { get; init; }
    [JsonPropertyName("innerUnrealType")] public string? InnerUnrealType { get; init; }
}

internal sealed class FunctionSnapshot
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("flags")] public uint Flags { get; init; }
    [JsonPropertyName("parameterSize")] public int ParameterSize { get; init; }
    [JsonPropertyName("parameterCount")] public int ParameterCount { get; init; }
    [JsonPropertyName("parameters")] public List<PropertySnapshot> Parameters { get; init; } = [];
}

[JsonSerializable(typeof(SdkSnapshot))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
internal sealed partial class SnapshotJsonContext : JsonSerializerContext;
