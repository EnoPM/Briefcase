using System.Reflection;
using Briefcase.ModApi;

namespace Briefcase.ManagedHost;

internal static class PatchDiscovery
{
    public static PatchSet Discover(Assembly assembly)
    {
        var declarations = new List<PatchDeclaration>();
        foreach (var patchType in assembly.GetTypes())
        {
            foreach (var method in patchType.GetMethods(
                         BindingFlags.Static | BindingFlags.Instance |
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.DeclaredOnly))
            {
                foreach (var attribute in method.GetCustomAttributes<PatchAttribute>(false))
                {
                    var phase = attribute switch
                    {
                        UnrealPrefixPatchAttribute or NativePrefixPatchAttribute => PatchPhase.Prefix,
                        UnrealPostfixPatchAttribute or NativePostfixPatchAttribute => PatchPhase.Postfix,
                        _ => throw new InvalidOperationException(
                            $"Unknown patch attribute {attribute.GetType().FullName}.")
                    };
                    var backend = attribute is NativePatchAttribute
                        ? PatchBackend.Native
                        : PatchBackend.Unreal;
                    var function = ResolveFunction(attribute);
                    ValidateOptions(method, attribute);
                    if (backend == PatchBackend.Native)
                        ValidateNativeTarget(method, function);
                    ValidateMethod(method, attribute.DeclaringType, function, phase);
                    declarations.Add(new PatchDeclaration(
                        backend,
                        attribute.DeclaringType,
                        function,
                        method,
                        phase,
                        attribute.Priority,
                        attribute.Before,
                        attribute.After,
                        attribute.BeforeMods,
                        attribute.AfterMods,
                        attribute.Reentrancy,
                        attribute.OnException,
                        attribute switch
                        {
                            UnrealPostfixPatchAttribute postfix => postfix.RunWhenOriginalSkipped,
                            NativePostfixPatchAttribute postfix => postfix.RunWhenOriginalSkipped,
                            _ => true
                        }));
                }
            }
        }
        return new PatchSet(declarations);
    }

    private static UnrealFunction ResolveFunction(PatchAttribute target)
    {
        if (!ImplementsUnrealObject(target.DeclaringType))
            throw new InvalidOperationException(
                $"{target.DeclaringType.FullName} is not a generated Unreal object class.");
        if (string.IsNullOrWhiteSpace(target.MethodName))
            throw new InvalidOperationException("Patch target method name cannot be empty.");

        // nameof(Spy.SomeFunction) proves that a C# member exists. We also
        // require one public instance method so generated descriptors cannot
        // accidentally describe a member that mods cannot invoke.
        var generatedMethods = target.DeclaringType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(candidate => candidate.Name == target.MethodName)
            .ToArray();
        if (generatedMethods.Length != 1)
            throw new InvalidOperationException(
                $"{target.DeclaringType.FullName}.{target.MethodName} must resolve to exactly one " +
                "generated instance method.");

        UnrealFunction? function = null;
        for (var current = target.DeclaringType; current is not null && function is null;
             current = current.BaseType)
        {
            var functions = current.GetNestedType("Functions", BindingFlags.Public);
            var property = functions?.GetProperty(
                target.MethodName, BindingFlags.Public | BindingFlags.Static);
            if (property?.PropertyType == typeof(UnrealFunction))
                function = property.GetValue(null) as UnrealFunction;
        }
        if (function is null)
            throw new InvalidOperationException(
                $"{target.DeclaringType.FullName}.Functions.{target.MethodName} " +
                "is not present in the generated SDK.");
        if (function.Name != target.MethodName)
            throw new InvalidOperationException(
                $"Generated function descriptor name mismatch for {target.MethodName}.");
        return function;
    }

    private static bool ImplementsUnrealObject(Type type) =>
        type.IsClass && type.GetInterfaces().Any(candidate =>
            candidate.IsGenericType &&
            candidate.GetGenericTypeDefinition() == typeof(IUnrealObject<>));

    private static void ValidateOptions(MethodInfo method, PatchAttribute attribute)
    {
        if (attribute.DeclaringType is null)
            throw new InvalidOperationException($"{method.Name}: patch declaring type cannot be null.");
        if (!Enum.IsDefined(attribute.Reentrancy))
            throw new InvalidOperationException($"{method.Name}: invalid reentrancy policy.");
        if (!Enum.IsDefined(attribute.OnException))
            throw new InvalidOperationException($"{method.Name}: invalid exception policy.");
        if (attribute.Before.Any(type => type is null) || attribute.After.Any(type => type is null))
            throw new InvalidOperationException($"{method.Name}: ordering types cannot contain null.");
        if (attribute.BeforeMods.Any(string.IsNullOrWhiteSpace) ||
            attribute.AfterMods.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{method.Name}: ordering mod identifiers cannot be empty.");
    }

    private static void ValidateNativeTarget(MethodInfo method, UnrealFunction function)
    {
        var inputs = function.Parameters.Where(parameter => !parameter.IsOut).ToArray();
        if (inputs.Length > 3 || function.ParameterBufferSize is < 0 or > 4096)
            throw new InvalidOperationException(
                $"{method.Name}: native patches support at most three input parameters and a " +
                "4096-byte reflected buffer.");
        foreach (var parameter in inputs)
            ValidateNativeValue(method, parameter.ManagedType, parameter.Size, false);
        if (function.ReturnParameter is { } result)
        {
            ValidateNativeValue(method, result.ManagedType, result.Size, true);
            if (result.ManagedType.IsValueType && !result.ManagedType.IsPrimitive &&
                result.Size is not (1 or 2 or 4 or 8) && inputs.Length != 0)
                throw new InvalidOperationException(
                    $"{method.Name}: native structure returns larger than eight bytes are " +
                    "currently supported only for functions without explicit parameters.");
        }
    }

    private static void ValidateNativeValue(
        MethodInfo method, Type type, int size, bool isReturn)
    {
        var scalar = type == typeof(sbyte) || type == typeof(byte) ||
                     type == typeof(short) || type == typeof(ushort) ||
                     type == typeof(int) || type == typeof(uint) ||
                     type == typeof(long) || type == typeof(ulong) ||
                     type == typeof(float) || type == typeof(double) || type == typeof(bool);
        var valueStruct = type.IsValueType && !type.IsPrimitive &&
                          type != typeof(UnrealObjectReference);
        var objectReference = type == typeof(UnrealObjectReference) && size == 8;
        if ((!scalar && !valueStruct && !objectReference) || size is <= 0 or > 4096)
            throw new InvalidOperationException(
                $"{method.Name}: native {(isReturn ? "return" : "parameter")} type " +
                $"{type.FullName} ({size} bytes) is not supported by the Windows x64 bridge.");
    }

    private static void ValidateMethod(
        MethodInfo method, Type declaringType, UnrealFunction function, PatchPhase phase)
    {
        if (!method.IsStatic || method.IsGenericMethodDefinition)
            throw new InvalidOperationException(
                $"{method.DeclaringType?.FullName}.{method.Name} must be static and non-generic.");
        if (phase == PatchPhase.Prefix)
        {
            if (method.ReturnType != typeof(void) && method.ReturnType != typeof(bool))
                throw new InvalidOperationException($"{method.Name}: a prefix must return void or bool.");
        }
        else if (method.ReturnType != typeof(void))
        {
            throw new InvalidOperationException($"{method.Name}: a postfix must return void.");
        }

        foreach (var parameter in method.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            var valueType = parameterType.IsByRef ? parameterType.GetElementType()! : parameterType;
            if (parameter.Name == "__instance")
            {
                if (parameterType.IsByRef || valueType != declaringType)
                    throw new InvalidOperationException(
                        $"{method.Name}.__instance must have type {declaringType.FullName}.");
                continue;
            }
            if (parameter.Name == "__runOriginal")
            {
                if (phase != PatchPhase.Prefix || parameterType != typeof(bool).MakeByRefType())
                    throw new InvalidOperationException(
                        $"{method.Name}.__runOriginal is supported only as ref bool in a prefix.");
                continue;
            }
            if (parameter.Name == "__result")
            {
                if (function.ReturnType is null || valueType != function.ReturnType)
                    throw new InvalidOperationException(
                        $"{method.Name}.__result does not match the generated return type.");
                continue;
            }

            var targetParameters = function.Parameters
                .Where(candidate => candidate.Name == parameter.Name)
                .ToArray();
            if (targetParameters.Length != 1)
                throw new InvalidOperationException(
                    $"{method.Name}.{parameter.Name} does not match a generated Unreal parameter.");
            var targetParameter = targetParameters[0];
            if (valueType != targetParameter.ManagedType)
                throw new InvalidOperationException(
                    $"{method.Name}.{parameter.Name} must have type {targetParameter.ManagedType.FullName}.");
        }
    }
}

internal sealed class PatchSet(List<PatchDeclaration> declarations) : IDisposable
{
    private readonly List<PatchDeclaration> _declarations = declarations;
    public int Count => _declarations.Count;
    public int UnrealCount => _declarations.Count(item => item.Backend == PatchBackend.Unreal);
    public int NativeCount => _declarations.Count(item => item.Backend == PatchBackend.Native);
    public IReadOnlyList<PatchDeclaration> Declarations => _declarations;

    public void Dispose()
    {
        // MethodInfo and ordering Type instances root their collectible assembly.
        // Clearing descriptors is mandatory before AssemblyLoadContext.Unload().
        _declarations.Clear();
    }
}

internal enum PatchPhase { Prefix, Postfix }
internal enum PatchBackend { Unreal, Native }

internal sealed record PatchDeclaration(
    PatchBackend Backend,
    Type DeclaringType,
    UnrealFunction Function,
    MethodInfo PatchMethod,
    PatchPhase Phase,
    int Priority,
    IReadOnlyList<Type> Before,
    IReadOnlyList<Type> After,
    IReadOnlyList<string> BeforeMods,
    IReadOnlyList<string> AfterMods,
    PatchReentrancy Reentrancy,
    PatchExceptionPolicy OnException,
    bool RunWhenOriginalSkipped);
