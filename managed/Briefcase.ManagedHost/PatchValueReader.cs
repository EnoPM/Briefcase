using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.ManagedHost;

/// <summary>
/// Copies one reflected ProcessEvent parameter into an address-free managed
/// value. Attributed patches and delegate subscriptions intentionally share
/// this single decoder so they support exactly the same Unreal types.
/// </summary>
internal static unsafe class PatchValueReader
{
    public static object Read(
        NativePatchingApi* api,
        UnrealApi unreal,
        NativePatchCall* call,
        UnrealParameter parameter)
    {
        _ = unreal; // Reserved for generated UObject wrappers in a future ABI.
        var bytes = ParameterSpan(call, parameter);
        if (parameter.ManagedType == typeof(byte[]) && bytes.Length == 0x10)
            return CopyByteArray(api, call, parameter.Offset);
        if (parameter.ManagedType == typeof(string) && bytes.Length == 0x10)
            return CopyString(api, call, parameter.Offset);
        if (parameter.ManagedType == typeof(UnrealText) && bytes.Length == 0x18)
            return new UnrealText(CopyText(api, call, parameter.Offset));
        if (RequiresPointerFreeCopy(parameter.ManagedType))
            return CopyValue(api, call, parameter);
        if (parameter.ManagedType == typeof(sbyte) && bytes.Length == 1) return (sbyte)bytes[0];
        if (parameter.ManagedType == typeof(byte) && bytes.Length == 1) return bytes[0];
        if (parameter.ManagedType == typeof(short)) return BinaryPrimitives.ReadInt16LittleEndian(bytes);
        if (parameter.ManagedType == typeof(ushort)) return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (parameter.ManagedType == typeof(int)) return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (parameter.ManagedType == typeof(uint)) return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (parameter.ManagedType == typeof(long)) return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (parameter.ManagedType == typeof(ulong)) return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (parameter.ManagedType == typeof(float))
            return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
        if (parameter.ManagedType == typeof(double))
            return BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes));
        if (parameter.ManagedType == typeof(bool) && bytes.Length == 1) return bytes[0] != 0;
        if (parameter.ManagedType.IsValueType &&
            Marshal.SizeOf(parameter.ManagedType) == bytes.Length)
        {
            fixed (byte* pointer = bytes)
                return Marshal.PtrToStructure((nint)pointer, parameter.ManagedType)
                       ?? throw new InvalidOperationException(
                           $"Could not marshal {parameter.ManagedType.FullName}.");
        }
        throw new NotSupportedException(
            $"Unreal parameter {parameter.Name} of type {parameter.ManagedType.FullName} is unsupported.");
    }

    private static object CopyValue(
        NativePatchingApi* api, NativePatchCall* call, UnrealParameter parameter)
    {
        EnsureCopyApi(api, api == null ? null : (void*)api->CopyValue,
            "pointer-free Unreal values");
        var kind = PropertyKind(parameter.ManagedType);
        uint required = 0;
        var status = api->CopyValue(
            api->Context, call, checked((uint)parameter.Offset), kind,
            null, 0, &required);
        if (status != NativeUnrealResult.BufferTooSmall ||
            required is < 12 or > 32u * 1024u * 1024u)
            throw new InvalidOperationException(
                $"Could not size Unreal value {parameter.Name}: {status}.");
        var bytes = new byte[required];
        fixed (byte* destination = bytes)
            status = api->CopyValue(
                api->Context, call, checked((uint)parameter.Offset), kind,
                destination, checked((uint)bytes.Length), &required);
        if (status != NativeUnrealResult.Ok || required != bytes.Length)
            throw new InvalidOperationException($"Could not copy Unreal value: {status}.");
        return UnrealValueWire.Decode(parameter.ManagedType, bytes)
               ?? throw new InvalidOperationException("The copied Unreal value was null.");
    }

    private static byte[] CopyByteArray(
        NativePatchingApi* api, NativePatchCall* call, int offset)
    {
        EnsureCopyApi(api, api == null ? null : (void*)api->CopyByteArray, "Unreal byte arrays");
        uint required = 0;
        var status = api->CopyByteArray(
            api->Context, call, checked((uint)offset), null, 0, &required);
        if (status == NativeUnrealResult.Ok && required == 0) return [];
        if (status != NativeUnrealResult.BufferTooSmall || required > 32u * 1024u * 1024u)
            throw new InvalidOperationException($"Could not size Unreal byte array: {status}.");
        var result = new byte[required];
        fixed (byte* destination = result)
            status = api->CopyByteArray(
                api->Context, call, checked((uint)offset), destination,
                checked((uint)result.Length), &required);
        if (status != NativeUnrealResult.Ok || required != result.Length)
            throw new InvalidOperationException($"Could not copy Unreal byte array: {status}.");
        return result;
    }

    private static string CopyString(
        NativePatchingApi* api, NativePatchCall* call, int offset)
    {
        EnsureCopyApi(api, api == null ? null : (void*)api->CopyString, "Unreal strings");
        uint required = 0;
        var status = api->CopyString(
            api->Context, call, checked((uint)offset), null, 0, &required);
        if (status == NativeUnrealResult.Ok && required == 0) return string.Empty;
        if (status != NativeUnrealResult.BufferTooSmall || required > 1024u * 1024u)
            throw new InvalidOperationException($"Could not size Unreal string: {status}.");
        return CopyCharacters(api, call, offset, required, text: false);
    }

    private static string CopyText(
        NativePatchingApi* api, NativePatchCall* call, int offset)
    {
        EnsureCopyApi(api, api == null ? null : (void*)api->CopyText, "Unreal FText values");
        uint required = 0;
        var status = api->CopyText(
            api->Context, call, checked((uint)offset), null, 0, &required);
        if (status == NativeUnrealResult.Ok && required == 0) return string.Empty;
        if (status != NativeUnrealResult.BufferTooSmall || required > 64u * 1024u)
            throw new InvalidOperationException($"Could not size Unreal FText: {status}.");
        return CopyCharacters(api, call, offset, required, text: true);
    }

    private static string CopyCharacters(
        NativePatchingApi* api, NativePatchCall* call, int offset,
        uint required, bool text)
    {
        var characters = new char[required];
        NativeUnrealResult status;
        fixed (char* destination = characters)
            status = text
                ? api->CopyText(api->Context, call, checked((uint)offset), destination,
                    checked((uint)characters.Length), &required)
                : api->CopyString(api->Context, call, checked((uint)offset), destination,
                    checked((uint)characters.Length), &required);
        if (status != NativeUnrealResult.Ok || required != characters.Length)
            throw new InvalidOperationException($"Could not copy Unreal text: {status}.");
        var length = characters.Length > 0 && characters[^1] == '\0'
            ? characters.Length - 1 : characters.Length;
        return new string(characters, 0, length);
    }

    private static void EnsureCopyApi(
        NativePatchingApi* api, void* function, string value)
    {
        if (api == null || api->ApiVersion < BriefcaseAbi.PatchingApiVersion || function == null)
            throw new NotSupportedException($"The host cannot copy {value}.");
    }

    private static bool RequiresPointerFreeCopy(Type type) =>
        type == typeof(UnrealObjectReference) ||
        typeof(IUnrealStructValue).IsAssignableFrom(type) ||
        typeof(IUnrealManagedStructValue).IsAssignableFrom(type) ||
        type == typeof(byte[]) ||
        type.IsGenericType && type.GetGenericTypeDefinition() is var definition &&
        (definition == typeof(UnrealArray<>) || definition == typeof(UnrealSet<>) ||
         definition == typeof(UnrealMap<,>));

    private static UnrealPropertyKind PropertyKind(Type type)
    {
        if (type == typeof(UnrealObjectReference)) return UnrealPropertyKind.Object;
        if (typeof(IUnrealStructValue).IsAssignableFrom(type) ||
            typeof(IUnrealManagedStructValue).IsAssignableFrom(type))
            return UnrealPropertyKind.Struct;
        if (type == typeof(byte[])) return UnrealPropertyKind.Array;
        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(UnrealArray<>)) return UnrealPropertyKind.Array;
            if (definition == typeof(UnrealSet<>)) return UnrealPropertyKind.Set;
            if (definition == typeof(UnrealMap<,>)) return UnrealPropertyKind.Map;
        }
        throw new NotSupportedException($"{type.FullName} has no pointer-free Unreal value kind.");
    }

    private static Span<byte> ParameterSpan(
        NativePatchCall* call, UnrealParameter parameter)
    {
        if (call == null || call->StructSize < (uint)sizeof(NativePatchCall) ||
            parameter.Offset < 0 || parameter.Size <= 0 ||
            (uint)parameter.Size > call->ParameterSize ||
            parameter.Offset > (int)call->ParameterSize - parameter.Size ||
            call->Parameters == null)
            throw new InvalidOperationException(
                $"Parameter {parameter.Name} is outside the runtime buffer.");
        return new Span<byte>((byte*)call->Parameters + parameter.Offset, parameter.Size);
    }
}
