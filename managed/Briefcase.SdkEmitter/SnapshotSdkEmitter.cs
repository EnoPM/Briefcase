using System.Reflection;
using System.Reflection.Emit;

namespace Briefcase.SdkEmitter;

internal readonly record struct EmissionResult(
    SdkEmissionPlan Plan, string AssemblyPath, int TypeCount, int PropertyCount, int FunctionCount);

/// <summary>
/// Emits the supported portion of an Unreal reflection snapshot as a normal IL
/// reference assembly. The work is split into shells, members, and completion:
/// every generated type therefore exists before a cross-type signature needs it.
/// </summary>
internal static class SnapshotSdkEmitter
{
    public static EmissionResult Emit(
        SdkSnapshot snapshot, string destination, string? assemblyNameOverride = null)
    {
        var plan = SdkEmissionPlan.Create(snapshot);
        destination = Path.GetFullPath(destination);

        var assemblyName = new AssemblyName(assemblyNameOverride ?? snapshot.SdkAssemblyName)
        {
            Version = new Version(1, 0, 0, 0)
        };
        using var types = new EmissionTypeSystem();
        var assembly = new PersistedAssemblyBuilder(assemblyName, types.CoreAssembly);
        var module = assembly.DefineDynamicModule(assemblyName.Name!);
        var state = new EmissionState(plan, module, types);

        state.DefineTypeShells();
        state.DefineMembers();
        state.CompleteTypes();

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        assembly.Save(temporary);
        CoreReferenceNormalizer.Normalize(temporary);
        File.Move(temporary, destination, overwrite: true);

        return new EmissionResult(
            plan,
            destination,
            plan.Types.Count,
            plan.Types.Sum(type => type.Properties.Count),
            plan.Types.Sum(type => type.Functions.Count));
    }

    private sealed class EmissionState(
        SdkEmissionPlan plan, ModuleBuilder module, EmissionTypeSystem types)
    {
        private readonly Dictionary<string, TypeBuilder> _builders = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ConstructorBuilder> _constructors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (TypeBuilder Properties, TypeBuilder Functions)> _descriptors =
            new(StringComparer.Ordinal);

        public void DefineTypeShells()
        {
            foreach (var type in plan.Types.Where(type => type.Snapshot.Kind == "ScriptStruct"))
                DefineStructShell(type);
            foreach (var type in plan.Types.Where(type => type.Snapshot.Kind == "Class"))
                DefineClassShell(type);
        }

        public void DefineMembers()
        {
            foreach (var type in plan.Types.Where(type => type.Snapshot.Kind == "ScriptStruct"))
                DefineStructMembers(type);
            foreach (var type in ClassesInBaseOrder())
                DefineClassMembers(type);
        }

        public void CompleteTypes()
        {
            // Struct signatures may refer to other struct TypeBuilders. All of
            // their definitions are complete before any class is finalized.
            foreach (var type in plan.Types.Where(type => type.Snapshot.Kind == "ScriptStruct"))
                _builders[type.Snapshot.Path].CreateType();

            foreach (var type in ClassesInBaseOrder())
            {
                var descriptors = _descriptors[type.Snapshot.Path];
                descriptors.Properties.CreateType();
                descriptors.Functions.CreateType();
                _builders[type.Snapshot.Path].CreateType();
            }
        }

        private void DefineStructShell(PlannedType type)
        {
            var builder = module.DefineType(
                type.FullName,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout |
                TypeAttributes.BeforeFieldInit,
                types.ValueType,
                PackingSize.Unspecified,
                type.Snapshot.Size);
            builder.AddInterfaceImplementation(types.IUnrealStructValue);
            DefineConstants(builder, type.Snapshot.Path, type.Snapshot.Size);
            _builders.Add(type.Snapshot.Path, builder);
        }

        private TypeBuilder DefineClassShell(PlannedType type)
        {
            if (_builders.TryGetValue(type.Snapshot.Path, out var existing)) return existing;

            var baseType = types.UnrealObject;
            if (type.Snapshot.SuperPath is { } parentPath &&
                plan.TypesByPath.TryGetValue(parentPath, out var parent) &&
                parent.Snapshot.Kind == "Class")
                baseType = DefineClassShell(parent);

            var builder = module.DefineType(
                type.FullName,
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
                baseType);
            builder.AddInterfaceImplementation(types.IUnrealObject.MakeGenericType(builder));
            DefineConstants(builder, type.Snapshot.Path, nativeSize: null);
            _builders.Add(type.Snapshot.Path, builder);
            return builder;
        }

        private void DefineStructMembers(PlannedType type)
        {
            var builder = _builders[type.Snapshot.Path];
            foreach (var property in type.Properties)
            {
                var field = builder.DefineField(
                    property.MemberName,
                    types.Resolve(property.Type, _builders),
                    FieldAttributes.Public);
                field.SetOffset(property.Snapshot.Offset);
            }

            DefineStructSize(builder, type.Snapshot.Size);
            DefineStructWrite(builder, type.Snapshot.Size);
        }

        private void DefineStructSize(TypeBuilder builder, int nativeSize)
        {
            var contract = types.IUnrealStructValue.GetProperty("Size")!.GetMethod!;
            var method = builder.DefineMethod(
                $"{types.IUnrealStructValue.FullName}.get_Size",
                MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                types.Int32,
                Type.EmptyTypes);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, nativeSize);
            il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(method, contract);
        }

        private void DefineStructWrite(TypeBuilder builder, int nativeSize)
        {
            var contract = types.IUnrealStructValue.GetMethod("WriteTo")!;
            var method = builder.DefineMethod(
                $"{types.IUnrealStructValue.FullName}.WriteTo",
                MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.HideBySig |
                MethodAttributes.NewSlot | MethodAttributes.Virtual,
                types.Void,
                [types.SpanByte]);
            var il = method.GetILGenerator();

            var length = types.SpanByte.GetProperty("Length")!.GetMethod!;
            var validLength = il.DefineLabel();
            il.Emit(OpCodes.Ldarga_S, 1);
            il.Emit(OpCodes.Call, length);
            il.Emit(OpCodes.Ldc_I4, nativeSize);
            il.Emit(OpCodes.Beq_S, validLength);
            il.Emit(OpCodes.Ldstr, "Native struct size mismatch.");
            il.Emit(OpCodes.Ldstr, "destination");
            il.Emit(OpCodes.Newobj, types.ArgumentException.GetConstructor([types.String, types.String])!);
            il.Emit(OpCodes.Throw);
            il.MarkLabel(validLength);

            var memoryWrite = types.MemoryMarshal
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(candidate => candidate.Name == "Write" &&
                                     candidate.IsGenericMethodDefinition &&
                                     candidate.GetParameters().Length == 2)
                .MakeGenericMethod(builder);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, memoryWrite);
            il.Emit(OpCodes.Ret);
            builder.DefineMethodOverride(method, contract);
        }

        private void DefineClassMembers(PlannedType type)
        {
            var builder = _builders[type.Snapshot.Path];
            var constructor = DefineConstructor(type, builder);
            DefineFromObject(builder, constructor);
            DefineStaticClass(builder, type.Snapshot.Path);

            var propertyDescriptors = builder.DefineNestedType(
                "Properties",
                TypeAttributes.NestedPublic | TypeAttributes.Abstract |
                TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
            foreach (var property in type.Properties)
                DefineProperty(builder, propertyDescriptors, type.Snapshot.Path, property);

            var functionDescriptors = builder.DefineNestedType(
                "Functions",
                TypeAttributes.NestedPublic | TypeAttributes.Abstract |
                TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit);
            foreach (var function in type.Functions)
                DefineFunction(builder, functionDescriptors, type.Snapshot.Path, function);

            _descriptors.Add(type.Snapshot.Path, (propertyDescriptors, functionDescriptors));
        }

        private ConstructorBuilder DefineConstructor(PlannedType type, TypeBuilder builder)
        {
            var constructor = builder.DefineConstructor(
                MethodAttributes.Family,
                CallingConventions.Standard,
                [types.UnrealApi, types.UnrealObjectHandle]);
            ConstructorInfo baseConstructor;
            if (type.Snapshot.SuperPath is { } parentPath && _constructors.TryGetValue(parentPath, out var parent))
            {
                baseConstructor = parent;
            }
            else
            {
                baseConstructor = types.UnrealObject.GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [types.UnrealApi, types.UnrealObjectHandle],
                    modifiers: null)
                    ?? throw new MissingMethodException("UnrealObject constructor was not found.");
            }

            var il = constructor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, baseConstructor);
            il.Emit(OpCodes.Ret);
            _constructors.Add(type.Snapshot.Path, constructor);
            return constructor;
        }

        private void DefineFromObject(TypeBuilder builder, ConstructorBuilder constructor)
        {
            var closedContract = types.IUnrealObject.MakeGenericType(builder);
            var method = builder.DefineMethod(
                "FromObject",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                builder,
                [types.UnrealApi, types.UnrealObjectHandle]);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Ret);

            var openContract = types.IUnrealObject.GetMethod(
                "FromObject", BindingFlags.Public | BindingFlags.Static)!;
            builder.DefineMethodOverride(method, TypeBuilder.GetMethod(closedContract, openContract));
        }

        private void DefineStaticClass(TypeBuilder builder, string unrealPath)
        {
            var closedType = types.UnrealClass.MakeGenericType(builder);
            var constructor = TypeBuilder.GetConstructor(
                closedType, types.UnrealClass.GetConstructors().Single());
            var getter = builder.DefineMethod(
                "get_StaticClass",
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName,
                closedType,
                Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldstr, unrealPath);
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Ret);
            builder.DefineProperty("StaticClass", PropertyAttributes.None, closedType, null)
                .SetGetMethod(getter);
        }

        private void DefineProperty(
            TypeBuilder owner,
            TypeBuilder descriptorOwner,
            string unrealPath,
            PlannedProperty property)
        {
            var valueType = types.Resolve(property.Type, _builders);
            var descriptorType = types.UnrealProperty.MakeGenericType(valueType);
            var descriptorConstructor = property.Type.Kind == ManagedTypeKind.GeneratedStruct
                ? TypeBuilder.GetConstructor(
                    descriptorType, types.UnrealProperty.GetConstructors().Single())
                : descriptorType.GetConstructors().Single();
            var descriptorGetter = descriptorOwner.DefineMethod(
                "get_" + property.MemberName,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName,
                descriptorType,
                Type.EmptyTypes);
            var descriptorIl = descriptorGetter.GetILGenerator();
            descriptorIl.Emit(OpCodes.Ldstr, unrealPath);
            descriptorIl.Emit(OpCodes.Ldstr, property.Snapshot.Name);
            descriptorIl.Emit(OpCodes.Ldc_I4, property.Snapshot.Offset);
            descriptorIl.Emit(OpCodes.Ldc_I4, property.Snapshot.ElementSize);
            descriptorIl.Emit(OpCodes.Newobj, descriptorConstructor);
            descriptorIl.Emit(OpCodes.Ret);
            descriptorOwner.DefineProperty(
                property.MemberName, PropertyAttributes.None, descriptorType, null)
                .SetGetMethod(descriptorGetter);

            var getter = owner.DefineMethod(
                "get_" + property.MemberName,
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                valueType,
                Type.EmptyTypes);
            var read = valueType == types.String
                ? types.UnrealObject.GetMethod(
                    "ReadString", BindingFlags.Instance | BindingFlags.NonPublic)!
                : valueType == types.UnrealText
                    ? types.UnrealObject.GetMethod(
                        "ReadText", BindingFlags.Instance | BindingFlags.NonPublic)!
                    : types.UnrealObject.GetMethod("Read")!.MakeGenericMethod(valueType);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, descriptorGetter);
            il.Emit(OpCodes.Call, read);
            il.Emit(OpCodes.Ret);
            owner.DefineProperty(property.MemberName, PropertyAttributes.None, valueType, null)
                .SetGetMethod(getter);
        }

        private void DefineFunction(
            TypeBuilder owner,
            TypeBuilder descriptorOwner,
            string unrealPath,
            PlannedFunction function)
        {
            var inputTypes = function.Inputs.Select(parameter =>
                types.Resolve(parameter.Type, _builders)).ToArray();
            var returnType = function.ReturnParameter is { } returned
                ? types.Resolve(returned.Type, _builders)
                : types.Void;

            var descriptorGetter = descriptorOwner.DefineMethod(
                "get_" + function.MemberName,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName,
                types.UnrealFunction,
                Type.EmptyTypes);
            EmitFunctionDescriptor(
                descriptorGetter.GetILGenerator(), unrealPath, function);
            descriptorOwner.DefineProperty(
                function.MemberName, PropertyAttributes.None, types.UnrealFunction, null)
                .SetGetMethod(descriptorGetter);

            var method = owner.DefineMethod(
                function.MemberName,
                MethodAttributes.Public | MethodAttributes.HideBySig,
                returnType,
                inputTypes);
            var inputs = function.Inputs.ToArray();
            for (var index = 0; index < inputs.Length; index++)
                method.DefineParameter(index + 1, ParameterAttributes.None, inputs[index].Snapshot.Name);

            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, descriptorGetter);
            EmitArgumentArray(il, inputTypes);
            if (function.ReturnParameter is null)
            {
                il.Emit(OpCodes.Call, types.UnrealObject.GetMethod(
                    "InvokeVoid", BindingFlags.Instance | BindingFlags.NonPublic)!);
            }
            else
            {
                var invoke = returnType == types.UnrealText
                    ? types.UnrealObject.GetMethod(
                        "InvokeText", BindingFlags.Instance | BindingFlags.NonPublic)!
                    : types.UnrealObject.GetMethod(
                        "Invoke", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .MakeGenericMethod(returnType);
                il.Emit(OpCodes.Call, invoke);
            }
            il.Emit(OpCodes.Ret);
        }

        private void EmitFunctionDescriptor(
            ILGenerator il, string unrealPath, PlannedFunction function)
        {
            var parameterConstructor = types.UnrealParameter.GetConstructors().Single();
            il.Emit(OpCodes.Ldstr, unrealPath);
            il.Emit(OpCodes.Ldstr, function.Snapshot.Name);
            il.Emit(OpCodes.Ldc_I4, function.Snapshot.ParameterSize);
            EmitParameterArray(il, function.Parameters, parameterConstructor);

            if (function.ReturnParameter is { } returned)
            {
                EmitTypeObject(il, types.Resolve(returned.Type, _builders));
            }
            else
            {
                il.Emit(OpCodes.Ldnull);
            }

            var functionConstructor = types.UnrealFunction.GetConstructors().Single();
            il.Emit(OpCodes.Newobj, functionConstructor);
            if (function.ReturnParameter is { } returnParameter)
            {
                var local = il.DeclareLocal(types.UnrealFunction);
                var nullableParameter = types.Nullable.MakeGenericType(types.UnrealParameter);
                il.Emit(OpCodes.Stloc, local);
                il.Emit(OpCodes.Ldloc, local);
                EmitParameter(il, returnParameter, parameterConstructor);
                il.Emit(OpCodes.Newobj, nullableParameter.GetConstructors().Single());
                il.Emit(OpCodes.Callvirt, types.UnrealFunction.GetProperty("ReturnParameter")!.SetMethod!);
                il.Emit(OpCodes.Ldloc, local);
            }
            il.Emit(OpCodes.Ret);
        }

        private void EmitParameterArray(
            ILGenerator il,
            IReadOnlyList<PlannedParameter> parameters,
            ConstructorInfo constructor)
        {
            il.Emit(OpCodes.Ldc_I4, parameters.Count);
            il.Emit(OpCodes.Newarr, types.UnrealParameter);
            for (var index = 0; index < parameters.Count; index++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, index);
                EmitParameter(il, parameters[index], constructor);
                il.Emit(OpCodes.Stelem, types.UnrealParameter);
            }
        }

        private void EmitParameter(
            ILGenerator il, PlannedParameter parameter, ConstructorInfo constructor)
        {
            il.Emit(OpCodes.Ldstr, parameter.Snapshot.Name);
            EmitTypeObject(il, types.Resolve(parameter.Type, _builders));
            il.Emit(OpCodes.Ldc_I4, parameter.Snapshot.Offset);
            il.Emit(OpCodes.Ldc_I4, parameter.Snapshot.ElementSize);
            il.Emit(parameter.IsReturn ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Newobj, constructor);
        }

        private void EmitTypeObject(ILGenerator il, Type type)
        {
            il.Emit(OpCodes.Ldtoken, type);
            il.Emit(OpCodes.Call, types.Type.GetMethod("GetTypeFromHandle")!);
        }

        private void EmitArgumentArray(ILGenerator il, IReadOnlyList<Type> inputTypes)
        {
            il.Emit(OpCodes.Ldc_I4, inputTypes.Count);
            il.Emit(OpCodes.Newarr, types.Object);
            for (var index = 0; index < inputTypes.Count; index++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, index);
                il.Emit(OpCodes.Ldarg, index + 1);
                if (inputTypes[index].IsValueType) il.Emit(OpCodes.Box, inputTypes[index]);
                il.Emit(OpCodes.Stelem_Ref);
            }
        }

        private IEnumerable<PlannedType> ClassesInBaseOrder()
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in plan.Types.Where(type => type.Snapshot.Kind == "Class"))
                foreach (var ordered in Visit(type, visited))
                    yield return ordered;
        }

        private IEnumerable<PlannedType> Visit(PlannedType type, HashSet<string> visited)
        {
            if (!visited.Add(type.Snapshot.Path)) yield break;
            if (type.Snapshot.SuperPath is { } parentPath &&
                plan.TypesByPath.TryGetValue(parentPath, out var parent) &&
                parent.Snapshot.Kind == "Class")
            {
                foreach (var ancestor in Visit(parent, visited)) yield return ancestor;
            }
            yield return type;
        }

        private void DefineConstants(TypeBuilder builder, string unrealPath, int? nativeSize)
        {
            var path = builder.DefineField(
                "UnrealPath",
                types.String,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal |
                FieldAttributes.HasDefault);
            path.SetConstant(unrealPath);
            if (nativeSize is not { } size) return;
            var native = builder.DefineField(
                "NativeSize",
                types.Int32,
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal |
                FieldAttributes.HasDefault);
            native.SetConstant(size);
        }
    }
}
