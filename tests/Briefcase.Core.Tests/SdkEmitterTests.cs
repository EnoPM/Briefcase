using Briefcase.SdkEmitter;

namespace Briefcase.Core.Tests;

public sealed class SdkEmitterTests
{
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

    private static SdkSnapshot CreateSnapshot() => new()
    {
        SchemaVersion = 2,
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
