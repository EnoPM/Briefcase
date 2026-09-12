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
            else if (type.Snapshot.Kind == "Enum")
                ValidateEnum(type, expected, actual);
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
        if (!actual.IsValueType || !expected.IsValueType)
            throw new InvalidDataException($"{plan.FullName} has an invalid struct contract.");
        if (plan.UsesManagedStructRepresentation)
        {
            if (!typeof(IUnrealManagedStructValue).IsAssignableFrom(actual) ||
                !typeof(IUnrealManagedStructValue).IsAssignableFrom(expected))
                throw new InvalidDataException($"{plan.FullName} has an invalid managed struct contract.");
        }
        else
        {
            if (!actual.IsExplicitLayout || !expected.IsExplicitLayout ||
                !typeof(IUnrealStructValue).IsAssignableFrom(actual) ||
                !typeof(IUnrealStructValue).IsAssignableFrom(expected) ||
                actual.StructLayoutAttribute?.Size != plan.Snapshot.Size ||
                expected.StructLayoutAttribute?.Size != plan.Snapshot.Size)
                throw new InvalidDataException($"{plan.FullName} has an invalid native struct contract.");
        }
        CompareConstant(expected, actual, "UnrealPath");
        CompareConstant(expected, actual, "NativeSize");
        ValidateMetadata(plan, expected, actual);

        foreach (var property in plan.Properties)
        {
            var expectedField = expected.GetField(
                property.MemberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? throw new MissingFieldException(expected.FullName, property.MemberName);
            var actualField = actual.GetField(
                property.MemberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?? throw new MissingFieldException(actual.FullName, property.MemberName);
            if (TypeIdentity(expectedField.FieldType) != TypeIdentity(actualField.FieldType))
                throw new InvalidDataException(
                    $"{plan.FullName}.{property.MemberName} differs from the reference SDK.");
            if (plan.UsesManagedStructRepresentation)
            {
                var expectedIdentity = expectedField.GetCustomAttribute<UnrealStructFieldAttribute>();
                var actualIdentity = actualField.GetCustomAttribute<UnrealStructFieldAttribute>();
                if (expectedIdentity?.Name != actualIdentity?.Name ||
                    expectedIdentity?.Offset != actualIdentity?.Offset)
                    throw new InvalidDataException(
                        $"{plan.FullName}.{property.MemberName} has a different Unreal field identity.");
            }
            else if (Marshal.OffsetOf(expected, property.MemberName) !=
                     Marshal.OffsetOf(actual, property.MemberName))
                throw new InvalidDataException(
                    $"{plan.FullName}.{property.MemberName} has a different native offset.");
        }

        if (!plan.UsesManagedStructRepresentation)
        {
            var bytes = new byte[plan.Snapshot.Size];
            ((IUnrealStructValue)Activator.CreateInstance(actual)!).WriteTo(bytes);
            if (bytes.Any(value => value != 0))
                throw new InvalidDataException($"{plan.FullName}.WriteTo corrupted a default value.");
        }
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
        ValidateMetadata(plan, expected, actual);

        var expectedProperties = expected.GetNestedType("Properties", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Properties");
        var actualProperties = actual.GetNestedType("Properties", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Properties");
        foreach (var property in plan.Properties)
        {
            CompareProperty(expected, actual, property.MemberName);
            CompareProperty(expectedProperties, actualProperties, property.MemberName);
            var expectedDescriptor =
                expectedProperties.GetProperty(property.MemberName)!.GetValue(null)!;
            var actualDescriptor =
                actualProperties.GetProperty(property.MemberName)!.GetValue(null)!;
            CompareDescriptorValue(
                expectedDescriptor,
                actualDescriptor,
                ["OwnerPath", "Name", "Offset", "Size"]);
            var expectedSignature = expectedDescriptor.GetType()
                .GetProperty("DelegateSignature")!.GetValue(expectedDescriptor) as UnrealFunction;
            var actualSignature = actualDescriptor.GetType()
                .GetProperty("DelegateSignature")!.GetValue(actualDescriptor) as UnrealFunction;
            if ((expectedSignature is null) != (actualSignature is null))
                throw new InvalidDataException(
                    $"{plan.FullName}.{property.MemberName} has a different delegate signature.");
            if (expectedSignature is not null)
                CompareFunctionDescriptor(expectedSignature, actualSignature!);
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

    private static void ValidateEnum(PlannedType plan, Type expected, Type actual)
    {
        if (!expected.IsEnum || !actual.IsEnum ||
            Enum.GetUnderlyingType(expected) != typeof(long) ||
            Enum.GetUnderlyingType(actual) != typeof(long))
            throw new InvalidDataException($"{plan.FullName} has an invalid enum contract.");

        var expectedPath = expected.GetCustomAttribute<UnrealTypePathAttribute>()?.Path;
        var actualPath = actual.GetCustomAttribute<UnrealTypePathAttribute>()?.Path;
        if (expectedPath != plan.Snapshot.Path || actualPath != plan.Snapshot.Path)
            throw new InvalidDataException($"{plan.FullName} has an invalid Unreal enum path.");

        var expectedValues = expected.GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(field => field.Name, field => Convert.ToInt64(field.GetRawConstantValue()),
                StringComparer.Ordinal);
        var actualValues = actual.GetFields(BindingFlags.Public | BindingFlags.Static)
            .ToDictionary(field => field.Name, field => Convert.ToInt64(field.GetRawConstantValue()),
                StringComparer.Ordinal);
        if (expectedValues.Count != actualValues.Count ||
            expectedValues.Any(pair => !actualValues.TryGetValue(pair.Key, out var value) ||
                                       value != pair.Value))
            throw new InvalidDataException($"{plan.FullName} has different enum values.");
    }

    private static void ValidateMetadata(PlannedType plan, Type expected, Type actual)
    {
        CompareProperty(expected, actual, "Reflection");
        var expectedType = (UnrealReflectedType)expected.GetProperty("Reflection")!.GetValue(null)!;
        var actualType = (UnrealReflectedType)actual.GetProperty("Reflection")!.GetValue(null)!;
        if (expectedType != actualType || actualType.Path != plan.Snapshot.Path)
            throw new InvalidDataException($"{plan.FullName} has different reflected type metadata.");

        var expectedMetadata = expected.GetNestedType("Metadata", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Metadata");
        var actualMetadata = actual.GetNestedType("Metadata", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Metadata");
        var expectedProperties = expectedMetadata.GetNestedType("Properties", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Metadata.Properties");
        var actualProperties = actualMetadata.GetNestedType("Properties", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Metadata.Properties");
        foreach (var property in plan.MetadataProperties)
        {
            CompareProperty(expectedProperties, actualProperties, property.MemberName);
            var left = (UnrealReflectedProperty)expectedProperties
                .GetProperty(property.MemberName)!.GetValue(null)!;
            var right = (UnrealReflectedProperty)actualProperties
                .GetProperty(property.MemberName)!.GetValue(null)!;
            if (left != right)
                throw new InvalidDataException(
                    $"{plan.FullName}.{property.MemberName} has different property metadata.");
        }

        var expectedFunctions = expectedMetadata.GetNestedType("Functions", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Metadata.Functions");
        var actualFunctions = actualMetadata.GetNestedType("Functions", BindingFlags.Public)
            ?? throw new MissingMemberException(plan.FullName, "Metadata.Functions");
        foreach (var function in plan.MetadataFunctions)
        {
            CompareProperty(expectedFunctions, actualFunctions, function.MemberName);
            var left = (UnrealReflectedFunction)expectedFunctions
                .GetProperty(function.MemberName)!.GetValue(null)!;
            var right = (UnrealReflectedFunction)actualFunctions
                .GetProperty(function.MemberName)!.GetValue(null)!;
            if (left.Name != right.Name || left.Flags != right.Flags ||
                left.ParameterBufferSize != right.ParameterBufferSize ||
                left.ParameterCount != right.ParameterCount ||
                left.Parameters.Count != right.Parameters.Count)
                throw new InvalidDataException(
                    $"{plan.FullName}.{function.MemberName} has different function metadata.");
            for (var index = 0; index < left.Parameters.Count; index++)
            {
                if (left.Parameters[index] != right.Parameters[index])
                    throw new InvalidDataException(
                        $"{plan.FullName}.{function.MemberName} parameter {index} differs.");
            }
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
                $"{actualOwner.FullName}.{function.MemberName} differs from the reference SDK. " +
                $"Expected {DescribeSignature(expected)}; actual {DescribeSignature(actual)}.");
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

    private static string DescribeSignature(MethodInfo method) =>
        $"{TypeIdentity(method.ReturnType)} {method.Name}(" +
        string.Join(", ", method.GetParameters().Select(parameter =>
            $"{TypeIdentity(parameter.ParameterType)} {parameter.Name}")) + ")";

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
        if (type.IsByRef) return TypeIdentity(type.GetElementType()) + "&";
        if (type.IsPointer) return TypeIdentity(type.GetElementType()) + "*";
        if (type.IsArray)
        {
            var commas = new string(',', type.GetArrayRank() - 1);
            return TypeIdentity(type.GetElementType()) + $"[{commas}]";
        }
        if (!type.IsGenericType) return type.FullName ?? type.Name;
        return type.GetGenericTypeDefinition().FullName + "[" +
               string.Join(",", type.GetGenericArguments().Select(TypeIdentity)) + "]";
    }
}
