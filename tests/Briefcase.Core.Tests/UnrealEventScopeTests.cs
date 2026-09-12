using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Core.Tests;

public sealed unsafe class UnrealEventScopeTests
{
    private static nint _callback;
    private static nint _userContext;
    private static int _subscribeCount;
    private static int _unsubscribeCount;

    [Fact]
    public void Subscription_is_registered_on_the_game_thread_and_decodes_arguments()
    {
        ResetNativeState();
        var native = CreateUnrealApi();
        var patching = new NativePatchingApi
        {
            StructSize = (uint)sizeof(NativePatchingApi),
            ApiVersion = BriefcaseAbi.PatchingApiVersion
        };
        var unreal = new UnrealApi(&native);
        var driver = new ManualGameThreadDriver();
        var errors = new List<string>();
        using var service = new GameThreadService(
            driver, _ => null, _ => { }, errors.Add);
        using var gameThreadScope = service.CreateScope("event.test");
        using var events = new UnrealEventScope(
            unreal, &native, &patching, new GameThreadApi(gameThreadScope),
            "event.test", errors.Add);
        var source = new UnrealObject(unreal, new UnrealObjectHandle(7, 11));
        var signature = new UnrealFunction(
            "/Script/DeceiveInc.EventSource", "OnChanged__DelegateSignature", 4,
            [new UnrealParameter("Value", typeof(int), 0, 4)]);
        var descriptor = new UnrealProperty<UnrealMulticastDelegate>(
            "/Script/DeceiveInc.EventSource", "OnChanged", 32, 16, signature);
        var received = 0;

        using var subscription = events.Subscribe(
            source, descriptor, (publisher, arguments) =>
            {
                Assert.Same(source, publisher);
                received = arguments.Get<int>("Value");
            });

        Assert.Equal(0, Volatile.Read(ref _subscribeCount));
        driver.Pump(default);
        Assert.Equal(1, Volatile.Read(ref _subscribeCount));

        Fire(37, source.Handle);

        Assert.Equal(37, received);
        Assert.Empty(errors);
        subscription.Dispose();
        Assert.Equal(1, Volatile.Read(ref _unsubscribeCount));
    }

    [Fact]
    public void Disposing_the_mod_scope_unregisters_every_delegate()
    {
        ResetNativeState();
        var native = CreateUnrealApi();
        var patching = new NativePatchingApi
        {
            StructSize = (uint)sizeof(NativePatchingApi),
            ApiVersion = BriefcaseAbi.PatchingApiVersion
        };
        var unreal = new UnrealApi(&native);
        var driver = new ManualGameThreadDriver();
        using var service = new GameThreadService(
            driver, _ => null, _ => { }, _ => { });
        using var gameThreadScope = service.CreateScope("event.cleanup");
        var events = new UnrealEventScope(
            unreal, &native, &patching, new GameThreadApi(gameThreadScope),
            "event.cleanup", _ => { });
        var source = new UnrealObject(unreal, new UnrealObjectHandle(7, 11));
        var descriptor = new UnrealProperty<UnrealMulticastDelegate>(
            "/Script/DeceiveInc.EventSource", "OnChanged", 32, 16,
            new UnrealFunction(
                "/Script/DeceiveInc.EventSource", "OnChanged__DelegateSignature", 4,
                [new UnrealParameter("Value", typeof(int), 0, 4)]));

        events.Subscribe(source, descriptor, (_, _) => { });
        driver.Pump(default);
        events.Dispose();

        Assert.Equal(1, Volatile.Read(ref _subscribeCount));
        Assert.Equal(1, Volatile.Read(ref _unsubscribeCount));
    }

    private static NativeUnrealApi CreateUnrealApi() => new()
    {
        StructSize = (uint)sizeof(NativeUnrealApi),
        ApiVersion = BriefcaseAbi.UnrealApiVersion,
        FindObject = &FindObject,
        FindObjectsOfClass = &FindObjects,
        GetObjectName = &GetText,
        GetObjectPath = &GetText,
        GetPropertyInfo = &GetPropertyInfo,
        ReadProperty = &ReadProperty,
        SubscribeMulticastDelegate = &Subscribe,
        UnsubscribeDelegate = &Unsubscribe
    };

    private static void ResetNativeState()
    {
        _callback = 0;
        _userContext = 0;
        _subscribeCount = 0;
        _unsubscribeCount = 0;
    }

    private static void Fire(int value, UnrealObjectHandle source)
    {
        var callback = (delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint*, void>)_callback;
        Assert.True(callback != null);
        var call = new NativePatchCall
        {
            StructSize = (uint)sizeof(NativePatchCall),
            Phase = NativePatchPhase.Prefix,
            Instance = source,
            Parameters = &value,
            ParameterSize = 4
        };
        uint runOriginal = 1;
        callback((void*)_userContext, &call, &runOriginal);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult FindObject(
        void* context, byte* path, uint pathLength, UnrealObjectHandle* result)
    {
        *result = new UnrealObjectHandle(100, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult FindObjects(
        void* context, UnrealObjectHandle unrealClass, UnrealObjectHandle* results,
        uint capacity, uint* written, uint* total) =>
        NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetText(
        void* context, UnrealObjectHandle value, byte* destination,
        uint capacity, uint* required) =>
        NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetPropertyInfo(
        void* context, UnrealObjectHandle owner, byte* name,
        uint nameLength, NativePropertyInfo* result) =>
        NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult ReadProperty(
        void* context, UnrealObjectHandle instance, UnrealObjectHandle owner,
        byte* name, uint nameLength, int offset, int size, int dimension,
        UnrealPropertyKind kind, void* output, uint outputSize) =>
        NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult Subscribe(
        void* context, UnrealObjectHandle source, UnrealObjectHandle owner,
        byte* name, uint nameLength, int offset, int size, int dimension,
        delegate* unmanaged[Cdecl]<void*, NativePatchCall*, uint*, void> callback,
        void* userContext, ulong* registrationId)
    {
        if (source != new UnrealObjectHandle(7, 11) ||
            owner != new UnrealObjectHandle(100, 1) ||
            offset != 32 || size != 16 || dimension != 1)
            return NativeUnrealResult.LayoutMismatch;
        _callback = (nint)callback;
        _userContext = (nint)userContext;
        *registrationId = 42;
        Interlocked.Increment(ref _subscribeCount);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Unsubscribe(void* context, ulong registrationId)
    {
        if (registrationId != 42) return 0;
        _callback = 0;
        _userContext = 0;
        Interlocked.Increment(ref _unsubscribeCount);
        return 1;
    }
}
