using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Core.Tests;

public sealed unsafe class UnrealObjectAccessTests
{
    private static string lastInvocation = string.Empty;
    private static int rootAcquires;
    private static int rootReleases;

    [Fact]
    public void Default_object_outer_and_flags_remain_address_free()
    {
        var native = CreateApi();
        var api = new UnrealApi(&native);

        var defaultObject = api.GetDefaultObject(TestObject.StaticClass);

        Assert.Equal(new UnrealObjectHandle(110, 1), defaultObject.Handle);
        Assert.True(defaultObject.IsClassDefaultObject);
        Assert.Equal(new UnrealObjectHandle(40, 1), defaultObject.Outer!.Handle);
        Assert.Equal(defaultObject.Handle, defaultObject.Reference.Handle);
    }

    [Fact]
    public void Typed_lookup_and_subsystem_validate_the_returned_class()
    {
        var native = CreateApi();
        var api = new UnrealApi(&native);

        var found = api.FindObject("/Game/Test.Object", TestObject.StaticClass);
        var wrapped = api.FromReference(found.Reference, TestObject.StaticClass);
        var subsystem = api.GetEngineSubsystem(TestObject.StaticClass);

        Assert.Equal(new UnrealObjectHandle(20, 1), found.Handle);
        Assert.Equal(found.Handle, wrapped.Handle);
        Assert.Equal(new UnrealObjectHandle(32, 1), subsystem.Handle);
        Assert.Equal("GetEngineSubsystem", Volatile.Read(ref lastInvocation));
    }

    [Fact]
    public void Create_object_uses_the_vanilla_spawn_object_ufunction()
    {
        var native = CreateApi();
        var api = new UnrealApi(&native);
        var outer = TestObject.FromObject(api, new UnrealObjectHandle(20, 1));

        var created = api.CreateObject(TestObject.StaticClass, outer);

        Assert.Equal(new UnrealObjectHandle(30, 1), created.Handle);
        Assert.Equal("SpawnObject", Volatile.Read(ref lastInvocation));
    }

    [Fact]
    public void Spawn_and_destroy_actor_use_the_deferred_vanilla_path()
    {
        var native = CreateApi();
        var api = new UnrealApi(&native);
        var world = TestObject.FromObject(api, new UnrealObjectHandle(20, 1));

        var actor = api.SpawnActor(TestActor.StaticClass, world, new FTestTransform(),
            UnrealSpawnCollisionHandling.AlwaysSpawn);
        api.DestroyActor(actor);

        Assert.Equal(new UnrealObjectHandle(31, 1), actor.Handle);
        Assert.Equal("K2_DestroyActor", Volatile.Read(ref lastInvocation));
    }

    [Fact]
    public void Asset_scope_loads_roots_and_releases_every_lease_on_unload()
    {
        Interlocked.Exchange(ref rootAcquires, 0);
        Interlocked.Exchange(ref rootReleases, 0);
        var native = CreateApi();
        var unreal = new UnrealApi(&native);
        using var scope = new UnrealAssetScope(unreal, "test.mod", _ => { });
        var assets = new UnrealAssetApi(scope);

        var loaded = assets.Load("/Game/Test.Asset", TestObject.StaticClass);
        var retained = assets.Retain(loaded.Value);

        Assert.Equal(new UnrealObjectHandle(33, 1), loaded.Value.Handle);
        Assert.Equal(2, Volatile.Read(ref rootAcquires));
        scope.Dispose();
        Assert.True(loaded.IsDisposed);
        Assert.True(retained.IsDisposed);
        Assert.Equal(2, Volatile.Read(ref rootReleases));

        loaded.Dispose();
        retained.Dispose();
        Assert.Equal(2, Volatile.Read(ref rootReleases));
    }

    private static NativeUnrealApi CreateApi() => new()
    {
        StructSize = checked((uint)sizeof(NativeUnrealApi)),
        ApiVersion = BriefcaseAbi.UnrealApiVersion,
        FindObject = &FindObject,
        FindObjectsOfClass = &FindObjectsOfClass,
        GetObjectName = &GetObjectText,
        GetObjectPath = &GetObjectText,
        GetObjectClass = &GetObjectClass,
        IsObjectA = &IsObjectA,
        GetPropertyInfo = &GetPropertyInfo,
        ReadProperty = &ReadProperty,
        InvokeFunction = &InvokeFunction,
        GetClassDefaultObject = &GetClassDefaultObject,
        GetObjectOuter = &GetObjectOuter,
        GetObjectFlags = &GetObjectFlags,
        LoadObject = &LoadObject,
        AcquireObjectRoot = &AcquireObjectRoot,
        ReleaseObjectRoot = &ReleaseObjectRoot
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult FindObject(
        void* _, byte* path, uint length, UnrealObjectHandle* result)
    {
        var value = Encoding.UTF8.GetString(path, checked((int)length));
        var index = value switch
        {
            "/Script/Test.TestObject" => 10u,
            "/Script/Test.TestActor" => 11u,
            "/Script/Engine.Actor" => 12u,
            "/Script/Engine.GameplayStatics" => 13u,
            "/Script/Engine.SubsystemBlueprintLibrary" => 14u,
            _ => 20u
        };
        *result = new UnrealObjectHandle(index, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult FindObjectsOfClass(
        void* _, UnrealObjectHandle __, UnrealObjectHandle* ___,
        uint ____, uint* written, uint* total)
    {
        *written = 0;
        *total = 0;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetObjectText(
        void* _, UnrealObjectHandle __, byte* destination, uint capacity, uint* required)
    {
        *required = 2;
        if (destination == null || capacity < 2) return NativeUnrealResult.BufferTooSmall;
        destination[0] = (byte)'X';
        destination[1] = 0;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetObjectClass(
        void* _, UnrealObjectHandle __, UnrealObjectHandle* result)
    {
        *result = new UnrealObjectHandle(10, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult IsObjectA(
        void* _, UnrealObjectHandle instance, UnrealObjectHandle requestedClass, uint* result)
    {
        var isActor = requestedClass.Index == 12 &&
                      instance.Index is 111 or 31;
        var isTestObject = requestedClass.Index == 10 &&
                           instance.Index is 20 or 30 or 32 or 33 or 110;
        var isTestActor = requestedClass.Index == 11 && instance.Index == 31;
        *result = isActor || isTestObject || isTestActor ? 1u : 0u;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetPropertyInfo(
        void* _, UnrealObjectHandle __, byte* ___, uint ____, NativePropertyInfo* _____) =>
        NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult ReadProperty(
        void* _, UnrealObjectHandle __, UnrealObjectHandle ___, byte* ____, uint _____,
        int ______, int _______, int ________, UnrealPropertyKind _________,
        void* __________, uint ___________) => NativeUnrealResult.Unsupported;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult InvokeFunction(
        void* _, UnrealObjectHandle __, UnrealObjectHandle ___, byte* name, uint nameLength,
        uint parameterSize, void* parameters, uint ________)
    {
        var invocation = Encoding.UTF8.GetString(name, checked((int)nameLength));
        Volatile.Write(ref lastInvocation, invocation);
        var outputOffset = invocation switch
        {
            "GetEngineSubsystem" => 8,
            "GetGameInstanceSubsystem" or "GetWorldSubsystem" or
                "GetLocalPlayerSubsystem" => 16,
            "SpawnObject" => 16,
            "BeginDeferredActorSpawnFromClass" => 80,
            "FinishSpawningActor" => 64,
            _ => -1
        };
        if (outputOffset >= 0 && parameters != null && outputOffset <= parameterSize - 8)
        {
            var handle = invocation switch
            {
                "SpawnObject" => new UnrealObjectHandle(30, 1),
                "BeginDeferredActorSpawnFromClass" or "FinishSpawningActor" =>
                    new UnrealObjectHandle(31, 1),
                _ => new UnrealObjectHandle(32, 1)
            };
            *(UnrealObjectHandle*)((byte*)parameters + outputOffset) = handle;
        }
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetClassDefaultObject(
        void* _, UnrealObjectHandle classHandle, UnrealObjectHandle* result)
    {
        *result = new UnrealObjectHandle(classHandle.Index + 100, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetObjectOuter(
        void* _, UnrealObjectHandle __, UnrealObjectHandle* result)
    {
        *result = new UnrealObjectHandle(40, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult GetObjectFlags(
        void* _, UnrealObjectHandle instance, uint* result)
    {
        *result = instance.Index >= 100
            ? (uint)UnrealObjectFlags.ClassDefaultObject
            : 0;
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult LoadObject(
        void* _, UnrealObjectHandle expectedClass, byte* path, uint pathLength,
        UnrealObjectHandle* result)
    {
        if (expectedClass.Index != 10 ||
            Encoding.UTF8.GetString(path, checked((int)pathLength)) != "/Game/Test.Asset")
            return NativeUnrealResult.NotFound;
        *result = new UnrealObjectHandle(33, 1);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult AcquireObjectRoot(void* _, UnrealObjectHandle __)
    {
        Interlocked.Increment(ref rootAcquires);
        return NativeUnrealResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeUnrealResult ReleaseObjectRoot(void* _, UnrealObjectHandle __)
    {
        Interlocked.Increment(ref rootReleases);
        return NativeUnrealResult.Ok;
    }

    private sealed class TestObject : UnrealObject, IUnrealObject<TestObject>
    {
        private TestObject(UnrealApi api, UnrealObjectHandle handle) : base(api, handle) { }
        public static UnrealClass<TestObject> StaticClass { get; } =
            new("/Script/Test.TestObject");
        public static TestObject FromObject(UnrealApi api, UnrealObjectHandle handle) =>
            new(api, handle);
    }

    private sealed class TestActor : UnrealObject, IUnrealObject<TestActor>
    {
        private TestActor(UnrealApi api, UnrealObjectHandle handle) : base(api, handle) { }
        public static UnrealClass<TestActor> StaticClass { get; } =
            new("/Script/Test.TestActor");
        public static TestActor FromObject(UnrealApi api, UnrealObjectHandle handle) =>
            new(api, handle);
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct FTestTransform : IUnrealStructValue
    {
        public const string UnrealPath = "/Script/CoreUObject.Transform";
        readonly int IUnrealStructValue.Size => 48;
        readonly void IUnrealStructValue.WriteTo(Span<byte> destination) => destination.Clear();
    }
}
