using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.ManagedHost;

internal sealed unsafe class PatchRuntime : IDisposable
{
    private readonly List<PatchRegistration> _registrations;

    private PatchRuntime(List<PatchRegistration> registrations) =>
        _registrations = registrations;

    public static PatchRuntime Attach(ModContext context, PatchSet patchSet, ModInfo modInfo)
    {
        var native = context.PatchingNative;
        if (patchSet.Count == 0) return new PatchRuntime([]);
        if (native == null || native->ApiVersion < BriefcaseAbi.PatchingApiVersion ||
            native->RegisterPatch == null || native->UnregisterPatch == null ||
            native->RegisterNativePatch == null || native->UnregisterNativePatch == null)
            throw new InvalidOperationException("The native patching API is unavailable.");

        var state = new ModPatchState(context, modInfo.Id);
        var registrations = new List<PatchRegistration>();
        try
        {
            foreach (var declaration in Ordered(patchSet.Declarations))
                registrations.Add(new PatchRegistration(native, state, declaration));
            return new PatchRuntime(registrations);
        }
        catch
        {
            for (var index = registrations.Count - 1; index >= 0; index--)
                registrations[index].Dispose();
            throw;
        }
    }

    private static IEnumerable<PatchDeclaration> Ordered(
        IReadOnlyList<PatchDeclaration> declarations) =>
        declarations
            .OrderBy(declaration => declaration.Phase)
            .ThenBy(declaration => declaration.Phase == PatchPhase.Prefix
                ? -declaration.Priority
                : declaration.Priority);

    public void Dispose()
    {
        for (var index = _registrations.Count - 1; index >= 0; index--)
            _registrations[index].Dispose();
        _registrations.Clear();
    }
}

internal sealed class ModPatchState(ModContext context, string modId)
{
    public ModContext Context { get; } = context;
    public string ModId { get; } = modId;
    public bool Disabled { get; set; }
}

internal sealed unsafe class PatchRegistration : IDisposable
{
    [ThreadStatic] private static HashSet<PatchDeclaration>? _activePatches;
    [ThreadStatic] private static HashSet<string>? _suppressedTargets;

    private NativePatchingApi* _api;
    private readonly PatchDeclaration _declaration;
    private readonly PatchBackend _backend;
    private readonly ModPatchState _modState;
    private readonly string _targetKey;
    private GCHandle _stateHandle;
    private ulong _registrationId;
    private bool _disabled;

    public PatchRegistration(
        NativePatchingApi* api,
        ModPatchState modState,
        PatchDeclaration declaration)
    {
        _api = api;
        _declaration = declaration;
        _backend = declaration.Backend;
        _modState = modState;
        _targetKey = $"{declaration.Function.OwnerPath}.{declaration.Function.Name}";
        _stateHandle = GCHandle.Alloc(this);

        var targetPath = declaration.DeclaringType
            .GetField("UnrealPath", BindingFlags.Public | BindingFlags.Static)?
            .GetRawConstantValue() as string
            ?? throw new InvalidOperationException(
                $"{declaration.DeclaringType.FullName} has no generated UnrealPath.");
        var unreal = modState.Context.Unreal;
        var targetClass = unreal.FindObject(targetPath).Handle;
        var functionOwnerClass = unreal.FindObject(declaration.Function.OwnerPath).Handle;
        var name = Encoding.UTF8.GetBytes(declaration.Function.Name);
        ulong registrationId = 0;
        NativeUnrealResult result;
        fixed (byte* namePointer = name)
        {
            var register = declaration.Backend == PatchBackend.Native
                ? api->RegisterNativePatch
                : api->RegisterPatch;
            result = register(
                api->Context, targetClass, functionOwnerClass,
                namePointer, checked((uint)name.Length),
                declaration.Phase == PatchPhase.Prefix
                    ? NativePatchPhase.Prefix
                    : NativePatchPhase.Postfix,
                &Invoke, (void*)GCHandle.ToIntPtr(_stateHandle), &registrationId);
        }
        if (result != NativeUnrealResult.Ok)
        {
            _stateHandle.Free();
            _api = null;
            throw new UnrealApiException(
                $"Register{declaration.Backend}Patch({_targetKey})", result);
        }
        _registrationId = registrationId;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Invoke(void* userContext, NativePatchCall* call, uint* runOriginal)
    {
        if (userContext == null || call == null || runOriginal == null ||
            call->StructSize < (uint)sizeof(NativePatchCall)) return;
        try
        {
            var handle = GCHandle.FromIntPtr((nint)userContext);
            if (handle.Target is PatchRegistration registration)
                registration.Dispatch(call, runOriginal);
        }
        catch
        {
            // No managed exception may cross a reverse P/Invoke boundary.
        }
    }

    private void Dispatch(NativePatchCall* call, uint* runOriginal)
    {
        if (_disabled || _modState.Disabled) return;
        if (_declaration.Phase == PatchPhase.Postfix && call->OriginalRan == 0 &&
            !_declaration.RunWhenOriginalSkipped) return;

        _activePatches ??= [];
        _suppressedTargets ??= [];
        if (_suppressedTargets.Contains(_targetKey)) return;
        if (_activePatches.Contains(_declaration) &&
            _declaration.Reentrancy == PatchReentrancy.SuppressCurrentPatch) return;

        var suppressTarget = _declaration.Reentrancy == PatchReentrancy.SuppressTargetPatches;
        _activePatches.Add(_declaration);
        if (suppressTarget) _suppressedTargets.Add(_targetKey);
        try
        {
            InvokeManagedPatch(call, runOriginal);
        }
        catch (Exception exception)
        {
            var actual = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            _modState.Context.Error(
                $"Patch {_declaration.PatchMethod.DeclaringType?.FullName}." +
                $"{_declaration.PatchMethod.Name} failed: {actual}");
            switch (_declaration.OnException)
            {
                case PatchExceptionPolicy.DisablePatch:
                    _disabled = true;
                    break;
                case PatchExceptionPolicy.DisableMod:
                    _modState.Disabled = true;
                    break;
            }
        }
        finally
        {
            _activePatches.Remove(_declaration);
            if (suppressTarget) _suppressedTargets.Remove(_targetKey);

            // Thread-static containers live in the non-collectible host. Drop the
            // containers themselves when the outermost callback returns so they
            // cannot retain a descriptor (and therefore a mod AssemblyLoadContext)
            // if a future equality or exception path changes the removal logic.
            if (_activePatches.Count == 0) _activePatches = null;
            if (_suppressedTargets.Count == 0) _suppressedTargets = null;
        }
    }

    private void InvokeManagedPatch(NativePatchCall* call, uint* runOriginal)
    {
        if (call->ParameterSize != _declaration.Function.ParameterBufferSize ||
            (call->ParameterSize != 0 && call->Parameters == null))
            throw new InvalidOperationException(
                $"Runtime parameter buffer for {_targetKey} does not match generated metadata.");

        var methodParameters = _declaration.PatchMethod.GetParameters();
        var arguments = new object?[methodParameters.Length];
        var writeBack = new List<(int ArgumentIndex, UnrealParameter Parameter)>();
        var currentRunOriginal = *runOriginal != 0;
        var instance = CreateInstance(call->Instance);

        for (var index = 0; index < methodParameters.Length; index++)
        {
            var methodParameter = methodParameters[index];
            if (methodParameter.Name == "__instance")
            {
                arguments[index] = instance;
                continue;
            }
            if (methodParameter.Name == "__runOriginal")
            {
                arguments[index] = currentRunOriginal;
                continue;
            }
            if (methodParameter.Name == "__result")
            {
                var returnParameter = _declaration.Function.ReturnParameter
                    ?? throw new InvalidOperationException(
                        $"{_targetKey} has no generated return parameter.");
                arguments[index] = ReadParameter(call, returnParameter);
                if (methodParameter.ParameterType.IsByRef)
                    writeBack.Add((index, returnParameter));
                continue;
            }

            var targetParameter = _declaration.Function.Parameters.Single(
                parameter => parameter.Name == methodParameter.Name);
            arguments[index] = ReadParameter(call, targetParameter);
            if (methodParameter.ParameterType.IsByRef)
                writeBack.Add((index, targetParameter));
        }

        var result = _declaration.PatchMethod.Invoke(null, arguments);
        if (_declaration.Phase == PatchPhase.Prefix && result is bool shouldRun)
            currentRunOriginal &= shouldRun;
        var runOriginalIndex = Array.FindIndex(
            methodParameters, parameter => parameter.Name == "__runOriginal");
        if (runOriginalIndex >= 0 && arguments[runOriginalIndex] is bool requested)
            currentRunOriginal = requested;
        *runOriginal = currentRunOriginal ? 1u : 0u;

        foreach (var (argumentIndex, parameter) in writeBack)
            WriteParameter(call, parameter, arguments[argumentIndex]);
    }

    private object CreateInstance(UnrealObjectHandle handle)
    {
        var factory = _declaration.DeclaringType.GetMethod(
            "FromObject", BindingFlags.Public | BindingFlags.Static,
            null, [typeof(UnrealApi), typeof(UnrealObjectHandle)], null)
            ?? throw new InvalidOperationException(
                $"{_declaration.DeclaringType.FullName}.FromObject was not found.");
        return factory.Invoke(null, [_modState.Context.Unreal, handle])
               ?? throw new InvalidOperationException("Generated FromObject returned null.");
    }

    private object ReadParameter(NativePatchCall* call, UnrealParameter parameter)
    {
        var bytes = ParameterSpan(call, parameter);
        if (parameter.ManagedType == typeof(byte[]) && bytes.Length == 0x10)
            return CopyByteArray(call, parameter.Offset);
        if (parameter.ManagedType == typeof(string) && bytes.Length == 0x10)
            return CopyString(call, parameter.Offset);
        if (parameter.ManagedType == typeof(UnrealText) && bytes.Length == 0x18)
            return new UnrealText(CopyText(call, parameter.Offset));
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
            $"Patch parameter {parameter.Name} of type {parameter.ManagedType.FullName} is unsupported.");
    }

    private byte[] CopyByteArray(NativePatchCall* call, int parameterOffset)
    {
        var api = _api;
        if (api == null || api->ApiVersion < BriefcaseAbi.PatchingApiVersion ||
            api->CopyByteArray == null)
            throw new NotSupportedException("The host cannot copy Unreal byte arrays.");
        uint required = 0;
        var status = api->CopyByteArray(
            api->Context, call, checked((uint)parameterOffset), null, 0, &required);
        if (status == NativeUnrealResult.Ok && required == 0) return [];
        if (status != NativeUnrealResult.BufferTooSmall || required > 32u * 1024u * 1024u)
            throw new InvalidOperationException($"Could not size Unreal byte array: {status}.");
        var result = new byte[required];
        fixed (byte* destination = result)
            status = api->CopyByteArray(
                api->Context, call, checked((uint)parameterOffset), destination,
                checked((uint)result.Length), &required);
        if (status != NativeUnrealResult.Ok || required != result.Length)
            throw new InvalidOperationException($"Could not copy Unreal byte array: {status}.");
        return result;
    }

    private string CopyString(NativePatchCall* call, int parameterOffset)
    {
        var api = _api;
        if (api == null || api->ApiVersion < BriefcaseAbi.PatchingApiVersion ||
            api->CopyString == null)
            throw new NotSupportedException("The host cannot copy Unreal strings.");
        uint required = 0;
        var status = api->CopyString(
            api->Context, call, checked((uint)parameterOffset), null, 0, &required);
        if (status == NativeUnrealResult.Ok && required == 0) return string.Empty;
        if (status != NativeUnrealResult.BufferTooSmall || required > 1024u * 1024u)
            throw new InvalidOperationException($"Could not size Unreal string: {status}.");
        var characters = new char[required];
        fixed (char* destination = characters)
            status = api->CopyString(
                api->Context, call, checked((uint)parameterOffset), destination,
                checked((uint)characters.Length), &required);
        if (status != NativeUnrealResult.Ok || required != characters.Length)
            throw new InvalidOperationException($"Could not copy Unreal string: {status}.");
        var length = characters.Length > 0 && characters[^1] == '\0'
            ? characters.Length - 1 : characters.Length;
        return new string(characters, 0, length);
    }

    private string CopyText(NativePatchCall* call, int parameterOffset)
    {
        var api = _api;
        if (api == null || api->ApiVersion < BriefcaseAbi.PatchingApiVersion ||
            api->CopyText == null)
            throw new NotSupportedException("The host cannot copy Unreal FText values.");
        uint required = 0;
        var status = api->CopyText(
            api->Context, call, checked((uint)parameterOffset), null, 0, &required);
        if (status == NativeUnrealResult.Ok && required == 0) return string.Empty;
        if (status != NativeUnrealResult.BufferTooSmall || required > 64u * 1024u)
            throw new InvalidOperationException($"Could not size Unreal FText: {status}.");
        var characters = new char[required];
        fixed (char* destination = characters)
            status = api->CopyText(
                api->Context, call, checked((uint)parameterOffset), destination,
                checked((uint)characters.Length), &required);
        if (status != NativeUnrealResult.Ok || required != characters.Length)
            throw new InvalidOperationException($"Could not copy Unreal FText: {status}.");
        var length = characters.Length > 0 && characters[^1] == '\0'
            ? characters.Length - 1 : characters.Length;
        return new string(characters, 0, length);
    }

    private static void WriteParameter(
        NativePatchCall* call, UnrealParameter parameter, object? value)
    {
        var bytes = ParameterSpan(call, parameter);
        if (parameter.ManagedType == typeof(sbyte) && value is sbyte int8 && bytes.Length == 1)
            bytes[0] = (byte)int8;
        else if (parameter.ManagedType == typeof(byte) && value is byte uint8 && bytes.Length == 1)
            bytes[0] = uint8;
        else if (parameter.ManagedType == typeof(short) && value is short int16)
            BinaryPrimitives.WriteInt16LittleEndian(bytes, int16);
        else if (parameter.ManagedType == typeof(ushort) && value is ushort uint16)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, uint16);
        else if (parameter.ManagedType == typeof(int) && value is int int32)
            BinaryPrimitives.WriteInt32LittleEndian(bytes, int32);
        else if (parameter.ManagedType == typeof(uint) && value is uint uint32)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, uint32);
        else if (parameter.ManagedType == typeof(long) && value is long int64)
            BinaryPrimitives.WriteInt64LittleEndian(bytes, int64);
        else if (parameter.ManagedType == typeof(ulong) && value is ulong uint64)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, uint64);
        else if (parameter.ManagedType == typeof(float) && value is float single)
            BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(single));
        else if (parameter.ManagedType == typeof(double) && value is double real)
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(real));
        else if (parameter.ManagedType == typeof(bool) && value is bool boolean && bytes.Length == 1)
            bytes[0] = boolean ? (byte)1 : (byte)0;
        else if (parameter.ManagedType.IsValueType && value is not null &&
                 value.GetType() == parameter.ManagedType &&
                 Marshal.SizeOf(parameter.ManagedType) == bytes.Length)
        {
            fixed (byte* pointer = bytes)
                Marshal.StructureToPtr(value, (nint)pointer, false);
        }
        else
            throw new NotSupportedException(
                $"Patch parameter {parameter.Name} cannot be written as {parameter.ManagedType.FullName}.");
    }

    private static Span<byte> ParameterSpan(NativePatchCall* call, UnrealParameter parameter)
    {
        if (parameter.Offset < 0 || parameter.Size <= 0 ||
            (uint)parameter.Size > call->ParameterSize ||
            parameter.Offset > (int)call->ParameterSize - parameter.Size)
            throw new InvalidOperationException(
                $"Parameter {parameter.Name} is outside the runtime buffer.");
        return new Span<byte>((byte*)call->Parameters + parameter.Offset, parameter.Size);
    }

    public void Dispose()
    {
        var api = _api;
        if (api == null) return;
        _api = null;
        if (_backend == PatchBackend.Native)
            api->UnregisterNativePatch(api->Context, _registrationId);
        else
            api->UnregisterPatch(api->Context, _registrationId);
        _registrationId = 0;
        if (_stateHandle.IsAllocated) _stateHandle.Free();
    }
}
