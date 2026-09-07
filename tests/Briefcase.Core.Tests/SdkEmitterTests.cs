using System.Reflection;
using Briefcase.ModApi;
using Briefcase.SdkEmitter;

namespace Briefcase.Core.Tests;

public sealed class SdkEmitterTests
{
    [Fact]
    public void SnapshotReader_round_trips_the_compact_bserializer_contract()
    {
        var original = CreateSnapshot(schemaVersion: 3);
        original.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.Settings",
            Name = "Settings",
            Kind = "Class",
            Size = 64,
            Properties =
            [
                new PropertySnapshot
                {
                    Name = "Weights",
                    UnrealType = "ArrayProperty",
                    Offset = 40,
                    ElementSize = 16,
                    ArrayDimension = 1,
                    Type = new UnrealTypeSnapshot
                    {
                        UnrealType = "ArrayProperty",
                        ElementSize = 16,
                        InnerType = new UnrealTypeSnapshot
                        {
                            UnrealType = "FloatProperty",
                            ElementSize = 4
                        }
                    }
                }
            ]
        });

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "snapshot.bserializer");
        using (var stream = File.Create(path))
            BriefcaseSnapshotSerializer.WriteBinary(original, stream);

        var snapshot = SnapshotReader.Read(path);

        Assert.Equal("Client", snapshot.Target);
        var property = Assert.Single(Assert.Single(snapshot.Types).Properties);
        Assert.Equal("FloatProperty", property.Type?.InnerType?.UnrealType);
        Assert.Equal("BRSK"u8.ToArray(), File.ReadAllBytes(path)[..4]);
    }

    [Fact]
    public void Binary_snapshot_reader_rejects_unknown_versions_and_trailing_data()
    {
        var snapshot = CreateSnapshot(schemaVersion: 3);
        using var stream = new MemoryStream();
        BriefcaseSnapshotSerializer.WriteBinary(snapshot, stream);
        var bytes = stream.ToArray();
        bytes[4] = 2;
        Assert.Throws<InvalidDataException>(() =>
            BriefcaseSnapshotSerializer.ReadBinary(new MemoryStream(bytes)));

        stream.SetLength(0);
        stream.Position = 0;
        BriefcaseSnapshotSerializer.WriteBinary(snapshot, stream);
        stream.WriteByte(0xFF);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() =>
            BriefcaseSnapshotSerializer.ReadBinary(stream));
    }

    [Fact]
    public void Binary_conversion_normalizes_legacy_null_collections()
    {
        using var source = TemporaryFile.Json("""
            {
              "schemaVersion": 2,
              "target": "Server",
              "sdkAssemblyName": "Briefcase.DeceiveInc.Server.Sdk",
              "gameBuild": { "peTimestamp": 1, "imageSize": 2 },
              "types": [{
                "path": "/Script/DeceiveInc.Legacy",
                "name": "Legacy",
                "kind": "Class",
                "properties": null,
                "functions": null,
                "values": null
              }]
            }
            """);
        var legacy = SnapshotReader.Read(source.Path);
        using var binary = new MemoryStream();

        BriefcaseSnapshotSerializer.WriteBinary(legacy, binary);
        binary.Position = 0;
        var normalized = BriefcaseSnapshotSerializer.ReadBinary(binary);

        var type = Assert.Single(normalized.Types);
        Assert.Empty(type.Properties);
        Assert.Empty(type.Functions);
        Assert.Empty(type.Values);
    }

    [Fact]
    public void SnapshotReader_accepts_the_versioned_client_contract()
    {
        using var file = TemporaryFile.Json("""
            {
              "schemaVersion": 2,
              "target": "Client",
              "sdkAssemblyName": "Briefcase.DeceiveInc.Client.Sdk",
              "gameBuild": { "peTimestamp": 1, "imageSize": 2 },
              "types": []
            }
            """);

        var snapshot = SnapshotReader.Read(file.Path);

        Assert.Equal("Client", snapshot.Target);
        Assert.Equal((uint)1, snapshot.GameBuild.PeTimestamp);
    }

    [Theory]
    [InlineData(1, "Client", "Briefcase.DeceiveInc.Client.Sdk", "schema 2")]
    [InlineData(2, "Editor", "Briefcase.DeceiveInc.Editor.Sdk", "Unsupported SDK target")]
    [InlineData(2, "Server", "Wrong.Name", "Expected SDK assembly")]
    public void SnapshotReader_rejects_incompatible_metadata(
        int schema, string target, string assemblyName, string expectedMessage)
    {
        using var file = TemporaryFile.Json($$"""
            {
              "schemaVersion": {{schema}},
              "target": "{{target}}",
              "sdkAssemblyName": "{{assemblyName}}",
              "gameBuild": {},
              "types": []
            }
            """);

        var error = Assert.Throws<InvalidDataException>(() => SnapshotReader.Read(file.Path));
        Assert.Contains(expectedMessage, error.Message);
    }

    [Fact]
    public void EmissionPlan_filters_unsupported_types_and_maps_safe_members()
    {
        var snapshot = CreateSnapshot();
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Stats",
                Name = "Stats",
                Kind = "ScriptStruct",
                Size = 8,
                Properties =
                [
                    Property("Health", "IntProperty", 0, 4),
                    Property("Enabled", "BoolProperty", 4, 1),
                    Property("Overflow", "IntProperty", 7, 4)
                ]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Spy",
                Name = "Spy",
                Kind = "Class",
                Properties =
                [
                    Property("Handle", "IntProperty", 0, 4),
                    Property("DisplayName", "StrProperty", 4, 16)
                ]
            },
            new TypeSnapshot
            {
                Path = "/Game/Blueprints.Ignored",
                Name = "Ignored",
                Kind = "Class"
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Empty",
                Name = "Empty",
                Kind = "ScriptStruct",
                Size = 0
            }
        ]);

        var plan = SdkEmissionPlan.Create(snapshot);

        Assert.Equal(2, plan.Types.Count);
        Assert.Equal(1, plan.SkippedTypeCount);
        Assert.Equal(1, plan.SkippedPropertyCount);
        var spy = plan.TypesByPath["/Script/DeceiveInc.Spy"];
        Assert.Equal("Briefcase.DeceiveInc.Spy", spy.FullName);
        Assert.Contains(spy.Properties, item =>
            item.MemberName == "Handle_Property" && item.Type.Kind == ManagedTypeKind.Int32);
        Assert.Contains(spy.Properties, item =>
            item.MemberName == "DisplayName" && item.Type.Kind == ManagedTypeKind.String);
    }

    [Fact]
    public void EmissionPlan_keeps_supported_returns_and_skips_writable_out_parameters()
    {
        const ulong outParameter = 0x100;
        const ulong returnParameter = 0x400;
        var snapshot = CreateSnapshot();
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.Spy",
            Name = "Spy",
            Kind = "Class",
            Functions =
            [
                new FunctionSnapshot
                {
                    Name = "GetHealth",
                    ParameterSize = 4,
                    Parameters = [Property("ReturnValue", "IntProperty", 0, 4, returnParameter)]
                },
                new FunctionSnapshot
                {
                    Name = "GetLocation",
                    ParameterSize = 12,
                    Parameters = [Property("OutLocation", "FloatProperty", 0, 4, outParameter)]
                }
            ]
        });

        var plan = SdkEmissionPlan.Create(snapshot);
        var spy = Assert.Single(plan.Types);

        var function = Assert.Single(spy.Functions);
        Assert.Equal("GetHealth", function.MemberName);
        Assert.Equal(ManagedTypeKind.Int32, function.ReturnParameter?.Type.Kind);
        Assert.Equal(1, plan.SkippedFunctionCount);
    }

    [Fact]
    public void SnapshotReader_preserves_schema_3_recursive_type_metadata()
    {
        using var file = TemporaryFile.Json("""
            {
              "schemaVersion": 3,
              "target": "Server",
              "sdkAssemblyName": "Briefcase.DeceiveInc.Server.Sdk",
              "gameBuild": { "peTimestamp": 1, "imageSize": 2 },
              "types": [{
                "path": "/Script/DeceiveInc.Settings",
                "name": "Settings",
                "kind": "Class",
                "size": 64,
                "properties": [{
                  "name": "Weights",
                  "unrealType": "MapProperty",
                  "offset": 40,
                  "elementSize": 80,
                  "arrayDimension": 1,
                  "flags": 0,
                  "type": {
                    "unrealType": "MapProperty",
                    "elementSize": 80,
                    "keyType": { "unrealType": "NameProperty", "elementSize": 8 },
                    "valueType": {
                      "unrealType": "ArrayProperty",
                      "elementSize": 16,
                      "innerType": {
                        "unrealType": "StructProperty",
                        "elementSize": 12,
                        "referencedTypePath": "/Script/CoreUObject.Vector"
                      }
                    }
                  }
                }],
                "functions": []
              }]
            }
            """);

        var snapshot = SnapshotReader.Read(file.Path);
        var type = Assert.Single(snapshot.Types).Properties.Single().EffectiveType;

        Assert.Equal("MapProperty", type.UnrealType);
        Assert.Equal("NameProperty", type.KeyType?.UnrealType);
        Assert.Equal("ArrayProperty", type.ValueType?.UnrealType);
        Assert.Equal("/Script/CoreUObject.Vector",
            type.ValueType?.InnerType?.ReferencedTypePath);
    }

    [Fact]
    public void EmissionPlan_describes_unsupported_containers_and_out_parameters()
    {
        const ulong outParameter = 0x100;
        var snapshot = CreateSnapshot();
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.Settings",
            Name = "Settings",
            Kind = "Class",
            Properties =
            [
                new PropertySnapshot
                {
                    Name = "Weights",
                    UnrealType = "MapProperty",
                    Offset = 40,
                    ElementSize = 80,
                    ArrayDimension = 1,
                    Type = new UnrealTypeSnapshot
                    {
                        UnrealType = "MapProperty",
                        ElementSize = 80,
                        KeyType = new UnrealTypeSnapshot
                        {
                            UnrealType = "NameProperty",
                            ElementSize = 8
                        },
                        ValueType = new UnrealTypeSnapshot
                        {
                            UnrealType = "FloatProperty",
                            ElementSize = 4
                        }
                    }
                }
            ],
            Functions =
            [
                new FunctionSnapshot
                {
                    Name = "GetWeight",
                    ParameterSize = 4,
                    Parameters =
                    [
                        Property("Weight", "FloatProperty", 0, 4, outParameter)
                    ]
                }
            ]
        });

        var plan = SdkEmissionPlan.Create(snapshot);
        var type = Assert.Single(plan.Types);

        Assert.Empty(type.Properties);
        Assert.Empty(type.Functions);
        Assert.Single(type.MetadataProperties);
        Assert.Single(type.MetadataFunctions);
        Assert.Equal(1, plan.DescribedPropertyCount);
        Assert.Equal(1, plan.DescribedFunctionCount);
        Assert.Equal(UnrealTypeKind.Map,
            SdkEmissionPlan.MetadataKind(type.MetadataProperties[0].Snapshot.EffectiveType));
    }

    [Fact]
    public void EmissionPlan_treats_FName_as_a_blittable_value_and_keeps_enums()
    {
        const ulong returnParameter = 0x400;
        var snapshot = CreateSnapshot();
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Agent",
                Name = "Agent",
                Kind = "Class",
                Properties = [Property("AgentName", "NameProperty", 8, 8)],
                Functions =
                [
                    new FunctionSnapshot
                    {
                        Name = "GetAgentName",
                        ParameterSize = 8,
                        Parameters =
                        [
                            Property("ReturnValue", "NameProperty", 0, 8,
                                returnParameter)
                        ]
                    }
                ]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.AgentRole",
                Name = "AgentRole",
                Kind = "Enum",
                Values =
                [
                    new EnumValueSnapshot { Name = "AgentRole::Spy", Value = 0 },
                    new EnumValueSnapshot { Name = "AgentRole::Guard", Value = 1 }
                ]
            }
        ]);

        var plan = SdkEmissionPlan.Create(snapshot);
        var agent = plan.TypesByPath["/Script/DeceiveInc.Agent"];
        var role = plan.TypesByPath["/Script/DeceiveInc.AgentRole"];

        Assert.Equal(ManagedTypeKind.Name, Assert.Single(agent.Properties).Type.Kind);
        Assert.Equal(ManagedTypeKind.Name,
            Assert.Single(agent.Functions).ReturnParameter?.Type.Kind);
        Assert.Equal("Briefcase.DeceiveInc.EAgentRole", role.FullName);
        Assert.Equal(2, role.Snapshot.Values.Count);
    }

    [Fact]
    public void Public_type_metadata_formats_nested_Unreal_containers()
    {
        var metadata = new UnrealTypeMetadata(
            UnrealTypeKind.Map,
            "MapProperty",
            80,
            KeyType: new UnrealTypeMetadata(UnrealTypeKind.Name, "NameProperty", 8),
            ValueType: new UnrealTypeMetadata(
                UnrealTypeKind.Array,
                "ArrayProperty",
                16,
                InnerType: new UnrealTypeMetadata(
                    UnrealTypeKind.Struct,
                    "StructProperty",
                    12,
                    "/Script/CoreUObject.Vector")));

        Assert.Equal("TMap<FName, TArray<Vector>>", metadata.ToString());
    }
    [Fact]
    public void Persisted_emitter_exposes_enums_and_recursive_metadata()
    {
        const ulong outParameter = 0x100;
        const ulong returnParameter = 0x400;
        var snapshot = CreateSnapshot(schemaVersion: 3);
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.AgentRole",
                Name = "AgentRole",
                Kind = "Enum",
                Values =
                [
                    new EnumValueSnapshot { Name = "AgentRole::Spy", Value = 0 },
                    new EnumValueSnapshot { Name = "AgentRole::Guard", Value = 7 }
                ]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Settings",
                Name = "Settings",
                Kind = "Class",
                Size = 96,
                Properties =
                [
                    new PropertySnapshot
                    {
                        Name = "Weights",
                        UnrealType = "MapProperty",
                        Offset = 40,
                        ElementSize = 80,
                        ArrayDimension = 1,
                        Type = new UnrealTypeSnapshot
                        {
                            UnrealType = "MapProperty",
                            ElementSize = 80,
                            KeyType = new UnrealTypeSnapshot
                            {
                                UnrealType = "NameProperty",
                                ElementSize = 8
                            },
                            ValueType = new UnrealTypeSnapshot
                            {
                                UnrealType = "ArrayProperty",
                                ElementSize = 16,
                                InnerType = new UnrealTypeSnapshot
                                {
                                    UnrealType = "FloatProperty",
                                    ElementSize = 4
                                }
                            }
                        }
                    }
                ],
                Functions =
                [
                    new FunctionSnapshot
                    {
                        Name = "ResolveWeight",
                        Flags = 0x04020401,
                        ParameterSize = 8,
                        ParameterCount = 2,
                        Parameters =
                        [
                            Property("Weight", "FloatProperty", 0, 4, outParameter),
                            Property("ReturnValue", "BoolProperty", 4, 1, returnParameter)
                        ]
                    }
                ]
            }
        ]);

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "SyntheticSdk.dll");
        var assemblyName = "Briefcase.DeceiveInc.Synthetic." + Guid.NewGuid().ToString("N");
        var result = SnapshotSdkEmitter.Emit(snapshot, path, assemblyName);
        var assembly = Assembly.Load(File.ReadAllBytes(path));

        var role = assembly.GetType("Briefcase.DeceiveInc.EAgentRole", throwOnError: true)!;
        Assert.True(role.IsEnum);
        Assert.Equal(typeof(long), Enum.GetUnderlyingType(role));
        Assert.Equal(7L, Convert.ToInt64(role.GetField("Guard")!.GetRawConstantValue()));
        Assert.Equal("/Script/DeceiveInc.AgentRole",
            role.GetCustomAttribute<UnrealTypePathAttribute>()?.Path);

        var settings = assembly.GetType("Briefcase.DeceiveInc.Settings", throwOnError: true)!;
        var reflection = Assert.IsType<UnrealReflectedType>(
            settings.GetProperty("Reflection")!.GetValue(null));
        Assert.Equal(UnrealReflectedTypeKind.Class, reflection.Kind);
        Assert.Equal(96, reflection.NativeSize);

        var metadata = settings.GetNestedType("Metadata", BindingFlags.Public)!;
        var properties = metadata.GetNestedType("Properties", BindingFlags.Public)!;
        var weights = Assert.IsType<UnrealReflectedProperty>(
            properties.GetProperty("Weights")!.GetValue(null));
        Assert.Equal("TMap<FName, TArray<float>>", weights.Type.ToString());
        Assert.Equal(UnrealTypeKind.Map, weights.Type.Kind);
        Assert.Equal(UnrealTypeKind.Name, weights.Type.KeyType?.Kind);
        Assert.Equal(UnrealTypeKind.Float, weights.Type.ValueType?.InnerType?.Kind);

        var functions = metadata.GetNestedType("Functions", BindingFlags.Public)!;
        var resolve = Assert.IsType<UnrealReflectedFunction>(
            functions.GetProperty("ResolveWeight")!.GetValue(null));
        Assert.Equal(2, resolve.Parameters.Count);
        Assert.True(resolve.Parameters[0].IsOutput);
        Assert.False(resolve.Parameters[0].IsReturn);
        Assert.True(resolve.Parameters[1].IsReturn);
        Assert.Equal(2, result.TypeCount);
        Assert.Equal(1, result.Plan.DescribedPropertyCount);
        Assert.Equal(1, result.Plan.DescribedFunctionCount);
    }

    private static SdkSnapshot CreateSnapshot(int schemaVersion = 2) => new()
    {
        SchemaVersion = schemaVersion,
        Target = "Client",
        SdkAssemblyName = "Briefcase.DeceiveInc.Client.Sdk"
    };

    private static PropertySnapshot Property(
        string name, string unrealType, int offset, int size, ulong flags = 0) => new()
        {
            Name = name,
            UnrealType = unrealType,
            Offset = offset,
            ElementSize = size,
            ArrayDimension = 1,
            Flags = flags
        };
}
