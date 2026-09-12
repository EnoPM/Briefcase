using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

/// <summary>
/// Encodes and decodes Briefcase Value Copy v1 (BVC1). The format is recursive and contains
/// only values copied from Unreal: lengths, UTF-16 characters, validated object
/// handles and canonical struct bytes. Native addresses are never represented.
/// </summary>
internal static class UnrealValueWire
{
    private const uint Magic = 0x31435642;
    private const uint OutputsMagic = 0x314f5642;
    private const uint RecursiveStructMarker = uint.MaxValue;
    private const int MaximumDepth = 8;
    private const int MaximumElements = 100_000;
    private const int MaximumBytes = 32 * 1024 * 1024;

    internal static bool RequiresEncodedConstruction(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (typeof(IUnrealManagedStructValue).IsAssignableFrom(type) ||
            type == typeof(byte[]) || type == typeof(string) ||
            type == typeof(UnrealText)) return true;
        if (!type.IsGenericType) return false;
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(UnrealArray<>) ||
               definition == typeof(UnrealSet<>) ||
               definition == typeof(UnrealMap<,>);
    }

    /// <summary>
    /// Encodes one managed Unreal snapshot as BVC1. This is the exact inverse
    /// of <see cref="Decode(Type, byte[])"/>: every pointer-bearing value is
    /// represented by copied text, child nodes, or serial-checked handles.
    /// </summary>
    internal static byte[] Encode(Type declaredType, object value)
    {
        ArgumentNullException.ThrowIfNull(declaredType);
        ArgumentNullException.ThrowIfNull(value);
        if (value.GetType() != declaredType)
            throw new ArgumentException(
                $"Value type {value.GetType().FullName} does not match {declaredType.FullName}.",
                nameof(value));

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.Unicode, leaveOpen: true);
        writer.Write(Magic);
        WriteNode(writer, declaredType, value, 0);
        if (output.Length > MaximumBytes)
            throw new InvalidDataException("The Unreal value exceeds the 32 MiB BVC1 limit.");
        return output.ToArray();
    }

    private static void WriteNode(BinaryWriter output, Type declaredType, object value, int depth)
    {
        if (depth > MaximumDepth)
            throw new InvalidDataException("The Unreal value exceeds the nesting limit.");
        using var payloadStream = new MemoryStream();
        using var payload = new BinaryWriter(
            payloadStream, System.Text.Encoding.Unicode, leaveOpen: true);
        var kind = WritePayload(payload, declaredType, value, depth);
        if (payloadStream.Length > MaximumBytes)
            throw new InvalidDataException("An Unreal value node exceeds the 32 MiB limit.");
        output.Write((uint)kind);
        output.Write(checked((uint)payloadStream.Length));
        payloadStream.Position = 0;
        payloadStream.CopyTo(output.BaseStream);
        if (output.BaseStream.Length > MaximumBytes)
            throw new InvalidDataException("The Unreal value exceeds the 32 MiB BVC1 limit.");
    }

    private static UnrealPropertyKind WritePayload(
        BinaryWriter payload, Type type, object value, int depth)
    {
        if (type == typeof(string))
        {
            WriteString(payload, (string)value);
            return UnrealPropertyKind.String;
        }
        if (type == typeof(UnrealText))
        {
            WriteString(payload, ((UnrealText)value).Value ?? string.Empty);
            return UnrealPropertyKind.Text;
        }
        if (type == typeof(bool))
        {
            payload.Write((byte)((bool)value ? 1 : 0));
            return UnrealPropertyKind.Bool;
        }
        if (type == typeof(sbyte)) { payload.Write((sbyte)value); return UnrealPropertyKind.Int8; }
        if (type == typeof(byte)) { payload.Write((byte)value); return UnrealPropertyKind.Byte; }
        if (type == typeof(short)) { payload.Write((short)value); return UnrealPropertyKind.Int16; }
        if (type == typeof(ushort)) { payload.Write((ushort)value); return UnrealPropertyKind.UInt16; }
        if (type == typeof(int)) { payload.Write((int)value); return UnrealPropertyKind.Int32; }
        if (type == typeof(uint)) { payload.Write((uint)value); return UnrealPropertyKind.UInt32; }
        if (type == typeof(long)) { payload.Write((long)value); return UnrealPropertyKind.Int64; }
        if (type == typeof(ulong)) { payload.Write((ulong)value); return UnrealPropertyKind.UInt64; }
        if (type == typeof(float)) { payload.Write((float)value); return UnrealPropertyKind.Float; }
        if (type == typeof(double)) { payload.Write((double)value); return UnrealPropertyKind.Double; }
        if (type == typeof(UnrealName))
        {
            var name = (UnrealName)value;
            payload.Write(unchecked((uint)name.ComparisonIndex));
            payload.Write(unchecked((uint)name.Number));
            return UnrealPropertyKind.Name;
        }
        if (type == typeof(UnrealObjectReference))
        {
            WriteObjectReference(payload, (UnrealObjectReference)value);
            return UnrealPropertyKind.Object;
        }
        if (type == typeof(UnrealInterfaceReference))
        {
            WriteObjectReference(payload, ((UnrealInterfaceReference)value).Object);
            return UnrealPropertyKind.Interface;
        }
        if (type == typeof(UnrealLazyObjectReference))
        {
            var lazy = (UnrealLazyObjectReference)value;
            WriteObjectReference(payload, lazy.Object);
            payload.Write(lazy.Id.A); payload.Write(lazy.Id.B);
            payload.Write(lazy.Id.C); payload.Write(lazy.Id.D);
            return UnrealPropertyKind.LazyObject;
        }
        if (type == typeof(UnrealSoftObjectReference) ||
            type == typeof(UnrealSoftClassReference))
        {
            var (objectReference, assetPath, subPath) = value switch
            {
                UnrealSoftObjectReference soft => (soft.Object, soft.AssetPath, soft.SubPath),
                UnrealSoftClassReference soft => (soft.Object, soft.AssetPath, soft.SubPath),
                _ => throw new InvalidOperationException("Unexpected soft-reference value.")
            };
            WriteObjectReference(payload, objectReference);
            WriteString(payload, assetPath);
            WriteString(payload, subPath);
            return type == typeof(UnrealSoftObjectReference)
                ? UnrealPropertyKind.SoftObject : UnrealPropertyKind.SoftClass;
        }
        if (type == typeof(UnrealDelegate))
        {
            WriteDelegate(payload, (UnrealDelegate)value);
            return UnrealPropertyKind.Delegate;
        }
        if (type == typeof(UnrealMulticastDelegate))
        {
            var bindings = (UnrealMulticastDelegate)value;
            WriteCount(payload, bindings.Count);
            foreach (var binding in bindings) WriteDelegate(payload, binding);
            return UnrealPropertyKind.MulticastDelegate;
        }
        if (type == typeof(UnrealFieldPath))
        {
            var path = (UnrealFieldPath)value;
            WriteCount(payload, path.Count);
            foreach (var segment in path)
            {
                payload.Write(unchecked((uint)segment.ComparisonIndex));
                payload.Write(unchecked((uint)segment.Number));
            }
            return UnrealPropertyKind.FieldPath;
        }
        if (type == typeof(byte[]))
        {
            var values = (byte[])value;
            WriteCount(payload, values.Length);
            foreach (var item in values) WriteNode(payload, typeof(byte), item, depth + 1);
            return UnrealPropertyKind.Array;
        }
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(UnrealArray<>) || definition == typeof(UnrealSet<>))
            {
                var elementType = type.GetGenericArguments()[0];
                var values = ((IEnumerable)value).Cast<object?>().ToArray();
                WriteCount(payload, values.Length);
                foreach (var item in values)
                    WriteNode(payload, elementType, item ?? throw NullElement(type), depth + 1);
                return definition == typeof(UnrealArray<>)
                    ? UnrealPropertyKind.Array : UnrealPropertyKind.Set;
            }
            if (definition == typeof(UnrealMap<,>))
            {
                var arguments = type.GetGenericArguments();
                var values = ((IEnumerable)value).Cast<object?>().ToArray();
                WriteCount(payload, values.Length);
                foreach (var item in values)
                {
                    if (item is null) throw NullElement(type);
                    var entryType = item.GetType();
                    WriteNode(payload, arguments[0],
                        entryType.GetProperty("Key")!.GetValue(item) ?? throw NullElement(type),
                        depth + 1);
                    WriteNode(payload, arguments[1],
                        entryType.GetProperty("Value")!.GetValue(item) ?? throw NullElement(type),
                        depth + 1);
                }
                return UnrealPropertyKind.Map;
            }
        }
        if (value is IUnrealStructValue fixedStructure)
        {
            if (fixedStructure.Size is <= 0 or > 65_535)
                throw new InvalidDataException($"Unreal struct {type.FullName} has an invalid size.");
            var bytes = new byte[fixedStructure.Size];
            fixedStructure.WriteTo(bytes);
            payload.Write(checked((uint)bytes.Length));
            payload.Write(bytes);
            return UnrealPropertyKind.Struct;
        }
        if (value is IUnrealManagedStructValue)
        {
            var nativeSize = type.GetField("NativeSize", BindingFlags.Public | BindingFlags.Static)?
                .GetRawConstantValue();
            if (nativeSize is not int size || size is <= 0 or > 65_535)
                throw new InvalidDataException(
                    $"Managed Unreal struct {type.FullName} has no valid NativeSize constant.");
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => (Field: field,
                    Identity: field.GetCustomAttribute<UnrealStructFieldAttribute>()))
                .Where(item => item.Identity is not null)
                .OrderBy(item => item.Identity!.Offset)
                .ThenBy(item => item.Identity!.Name, StringComparer.Ordinal)
                .ToArray();
            // The recursive marker distinguishes this field list from the raw
            // canonical bytes used by fixed-layout generated structs.
            payload.Write(RecursiveStructMarker);
            payload.Write(checked((uint)size));
            WriteCount(payload, fields.Length);
            foreach (var (field, identity) in fields)
            {
                var fieldValue = field.GetValue(value) ?? throw NullElement(field.FieldType);
                payload.Write(identity!.Offset);
                WriteString(payload, identity.Name);
                WriteNode(payload, field.FieldType, fieldValue, depth + 1);
            }
            return UnrealPropertyKind.Struct;
        }
        throw new NotSupportedException($"BVC1 encoding for {type.FullName} is not implemented.");
    }

    private static void WriteString(BinaryWriter output, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumElements || value.IndexOf('\0') >= 0)
            throw new InvalidDataException("An Unreal string is invalid or exceeds the BVC1 limit.");
        output.Write(checked((uint)value.Length));
        output.Write(System.Text.Encoding.Unicode.GetBytes(value));
    }

    private static void WriteCount(BinaryWriter output, int count)
    {
        if (count is < 0 or > MaximumElements)
            throw new InvalidDataException($"Unreal container count {count} exceeds the limit.");
        output.Write(checked((uint)count));
    }

    private static void WriteObjectReference(BinaryWriter output, UnrealObjectReference value)
    {
        output.Write(value.Handle.Index);
        output.Write(value.Handle.SerialNumber);
    }

    private static void WriteDelegate(BinaryWriter output, UnrealDelegate value)
    {
        WriteObjectReference(output, value.Target);
        output.Write(unchecked((uint)value.FunctionName.ComparisonIndex));
        output.Write(unchecked((uint)value.FunctionName.Number));
    }

    private static InvalidDataException NullElement(Type type) =>
        new($"Unreal value {type.FullName} contains a null field or element.");

    public static T Decode<T>(byte[] bytes)
    {
        return (T)Decode(typeof(T), bytes)!;
    }

    internal static IReadOnlyDictionary<int, object?> DecodeOutputs(
        UnrealFunction function, byte[] bytes)
    {
        var reader = new Reader(bytes);
        if (reader.ReadUInt32() != OutputsMagic)
            throw new InvalidDataException("The Unreal output copy has an invalid BVO1 header.");
        var parameters = function.Parameters
            .GroupBy(parameter => parameter.Offset)
            .ToDictionary(group => group.Key, group => group.First());
        var values = new Dictionary<int, object?>();
        var count = CheckedCount(reader.ReadUInt32());
        for (var index = 0; index < count; index++)
        {
            var offset = reader.ReadInt32();
            if (parameters.TryGetValue(offset, out var parameter))
                values[offset] = Decode(parameter.ManagedType, ref reader, 0);
            else
                reader.SkipNode();
        }
        if (!reader.IsEmpty)
            throw new InvalidDataException("The Unreal output copy contains trailing data.");
        return values;
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
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, [values.GetType()], null)
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
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, [entries.GetType()], null)
                ?? throw new MissingMethodException(expected.FullName, ".ctor");
            result = constructor.Invoke([entries]);
        }
        else if (kind == UnrealPropertyKind.Struct)
        {
            result = DecodeStruct(expected, ref payload, depth);
            if (!payload.IsEmpty) throw new InvalidDataException("Struct payload has trailing data.");
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

        throw Mismatch(expected, kind);
    }

    private static object DecodeStruct(Type expected, ref Reader payload, int depth)
    {
        if (!expected.IsValueType)
            throw Mismatch(expected, UnrealPropertyKind.Struct);
        var markerOrSize = payload.ReadUInt32();
        if (markerOrSize != RecursiveStructMarker)
        {
            if (!typeof(IUnrealStructValue).IsAssignableFrom(expected))
                throw Mismatch(expected, UnrealPropertyKind.Struct);
            var size = checked((int)markerOrSize);
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

        if (!typeof(IUnrealManagedStructValue).IsAssignableFrom(expected))
            throw Mismatch(expected, UnrealPropertyKind.Struct);
        var nativeSize = checked((int)payload.ReadUInt32());
        var declaredSize = expected.GetField("NativeSize", BindingFlags.Public | BindingFlags.Static)?
            .GetRawConstantValue();
        if (declaredSize is int expectedSize && expectedSize != nativeSize)
            throw new InvalidDataException(
                $"Unreal struct {expected.FullName} has native size {nativeSize}; expected {expectedSize}.");

        var fields = expected.GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Select(field => (Field: field, Identity: field.GetCustomAttribute<UnrealStructFieldAttribute>()))
            .Where(item => item.Identity is not null)
            .GroupBy(item => (item.Identity!.Name, item.Identity.Offset))
            .ToDictionary(group => group.Key, group => group.First().Field);
        object result = Activator.CreateInstance(expected)!;
        var count = CheckedCount(payload.ReadUInt32());
        for (var index = 0; index < count; index++)
        {
            var offset = payload.ReadInt32();
            var name = payload.ReadString();
            if (fields.TryGetValue((name, offset), out var field))
                field.SetValue(result, Decode(field.FieldType, ref payload, depth + 1));
            else
                payload.SkipNode();
        }
        return result;
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
        public void SkipNode()
        {
            _ = ReadUInt32();
            _ = ReadBytes(checked((int)ReadUInt32()));
        }
    }
}
