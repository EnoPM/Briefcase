using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

/// <summary>
/// Decodes Briefcase Value Copy v1 (BVC1). The format is recursive and contains
/// only values copied from Unreal: lengths, UTF-16 characters, validated object
/// handles and canonical struct bytes. Native addresses are never represented.
/// </summary>
internal static class UnrealValueWire
{
    private const uint Magic = 0x31435642;
    private const int MaximumDepth = 8;

    public static T Decode<T>(byte[] bytes)
    {
        return (T)Decode(typeof(T), bytes)!;
    }

    internal static object? Decode(Type expectedType, byte[] bytes)
    {
        var reader = new Reader(bytes);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("The Unreal value copy has an invalid BVC1 header.");
        var value = Decode(expectedType, ref reader, 0);
        if (!reader.IsEmpty)
            throw new InvalidDataException("The Unreal value copy contains trailing data.");
        return value;
    }

    private static object? Decode(Type expected, ref Reader reader, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("The Unreal value copy exceeds the nesting limit.");
        var kind = (UnrealPropertyKind)reader.ReadUInt32();
        var payload = reader.ReadSlice(checked((int)reader.ReadUInt32()));
        object? result;

        if (kind is UnrealPropertyKind.Array or UnrealPropertyKind.Set)
        {
            var isByteArray = expected == typeof(byte[]);
            var definition = expected.IsGenericType ? expected.GetGenericTypeDefinition() : null;
            if (!isByteArray && definition != (kind == UnrealPropertyKind.Array
                    ? typeof(UnrealArray<>) : typeof(UnrealSet<>)))
                throw Mismatch(expected, kind);
            var elementType = isByteArray ? typeof(byte) : expected.GetGenericArguments()[0];
            var count = CheckedCount(payload.ReadUInt32());
            var values = Array.CreateInstance(elementType, count);
            for (var index = 0; index < count; index++)
                values.SetValue(Decode(elementType, ref payload, depth + 1), index);
            if (!payload.IsEmpty) throw new InvalidDataException("Container payload has trailing data.");
            if (isByteArray)
            {
                var bytes = new byte[count];
                Array.Copy(values, bytes, count);
                result = bytes;
            }
            else
            {
                var constructor = expected.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic, null, [values.GetType()], null)
                    ?? throw new MissingMethodException(expected.FullName, ".ctor");
                result = constructor.Invoke([values]);
            }
        }
        else if (kind == UnrealPropertyKind.Map)
        {
            if (!expected.IsGenericType || expected.GetGenericTypeDefinition() != typeof(UnrealMap<,>))
                throw Mismatch(expected, kind);
            var arguments = expected.GetGenericArguments();
            var entryType = typeof(UnrealMapEntry<,>).MakeGenericType(arguments);
            var count = CheckedCount(payload.ReadUInt32());
            var entries = Array.CreateInstance(entryType, count);
            for (var index = 0; index < count; index++)
            {
                var key = Decode(arguments[0], ref payload, depth + 1);
                var value = Decode(arguments[1], ref payload, depth + 1);
                entries.SetValue(Activator.CreateInstance(entryType, key, value), index);
            }
            if (!payload.IsEmpty) throw new InvalidDataException("Map payload has trailing data.");
            var constructor = expected.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, [entries.GetType()], null)
                ?? throw new MissingMethodException(expected.FullName, ".ctor");
            result = constructor.Invoke([entries]);
        }
        else
        {
            result = DecodeLeaf(expected, kind, ref payload);
            if (!payload.IsEmpty) throw new InvalidDataException("Value payload has trailing data.");
        }
        return result;
    }

    private static object DecodeLeaf(
        Type expected, UnrealPropertyKind kind, ref Reader payload)
    {
        if (kind == UnrealPropertyKind.String && expected == typeof(string))
            return payload.ReadString();
        if (kind == UnrealPropertyKind.Text && expected == typeof(UnrealText))
            return new UnrealText(payload.ReadString());
        if (kind == UnrealPropertyKind.Bool && expected == typeof(bool))
            return payload.ReadByte() != 0;
        if (kind == UnrealPropertyKind.Object && expected == typeof(UnrealObjectReference))
            return ReadObjectReference(ref payload);
        if (kind == UnrealPropertyKind.Name && expected == typeof(UnrealName))
            return ReadName(ref payload);
        if (kind == UnrealPropertyKind.Interface && expected == typeof(UnrealInterfaceReference))
            return new UnrealInterfaceReference(ReadObjectReference(ref payload));
        if (kind == UnrealPropertyKind.LazyObject &&
            expected == typeof(UnrealLazyObjectReference))
            return new UnrealLazyObjectReference(
                ReadObjectReference(ref payload),
                new UnrealGuid(
                    payload.ReadUInt32(), payload.ReadUInt32(),
                    payload.ReadUInt32(), payload.ReadUInt32()));
        if (kind == UnrealPropertyKind.SoftObject &&
            expected == typeof(UnrealSoftObjectReference))
            return new UnrealSoftObjectReference(
                ReadObjectReference(ref payload), payload.ReadString(), payload.ReadString());
        if (kind == UnrealPropertyKind.SoftClass &&
            expected == typeof(UnrealSoftClassReference))
            return new UnrealSoftClassReference(
                ReadObjectReference(ref payload), payload.ReadString(), payload.ReadString());
        if (kind == UnrealPropertyKind.Delegate && expected == typeof(UnrealDelegate))
            return ReadDelegate(ref payload);
        if (kind == UnrealPropertyKind.MulticastDelegate &&
            expected == typeof(UnrealMulticastDelegate))
        {
            var count = CheckedCount(payload.ReadUInt32());
            var bindings = new UnrealDelegate[count];
            for (var index = 0; index < count; index++)
                bindings[index] = ReadDelegate(ref payload);
            return new UnrealMulticastDelegate(bindings);
        }
        if (kind == UnrealPropertyKind.FieldPath && expected == typeof(UnrealFieldPath))
        {
            var count = CheckedCount(payload.ReadUInt32());
            var segments = new UnrealName[count];
            for (var index = 0; index < count; index++)
                segments[index] = ReadName(ref payload);
            return new UnrealFieldPath(segments);
        }

        var scalar = kind switch
        {
            UnrealPropertyKind.Int8 when expected == typeof(sbyte) => (object)(sbyte)payload.ReadByte(),
            UnrealPropertyKind.Byte when expected == typeof(byte) => payload.ReadByte(),
            UnrealPropertyKind.Int16 when expected == typeof(short) => payload.ReadInt16(),
            UnrealPropertyKind.UInt16 when expected == typeof(ushort) => payload.ReadUInt16(),
            UnrealPropertyKind.Int32 when expected == typeof(int) => payload.ReadInt32(),
            UnrealPropertyKind.UInt32 when expected == typeof(uint) => payload.ReadUInt32(),
            UnrealPropertyKind.Int64 when expected == typeof(long) => payload.ReadInt64(),
            UnrealPropertyKind.UInt64 when expected == typeof(ulong) => payload.ReadUInt64(),
            UnrealPropertyKind.Float when expected == typeof(float) =>
                BitConverter.Int32BitsToSingle(payload.ReadInt32()),
            UnrealPropertyKind.Double when expected == typeof(double) =>
                BitConverter.Int64BitsToDouble(payload.ReadInt64()),
            _ => null
        };
        if (scalar is not null) return scalar;

        if (kind == UnrealPropertyKind.Struct && expected.IsValueType &&
            typeof(IUnrealStructValue).IsAssignableFrom(expected))
        {
            var size = checked((int)payload.ReadUInt32());
            var bytes = payload.ReadBytes(size);
            if (Marshal.SizeOf(expected) != size)
                throw new InvalidDataException(
                    $"Unreal struct {expected.FullName} has an unexpected size {size}.");
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure(handle.AddrOfPinnedObject(), expected)
                       ?? throw new InvalidDataException(
                           $"Could not decode Unreal struct {expected.FullName}.");
            }
            finally { handle.Free(); }
        }
        throw Mismatch(expected, kind);
    }

    private static UnrealObjectReference ReadObjectReference(ref Reader payload) =>
        new(new UnrealObjectHandle(payload.ReadUInt32(), payload.ReadUInt32()));

    private static UnrealName ReadName(ref Reader payload) =>
        new(unchecked((int)payload.ReadUInt32()), unchecked((int)payload.ReadUInt32()));

    private static UnrealDelegate ReadDelegate(ref Reader payload) =>
        new(ReadObjectReference(ref payload), ReadName(ref payload));

    private static int CheckedCount(uint count) => count <= 100_000
        ? checked((int)count)
        : throw new InvalidDataException($"Unreal container count {count} exceeds the limit.");

    private static InvalidDataException Mismatch(Type expected, UnrealPropertyKind actual) =>
        new($"The Unreal value kind {actual} cannot be decoded as {expected.FullName}.");

    private ref struct Reader
    {
        private ReadOnlySpan<byte> _remaining;
        public Reader(ReadOnlySpan<byte> bytes) => _remaining = bytes;
        public bool IsEmpty => _remaining.IsEmpty;
        public byte ReadByte() => ReadBytes(1)[0];
        public short ReadInt16() => BinaryPrimitives.ReadInt16LittleEndian(ReadBytes(2));
        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));
        public int ReadInt32() => BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4));
        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
        public long ReadInt64() => BinaryPrimitives.ReadInt64LittleEndian(ReadBytes(8));
        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));
        public string ReadString()
        {
            var characters = checked((int)ReadUInt32());
            var bytes = ReadBytes(checked(characters * sizeof(char)));
            return System.Text.Encoding.Unicode.GetString(bytes);
        }
        public byte[] ReadBytes(int count)
        {
            if (count < 0 || count > _remaining.Length)
                throw new EndOfStreamException("The Unreal value copy is truncated.");
            var result = _remaining[..count].ToArray();
            _remaining = _remaining[count..];
            return result;
        }
        public Reader ReadSlice(int count) => new(ReadBytes(count));
    }
}
