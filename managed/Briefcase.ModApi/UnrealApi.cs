using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

public sealed class UnrealApiException : Exception
{
    public NativeUnrealResult Result { get; }

    internal UnrealApiException(string operation, NativeUnrealResult result)
        : base($"{operation} failed: {result}.")
    {
        Result = result;
    }
}

public readonly record struct UnrealPropertyMetadata(
    UnrealPropertyKind Kind,
    int Offset,
    int ElementSize,
    int ArrayDimension,
    ulong Flags);

public readonly unsafe partial struct UnrealApi
{
    private const uint ApiVersion = 1;
    private const uint InvocationApiVersion = 2;
    private const uint StringPropertyApiVersion = 3;
    private const uint TextApiVersion = 5;
    private const uint NativeInvocationApiVersion = 6;
    private const int MaximumTextCharacters = 65_536;
    private const uint MaximumEnumeration = 100_000;
    private static readonly ConcurrentDictionary<string, UnrealObjectHandle> MetadataHandles =
        new(StringComparer.Ordinal);
    private readonly NativeUnrealApi* _api;

    internal UnrealApi(NativeUnrealApi* api) => _api = api;

    public bool IsAvailable =>
        _api != null && _api->ApiVersion >= ApiVersion &&
        _api->FindObject != null && _api->FindObjectsOfClass != null &&
        _api->GetObjectName != null && _api->GetObjectPath != null &&
        _api->GetPropertyInfo != null && _api->ReadProperty != null;

    public bool IsInvocationAvailable =>
        IsAvailable && _api->ApiVersion >= InvocationApiVersion &&
        _api->InvokeFunction != null;

    public bool IsNativeInvocationAvailable =>
        IsAvailable && _api->ApiVersion >= NativeInvocationApiVersion &&
        _api->InvokeNativeBoolean != null;

    /// <summary>
    /// Calls a build-specific native Unreal member with the signature
    /// <c>bool Method()</c>. The UObject pointer remains inside version.dll;
    /// callers provide a serial-checked handle and the exact expected PE build.
    /// This operation must be invoked from Unreal's game thread.
    /// </summary>
    public bool InvokeNativeBoolean(
        UnrealObject instance,
        GameBuild expectedBuild,
        ulong functionRva)
    {
        ArgumentNullException.ThrowIfNull(instance);
        EnsureAvailable();
        if (!IsNativeInvocationAvailable)
            throw new NotSupportedException(
                "The native boolean invocation API is unavailable.");
        if (instance.IsNull)
            throw new ArgumentException("The Unreal instance is null.", nameof(instance));
        if (functionRva == 0)
            throw new ArgumentOutOfRangeException(nameof(functionRva));

        uint result = 0;
        var nativeBuild = new NativeGameBuild
        {
            PeTimestamp = expectedBuild.PeTimestamp,
            ImageSize = expectedBuild.ImageSize
        };
        var status = _api->InvokeNativeBoolean(
            _api->Context, instance.Handle, nativeBuild, functionRva, &result);
        EnsureSuccess("InvokeNativeBoolean", status);
        return result != 0;
    }

    public bool TryFindObject(string path, out UnrealObject? result)
    {
        EnsureAvailable();
        var encoded = Encode(path);
        UnrealObjectHandle handle;
        fixed (byte* pathPointer = encoded)
        {
            var status = _api->FindObject(_api->Context, pathPointer,
                checked((uint)encoded.Length), &handle);
            if (status == NativeUnrealResult.NotFound)
            {
                result = null;
                return false;
            }
            EnsureSuccess("FindObject", status);
        }
        result = new UnrealObject(this, handle);
        return true;
    }

    public UnrealObject FindObject(string path) =>
        TryFindObject(path, out var result)
            ? result!
            : throw new UnrealApiException("FindObject", NativeUnrealResult.NotFound);

    public IReadOnlyList<T> FindObjects<T>(UnrealClass<T> unrealClass)
        where T : UnrealObject, IUnrealObject<T>
    {
        var classHandle = FindMetadata(unrealClass.Path);
        uint written = 0;
        uint total = 0;
        var status = _api->FindObjectsOfClass(
            _api->Context, classHandle, null, 0, &written, &total);
        if (status is not (NativeUnrealResult.Ok or NativeUnrealResult.BufferTooSmall))
            EnsureSuccess("FindObjectsOfClass(count)", status);
        if (total == 0)
            return Array.Empty<T>();
        if (total > MaximumEnumeration)
            throw new InvalidOperationException(
                $"FindObjectsOfClass returned {total} entries; limit is {MaximumEnumeration}.");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var handles = new UnrealObjectHandle[checked((int)total)];
            fixed (UnrealObjectHandle* handlePointer = handles)
            {
                status = _api->FindObjectsOfClass(
                    _api->Context, classHandle, handlePointer,
                    checked((uint)handles.Length), &written, &total);
            }
            if (status == NativeUnrealResult.BufferTooSmall)
            {
                if (total > MaximumEnumeration)
                    throw new InvalidOperationException(
                        $"FindObjectsOfClass grew to {total} entries; limit is {MaximumEnumeration}.");
                continue;
            }
            EnsureSuccess("FindObjectsOfClass", status);
            var objects = new T[written];
            for (var index = 0; index < objects.Length; index++)
                objects[index] = T.FromObject(this, handles[index]);
            return objects;
        }
        throw new InvalidOperationException("The Unreal object registry changed during three enumeration attempts.");
    }

    public T FromReference<T>(UnrealObjectReference reference)
        where T : UnrealObject, IUnrealObject<T>
    {
        EnsureAvailable();
        if (reference.IsNull)
            throw new ArgumentException("The Unreal object reference is null.", nameof(reference));
        return T.FromObject(this, reference.Handle);
    }

    public UnrealObject FromReference(UnrealObjectReference reference)
    {
        EnsureAvailable();
        if (reference.IsNull)
            throw new ArgumentException("The Unreal object reference is null.", nameof(reference));
        return new UnrealObject(this, reference.Handle);
    }

    internal string GetName(UnrealObjectHandle handle) => GetText(handle, path: false);
    internal string GetPath(UnrealObjectHandle handle) => GetText(handle, path: true);

    internal UnrealObjectHandle GetClass(UnrealObjectHandle handle)
    {
        EnsureAvailable();
        UnrealObjectHandle result;
        EnsureSuccess("GetObjectClass",
            _api->GetObjectClass(_api->Context, handle, &result));
        return result;
    }

    internal bool IsA(UnrealObjectHandle handle, string classPath)
    {
        EnsureAvailable();
        var classHandle = FindMetadata(classPath);
        uint result;
        var status = _api->IsObjectA(_api->Context, handle, classHandle, &result);
        if (status == NativeUnrealResult.StaleHandle)
        {
            classHandle = RefreshMetadata(classPath);
            status = _api->IsObjectA(_api->Context, handle, classHandle, &result);
        }
        EnsureSuccess("IsObjectA", status);
        return result != 0;
    }

    public UnrealPropertyMetadata Inspect<T>(UnrealProperty<T> property)
    {
        EnsureAvailable();
        var ownerClass = FindMetadata(property.OwnerPath);
        var encoded = Encode(property.Name);
        NativePropertyInfo native = default;
        native.StructSize = checked((uint)sizeof(NativePropertyInfo));
        fixed (byte* namePointer = encoded)
        {
            EnsureSuccess("GetPropertyInfo",
                _api->GetPropertyInfo(_api->Context, ownerClass, namePointer,
                    checked((uint)encoded.Length), &native));
        }
        return new UnrealPropertyMetadata(
            native.Kind, native.Offset, native.ElementSize,
            native.ArrayDimension, native.Flags);
    }

    internal TValue Read<TValue>(
        UnrealObjectHandle objectHandle,
        UnrealProperty<TValue> property) where TValue : unmanaged
    {
        EnsureAvailable();
        var kind = KindOf<TValue>();
        var expectedManagedSize = Unsafe.SizeOf<TValue>();
        if (expectedManagedSize != property.Size)
            throw new InvalidOperationException(
                $"Managed type {typeof(TValue).Name} is {expectedManagedSize} bytes, " +
                $"but {property.OwnerPath}.{property.Name} declares {property.Size}.");

        var ownerClass = FindMetadata(property.OwnerPath);
        var encoded = Encode(property.Name);

        // Unreal stores a native/bitfield BoolProperty in one byte, which is
        // why generated layout metadata declares Size=1. The cross-language
        // ABI deliberately represents booleans as BriefcaseBool/uint32_t. Give the
        // native reader its four-byte ABI destination, then convert to the
        // one-byte CLR bool returned to generated C# callers.
        if (kind == UnrealPropertyKind.Bool)
        {
            uint nativeBoolean = 0;
            fixed (byte* namePointer = encoded)
            {
                var status = _api->ReadProperty(
                    _api->Context, objectHandle, ownerClass,
                    namePointer, checked((uint)encoded.Length),
                    property.Offset, property.Size, 1, kind,
                    &nativeBoolean, checked((uint)sizeof(uint)));
                if (status == NativeUnrealResult.StaleHandle)
                {
                    ownerClass = RefreshMetadata(property.OwnerPath);
                    status = _api->ReadProperty(
                        _api->Context, objectHandle, ownerClass,
                        namePointer, checked((uint)encoded.Length),
                        property.Offset, property.Size, 1, kind,
                        &nativeBoolean, checked((uint)sizeof(uint)));
                }
                EnsureSuccess($"ReadProperty({property.Name})", status);
            }
            var managedBoolean = nativeBoolean != 0;
            return Unsafe.As<bool, TValue>(ref managedBoolean);
        }

        TValue result = default;
        fixed (byte* namePointer = encoded)
        {
            var status = _api->ReadProperty(
                _api->Context, objectHandle, ownerClass,
                namePointer, checked((uint)encoded.Length),
                property.Offset, property.Size, 1, kind,
                &result, checked((uint)sizeof(TValue)));
            if (status == NativeUnrealResult.StaleHandle)
            {
                // UE assigns some UObject serial numbers lazily. A metadata
                // UClass cached while its serial is zero can therefore acquire
                // a new valid handle without being destroyed. Refresh only the
                // path-addressable class handle; an actually stale instance
                // still fails on the second call.
                ownerClass = RefreshMetadata(property.OwnerPath);
                status = _api->ReadProperty(
                    _api->Context, objectHandle, ownerClass,
                    namePointer, checked((uint)encoded.Length),
                    property.Offset, property.Size, 1, kind,
                    &result, checked((uint)sizeof(TValue)));
            }
            EnsureSuccess($"ReadProperty({property.Name})", status);
        }
        return result;
    }

    internal string ReadString(
        UnrealObjectHandle objectHandle,
        UnrealProperty<string> property)
    {
        EnsureAvailable();
        if (_api->ApiVersion < StringPropertyApiVersion || _api->ReadStringProperty == null)
            throw new InvalidOperationException(
                "The host does not expose bounded FString property reads.");

        var ownerClass = FindMetadata(property.OwnerPath);
        var encoded = Encode(property.Name);
        uint required = 0;
        NativeUnrealResult status;
        fixed (byte* namePointer = encoded)
        {
            status = _api->ReadStringProperty(
                _api->Context, objectHandle, ownerClass,
                namePointer, checked((uint)encoded.Length),
                property.Offset, property.Size, 1, null, 0, &required);
            if (status == NativeUnrealResult.StaleHandle)
            {
                ownerClass = RefreshMetadata(property.OwnerPath);
                status = _api->ReadStringProperty(
                    _api->Context, objectHandle, ownerClass,
                    namePointer, checked((uint)encoded.Length),
                    property.Offset, property.Size, 1, null, 0, &required);
            }
        }
        if (status == NativeUnrealResult.Ok && required == 0) return "";
        if (status != NativeUnrealResult.BufferTooSmall)
            EnsureSuccess($"ReadStringProperty({property.Name}, size)", status);
        if (required is 0 or > 65_536)
            throw new InvalidOperationException(
                $"Unreal returned an invalid FString length: {required}.");

        var characters = new char[required];
        fixed (byte* namePointer = encoded)
        fixed (char* destination = characters)
        {
            status = _api->ReadStringProperty(
                _api->Context, objectHandle, ownerClass,
                namePointer, checked((uint)encoded.Length),
                property.Offset, property.Size, 1,
                destination, checked((uint)characters.Length), &required);
        }
        EnsureSuccess($"ReadStringProperty({property.Name})", status);
        var length = checked((int)required);
        if (length > 0 && characters[length - 1] == '\0') length--;
        return new string(characters, 0, length);
    }

    internal UnrealText ReadText(
        UnrealObjectHandle objectHandle,
        UnrealProperty<UnrealText> property)
    {
        EnsureAvailable();
        if (_api->ApiVersion < TextApiVersion || _api->ReadTextProperty == null)
            throw new InvalidOperationException(
                "The host does not expose bounded FText property reads.");

        var ownerClass = FindMetadata(property.OwnerPath);
        var encoded = Encode(property.Name);
        uint required = 0;
        NativeUnrealResult status;
        fixed (byte* namePointer = encoded)
        {
            status = _api->ReadTextProperty(
                _api->Context, objectHandle, ownerClass,
                namePointer, checked((uint)encoded.Length),
                property.Offset, property.Size, 1, null, 0, &required);
            if (status == NativeUnrealResult.StaleHandle)
            {
                ownerClass = RefreshMetadata(property.OwnerPath);
                status = _api->ReadTextProperty(
                    _api->Context, objectHandle, ownerClass,
                    namePointer, checked((uint)encoded.Length),
                    property.Offset, property.Size, 1, null, 0, &required);
            }
        }
        if (status == NativeUnrealResult.Ok && required == 0) return new UnrealText("");
        if (status != NativeUnrealResult.BufferTooSmall)
            EnsureSuccess($"ReadTextProperty({property.Name}, size)", status);
        if (required is 0 or > MaximumTextCharacters)
            throw new InvalidOperationException(
                $"Unreal returned an invalid FText length: {required}.");

        var characters = new char[required];
        fixed (byte* namePointer = encoded)
        fixed (char* destination = characters)
        {
            status = _api->ReadTextProperty(
                _api->Context, objectHandle, ownerClass,
                namePointer, checked((uint)encoded.Length),
                property.Offset, property.Size, 1,
                destination, checked((uint)characters.Length), &required);
        }
        EnsureSuccess($"ReadTextProperty({property.Name})", status);
        var length = checked((int)required);
        if (length > 0 && characters[length - 1] == '\0') length--;
        return new UnrealText(new string(characters, 0, length));
    }

    internal void InvokeVoid(
        UnrealObjectHandle objectHandle,
        UnrealFunction function,
        IReadOnlyList<object?> arguments)
    {
        if (function.ReturnType is not null)
            throw new InvalidOperationException(
                $"{function.OwnerPath}.{function.Name} has a return value and cannot use InvokeVoid.");
        _ = InvokeBuffer(objectHandle, function, arguments);
    }

    internal TResult Invoke<TResult>(
        UnrealObjectHandle objectHandle,
        UnrealFunction function,
        IReadOnlyList<object?> arguments) where TResult : unmanaged
    {
        if (function.ReturnType != typeof(TResult) || function.ReturnParameter is not { } result)
            throw new InvalidOperationException(
                $"{function.OwnerPath}.{function.Name} does not return {typeof(TResult).Name}.");
        var invocation = InvokeBuffer(objectHandle, function, arguments);
        return ReadValue<TResult>(invocation.Buffer, result);
    }

    internal UnrealText InvokeText(
        UnrealObjectHandle objectHandle,
        UnrealFunction function,
        IReadOnlyList<object?> arguments)
    {
        if (function.ReturnType != typeof(UnrealText) ||
            function.ReturnParameter is not { } result)
            throw new InvalidOperationException(
                $"{function.OwnerPath}.{function.Name} does not return UnrealText.");
        var invocation = InvokeBuffer(objectHandle, function, arguments);
        if (!invocation.TextOutputs.TryGetValue(result.Offset, out var text))
            throw new InvalidOperationException(
                $"{function.OwnerPath}.{function.Name} did not return its FText value.");
        return new UnrealText(text);
    }

    private sealed record InvocationResult(
        byte[] Buffer, IReadOnlyDictionary<int, string> TextOutputs);

    private readonly record struct TextOutputAllocation(
        int ParameterOffset, nint Buffer, nint RequiredCharacters);

    private InvocationResult InvokeBuffer(
        UnrealObjectHandle objectHandle,
        UnrealFunction function,
        IReadOnlyList<object?> arguments)
    {
        if (!IsInvocationAvailable)
            throw new InvalidOperationException("The host does not expose Unreal invocation API v2.");
        if (function.ParameterBufferSize is < 0 or > 65_535)
            throw new InvalidOperationException(
                $"{function.OwnerPath}.{function.Name} has an invalid parameter buffer size.");
        var inputCount = function.Parameters.Count(parameter => !parameter.IsOut);
        if (inputCount != arguments.Count)
            throw new ArgumentException(
                $"{function.OwnerPath}.{function.Name} expects {inputCount} input arguments; " +
                $"received {arguments.Count}.", nameof(arguments));

        var buffer = new byte[function.ParameterBufferSize];
        var nativeAllocations = new List<nint>();
        var textArguments = new List<NativeTextArgument>();
        var textOutputs = new List<TextOutputAllocation>();
        try
        {
            var inputIndex = 0;
            foreach (var parameter in function.Parameters)
            {
                if (parameter.Offset < 0 || parameter.Size <= 0 ||
                    parameter.Offset > buffer.Length - parameter.Size)
                    throw new InvalidOperationException(
                        $"Generated layout for parameter {parameter.Name} is outside the parameter buffer.");
                if (parameter.IsOut) continue;
                WriteArgument(
                    buffer.AsSpan(parameter.Offset, parameter.Size),
                    parameter.ManagedType,
                    arguments[inputIndex++],
                    parameter.Name,
                    parameter.Offset,
                    nativeAllocations,
                    textArguments);
            }

            if (function.ReturnParameter is { } returnParameter &&
                (returnParameter.Offset < 0 || returnParameter.Size <= 0 ||
                 returnParameter.Offset > buffer.Length - returnParameter.Size))
                throw new InvalidOperationException(
                    $"Generated return layout for {function.Name} is outside the parameter buffer.");

            if (function.ReturnParameter is { ManagedType: var returnType } textReturn &&
                returnType == typeof(UnrealText))
            {
                var output = (char*)NativeMemory.AllocZeroed(
                    checked((nuint)(MaximumTextCharacters * sizeof(char))));
                var required = (uint*)NativeMemory.AllocZeroed((nuint)sizeof(uint));
                if (output == null || required == null)
                {
                    if (output != null) NativeMemory.Free(output);
                    if (required != null) NativeMemory.Free(required);
                    throw new OutOfMemoryException();
                }
                nativeAllocations.Add((nint)output);
                nativeAllocations.Add((nint)required);
                textArguments.Add(new NativeTextArgument
                {
                    StructSize = checked((uint)sizeof(NativeTextArgument)),
                    ParameterOffset = textReturn.Offset,
                    Flags = NativeTextArgumentFlags.Output,
                    Output = output,
                    RequiredCharacters = required,
                    OutputCapacityCharacters = MaximumTextCharacters
                });
                textOutputs.Add(new TextOutputAllocation(
                    textReturn.Offset, (nint)output, (nint)required));
            }

            var ownerClass = FindMetadata(function.OwnerPath);
            var encodedName = Encode(function.Name);
            var nativeTextArguments = textArguments.ToArray();
            fixed (byte* namePointer = encodedName)
            fixed (byte* parameterPointer = buffer)
            fixed (NativeTextArgument* textPointer = nativeTextArguments)
            {
                NativeUnrealResult status;
                if (nativeTextArguments.Length == 0)
                {
                    status = _api->InvokeFunction(
                        _api->Context, objectHandle, ownerClass, namePointer,
                        checked((uint)encodedName.Length),
                        checked((uint)function.ParameterBufferSize),
                        buffer.Length == 0 ? null : parameterPointer,
                        checked((uint)buffer.Length));
                }
                else
                {
                    if (_api->ApiVersion < TextApiVersion || _api->InvokeFunctionText == null)
                        throw new InvalidOperationException(
                            "The host does not expose owning FText invocation support.")
                        ;
                    status = _api->InvokeFunctionText(
                        _api->Context, objectHandle, ownerClass, namePointer,
                        checked((uint)encodedName.Length),
                        checked((uint)function.ParameterBufferSize),
                        buffer.Length == 0 ? null : parameterPointer,
                        checked((uint)buffer.Length), textPointer,
                        checked((uint)nativeTextArguments.Length));
                }
                if (status == NativeUnrealResult.StaleHandle)
                {
                    ownerClass = RefreshMetadata(function.OwnerPath);
                    status = nativeTextArguments.Length == 0
                        ? _api->InvokeFunction(
                            _api->Context, objectHandle, ownerClass, namePointer,
                            checked((uint)encodedName.Length),
                            checked((uint)function.ParameterBufferSize),
                            buffer.Length == 0 ? null : parameterPointer,
                            checked((uint)buffer.Length))
                        : _api->InvokeFunctionText(
                            _api->Context, objectHandle, ownerClass, namePointer,
                            checked((uint)encodedName.Length),
                            checked((uint)function.ParameterBufferSize),
                            buffer.Length == 0 ? null : parameterPointer,
                            checked((uint)buffer.Length), textPointer,
                            checked((uint)nativeTextArguments.Length));
                }
                EnsureSuccess($"InvokeFunction({function.Name})", status);
            }

            var returnedTexts = new Dictionary<int, string>();
            foreach (var output in textOutputs)
            {
                var required = *(uint*)output.RequiredCharacters;
                if (required > MaximumTextCharacters)
                    throw new InvalidOperationException(
                        $"Unreal returned an invalid FText length: {required}.");
                var length = checked((int)required);
                var characters = (char*)output.Buffer;
                if (length > 0 && characters[length - 1] == '\0') length--;
                returnedTexts.Add(
                    output.ParameterOffset,
                    length == 0 ? "" : new string(characters, 0, length));
            }
            return new InvocationResult(buffer, returnedTexts);
        }
        finally
        {
            foreach (var allocation in nativeAllocations)
                NativeMemory.Free((void*)allocation);
        }
    }

    private static void WriteArgument(
        Span<byte> destination, Type managedType, object? value, string parameterName,
        int parameterOffset,
        List<nint> nativeAllocations,
        List<NativeTextArgument> textArguments)
    {
        if (managedType == typeof(sbyte) && value is sbyte int8 && destination.Length == 1)
            destination[0] = (byte)int8;
        else if (managedType == typeof(byte) && value is byte uint8 && destination.Length == 1)
            destination[0] = uint8;
        else if (managedType == typeof(short) && value is short int16 && destination.Length == 2)
            BinaryPrimitives.WriteInt16LittleEndian(destination, int16);
        else if (managedType == typeof(ushort) && value is ushort uint16 && destination.Length == 2)
            BinaryPrimitives.WriteUInt16LittleEndian(destination, uint16);
        else if (managedType == typeof(int) && value is int int32 && destination.Length == 4)
            BinaryPrimitives.WriteInt32LittleEndian(destination, int32);
        else if (managedType == typeof(uint) && value is uint uint32 && destination.Length == 4)
            BinaryPrimitives.WriteUInt32LittleEndian(destination, uint32);
        else if (managedType == typeof(long) && value is long int64 && destination.Length == 8)
            BinaryPrimitives.WriteInt64LittleEndian(destination, int64);
        else if (managedType == typeof(ulong) && value is ulong uint64 && destination.Length == 8)
            BinaryPrimitives.WriteUInt64LittleEndian(destination, uint64);
        else if (managedType == typeof(float) && value is float single && destination.Length == 4)
            BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(single));
        else if (managedType == typeof(double) && value is double real && destination.Length == 8)
            BinaryPrimitives.WriteInt64LittleEndian(destination, BitConverter.DoubleToInt64Bits(real));
        else if (managedType == typeof(bool) && value is bool boolean && destination.Length == 1)
            destination[0] = boolean ? (byte)1 : (byte)0;
        else if (managedType == typeof(FVector) && value is FVector vector && destination.Length == 12)
            MemoryMarshal.Write(destination, in vector);
        else if (managedType == typeof(FVector2D) && value is FVector2D vector2D && destination.Length == 8)
            MemoryMarshal.Write(destination, in vector2D);
        else if (managedType == typeof(FRotator) && value is FRotator rotator && destination.Length == 12)
            MemoryMarshal.Write(destination, in rotator);
        else if (managedType == typeof(FKey) && value is FKey key && destination.Length == 0x18)
            MemoryMarshal.Write(destination, in key);
        else if (managedType == typeof(UnrealName) &&
                 value is UnrealName unrealName && destination.Length == 8)
            MemoryMarshal.Write(destination, in unrealName);
        else if (managedType == typeof(UnrealText) &&
                 value is UnrealText unrealText && destination.Length == 24)
        {
            var text = unrealText.Value ?? string.Empty;
            if (text.IndexOf('\0') >= 0)
                throw new ArgumentException($"Argument {parameterName} contains a null character.");
            if (text.Length >= MaximumTextCharacters)
                throw new ArgumentOutOfRangeException(
                    parameterName, $"Unreal text input exceeds {MaximumTextCharacters - 1} characters.");
            var allocation = (char*)NativeMemory.Alloc(
                checked((nuint)((text.Length + 1) * sizeof(char))));
            if (allocation == null) throw new OutOfMemoryException();
            text.AsSpan().CopyTo(new Span<char>(allocation, text.Length));
            allocation[text.Length] = '\0';
            nativeAllocations.Add((nint)allocation);
            textArguments.Add(new NativeTextArgument
            {
                StructSize = checked((uint)sizeof(NativeTextArgument)),
                ParameterOffset = parameterOffset,
                Flags = NativeTextArgumentFlags.Input,
                InputCharacters = checked((uint)text.Length),
                Input = allocation
            });
        }
        else if (managedType == typeof(string) && value is string text && destination.Length == 0x10)
        {
            if (text.IndexOf('\0') >= 0)
                throw new ArgumentException($"Argument {parameterName} contains a null character.");
            if (text.Length > 4096)
                throw new ArgumentOutOfRangeException(parameterName, "Unreal string input exceeds 4096 characters.");
            if (text.Length == 0) return;

            // FString is {TCHAR* Data, int32 Num, int32 Max}. ProcessEvent only
            // borrows input parameters during this synchronous call. The UTF-16
            // allocation therefore remains private to the bridge and is freed
            // in InvokeBuffer's finally block after Unreal has returned.
            var characterCount = checked(text.Length + 1);
            var allocation = (char*)NativeMemory.Alloc(
                checked((nuint)(characterCount * sizeof(char))));
            if (allocation == null) throw new OutOfMemoryException();
            text.AsSpan().CopyTo(new Span<char>(allocation, text.Length));
            allocation[text.Length] = '\0';
            nativeAllocations.Add((nint)allocation);
            BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)allocation);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], characterCount);
            BinaryPrimitives.WriteInt32LittleEndian(destination[12..], characterCount);
        }
        else if (managedType == typeof(byte[]) && value is byte[] bytes && destination.Length == 0x10)
        {
            if (bytes.Length > 32 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(
                    parameterName, "Unreal byte-array input exceeds 32 MiB.");
            if (bytes.Length == 0) return;

            // TArray<uint8> is {uint8* Data, int32 Num, int32 Max}. Unreal only
            // borrows the allocation during this synchronous ProcessEvent call.
            var allocation = (byte*)NativeMemory.Alloc(checked((nuint)bytes.Length));
            if (allocation == null) throw new OutOfMemoryException();
            bytes.CopyTo(new Span<byte>(allocation, bytes.Length));
            nativeAllocations.Add((nint)allocation);
            BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)allocation);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(destination[12..], bytes.Length);
        }
        else if (managedType == typeof(UnrealObjectReference) &&
                 value is UnrealObjectReference objectReference && destination.Length == 8)
        {
            // The ABI transports a validated {GUObjectArray index, serial}
            // handle. version.dll resolves it to a UObject* only for the
            // duration of ProcessEvent and converts object outputs back.
            MemoryMarshal.Write(destination, in objectReference);
        }
        else if (managedType.IsValueType && value is IUnrealStructValue structValue &&
                 value.GetType() == managedType && structValue.Size == destination.Length)
        {
            // Generated Unreal structs use explicit native offsets and size.
            // Marshal into the already-bounded ProcessEvent buffer; no native
            // address becomes part of the public SDK surface.
            structValue.WriteTo(destination);
        }
        else
            throw new NotSupportedException(
                $"Argument {parameterName} ({managedType.FullName}, {destination.Length} bytes) " +
                "cannot yet be marshalled to an Unreal parameter buffer.");
    }

    private static TValue ReadValue<TValue>(
        ReadOnlySpan<byte> buffer, UnrealParameter parameter) where TValue : unmanaged
    {
        if (parameter.ManagedType != typeof(TValue) ||
            parameter.Size != Unsafe.SizeOf<TValue>() ||
            parameter.Offset < 0 || parameter.Offset > buffer.Length - parameter.Size)
            throw new InvalidOperationException(
                $"The returned layout for {parameter.Name} does not match {typeof(TValue).Name}.");
        return MemoryMarshal.Read<TValue>(buffer.Slice(parameter.Offset, parameter.Size));
    }

    private string GetText(UnrealObjectHandle handle, bool path)
    {
        EnsureAvailable();
        uint required = 0;
        var first = path
            ? _api->GetObjectPath(_api->Context, handle, null, 0, &required)
            : _api->GetObjectName(_api->Context, handle, null, 0, &required);
        if (first != NativeUnrealResult.BufferTooSmall)
            EnsureSuccess(path ? "GetObjectPath(size)" : "GetObjectName(size)", first);
        if (required is 0 or > 65_536)
            throw new InvalidOperationException($"Unreal returned an invalid UTF-8 size: {required}.");

        var bytes = new byte[required];
        fixed (byte* destination = bytes)
        {
            var status = path
                ? _api->GetObjectPath(_api->Context, handle, destination, required, &required)
                : _api->GetObjectName(_api->Context, handle, destination, required, &required);
            EnsureSuccess(path ? "GetObjectPath" : "GetObjectName", status);
        }
        return Encoding.UTF8.GetString(bytes, 0, checked((int)required) - 1);
    }

    private static byte[] Encode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 4096)
            throw new ArgumentOutOfRangeException(nameof(value), "UTF-8 input exceeds 4096 bytes.");
        return bytes;
    }

    private UnrealObjectHandle FindMetadata(string path) =>
        MetadataHandles.GetOrAdd(path, static (metadataPath, api) =>
            api.FindObject(metadataPath).Handle, this);

    private UnrealObjectHandle RefreshMetadata(string path)
    {
        var refreshed = FindObject(path).Handle;
        MetadataHandles[path] = refreshed;
        return refreshed;
    }

    private static UnrealPropertyKind KindOf<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(sbyte)) return UnrealPropertyKind.Int8;
        if (typeof(T) == typeof(short)) return UnrealPropertyKind.Int16;
        if (typeof(T) == typeof(ushort)) return UnrealPropertyKind.UInt16;
        if (typeof(T) == typeof(int)) return UnrealPropertyKind.Int32;
        if (typeof(T) == typeof(uint)) return UnrealPropertyKind.UInt32;
        if (typeof(T) == typeof(long)) return UnrealPropertyKind.Int64;
        if (typeof(T) == typeof(ulong)) return UnrealPropertyKind.UInt64;
        if (typeof(T) == typeof(float)) return UnrealPropertyKind.Float;
        if (typeof(T) == typeof(double)) return UnrealPropertyKind.Double;
        if (typeof(T) == typeof(bool)) return UnrealPropertyKind.Bool;
        if (typeof(T) == typeof(byte)) return UnrealPropertyKind.Byte;
        if (typeof(T) == typeof(UnrealName)) return UnrealPropertyKind.Name;
        if (typeof(T) == typeof(UnrealObjectReference)) return UnrealPropertyKind.Object;
        if (typeof(T).IsValueType && !typeof(T).IsPrimitive) return UnrealPropertyKind.Struct;
        throw new NotSupportedException($"Property reads for {typeof(T).FullName} are not implemented.");
    }

    private void EnsureAvailable()
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The host does not expose Unreal reflection API v1.");
    }

    private static void EnsureSuccess(string operation, NativeUnrealResult result)
    {
        if (result != NativeUnrealResult.Ok)
            throw new UnrealApiException(operation, result);
    }
}

public class UnrealObject
{
    private readonly UnrealApi _api;
    public UnrealObjectHandle Handle { get; }
    protected internal UnrealObject(UnrealApi api, UnrealObjectHandle handle) =>
        (_api, Handle) = (api, handle);
    public bool IsNull => Handle.IsNull;
    public string Name => IsNull ? "<null>" : _api.GetName(Handle);
    public string Path => IsNull ? "<null>" : _api.GetPath(Handle);
    public UnrealObjectHandle ClassHandle => IsNull ? default : _api.GetClass(Handle);
    public bool IsA<T>(UnrealClass<T> unrealClass) where T : UnrealObject, IUnrealObject<T> =>
        !IsNull && _api.IsA(Handle, unrealClass.Path);

    public TValue Read<TValue>(UnrealProperty<TValue> property) where TValue : unmanaged =>
        _api.Read(Handle, property);

    protected string ReadString(UnrealProperty<string> property) =>
        _api.ReadString(Handle, property);

    protected UnrealText ReadText(UnrealProperty<UnrealText> property) =>
        _api.ReadText(Handle, property);

    // The generated SDK exposes Unreal UFunctions as ordinary C# instance
    // methods. The native ProcessEvent bridge marshals the generated
    // parameter descriptor here; keeping the entry point on the base class
    // means generated wrappers never manipulate addresses or native buffers.
    protected void InvokeVoid(UnrealFunction function, params object?[] arguments) =>
        _api.InvokeVoid(Handle, function, arguments);

    protected TResult Invoke<TResult>(UnrealFunction function, params object?[] arguments)
        where TResult : unmanaged =>
        _api.Invoke<TResult>(Handle, function, arguments);

    protected UnrealText InvokeText(UnrealFunction function, params object?[] arguments) =>
        _api.InvokeText(Handle, function, arguments);
}
