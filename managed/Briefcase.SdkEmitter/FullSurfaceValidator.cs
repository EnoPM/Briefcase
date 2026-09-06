using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Briefcase.ModApi;

namespace Briefcase.SdkEmitter;

internal readonly record struct SurfaceValidationResult(
    int TypeCount, int PropertyCount, int FunctionCount);

/// <summary>
/// Compares every planned member with the SDK produced by the established C#
/// generator. Reflection is used for API shape and System.Reflection.Metadata
/// is used for PE identity, so invalid metadata and API drift are both caught.
/// </summary>
internal static class FullSurfaceValidator
{
    public static SurfaceValidationResult Validate(
        string referencePath, string emittedPath, SdkEmissionPlan plan)
    {
        ValidatePeIdentity(emittedPath);
        var reference = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.GetFullPath(referencePath));
        var emitted = AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.GetFullPath(emittedPath));

        foreach (var type in plan.Types)
        {
            var expected = RequireType(reference, type.FullName);
            var actual = RequireType(emitted, type.FullName);
            if (type.Snapshot.Kind == "ScriptStruct")
                ValidateStruct(type, expected, actual);
            else
                ValidateClass(type, expected, actual);
        }

        return new SurfaceValidationResult(
            plan.Types.Count,
            plan.Types.Sum(type => type.Properties.Count),
            plan.Types.Sum(type => type.Functions.Count));
    }

    private static void ValidatePeIdentity(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new InvalidDataException("The persisted SDK has no CLR metadata.");
        var metadata = pe.GetMetadataReader();
        var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        if (!name.EndsWith(".EmitPrototype", StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected emitted assembly identity {name}.");
    }

    private static void ValidateStruct(PlannedType plan, Type expected, Type actual)
    {
        if (!actual.IsValueType || !actual.IsExplicitLayout ||
            !typeof(IUnrealStructValue).IsAssignableFrom(actual))
            throw new InvalidDataException($"{plan.FullName} has an invalid struct contract.");
        if (actual.StructLayoutAttribute?.Size != plan.Snapshot.Size ||
            expected.StructLayoutAttribute?.Size != plan.Snapshot.Size)
            throw new InvalidDataException($"{plan.FullName} has an invalid native size.");
        CompareConstant(expected, actual, "UnrealPath");
        CompareConstant(expected, actual, "NativeSize");

        foreach (var property in plan.Properties)
        {
            var expectedField = expected.GetField(
                property.MemberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? throw new MissingFieldException(expected.FullName, property.MemberName);
            var actualField = actual.GetField(
                property.MemberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? throw new MissingFieldException(actual.FullName, property.MemberName);
            if (TypeIdentity(expectedField.FieldType) != TypeIdentity(actualField.FieldType) ||
                Marshal.OffsetOf(expected, property.MemberName) !=
                Marshal.OffsetOf(actual, property.MemberName))
                throw new InvalidDataException(
                    $"{plan.FullName}.{property.MemberName} differs from the reference SDK.");
        }

        var bytes = new byte[plan.Snapshot.Size];
        ((IUnrealStructValue)Activator.CreateInstance(actual)!).WriteTo(bytes);
        if (bytes.Any(value => value != 0))
            throw new InvalidDataException($"{plan.FullName}.WriteTo corrupted a default value.");
    }

    private static void ValidateClass(PlannedType plan, Type expected, Type actual)
    {
        if (!typeof(UnrealObject).IsAssignableFrom(actual) ||
            !actual.GetInterfaces().Any(contract => contract.IsGenericType &&
                contract.GetGenericTypeDefinition() == typeof(IUnrealObject<>)))
            throw new InvalidDataException($"{plan.FullName} has an invalid object contract.");
        if (expected.BaseType?.FullName != actual.BaseType?.FullName)
            throw new InvalidDataException($"{plan.FullName} has a different generated base type.");

        CompareConstant(expected, actual, "UnrealPath");
        CompareProperty(expected, actual, "StaticClass");
        CompareDescriptorValue(
            expected.GetProperty("StaticClass")!.GetValue(null)!,
            actual.GetProperty("StaticClass")!.GetValue(null)!,
            ["Path"]);
        CompareFromObject(expected, actual, invoke: true);

        var expectedProperties = expected.GetNestedType("Properties", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Properties");
        var actualProperties = actual.GetNestedType("Properties", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Properties");
        foreach (var property in plan.Properties)
        {
            CompareProperty(expected, actual, property.MemberName);
            CompareProperty(expectedProperties, actualProperties, property.MemberName);
            CompareDescriptorValue(
                expectedProperties.GetProperty(property.MemberName)!.GetValue(null)!,
                actualProperties.GetProperty(property.MemberName)!.GetValue(null)!,
                ["OwnerPath", "Name", "Offset", "Size"]);
        }

        var expectedFunctions = expected.GetNestedType("Functions", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Functions");
        var actualFunctions = actual.GetNestedType("Functions", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Functions");
        foreach (var function in plan.Functions)
        {
            CompareFunction(expected, actual, function);
            CompareProperty(expectedFunctions, actualFunctions, function.MemberName);
            CompareFunctionDescriptor(
                (UnrealFunction)expectedFunctions.GetProperty(function.MemberName)!.GetValue(null)!,
                (UnrealFunction)actualFunctions.GetProperty(function.MemberName)!.GetValue(null)!);
        }
    }

    private static void CompareFromObject(Type expected, Type actual, bool invoke)
    {
        var expectedMethod = expected.GetMethod(
            "FromObject", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(expected.FullName, "FromObject");
        var actualMethod = actual.GetMethod(
            "FromObject", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(actual.FullName, "FromObject");
        if (TypeIdentity(expectedMethod.ReturnType) != TypeIdentity(actualMethod.ReturnType) ||
            !ParameterTypes(expectedMethod).SequenceEqual(ParameterTypes(actualMethod)))
            throw new InvalidDataException($"{actual.FullName}.FromObject differs from the reference SDK.");
        if (!invoke) return;
        var handle = new Briefcase.ModApi.Interop.UnrealObjectHandle(uint.MaxValue, 0);
        if (actualMethod.Invoke(null, [default(UnrealApi), handle]) is not UnrealObject { IsNull: true })
            throw new InvalidDataException($"{actual.FullName}.FromObject returned an invalid wrapper.");
    }

    private static void CompareFunctionDescriptor(UnrealFunction expected, UnrealFunction actual)
    {
        if (expected.OwnerPath != actual.OwnerPath || expected.Name != actual.Name ||
            expected.ParameterBufferSize != actual.ParameterBufferSize ||
            TypeIdentity(expected.ReturnType) != TypeIdentity(actual.ReturnType) ||
            expected.Parameters.Count != actual.Parameters.Count)
            throw new InvalidDataException($"{actual.OwnerPath}.{actual.Name} descriptor differs.");

        for (var index = 0; index < expected.Parameters.Count; index++)
        {
            var left = expected.Parameters[index];
            var right = actual.Parameters[index];
            if (left.Name != right.Name || left.Offset != right.Offset || left.Size != right.Size ||
                left.IsOut != right.IsOut ||
                TypeIdentity(left.ManagedType) != TypeIdentity(right.ManagedType))
                throw new InvalidDataException($"{actual.OwnerPath}.{actual.Name} parameter {index} differs.");
        }

        if (expected.ReturnParameter is { } expectedReturn)
        {
            if (actual.ReturnParameter is not { } actualReturn ||
                expectedReturn.Name != actualReturn.Name ||
                expectedReturn.Offset != actualReturn.Offset ||
                expectedReturn.Size != actualReturn.Size ||
                expectedReturn.IsOut != actualReturn.IsOut ||
                TypeIdentity(expectedReturn.ManagedType) != TypeIdentity(actualReturn.ManagedType))
                throw new InvalidDataException(
                    $"{actual.OwnerPath}.{actual.Name} return descriptor differs: " +
                    $"expected name={expectedReturn.Name}, offset={expectedReturn.Offset}, " +
                    $"size={expectedReturn.Size}; actual present={actual.ReturnParameter.HasValue}.");
        }
        else if (actual.ReturnParameter is not null)
        {
            throw new InvalidDataException($"{actual.OwnerPath}.{actual.Name} has an unexpected return descriptor.");
        }
    }

    private static void CompareDescriptorValue(
        object expected, object actual, IReadOnlyList<string> propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var expectedProperty = expected.GetType().GetProperty(propertyName)
                ?? throw new MissingMemberException(expected.GetType().FullName, propertyName);
            var actualProperty = actual.GetType().GetProperty(propertyName)
                ?? throw new MissingMemberException(actual.GetType().FullName, propertyName);
            var left = expectedProperty.GetValue(expected);
            var right = actualProperty.GetValue(actual);
            if (!Equals(left, right))
                throw new InvalidDataException(
                    $"Descriptor property {actual.GetType().FullName}.{propertyName} differs.");
        }
    }

    private static void CompareFunction(
        Type expectedOwner, Type actualOwner, PlannedFunction function)
    {
        var expected = RequireDeclaredMethod(expectedOwner, function.MemberName);
        var actual = RequireDeclaredMethod(actualOwner, function.MemberName);
        if (TypeIdentity(expected.ReturnType) != TypeIdentity(actual.ReturnType) ||
            !ParameterTypes(expected).SequenceEqual(ParameterTypes(actual)) ||
            !expected.GetParameters().Select(parameter => parameter.Name)
                .SequenceEqual(actual.GetParameters().Select(parameter => parameter.Name)))
            throw new InvalidDataException(
                $"{actualOwner.FullName}.{function.MemberName} differs from the reference SDK.");
    }

    private static MethodInfo RequireDeclaredMethod(Type owner, string name)
    {
        var methods = owner.GetMethods(BindingFlags.Public | BindingFlags.Instance |
                                       BindingFlags.DeclaredOnly)
            .Where(method => method.Name == name)
            .ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new MissingMethodException(owner.FullName, name);
    }

    private static IEnumerable<string?> ParameterTypes(MethodInfo method) =>
        method.GetParameters().Select(parameter => TypeIdentity(parameter.ParameterType));

    private static void CompareConstant(Type expected, Type actual, string name)
    {
        var expectedField = expected.GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException(expected.FullName, name);
        var actualField = actual.GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingFieldException(actual.FullName, name);
        if (!expectedField.IsLiteral || !actualField.IsLiteral ||
            !Equals(expectedField.GetRawConstantValue(), actualField.GetRawConstantValue()))
            throw new InvalidDataException($"{actual.FullName}.{name} differs from the reference SDK.");
    }

    private static void CompareProperty(Type expected, Type actual, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static |
                                   BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var expectedProperty = expected.GetProperty(name, flags)
            ?? throw new MissingMemberException(expected.FullName, name);
        var actualProperty = actual.GetProperty(name, flags)
            ?? throw new MissingMemberException(actual.FullName, name);
        if (TypeIdentity(expectedProperty.PropertyType) != TypeIdentity(actualProperty.PropertyType))
            throw new InvalidDataException($"{actual.FullName}.{name} has a different property type.");
    }

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true, ignoreCase: false)!;

    private static string? TypeIdentity(Type? type)
    {
        if (type is null) return null;
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        return type.GetGenericTypeDefinition().FullName + "[" +
               string.Join(",", type.GetGenericArguments().Select(TypeIdentity)) + "]";
    }
}
