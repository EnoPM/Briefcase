using System.Reflection;
using System.Reflection.Emit;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Core.Tests;

public sealed class PatchDiscoveryTests
{
    [Fact]
    public void Discover_builds_a_typed_prefix_declaration()
    {
        var assembly = CreatePatchAssembly(
            typeof(UnrealPrefixPatchAttribute), typeof(void), isStatic: true,
            ("__instance", typeof(TestActor)), ("value", typeof(int)));

        using var patches = PatchDiscovery.Discover(assembly);

        var patch = Assert.Single(patches.Declarations);
        Assert.Equal(PatchBackend.Unreal, patch.Backend);
        Assert.Equal(PatchPhase.Prefix, patch.Phase);
        Assert.Equal(typeof(TestActor), patch.DeclaringType);
        Assert.Equal(nameof(TestActor.SetHealth), patch.Function.Name);
    }

    [Fact]
    public void Discover_rejects_an_instance_patch_method()
    {
        var assembly = CreatePatchAssembly(
            typeof(UnrealPrefixPatchAttribute), typeof(void), isStatic: false,
            ("__instance", typeof(TestActor)), ("value", typeof(int)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PatchDiscovery.Discover(assembly));
        Assert.Contains("must be static", error.Message);
    }

    [Fact]
    public void Discover_rejects_a_parameter_with_the_wrong_type()
    {
        var assembly = CreatePatchAssembly(
            typeof(UnrealPostfixPatchAttribute), typeof(void), isStatic: true,
            ("__instance", typeof(TestActor)), ("value", typeof(float)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            PatchDiscovery.Discover(assembly));
        Assert.Contains("must have type System.Int32", error.Message);
    }

    [Fact]
    public void Unreal_patch_accepts_writable_owning_struct_snapshots()
    {
        var method = typeof(PatchDiscoveryTests).GetMethod(
            nameof(OwningStructPatch), BindingFlags.NonPublic | BindingFlags.Static)!;
        var function = new UnrealFunction(
            "/Script/DeceiveInc.TestActor", "SetPayload", 16,
            [new UnrealParameter("payload", typeof(TestManagedPayload), 0, 16)]);
        var validate = typeof(PatchDiscovery).GetMethod(
            "ValidateMethod", BindingFlags.NonPublic | BindingFlags.Static)!;

        var error = Record.Exception(() =>
            validate.Invoke(null, [method, typeof(TestActor), function, PatchPhase.Prefix]));

        Assert.Null(error);
    }

    private static void OwningStructPatch(ref TestManagedPayload payload) { }

    [Fact]
    public void Unreal_patch_accepts_container_backed_struct_writeback()
    {
        var method = typeof(PatchDiscoveryTests).GetMethod(
            nameof(ContainerStructPatch), BindingFlags.NonPublic | BindingFlags.Static)!;
        var function = new UnrealFunction(
            "/Script/DeceiveInc.TestActor", "SetPayload", 16,
            [new UnrealParameter("payload", typeof(TestContainerPayload), 0, 16)]);
        var validate = typeof(PatchDiscovery).GetMethod(
            "ValidateMethod", BindingFlags.NonPublic | BindingFlags.Static)!;

        var error = Record.Exception(() =>
            validate.Invoke(null, [method, typeof(TestActor), function, PatchPhase.Prefix]));

        Assert.Null(error);
    }

    private static void ContainerStructPatch(ref TestContainerPayload payload) { }

    [Fact]
    public void Discover_distinguishes_native_patches()
    {
        var assembly = CreatePatchAssembly(
            typeof(NativePostfixPatchAttribute), typeof(void), isStatic: true,
            ("__instance", typeof(TestActor)), ("value", typeof(int)));

        using var patches = PatchDiscovery.Discover(assembly);
        var patch = Assert.Single(patches.Declarations);
        Assert.Equal(PatchBackend.Native, patch.Backend);
        Assert.Equal(PatchPhase.Postfix, patch.Phase);
    }

    private static Assembly CreatePatchAssembly(
        Type attributeType,
        Type returnType,
        bool isStatic,
        params (string Name, Type Type)[] parameters)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PatchFixture_" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Main");
        var type = module.DefineType("PatchContainer", TypeAttributes.Public);
        var attributes = MethodAttributes.Public;
        if (isStatic) attributes |= MethodAttributes.Static;
        var method = type.DefineMethod(
            "Patch", attributes, returnType, parameters.Select(item => item.Type).ToArray());
        for (var index = 0; index < parameters.Length; index++)
            method.DefineParameter(index + 1, ParameterAttributes.None, parameters[index].Name);
        var il = method.GetILGenerator();
        if (returnType == typeof(bool)) il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        var constructor = attributeType.GetConstructor([typeof(Type), typeof(string)])!;
        method.SetCustomAttribute(new CustomAttributeBuilder(
            constructor, [typeof(TestActor), nameof(TestActor.SetHealth)]));
        type.CreateType();
        return assembly;
    }
}

public sealed class TestActor : UnrealObject, IUnrealObject<TestActor>
{
    private TestActor(UnrealApi api, UnrealObjectHandle handle) : base(api, handle) { }

    public static TestActor FromObject(UnrealApi api, UnrealObjectHandle handle) =>
        new(api, handle);

    public void SetHealth(int value) => InvokeVoid(Functions.SetHealth, value);

    public static class Functions
    {
        public static UnrealFunction SetHealth { get; } = new(
            "/Script/DeceiveInc.TestActor",
            nameof(TestActor.SetHealth),
            sizeof(int),
            [new UnrealParameter("value", typeof(int), 0, sizeof(int))]);
    }
}

public struct TestManagedPayload : IUnrealManagedStructValue
{
    [UnrealStructField("Label", 0)] public string? Label;
    public TestManagedPayload() => Label = null;
}

public struct TestContainerPayload : IUnrealManagedStructValue
{
    [UnrealStructField("Values", 0)] public UnrealArray<int>? Values;
    public TestContainerPayload() => Values = null;
}
