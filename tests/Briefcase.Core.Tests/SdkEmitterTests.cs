using System.Reflection;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using Briefcase.SdkEmitter;

namespace Briefcase.Core.Tests;

public sealed class SdkEmitterTests
{
    [Fact]
    public void SnapshotReader_round_trips_the_compact_binary_contract()
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
        var path = Path.Combine(directory.Path, "snapshot.bsnap");
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
        bytes[4] = 3;
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
    public void EmissionPlan_keeps_supported_returns_and_writable_out_parameters()
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

        Assert.Equal(2, spy.Functions.Count);
        var function = Assert.Single(spy.Functions, item => item.MemberName == "GetHealth");
        Assert.Equal(ManagedTypeKind.Int32, function.ReturnParameter?.Type.Kind);
        var output = Assert.Single(spy.Functions, item => item.MemberName == "GetLocation");
        Assert.True(Assert.Single(output.Outputs).IsOutput);
        Assert.Equal(0, plan.SkippedFunctionCount);
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
    public void EmissionPlan_emits_recursive_containers_and_out_parameters()
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

        var property = Assert.Single(type.Properties);
        Assert.Equal(ManagedTypeKind.Map, property.Type.Kind);
        Assert.Equal(ManagedTypeKind.Name, property.Type.KeyType?.Kind);
        Assert.Equal(ManagedTypeKind.Float, property.Type.ValueType?.Kind);
        Assert.True(Assert.Single(Assert.Single(type.Functions).Outputs).IsOutput);
        Assert.Single(type.MetadataProperties);
        Assert.Single(type.MetadataFunctions);
        Assert.Equal(1, plan.DescribedPropertyCount);
        Assert.Equal(1, plan.DescribedFunctionCount);
        Assert.Equal(UnrealTypeKind.Map,
            SdkEmissionPlan.MetadataKind(type.MetadataProperties[0].Snapshot.EffectiveType));
    }

    [Fact]
    public void Value_wire_decodes_nested_address_free_containers()
    {
        static byte[] Node(UnrealPropertyKind kind, byte[] payload)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((uint)kind);
            writer.Write((uint)payload.Length);
            writer.Write(payload);
            return stream.ToArray();
        }

        static byte[] IntNode(int value)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(value);
            return Node(UnrealPropertyKind.Int32, stream.ToArray());
        }

        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(2u);
            writer.Write(IntNode(1337));
            writer.Write(IntNode(900));
        }
        var root = Node(UnrealPropertyKind.Array, payloadStream.ToArray());
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(0x31435642u);
            writer.Write(root);
        }

        var values = UnrealValueWire.Decode<UnrealArray<int>>(wireStream.ToArray());

        Assert.Equal([1337, 900], values.ToArray());
    }

    [Fact]
    public void Value_wire_decodes_Unreal_references_without_native_addresses()
    {
        static byte[] Wire(UnrealPropertyKind kind, Action<BinaryWriter> payload)
        {
            using var payloadStream = new MemoryStream();
            using (var writer = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, true))
                payload(writer);
            using var stream = new MemoryStream();
            using var root = new BinaryWriter(stream);
            root.Write(0x31435642u);
            root.Write((uint)kind);
            root.Write((uint)payloadStream.Length);
            root.Write(payloadStream.ToArray());
            return stream.ToArray();
        }

        static void String(BinaryWriter writer, string value)
        {
            writer.Write((uint)value.Length);
            writer.Write(System.Text.Encoding.Unicode.GetBytes(value));
        }

        var soft = UnrealValueWire.Decode<UnrealSoftObjectReference>(
            Wire(UnrealPropertyKind.SoftObject, writer =>
            {
                writer.Write(42u);
                writer.Write(7u);
                String(writer, "/Game/Agents/Spy");
                String(writer, "Default__Spy");
            }));
        var multicast = UnrealValueWire.Decode<UnrealMulticastDelegate>(
            Wire(UnrealPropertyKind.MulticastDelegate, writer =>
            {
                writer.Write(1u);
                writer.Write(42u);
                writer.Write(7u);
                writer.Write(11u);
                writer.Write(2u);
            }));

        Assert.Equal((uint)42, soft.Object.Handle.Index);
        Assert.Equal("/Game/Agents/Spy", soft.AssetPath);
        Assert.Equal("Default__Spy", soft.SubPath);
        var binding = Assert.Single(multicast);
        Assert.Equal(new UnrealName(11, 2), binding.FunctionName);
        Assert.Equal((uint)7, binding.Target.Handle.SerialNumber);
    }

    [Fact]
    public void EmissionPlan_exposes_special_Unreal_values_as_read_only_snapshots()
    {
        var snapshot = CreateSnapshot();
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.SpecialValues",
            Name = "SpecialValues",
            Kind = "Class",
            Properties =
            [
                Property("Interface", "InterfaceProperty", 0, 16),
                Property("Lazy", "LazyObjectProperty", 16, 28),
                Property("Soft", "SoftObjectProperty", 48, 40),
                Property("SoftClass", "SoftClassProperty", 88, 40),
                Property("Callback", "DelegateProperty", 128, 16),
                Property("Callbacks", "MulticastInlineDelegateProperty", 144, 16),
                Property("Path", "FieldPathProperty", 160, 32)
            ]
        });

        var properties = Assert.Single(SdkEmissionPlan.Create(snapshot).Types).Properties;

        Assert.Collection(properties.OrderBy(property => property.Snapshot.Offset),
            property => Assert.Equal(ManagedTypeKind.InterfaceReference, property.Type.Kind),
            property => Assert.Equal(ManagedTypeKind.LazyObjectReference, property.Type.Kind),
            property => Assert.Equal(ManagedTypeKind.SoftObjectReference, property.Type.Kind),
            property => Assert.Equal(ManagedTypeKind.SoftClassReference, property.Type.Kind),
            property => Assert.Equal(ManagedTypeKind.Delegate, property.Type.Kind),
            property => Assert.Equal(ManagedTypeKind.MulticastDelegate, property.Type.Kind),
            property => Assert.Equal(ManagedTypeKind.FieldPath, property.Type.Kind));
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

    [Fact]
    public void Persisted_emitter_caches_descriptors_and_emits_a_stack_buffer_fast_path()
    {
        const ulong returnParameter = 0x400;
        var snapshot = CreateSnapshot();
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.Controller",
            Name = "Controller",
            Kind = "Class",
            Functions =
            [
                new FunctionSnapshot
                {
                    Name = "SetMode",
                    ParameterSize = 8,
                    Parameters =
                    [
                        Property("Mode", "IntProperty", 0, 4),
                        Property("ReturnValue", "BoolProperty", 4, 1, returnParameter)
                    ]
                }
            ]
        });

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "FastPathSdk.dll");
        SnapshotSdkEmitter.Emit(
            snapshot, path, "Briefcase.DeceiveInc.FastPath." + Guid.NewGuid().ToString("N"));
        var assembly = Assembly.Load(File.ReadAllBytes(path));
        var controller = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
        var functions = controller.GetNestedType("Functions", BindingFlags.Public)!;
        var descriptor = functions.GetProperty("SetMode")!;

        Assert.Same(descriptor.GetValue(null), descriptor.GetValue(null));
        var il = controller.GetMethod("SetMode")!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.Contains(il.Select((value, index) => (value, index)), item =>
            item.value == 0xFE && item.index + 1 < il.Length && il[item.index + 1] == 0x0F);
        Assert.DoesNotContain((byte)0x8D, il); // newarr
    }

    [Fact]
    public void EmissionPlan_uses_canonical_prepared_path_for_nested_object_handles()
    {
        var snapshot = CreateSnapshot();
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.UnsafePayload",
                Name = "UnsafePayload",
                Kind = "ScriptStruct",
                Size = 8,
                Properties = [Property("Object", "ObjectProperty", 0, 8)]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Controller",
                Name = "Controller",
                Kind = "Class",
                Functions =
                [
                    new FunctionSnapshot
                    {
                        Name = "Consume",
                        ParameterSize = 8,
                        Parameters =
                        [
                            new PropertySnapshot
                            {
                                Name = "Payload",
                                UnrealType = "StructProperty",
                                Offset = 0,
                                ElementSize = 8,
                                ArrayDimension = 1,
                                Type = new UnrealTypeSnapshot
                                {
                                    UnrealType = "StructProperty",
                                    ElementSize = 8,
                                    ReferencedTypePath = "/Script/DeceiveInc.UnsafePayload"
                                }
                            }
                        ]
                    }
                ]
            }
        ]);

        var plan = SdkEmissionPlan.Create(snapshot);

        var function = Assert.Single(
            plan.TypesByPath["/Script/DeceiveInc.Controller"].Functions);
        Assert.Equal("Consume", function.MemberName);
        Assert.True(function.SupportsDirectInvocation);
        Assert.True(function.SupportsPreparedInvocation);
        Assert.False(function.SupportsPreparedFastPath);
        Assert.Equal(0, plan.SkippedFunctionCount);

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "PatchSurfaceSdk.dll");
        SnapshotSdkEmitter.Emit(
            snapshot, path,
            "Briefcase.DeceiveInc.PatchSurface." + Guid.NewGuid().ToString("N"));
        var assembly = Assembly.Load(File.ReadAllBytes(path));
        var controller = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
        var consume = controller.GetMethod(
            "Consume", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(consume);
        var il = consume!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.Contains(il.Select((value, index) => (value, index)), item =>
            item.value == 0xFE && item.index + 1 < il.Length && il[item.index + 1] == 0x0F);
        var functions = controller.GetNestedType("Functions", BindingFlags.Public)!;
        Assert.NotNull(functions.GetProperty(
            "Consume", BindingFlags.Public | BindingFlags.Static));

        var target = new UnrealPostfixPatchAttribute(controller, "Consume");
        var resolve = typeof(PatchDiscovery).GetMethod(
            "ResolveFunction", BindingFlags.NonPublic | BindingFlags.Static)!;
        var resolved = Assert.IsType<UnrealFunction>(resolve.Invoke(null, [target]));
        Assert.Equal("Consume", resolved.Name);
        Assert.Equal("Briefcase.DeceiveInc.FUnsafePayload",
            Assert.Single(resolved.Parameters).ManagedType.FullName);
    }

    [Fact]
    public void Emitter_exposes_owning_structs_as_managed_snapshots_and_complex_returns()
    {
        const ulong returnParameter = 0x400;
        const string payloadPath = "/Script/DeceiveInc.ManagedPayload";
        var snapshot = CreateSnapshot();
        snapshot.Types.AddRange([
            new TypeSnapshot
            {
                Path = payloadPath,
                Name = "ManagedPayload",
                Kind = "ScriptStruct",
                Size = 32,
                Properties =
                [
                    Property("Label", "StrProperty", 0, 16),
                    new PropertySnapshot
                    {
                        Name = "Scores",
                        UnrealType = "ArrayProperty",
                        Offset = 16,
                        ElementSize = 16,
                        ArrayDimension = 1,
                        Type = new UnrealTypeSnapshot
                        {
                            UnrealType = "ArrayProperty",
                            ElementSize = 16,
                            InnerType = new UnrealTypeSnapshot
                            {
                                UnrealType = "IntProperty",
                                ElementSize = 4
                            }
                        }
                    }
                ]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.Controller",
                Name = "Controller",
                Kind = "Class",
                Properties =
                [
                    new PropertySnapshot
                    {
                        Name = "Payload",
                        UnrealType = "StructProperty",
                        Offset = 40,
                        ElementSize = 32,
                        ArrayDimension = 1,
                        Type = new UnrealTypeSnapshot
                        {
                            UnrealType = "StructProperty",
                            ElementSize = 32,
                            ReferencedTypePath = payloadPath
                        }
                    }
                ],
                Functions =
                [
                    new FunctionSnapshot
                    {
                        Name = "GetPayload",
                        ParameterSize = 32,
                        Parameters =
                        [
                            new PropertySnapshot
                            {
                                Name = "ReturnValue",
                                UnrealType = "StructProperty",
                                Offset = 0,
                                ElementSize = 32,
                                ArrayDimension = 1,
                                Flags = returnParameter,
                                Type = new UnrealTypeSnapshot
                                {
                                    UnrealType = "StructProperty",
                                    ElementSize = 32,
                                    ReferencedTypePath = payloadPath
                                }
                            }
                        ]
                    }
                ]
            }
        ]);

        var plan = SdkEmissionPlan.Create(snapshot);
        var payloadPlan = plan.TypesByPath[payloadPath];
        Assert.True(payloadPlan.UsesManagedStructRepresentation);
        Assert.Equal([ManagedTypeKind.String, ManagedTypeKind.Array],
            payloadPlan.Properties.Select(property => property.Type.Kind));
        var functionPlan = Assert.Single(
            plan.TypesByPath["/Script/DeceiveInc.Controller"].Functions);
        Assert.True(functionPlan.SupportsDirectInvocation);
        Assert.True(functionPlan.SupportsPreparedValueOutputInvocation);
        Assert.False(functionPlan.SupportsPreparedInvocation);

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ManagedStructSdk.dll");
        SnapshotSdkEmitter.Emit(
            snapshot, path,
            "Briefcase.DeceiveInc.ManagedStruct." + Guid.NewGuid().ToString("N"));
        var assembly = Assembly.Load(File.ReadAllBytes(path));
        var payload = assembly.GetType("Briefcase.DeceiveInc.FManagedPayload", true)!;
        Assert.True(payload.IsValueType);
        Assert.True(typeof(IUnrealManagedStructValue).IsAssignableFrom(payload));
        Assert.False(typeof(IUnrealStructValue).IsAssignableFrom(payload));
        var label = payload.GetField("Label")!;
        var identity = label.GetCustomAttribute<UnrealStructFieldAttribute>()!;
        Assert.Equal("Label", identity.Name);
        Assert.Equal(0, identity.Offset);

        var controller = assembly.GetType("Briefcase.DeceiveInc.Controller", true)!;
        var property = controller.GetProperty("Payload")!;
        Assert.Equal(payload, property.PropertyType);
        Assert.NotNull(property.GetMethod);
        Assert.NotNull(property.SetMethod);
        Assert.Equal(payload, controller.GetMethod("GetPayload")!.ReturnType);
    }

    [Fact]
    public void Writable_managed_structs_get_property_setters_and_input_ref_functions()
    {
        const string payloadPath = "/Script/DeceiveInc.EditablePayload";
        const ulong writableReference = 0x100 | 0x08000000;
        var snapshot = CreateSnapshot();
        snapshot.Types.AddRange(
        [
            new TypeSnapshot
            {
                Path = payloadPath,
                Name = "EditablePayload",
                Kind = "ScriptStruct",
                Size = 24,
                Properties =
                [
                    Property("Label", "StrProperty", 0, 16),
                    Property("Count", "IntProperty", 16, 4)
                ]
            },
            new TypeSnapshot
            {
                Path = "/Script/DeceiveInc.EditableController",
                Name = "EditableController",
                Kind = "Class",
                Properties =
                [
                    new PropertySnapshot
                    {
                        Name = "Payload", UnrealType = "StructProperty",
                        Offset = 40, ElementSize = 24, ArrayDimension = 1,
                        Type = new UnrealTypeSnapshot
                        {
                            UnrealType = "StructProperty", ElementSize = 24,
                            ReferencedTypePath = payloadPath
                        }
                    }
                ],
                Functions =
                [
                    StructFunction("SetPayload", payloadPath, flags: 0),
                    StructFunction("UpdatePayload", payloadPath, writableReference)
                ]
            }
        ]);

        var plan = SdkEmissionPlan.Create(snapshot);
        var payloadPlan = plan.TypesByPath[payloadPath];
        Assert.True(payloadPlan.UsesManagedStructRepresentation);
        Assert.True(payloadPlan.SupportsManagedStructWrite);
        Assert.All(plan.TypesByPath["/Script/DeceiveInc.EditableController"].Functions,
            function => Assert.True(function.SupportsPreparedValueOutputInvocation));

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "WritableManagedStructSdk.dll");
        SnapshotSdkEmitter.Emit(snapshot, path,
            "Briefcase.DeceiveInc.WritableManagedStruct." + Guid.NewGuid().ToString("N"));
        var assembly = Assembly.Load(File.ReadAllBytes(path));
        var controller = assembly.GetType(
            "Briefcase.DeceiveInc.EditableController", throwOnError: true)!;
        Assert.NotNull(controller.GetProperty("Payload")!.SetMethod);
        Assert.False(controller.GetMethod("SetPayload")!.GetParameters()[0].ParameterType.IsByRef);
        Assert.True(controller.GetMethod("UpdatePayload")!.GetParameters()[0].ParameterType.IsByRef);

        return;

        static FunctionSnapshot StructFunction(string name, string path, ulong flags) => new()
        {
            Name = name,
            ParameterSize = 24,
            Parameters =
            [
                new PropertySnapshot
                {
                    Name = "payload", UnrealType = "StructProperty", Offset = 0,
                    ElementSize = 24, ArrayDimension = 1, Flags = flags,
                    Type = new UnrealTypeSnapshot
                    {
                        UnrealType = "StructProperty", ElementSize = 24,
                        ReferencedTypePath = path
                    }
                }
            ]
        };
    }

    [Fact]
    public void Emitter_exposes_FString_and_FText_inputs_returns_and_writable_outputs()
    {
        const ulong outParameter = 0x100;
        const ulong returnParameter = 0x400;
        const ulong referenceParameter = 0x08000000;
        var snapshot = CreateSnapshot();
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.TextController",
            Name = "TextController",
            Kind = "Class",
            Functions =
            [
                Function("SetName", Property("Value", "StrProperty", 0, 16)),
                Function("GetName", Property(
                    "ReturnValue", "StrProperty", 0, 16, returnParameter)),
                Function("GetNameOut", Property(
                    "Value", "StrProperty", 0, 16, outParameter)),
                Function("RewriteName", Property(
                    "Value", "StrProperty", 0, 16,
                    outParameter | referenceParameter)),
                Function("RewriteLabel", Property(
                    "Value", "TextProperty", 0, 24,
                    outParameter | referenceParameter))
            ]
        });

        var plan = SdkEmissionPlan.Create(snapshot);
        var functions = plan.TypesByPath["/Script/DeceiveInc.TextController"]
            .Functions.ToDictionary(function => function.MemberName);

        Assert.Equal(5, functions.Count);
        Assert.All(functions.Values,
            function => Assert.True(function.SupportsPreparedValueOutputInvocation));
        Assert.Equal(0, plan.SkippedFunctionCount);

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "OwningTextSdk.dll");
        SnapshotSdkEmitter.Emit(
            snapshot, path,
            "Briefcase.DeceiveInc.OwningText." + Guid.NewGuid().ToString("N"));
        var assembly = Assembly.Load(File.ReadAllBytes(path));
        var controller = assembly.GetType(
            "Briefcase.DeceiveInc.TextController", throwOnError: true)!;

        Assert.Equal(typeof(void), controller.GetMethod("SetName")!.ReturnType);
        Assert.Equal(typeof(string),
            controller.GetMethod("SetName")!.GetParameters()[0].ParameterType);
        Assert.Equal(typeof(string), controller.GetMethod("GetName")!.ReturnType);
        Assert.Equal(typeof(string).MakeByRefType(),
            controller.GetMethod("GetNameOut")!.GetParameters()[0].ParameterType);
        Assert.True(controller.GetMethod("GetNameOut")!.GetParameters()[0].IsOut);
        Assert.Equal(typeof(string).MakeByRefType(),
            controller.GetMethod("RewriteName")!.GetParameters()[0].ParameterType);
        Assert.True(controller.GetMethod("RewriteName")!.GetParameters()[0].IsIn);
        Assert.True(controller.GetMethod("RewriteName")!.GetParameters()[0].IsOut);
        Assert.Equal(typeof(UnrealText).MakeByRefType(),
            controller.GetMethod("RewriteLabel")!.GetParameters()[0].ParameterType);

        return;

        static FunctionSnapshot Function(string name, PropertySnapshot parameter) => new()
        {
            Name = name,
            ParameterSize = parameter.ElementSize,
            Parameters = [parameter]
        };
    }

    [Fact]
    public void Binary_snapshot_v2_round_trips_delegate_signatures_and_reads_v1()
    {
        var snapshot = CreateSnapshot(schemaVersion: 4);
        snapshot.Types.Add(new TypeSnapshot
        {
            Path = "/Script/DeceiveInc.EventSource",
            Name = "EventSource",
            Kind = "Class",
            Properties =
            [
                new PropertySnapshot
                {
                    Name = "OnChanged",
                    UnrealType = "MulticastInlineDelegateProperty",
                    Offset = 32,
                    ElementSize = 16,
                    ArrayDimension = 1,
                    Type = new UnrealTypeSnapshot
                    {
                        UnrealType = "MulticastInlineDelegateProperty",
                        ElementSize = 16,
                        DelegateSignature = new FunctionSnapshot
                        {
                            Name = "OnChanged__DelegateSignature",
                            ParameterSize = 4,
                            ParameterCount = 1,
                            Parameters = [Property("Value", "IntProperty", 0, 4)]
                        }
                    }
                }
            ]
        });

        using var binary = new MemoryStream();
        BriefcaseSnapshotSerializer.WriteBinary(snapshot, binary);
        binary.Position = 0;
        var restored = BriefcaseSnapshotSerializer.ReadBinary(binary);

        var signature = Assert.Single(restored.Types).Properties.Single()
            .EffectiveType.DelegateSignature;
        Assert.NotNull(signature);
        Assert.Equal("OnChanged__DelegateSignature", signature.Name);
        Assert.Equal(ManagedTypeKind.Int32, SdkEmissionPlan.Create(restored).Types.Single()
            .Properties.Single().DelegateSignature!.Parameters.Single().Type.Kind);

        using var directory = new TemporaryDirectory();
        var assemblyPath = Path.Combine(directory.Path, "DelegateSdk.dll");
        SnapshotSdkEmitter.Emit(restored, assemblyPath, "Briefcase.DeceiveInc.DelegateSdk");
        var assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));
        var source = assembly.GetType("Briefcase.DeceiveInc.EventSource", true)!;
        var properties = source.GetNestedType("Properties", BindingFlags.Public)!;
        var descriptor = properties.GetProperty("OnChanged")!.GetValue(null)!;
        var generatedSignature = (UnrealFunction?)descriptor.GetType()
            .GetProperty("DelegateSignature")!.GetValue(descriptor);
        Assert.NotNull(generatedSignature);
        Assert.Equal(typeof(int), Assert.Single(generatedSignature.Parameters).ManagedType);

        var legacy = CreateSnapshot(schemaVersion: 3);
        binary.SetLength(0);
        BriefcaseSnapshotSerializer.WriteBinary(legacy, binary);
        var legacyBytes = binary.ToArray();
        legacyBytes[4] = 1;
        var readLegacy = BriefcaseSnapshotSerializer.ReadBinary(new MemoryStream(legacyBytes));
        Assert.Equal(3, readLegacy.SchemaVersion);
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
