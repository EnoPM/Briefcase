using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Core.Tests;

public sealed class ComplexUnrealValueTests
{
    [Fact]
    public void Public_container_values_round_trip_through_BVC1()
    {
        var array = new UnrealArray<int>(12, 34, 56);
        var set = new UnrealSet<string>("alpha", "beta");
        var map = new UnrealMap<UnrealName, UnrealArray<int>>(
            new(new UnrealName(7, 0), array),
            new(new UnrealName(8, 1), new UnrealArray<int>(90)));

        var decodedArray = UnrealValueWire.Decode<UnrealArray<int>>(
            UnrealValueWire.Encode(typeof(UnrealArray<int>), array));
        var decodedSet = UnrealValueWire.Decode<UnrealSet<string>>(
            UnrealValueWire.Encode(typeof(UnrealSet<string>), set));
        var decodedMap = UnrealValueWire.Decode<UnrealMap<UnrealName, UnrealArray<int>>>(
            UnrealValueWire.Encode(typeof(UnrealMap<UnrealName, UnrealArray<int>>), map));

        Assert.Equal([12, 34, 56], decodedArray.ToArray());
        Assert.Equal(["alpha", "beta"], decodedSet.ToArray());
        Assert.Equal(new UnrealName(7, 0), decodedMap[0].Key);
        Assert.Equal([90], decodedMap[1].Value.ToArray());
    }

    [Fact]
    public void Recursive_struct_wire_decodes_owning_and_container_fields()
    {
        var payload = new List<byte>();
        UInt32(payload, uint.MaxValue); // recursive USTRUCT marker
        UInt32(payload, 32);            // native size, used only for validation
        UInt32(payload, 3);             // two known fields and one forward-compatible field

        Field(payload, 0, "Label", Node(UnrealPropertyKind.String, StringPayload("Briefcase")));
        Field(payload, 16, "Scores", Node(
            UnrealPropertyKind.Array,
            Payload(
                UInt32Bytes(2),
                Node(UnrealPropertyKind.Int32, Int32Bytes(12)),
                Node(UnrealPropertyKind.Int32, Int32Bytes(34)))));
        Field(payload, 24, "FutureField", Node(UnrealPropertyKind.UInt64, UInt64Bytes(99)));

        var wire = Payload(
            UInt32Bytes(0x31435642),
            Node(UnrealPropertyKind.Struct, payload.ToArray()));
        var value = UnrealValueWire.Decode<ManagedPayload>(wire);

        Assert.Equal("Briefcase", value.Label);
        Assert.Equal([12, 34], value.Scores!.ToArray());
    }

    [Fact]
    public void Managed_struct_encoder_round_trips_recursive_fields()
    {
        var originalWire = Payload(
            UInt32Bytes(0x31435642),
            Node(UnrealPropertyKind.Struct, Payload(
                UInt32Bytes(uint.MaxValue), UInt32Bytes(32), UInt32Bytes(2),
                Int32Bytes(0), StringPayload("Label"),
                Node(UnrealPropertyKind.String, StringPayload("Briefcase")),
                Int32Bytes(16), StringPayload("Scores"),
                Node(UnrealPropertyKind.Array, Payload(
                    UInt32Bytes(2),
                    Node(UnrealPropertyKind.Int32, Int32Bytes(12)),
                    Node(UnrealPropertyKind.Int32, Int32Bytes(34)))))));
        var original = UnrealValueWire.Decode<ManagedPayload>(originalWire);

        var encoded = UnrealValueWire.Encode(typeof(ManagedPayload), original);
        var decoded = UnrealValueWire.Decode<ManagedPayload>(encoded);

        Assert.Equal("Briefcase", decoded.Label);
        Assert.Equal([12, 34], decoded.Scores!.ToArray());
    }

    [Fact]
    public void Recursive_struct_wire_rejects_a_stale_native_size()
    {
        var payload = Payload(
            UInt32Bytes(uint.MaxValue), UInt32Bytes(31), UInt32Bytes(0));
        var wire = Payload(
            UInt32Bytes(0x31435642),
            Node(UnrealPropertyKind.Struct, payload));

        var error = Assert.Throws<InvalidDataException>(
            () => UnrealValueWire.Decode<ManagedPayload>(wire));
        Assert.Contains("expected 32", error.Message);
    }

    [Fact]
    public void Output_envelope_decodes_a_complex_return_by_parameter_offset()
    {
        var payload = new List<byte>();
        UInt32(payload, uint.MaxValue);
        UInt32(payload, 32);
        UInt32(payload, 1);
        Field(payload, 0, "Label", Node(UnrealPropertyKind.String, StringPayload("result")));
        var envelope = Payload(
            UInt32Bytes(0x314f5642), UInt32Bytes(1), Int32Bytes(8),
            Node(UnrealPropertyKind.Struct, payload.ToArray()));
        var returned = new UnrealParameter("ReturnValue", typeof(ManagedPayload), 8, 32, true);
        var function = new UnrealFunction("/Script/Test.Owner", "Get", 40, [returned],
            typeof(ManagedPayload)) { ReturnParameter = returned };

        var values = UnrealValueWire.DecodeOutputs(function, envelope);

        Assert.Equal("result", Assert.IsType<ManagedPayload>(values[8]).Label);
    }

    private struct ManagedPayload : IUnrealManagedStructValue
    {
        public const int NativeSize = 32;
        [UnrealStructField("Label", 0)] public string? Label;
        [UnrealStructField("Scores", 16)] public UnrealArray<int>? Scores;

        public ManagedPayload() => (Label, Scores) = (null, null);
    }

    private static void Field(List<byte> destination, int offset, string name, byte[] node)
    {
        destination.AddRange(Int32Bytes(offset));
        destination.AddRange(StringPayload(name));
        destination.AddRange(node);
    }

    private static byte[] Node(UnrealPropertyKind kind, byte[] payload) =>
        Payload(UInt32Bytes((uint)kind), UInt32Bytes((uint)payload.Length), payload);

    private static byte[] StringPayload(string value) =>
        Payload(UInt32Bytes((uint)value.Length), Encoding.Unicode.GetBytes(value));

    private static byte[] Int32Bytes(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt32Bytes(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt64Bytes(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static void UInt32(List<byte> destination, uint value) =>
        destination.AddRange(UInt32Bytes(value));

    private static byte[] Payload(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
