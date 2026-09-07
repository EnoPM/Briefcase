using System.Text;
using System.Text.Json;

namespace Briefcase.SdkSnapshots;

/// <summary>
/// Reads the development JSON format and Briefcase's compact production format.
/// The Briefcase Snapshot binary layout uses length-prefixed UTF-8 strings and
/// count-prefixed collections. Its fixed SDK schema avoids embedding reflection
/// metadata in every snapshot.
/// </summary>
public static class BriefcaseSnapshotSerializer
{
    private static ReadOnlySpan<byte> Magic => "BRSK"u8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const ushort BinaryFormatVersion = 1;
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int MaximumStringBytes = 1024 * 1024;
    private const int MaximumTypes = 100_000;
    private const int MaximumMembers = 100_000;
    private const int MaximumTypeDepth = 8;

    public static SdkSnapshot Read(string path)
    {
        path = Path.GetFullPath(path);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
            throw new InvalidDataException(
                $"SDK snapshot exceeds the {MaximumFileBytes / (1024 * 1024)} MiB limit.");

        SdkSnapshot snapshot;
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            snapshot = JsonSerializer.Deserialize(stream, SnapshotJsonContext.Default.SdkSnapshot)
                ?? throw new InvalidDataException("The SDK snapshot is empty.");
        }
        else if (Path.GetExtension(path).Equals(
                     ".bsnap", StringComparison.OrdinalIgnoreCase))
        {
            snapshot = ReadBinary(stream);
        }
        else
        {
            throw new InvalidDataException(
                $"Unsupported SDK snapshot extension '{Path.GetExtension(path)}'.");
        }

        Validate(snapshot);
        return snapshot;
    }

    public static void WriteBinary(SdkSnapshot snapshot, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The stream is not writable.", nameof(destination));
        Validate(snapshot);

        using var writer = new BinaryWriter(destination, StrictUtf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(BinaryFormatVersion);
        writer.Write((ushort)0);
        WriteSnapshot(writer, snapshot);
    }

    public static SdkSnapshot ReadBinary(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The stream is not readable.", nameof(source));

        using var reader = new BinaryReader(source, StrictUtf8, leaveOpen: true);
        var magic = ReadExactly(reader, Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("The SDK snapshot has an invalid binary signature.");
        var version = reader.ReadUInt16();
        var reserved = reader.ReadUInt16();
        if (version != BinaryFormatVersion || reserved != 0)
            throw new InvalidDataException(
                $"Unsupported Briefcase binary snapshot format {version}.");

        var snapshot = ReadSnapshot(reader);
        if (source.CanSeek && source.Position != source.Length)
            throw new InvalidDataException("The SDK snapshot contains trailing binary data.");
        return snapshot;
    }

    private static void Validate(SdkSnapshot snapshot)
    {
        if (snapshot.SchemaVersion is not (2 or 3))
            throw new InvalidDataException(
                $"Persisted SDK generation requires snapshot schema 2 or 3; found {snapshot.SchemaVersion}.");
        if (snapshot.Target is not ("Client" or "Server"))
            throw new InvalidDataException($"Unsupported SDK target {snapshot.Target}.");
        var expectedName = $"Briefcase.DeceiveInc.{snapshot.Target}.Sdk";
        if (snapshot.SdkAssemblyName != expectedName)
            throw new InvalidDataException(
                $"Expected SDK assembly {expectedName}; found {snapshot.SdkAssemblyName}.");
    }

    private static void WriteSnapshot(BinaryWriter writer, SdkSnapshot snapshot)
    {
        writer.Write(snapshot.SchemaVersion);
        WriteString(writer, snapshot.Target);
        WriteString(writer, snapshot.SdkAssemblyName);
        writer.Write(snapshot.GameBuild.PeTimestamp);
        writer.Write(snapshot.GameBuild.ImageSize);
        writer.Write(snapshot.CapturedObjectCount);
        WriteList(writer, snapshot.Types, WriteType);
    }

    private static SdkSnapshot ReadSnapshot(BinaryReader reader) => new()
    {
        SchemaVersion = reader.ReadInt32(),
        Target = ReadString(reader),
        SdkAssemblyName = ReadString(reader),
        GameBuild = new GameBuildSnapshot
        {
            PeTimestamp = reader.ReadUInt32(),
            ImageSize = reader.ReadUInt32()
        },
        CapturedObjectCount = reader.ReadInt32(),
        Types = ReadList(reader, MaximumTypes, "types", ReadType)
    };

    private static void WriteType(BinaryWriter writer, TypeSnapshot type)
    {
        WriteString(writer, type.Path);
        WriteString(writer, type.Name);
        WriteString(writer, type.Kind);
        WriteNullableString(writer, type.SuperPath);
        writer.Write(type.Size);
        WriteList(writer, type.Properties, WriteProperty);
        WriteList(writer, type.Functions, WriteFunction);
        WriteList(writer, type.Values, WriteEnumValue);
    }

    private static TypeSnapshot ReadType(BinaryReader reader) => new()
    {
        Path = ReadString(reader),
        Name = ReadString(reader),
        Kind = ReadString(reader),
        SuperPath = ReadNullableString(reader),
        Size = reader.ReadInt32(),
        Properties = ReadList(reader, MaximumMembers, "properties", ReadProperty),
        Functions = ReadList(reader, MaximumMembers, "functions", ReadFunction),
        Values = ReadList(reader, MaximumMembers, "enum values", ReadEnumValue)
    };

    private static void WriteProperty(BinaryWriter writer, PropertySnapshot property)
    {
        WriteString(writer, property.Name);
        WriteString(writer, property.UnrealType);
        writer.Write(property.Offset);
        writer.Write(property.ElementSize);
        writer.Write(property.ArrayDimension);
        writer.Write(property.Flags);
        WriteNullableString(writer, property.ReferencedTypePath);
        WriteNullableString(writer, property.InnerUnrealType);
        WriteNullableType(writer, property.Type, 0);
    }

    private static PropertySnapshot ReadProperty(BinaryReader reader) => new()
    {
        Name = ReadString(reader),
        UnrealType = ReadString(reader),
        Offset = reader.ReadInt32(),
        ElementSize = reader.ReadInt32(),
        ArrayDimension = reader.ReadInt32(),
        Flags = reader.ReadUInt64(),
        ReferencedTypePath = ReadNullableString(reader),
        InnerUnrealType = ReadNullableString(reader),
        Type = ReadNullableType(reader, 0)
    };

    private static void WriteNullableType(
        BinaryWriter writer, UnrealTypeSnapshot? type, int depth)
    {
        writer.Write(type is not null);
        if (type is null) return;
        if (depth > MaximumTypeDepth)
            throw new InvalidDataException("Unreal type metadata exceeds the maximum depth.");
        WriteString(writer, type.UnrealType);
        writer.Write(type.ElementSize);
        WriteNullableString(writer, type.ReferencedTypePath);
        WriteNullableType(writer, type.InnerType, depth + 1);
        WriteNullableType(writer, type.KeyType, depth + 1);
        WriteNullableType(writer, type.ValueType, depth + 1);
        WriteNullableType(writer, type.UnderlyingType, depth + 1);
        writer.Write(type.BooleanLayout is not null);
        if (type.BooleanLayout is { } boolean)
        {
            writer.Write(boolean.FieldSize);
            writer.Write(boolean.ByteOffset);
            writer.Write(boolean.ByteMask);
            writer.Write(boolean.FieldMask);
        }
    }

    private static UnrealTypeSnapshot? ReadNullableType(BinaryReader reader, int depth)
    {
        if (!reader.ReadBoolean()) return null;
        if (depth > MaximumTypeDepth)
            throw new InvalidDataException("Unreal type metadata exceeds the maximum depth.");
        var unrealType = ReadString(reader);
        var elementSize = reader.ReadInt32();
        var referencedTypePath = ReadNullableString(reader);
        var innerType = ReadNullableType(reader, depth + 1);
        var keyType = ReadNullableType(reader, depth + 1);
        var valueType = ReadNullableType(reader, depth + 1);
        var underlyingType = ReadNullableType(reader, depth + 1);
        BooleanLayoutSnapshot? booleanLayout = null;
        if (reader.ReadBoolean())
        {
            booleanLayout = new BooleanLayoutSnapshot
            {
                FieldSize = reader.ReadByte(),
                ByteOffset = reader.ReadByte(),
                ByteMask = reader.ReadByte(),
                FieldMask = reader.ReadByte()
            };
        }
        return new UnrealTypeSnapshot
        {
            UnrealType = unrealType,
            ElementSize = elementSize,
            ReferencedTypePath = referencedTypePath,
            InnerType = innerType,
            KeyType = keyType,
            ValueType = valueType,
            UnderlyingType = underlyingType,
            BooleanLayout = booleanLayout
        };
    }

    private static void WriteFunction(BinaryWriter writer, FunctionSnapshot function)
    {
        WriteString(writer, function.Name);
        writer.Write(function.Flags);
        writer.Write(function.ParameterSize);
        writer.Write(function.ParameterCount);
        WriteList(writer, function.Parameters, WriteProperty);
    }

    private static FunctionSnapshot ReadFunction(BinaryReader reader) => new()
    {
        Name = ReadString(reader),
        Flags = reader.ReadUInt32(),
        ParameterSize = reader.ReadInt32(),
        ParameterCount = reader.ReadInt32(),
        Parameters = ReadList(reader, MaximumMembers, "parameters", ReadProperty)
    };

    private static void WriteEnumValue(BinaryWriter writer, EnumValueSnapshot value)
    {
        WriteString(writer, value.Name);
        writer.Write(value.Value);
    }

    private static EnumValueSnapshot ReadEnumValue(BinaryReader reader) => new()
    {
        Name = ReadString(reader),
        Value = reader.ReadInt64()
    };

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null) WriteString(writer, value);
    }

    private static string? ReadNullableString(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadString(reader) : null;

    private static void WriteString(BinaryWriter writer, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
            throw new InvalidDataException("A snapshot string exceeds the maximum length.");
        Write7BitEncodedInt(writer, bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = Read7BitEncodedInt(reader);
        if (length > MaximumStringBytes)
            throw new InvalidDataException("A snapshot string exceeds the maximum length.");
        return StrictUtf8.GetString(ReadExactly(reader, length));
    }

    private static void WriteList<T>(
        BinaryWriter writer, IReadOnlyCollection<T>? values, Action<BinaryWriter, T> write)
    {
        // Schema-2 reference snapshots sometimes represented an unavailable
        // collection as JSON null. The compact contract canonicalizes it to an
        // empty collection so its reader never has to propagate null lists.
        if (values is null)
        {
            writer.Write(0);
            return;
        }
        writer.Write(values.Count);
        foreach (var value in values) write(writer, value);
    }

    private static List<T> ReadList<T>(
        BinaryReader reader, int maximum, string label, Func<BinaryReader, T> read)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Invalid {label} count {count}.");
        var values = new List<T>(count);
        for (var index = 0; index < count; ++index) values.Add(read(reader));
        return values;
    }

    private static void Write7BitEncodedInt(BinaryWriter writer, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            writer.Write((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        writer.Write((byte)remaining);
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var current = reader.ReadByte();
            if (shift == 28 && (current & 0xF0) != 0)
                throw new InvalidDataException("Invalid 7-bit encoded string length.");
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0) return checked((int)value);
        }
        throw new InvalidDataException("Invalid 7-bit encoded string length.");
    }

    private static byte[] ReadExactly(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return bytes;
    }
}

public static class SnapshotReader
{
    public static SdkSnapshot Read(string path) => BriefcaseSnapshotSerializer.Read(path);
}
