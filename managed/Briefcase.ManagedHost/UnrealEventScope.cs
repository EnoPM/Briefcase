using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.ManagedHost;

/// <summary>
/// Owns every native delegate binding created by one mod. Registration is
/// posted to Unreal's game thread; disposal first disables the native callback,
/// then releases all managed references held by the collectible mod context.
/// </summary>
internal sealed unsafe class UnrealEventScope : IUnrealEventScope
{
    private readonly object _gate = new();
    private readonly UnrealApi _unreal;
    private readonly NativeUnrealApi* _native;
    private readonly NativePatchingApi* _patching;
    private readonly GameThreadApi _gameThread;
    private readonly string _owner;
    private readonly Action<string> _error;
    private readonly List<IRegistration> _registrations = [];
    private bool _disposed;

    public UnrealEventScope(
        UnrealApi unreal,
        NativeUnrealApi* native,
        NativePatchingApi* patching,
        GameThreadApi gameThread,
        string owner,
        Action<string> error)
    {
        _unreal = unreal;
        _native = native;
        _patching = patching;
        _gameThread = gameThread;
        _owner = owner;
        _error = error;
    }

    public bool IsAvailable => !_disposed && _gameThread.IsAvailable &&
        _native != null && _native->ApiVersion >= BriefcaseAbi.UnrealApiVersion &&
        _native->SubscribeMulticastDelegate != null && _native->UnsubscribeDelegate != null &&
        _patching != null && _patching->ApiVersion >= BriefcaseAbi.PatchingApiVersion;

    public IDisposable Subscribe<TSource>(
        TSource source,
        UnrealProperty<UnrealMulticastDelegate> unrealEvent,
        Action<TSource, UnrealEventArguments> handler)
        where TSource : UnrealObject
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The native Unreal event API is unavailable.");
        if (source.IsNull)
            throw new ArgumentException("The Unreal event source is null.", nameof(source));
        var signature = unrealEvent.DelegateSignature ??
            throw new InvalidOperationException(
                $"{unrealEvent.OwnerPath}.{unrealEvent.Name} has no generated delegate signature. " +
                "Regenerate the SDK from a schema-4 snapshot.");
        if (signature.ReturnParameter is not null ||
            signature.Parameters.Any(parameter => parameter.IsOut || parameter.IsReference))
            throw new NotSupportedException(
                "Multicast delegates with return or writable output parameters are unsupported.");
        if (signature.Parameters.Select(parameter => parameter.Name)
            .Distinct(StringComparer.Ordinal).Count() != signature.Parameters.Count)
            throw new InvalidOperationException("The delegate signature contains duplicate parameter names.");

        var registration = new Registration<TSource>(
            this, source, unrealEvent, signature, handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registrations.Add(registration);
        }
        try
        {
            registration.Schedule();
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        IRegistration[] registrations;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = _registrations.ToArray();
            _registrations.Clear();
        }
        for (var index = registrations.Length - 1; index >= 0; index--)
            registrations[index].DisposeFromOwner();
    }

    private void Remove(IRegistration registration)
    {
        lock (_gate) _registrations.Remove(registration);
    }

    private void Report(Exception exception) =>
        _error($"Unreal event subscription for {_owner} failed: {exception}");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Invoke(void* userContext, NativePatchCall* call, uint* runOriginal)
    {
        if (userContext == null || call == null || runOriginal == null ||
            call->StructSize < (uint)sizeof(NativePatchCall)) return;
        // The native delegate targets a compatible CDO function used only as a
        // ProcessEvent sink. Never execute that function's real implementation.
        *runOriginal = 0;
        try
        {
            var handle = GCHandle.FromIntPtr((nint)userContext);
            if (handle.Target is IRegistration registration)
                registration.Dispatch(call);
        }
        catch
        {
            // Managed exceptions never cross the reverse P/Invoke boundary.
        }
    }

    private interface IRegistration : IDisposable
    {
        void Schedule();
        void Dispatch(NativePatchCall* call);
        void DisposeFromOwner();
    }

    private sealed class Registration<TSource> : IRegistration
        where TSource : UnrealObject
    {
        private readonly object _gate = new();
        private readonly UnrealEventScope _owner;
        private readonly TSource _source;
        private readonly UnrealProperty<UnrealMulticastDelegate> _event;
        private readonly UnrealFunction _signature;
        private readonly Action<TSource, UnrealEventArguments> _handler;
        private IDisposable? _pending;
        private GCHandle _stateHandle;
        private ulong _registrationId;
        private int _disposed;

        public Registration(
            UnrealEventScope owner,
            TSource source,
            UnrealProperty<UnrealMulticastDelegate> unrealEvent,
            UnrealFunction signature,
            Action<TSource, UnrealEventArguments> handler) =>
            (_owner, _source, _event, _signature, _handler) =
            (owner, source, unrealEvent, signature, handler);

        public void Schedule()
        {
            lock (_gate)
                _pending = _owner._gameThread.Post(RegisterNative);
        }

        private void RegisterNative()
        {
            lock (_gate)
            {
                _pending = null;
                if (Volatile.Read(ref _disposed) != 0) return;
                try
                {
                    var ownerClass = _owner._unreal.FindObject(_event.OwnerPath).Handle;
                    var name = Encoding.UTF8.GetBytes(_event.Name);
                    _stateHandle = GCHandle.Alloc(this);
                    ulong registrationId = 0;
                    NativeUnrealResult result;
                    fixed (byte* namePointer = name)
                        result = _owner._native->SubscribeMulticastDelegate(
                            _owner._native->Context, _source.Handle, ownerClass,
                            namePointer, checked((uint)name.Length), _event.Offset,
                            _event.Size, 1, &Invoke,
                            (void*)GCHandle.ToIntPtr(_stateHandle), &registrationId);
                    if (result != NativeUnrealResult.Ok)
                        throw new UnrealApiException(
                            $"Subscribe({_event.OwnerPath}.{_event.Name})", result);
                    _registrationId = registrationId;
                }
                catch (Exception exception)
                {
                    if (_stateHandle.IsAllocated) _stateHandle.Free();
                    _owner.Report(exception);
                }
            }
        }

        public void Dispatch(NativePatchCall* call)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                if (call->Instance != _source.Handle ||
                    call->ParameterSize != _signature.ParameterBufferSize ||
                    (call->ParameterSize != 0 && call->Parameters == null))
                    throw new InvalidOperationException(
                        $"Runtime arguments for {_event.OwnerPath}.{_event.Name} " +
                        "do not match the generated signature.");

                var values = new Dictionary<string, object?>(
                    _signature.Parameters.Count, StringComparer.Ordinal);
                foreach (var parameter in _signature.Parameters)
                    values.Add(parameter.Name, PatchValueReader.Read(
                        _owner._patching, _owner._unreal, call, parameter));
                _handler(_source, new UnrealEventArguments(values));
            }
            catch (Exception exception)
            {
                _owner.Report(exception);
            }
        }

        public void Dispose()
        {
            DisposeCore();
            _owner.Remove(this);
        }

        public void DisposeFromOwner() => DisposeCore();

        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate)
            {
                _pending?.Dispose();
                _pending = null;
                if (_registrationId != 0)
                {
                    _owner._native->UnsubscribeDelegate(
                        _owner._native->Context, _registrationId);
                    _registrationId = 0;
                }
                if (_stateHandle.IsAllocated) _stateHandle.Free();
            }
        }
    }
}
