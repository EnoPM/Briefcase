using System.Security.Cryptography;
using System.Text;
using Briefcase.ModApi;

namespace Briefcase.SdkEmitter;

/// <summary>
/// Separates the complete reflected metadata surface from the smaller set of
/// values that the current native ABI can safely marshal. Unknown or owning
/// Unreal values remain visible through Metadata without acquiring a misleading
/// C# getter or callable method.
/// </summary>
internal sealed class SdkEmissionPlan
{
    private const ulong OutParameterFlag = 0x100;
    private const ulong ReturnParameterFlag = 0x400;
    private const ulong ReferenceParameterFlag = 0x08000000;
    private const ulong ConstParameterFlag = 0x2;

    private SdkEmissionPlan(
        SdkSnapshot snapshot,
        IReadOnlyList<PlannedType> types,
        IReadOnlyDictionary<string, PlannedType> typesByPath,
        int skippedTypeCount,
        int skippedPropertyCount,
        int skippedFunctionCount)
    {
        Snapshot = snapshot;
        Types = types;
        TypesByPath = typesByPath;
        SkippedTypeCount = skippedTypeCount;
        SkippedPropertyCount = skippedPropertyCount;
        SkippedFunctionCount = skippedFunctionCount;
    }

    public SdkSnapshot Snapshot { get; }
    public IReadOnlyList<PlannedType> Types { get; }
    public IReadOnlyDictionary<string, PlannedType> TypesByPath { get; }
    public int SkippedTypeCount { get; }
    public int SkippedPropertyCount { get; }
    public int SkippedFunctionCount { get; }
    public int DescribedPropertyCount => Types.Sum(type => type.MetadataProperties.Count);
    public int DescribedFunctionCount => Types.Sum(type => type.MetadataFunctions.Count);

    public static SdkEmissionPlan Create(SdkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = snapshot.Types
            .Where(type => type.Kind is "Class" or "ScriptStruct" or "Enum" &&
                           type.Path.StartsWith("/Script/", StringComparison.Ordinal))
            .GroupBy(type => type.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(type => type.Path, StringComparer.Ordinal)
            .ToArray();

        var usable = candidates
            .Where(type => type.Kind is "Class" or "Enum" || type.Size is > 0 and <= 65_535)
            .ToArray();
        var names = BuildTypeNames(usable);
        var knownPaths = names.Keys.ToHashSet(StringComparer.Ordinal);
        var knownTypes = usable.ToDictionary(type => type.Path, StringComparer.Ordinal);
        var managedStructPaths = usable
            .Where(type => type.Kind == "ScriptStruct" &&
                           !IsCanonicalGeneratedStruct(
                               type.Path, knownPaths, knownTypes,
                               new HashSet<string>(StringComparer.Ordinal)))
            .Select(type => type.Path)
            .ToHashSet(StringComparer.Ordinal);
        var writableManagedStructPaths = managedStructPaths
            .Where(path => IsWireWritableGeneratedStruct(
                path, knownPaths, knownTypes, new HashSet<string>(StringComparer.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
        var planned = new List<PlannedType>(usable.Length);
        var skippedProperties = 0;
        var skippedFunctions = 0;

        foreach (var type in usable)
        {
            var metadataProperties = PlanMetadataProperties(type);
            var metadataFunctions = PlanMetadataFunctions(type);
            if (type.Kind == "Enum")
            {
                planned.Add(new PlannedType(
                    type, names[type.Path], [], [], metadataProperties, metadataFunctions,
                    UsesManagedStructRepresentation: false,
                    SupportsManagedStructWrite: false));
                continue;
            }

            var used = type.Kind == "Class"
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    "Handle", "Name", "Path", "ClassHandle", "StaticClass", "Properties",
                    "Functions", "Metadata", "Reflection", "UnrealPath", "FromObject", "Read",
                    "Reference", "Outer", "Flags", "IsClassDefaultObject",
                    "ReadString", "ReadText", "InvokeText", "IsA", names[type.Path].Name
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    "Metadata", "Reflection", "UnrealPath", "NativeSize", names[type.Path].Name
                };

            var properties = new List<PlannedProperty>();
            foreach (var property in type.Properties.OrderBy(property =>
                         type.Kind == "Class" ? property.Name : property.Offset.ToString("D10"),
                         StringComparer.Ordinal))
            {
                var allowAggregateRead = type.Kind is "Class" or "ScriptStruct";
                var managedType = ManagedTypeDescriptor.TryCreate(
                    property, knownPaths,
                    allowReadContainers: allowAggregateRead,
                    allowStringProperty: allowAggregateRead,
                    allowTextProperty: allowAggregateRead);
                var validLayout = type.Kind == "Class" ||
                    property.Offset >= 0 && property.ElementSize > 0 &&
                    property.Offset <= type.Size - property.ElementSize;
                if (managedType is null || property.ArrayDimension != 1 || !validLayout)
                {
                    skippedProperties++;
                    continue;
                }

                PlannedFunction? delegateSignature = null;
                if (managedType.Kind == ManagedTypeKind.MulticastDelegate &&
                    property.EffectiveType.DelegateSignature is { } signature)
                {
                    delegateSignature = TryPlanFunction(
                        signature, knownPaths, knownTypes, managedStructPaths,
                        writableManagedStructPaths,
                        new HashSet<string>(StringComparer.Ordinal));
                }

                properties.Add(new PlannedProperty(
                    property,
                    managedType,
                    UniqueMember(property.Name, type.Kind == "Class" ? "Property" : "Field", used),
                    type.Kind == "Class" &&
                    IsEncodedWireType(managedType, managedStructPaths) &&
                    IsWireWritableType(
                        managedType, writableManagedStructPaths),
                    delegateSignature));
            }

            var functions = new List<PlannedFunction>();
            if (type.Kind == "Class")
            {
                foreach (var function in type.Functions.OrderBy(
                             function => function.Name, StringComparer.Ordinal))
                {
                    var plannedFunction = TryPlanFunction(
                        function, knownPaths, knownTypes, managedStructPaths,
                        writableManagedStructPaths, used);
                    if (plannedFunction is null) skippedFunctions++;
                    else functions.Add(plannedFunction);
                }
            }

            planned.Add(new PlannedType(
                type, names[type.Path], properties, functions,
                metadataProperties, metadataFunctions,
                type.Kind == "ScriptStruct" && managedStructPaths.Contains(type.Path),
                type.Kind == "ScriptStruct" && writableManagedStructPaths.Contains(type.Path)));
        }

        var byPath = planned.ToDictionary(type => type.Snapshot.Path, StringComparer.Ordinal);
        return new SdkEmissionPlan(
            snapshot, planned, byPath, candidates.Length - usable.Length,
            skippedProperties, skippedFunctions);
    }

    private static IReadOnlyList<PlannedMetadataProperty> PlanMetadataProperties(TypeSnapshot type)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "Properties" };
        return type.Properties
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ThenBy(property => property.Offset)
            .Select(property => new PlannedMetadataProperty(
                property, UniqueMember(property.Name, "Property", used)))
            .ToArray();
    }

    private static IReadOnlyList<PlannedMetadataFunction> PlanMetadataFunctions(TypeSnapshot type)
    {
        var used = new HashSet<string>(StringComparer.Ordinal) { "Functions" };
        return type.Functions
            .OrderBy(function => function.Name, StringComparer.Ordinal)
            .Select(function => new PlannedMetadataFunction(
                function, UniqueMember(function.Name, "Function", used)))
            .ToArray();
    }

    private static PlannedFunction? TryPlanFunction(
        FunctionSnapshot function,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes,
        IReadOnlySet<string> managedStructPaths,
        IReadOnlySet<string> writableManagedStructPaths,
        HashSet<string> usedMembers)
    {
        var parameters = function.Parameters.OrderBy(parameter => parameter.Offset).ToArray();
        if (parameters.Count(IsReturnParameter) > 1) return null;

        var planned = new List<PlannedParameter>(parameters.Length);
        foreach (var parameter in parameters)
        {
            var isReturn = IsReturnParameter(parameter);
            var managedType = ManagedTypeDescriptor.TryCreate(
                parameter, knownPaths, allowInputContainers: !isReturn,
                allowReadContainers: true,
                allowTextProperty: true);
            if (managedType is null || parameter.ArrayDimension != 1 ||
                !CanUseExactParameterName(parameter.Name))
                return null;

            // UE represents const FStruct& as ConstParm | OutParm | ReferenceParm.
            // It remains an ordinary input in the ProcessEvent buffer. Writable
            // references are emitted as C# ref parameters and pure outputs as
            // C# out parameters.
            var isConstReference =
                (parameter.Flags & (ConstParameterFlag | ReferenceParameterFlag)) ==
                (ConstParameterFlag | ReferenceParameterFlag);
            var isOutput = !isReturn && !isConstReference &&
                           (parameter.Flags & OutParameterFlag) != 0;
            var isReference = isOutput &&
                              (parameter.Flags & ReferenceParameterFlag) != 0;
            if (managedType.Kind is ManagedTypeKind.InterfaceReference or
                    ManagedTypeKind.LazyObjectReference or
                    ManagedTypeKind.SoftObjectReference or
                    ManagedTypeKind.SoftClassReference or ManagedTypeKind.Delegate or
                    ManagedTypeKind.MulticastDelegate or ManagedTypeKind.FieldPath)
                return null;
            planned.Add(new PlannedParameter(
                parameter, managedType, isReturn, isOutput, isReference));
        }

        // Generated structs containing UObject or weak-object fields use an
        // address-free canonical image. The native prepared plan converts each
        // nested handle immediately around ProcessEvent. Structs with owning
        // FString/FText/container fields use managed snapshots. Pure out and
        // return values and safely reconstructible inputs have a lifetime-aware
        // prepared invocation path backed by Unreal's allocator and FProperty
        // construction rules.
        bool IsManagedStructParameter(PlannedParameter parameter) =>
            parameter.Type.Kind == ManagedTypeKind.GeneratedStruct &&
            parameter.Type.ReferencedTypePath is { } path &&
            managedStructPaths.Contains(path);
        bool IsEncodedWireParameter(PlannedParameter parameter) =>
            IsManagedStructParameter(parameter) ||
            parameter.Type.Kind is ManagedTypeKind.String or ManagedTypeKind.Text or
                ManagedTypeKind.ByteArray or ManagedTypeKind.Array or
                ManagedTypeKind.Set or ManagedTypeKind.Map;
        var encodedWireParameters = planned.Where(IsEncodedWireParameter).ToArray();
        var supportsPreparedValueOutputInvocation = encodedWireParameters.Length > 0 &&
            encodedWireParameters.All(parameter =>
                parameter.IsReturn || parameter.IsOutput && !parameter.IsReference ||
                IsWireWritableType(parameter.Type, writableManagedStructPaths)) &&
            planned.Where(parameter => !IsEncodedWireParameter(parameter))
                .All(parameter => IsPreparedParameter(parameter, knownPaths, knownTypes));
        var supportsDirectInvocation = encodedWireParameters.Length == 0 ||
                                       supportsPreparedValueOutputInvocation;

        var supportsPreparedInvocation = encodedWireParameters.Length == 0 &&
            supportsDirectInvocation &&
            planned.All(parameter => IsPreparedParameter(
                parameter, knownPaths, knownTypes));
        var preparedFastPath = supportsPreparedInvocation && planned.All(parameter =>
            parameter.Type.Kind switch
            {
                ManagedTypeKind.Boolean or ManagedTypeKind.Int8 or ManagedTypeKind.UInt8 or
                ManagedTypeKind.Int16 or ManagedTypeKind.UInt16 or ManagedTypeKind.Int32 or
                ManagedTypeKind.UInt32 or ManagedTypeKind.Int64 or ManagedTypeKind.UInt64 or
                ManagedTypeKind.Float or ManagedTypeKind.Double or ManagedTypeKind.Name => true,
                ManagedTypeKind.ObjectReference =>
                    parameter.Snapshot.EffectiveType.UnrealType is
                        "ObjectProperty" or "ClassProperty",
                ManagedTypeKind.GeneratedStruct => IsPlainGeneratedStruct(
                    parameter.Type.ReferencedTypePath, knownPaths, knownTypes,
                    new HashSet<string>(StringComparer.Ordinal)),
                _ => false
            });
        return new PlannedFunction(
            function, UniqueMember(function.Name, "Method", usedMembers), planned,
            supportsDirectInvocation, supportsPreparedInvocation,
            supportsPreparedValueOutputInvocation, preparedFastPath);
    }

    private static bool IsPreparedParameter(
        PlannedParameter parameter,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes) =>
        parameter.Type.Kind switch
        {
            ManagedTypeKind.Boolean or ManagedTypeKind.Int8 or ManagedTypeKind.UInt8 or
            ManagedTypeKind.Int16 or ManagedTypeKind.UInt16 or ManagedTypeKind.Int32 or
            ManagedTypeKind.UInt32 or ManagedTypeKind.Int64 or ManagedTypeKind.UInt64 or
            ManagedTypeKind.Float or ManagedTypeKind.Double or ManagedTypeKind.Name or
            ManagedTypeKind.ObjectReference => true,
            ManagedTypeKind.GeneratedStruct => IsCanonicalGeneratedStruct(
                parameter.Type.ReferencedTypePath, knownPaths, knownTypes,
                new HashSet<string>(StringComparer.Ordinal)),
            _ => false
        };

    private static bool IsPlainGeneratedStruct(
        string? path,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes,
        HashSet<string> visiting) =>
        IsFixedGeneratedStruct(
            path, knownPaths, knownTypes, visiting, allowObjectHandles: false);

    private static bool IsCanonicalGeneratedStruct(
        string? path,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes,
        HashSet<string> visiting) =>
        IsFixedGeneratedStruct(
            path, knownPaths, knownTypes, visiting, allowObjectHandles: true);

    private static bool IsWireWritableGeneratedStruct(
        string? path,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes,
        HashSet<string> visiting)
    {
        if (path is null || !knownTypes.TryGetValue(path, out var type) ||
            type.Kind != "ScriptStruct" || type.Size <= 0 || !visiting.Add(path))
            return false;
        try
        {
            if (type.SuperPath is { } parent &&
                !IsWireWritableGeneratedStruct(parent, knownPaths, knownTypes, visiting))
                return false;
            foreach (var property in type.Properties)
            {
                if (property.ArrayDimension != 1 || property.Offset < 0 ||
                    property.ElementSize <= 0 ||
                    property.Offset > type.Size - property.ElementSize)
                    return false;
                var managed = ManagedTypeDescriptor.TryCreate(
                    property, knownPaths, allowReadContainers: true,
                    allowStringProperty: true, allowTextProperty: true);
                if (managed is null) return false;
                if (!IsWireWritableType(
                        managed, knownPaths, knownTypes, visiting))
                    return false;
            }
            return true;
        }
        finally
        {
            visiting.Remove(path);
        }
    }

    private static bool IsWireWritableType(
        ManagedTypeDescriptor type,
        IReadOnlySet<string> writableManagedStructPaths) =>
        type.Kind switch
        {
            ManagedTypeKind.Boolean or ManagedTypeKind.Int8 or ManagedTypeKind.UInt8 or
            ManagedTypeKind.Int16 or ManagedTypeKind.UInt16 or ManagedTypeKind.Int32 or
            ManagedTypeKind.UInt32 or ManagedTypeKind.Int64 or ManagedTypeKind.UInt64 or
            ManagedTypeKind.Float or ManagedTypeKind.Double or ManagedTypeKind.Name or
            ManagedTypeKind.ObjectReference or ManagedTypeKind.String or ManagedTypeKind.Text => true,
            ManagedTypeKind.GeneratedStruct => type.ReferencedTypePath is { } path &&
                                               writableManagedStructPaths.Contains(path),
            ManagedTypeKind.ByteArray => true,
            ManagedTypeKind.Array or ManagedTypeKind.Set => type.InnerType is { } inner &&
                IsWireWritableType(inner, writableManagedStructPaths),
            ManagedTypeKind.Map => type.KeyType is { } key && type.ValueType is { } value &&
                IsWireWritableType(key, writableManagedStructPaths) &&
                IsWireWritableType(value, writableManagedStructPaths),
            _ => false
        };

    private static bool IsWireWritableType(
        ManagedTypeDescriptor type,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes,
        HashSet<string> visiting) =>
        type.Kind switch
        {
            ManagedTypeKind.Boolean or ManagedTypeKind.Int8 or ManagedTypeKind.UInt8 or
            ManagedTypeKind.Int16 or ManagedTypeKind.UInt16 or ManagedTypeKind.Int32 or
            ManagedTypeKind.UInt32 or ManagedTypeKind.Int64 or ManagedTypeKind.UInt64 or
            ManagedTypeKind.Float or ManagedTypeKind.Double or ManagedTypeKind.Name or
            ManagedTypeKind.ObjectReference or ManagedTypeKind.String or ManagedTypeKind.Text => true,
            ManagedTypeKind.GeneratedStruct => IsWireWritableGeneratedStruct(
                type.ReferencedTypePath, knownPaths, knownTypes, visiting),
            ManagedTypeKind.ByteArray => true,
            ManagedTypeKind.Array or ManagedTypeKind.Set => type.InnerType is { } inner &&
                IsWireWritableType(inner, knownPaths, knownTypes, visiting),
            ManagedTypeKind.Map => type.KeyType is { } key && type.ValueType is { } value &&
                IsWireWritableType(key, knownPaths, knownTypes, visiting) &&
                IsWireWritableType(value, knownPaths, knownTypes, visiting),
            _ => false
        };

    private static bool IsEncodedWireType(
        ManagedTypeDescriptor type,
        IReadOnlySet<string> managedStructPaths) =>
        type.Kind is ManagedTypeKind.ByteArray or ManagedTypeKind.Array or
            ManagedTypeKind.Set or ManagedTypeKind.Map ||
        type.Kind == ManagedTypeKind.GeneratedStruct &&
        type.ReferencedTypePath is { } path && managedStructPaths.Contains(path);

    private static bool IsFixedGeneratedStruct(
        string? path,
        IReadOnlySet<string> knownPaths,
        IReadOnlyDictionary<string, TypeSnapshot> knownTypes,
        HashSet<string> visiting,
        bool allowObjectHandles)
    {
        if (path is null || !knownTypes.TryGetValue(path, out var type) ||
            type.Kind != "ScriptStruct" || type.Size <= 0 || !visiting.Add(path))
            return false;
        try
        {
            if (type.SuperPath is { } parent &&
                !IsFixedGeneratedStruct(
                    parent, knownPaths, knownTypes, visiting, allowObjectHandles))
                return false;
            foreach (var property in type.Properties)
            {
                if (property.ArrayDimension != 1 || property.Offset < 0 ||
                    property.ElementSize <= 0 ||
                    property.Offset > type.Size - property.ElementSize)
                    return false;
                var managed = ManagedTypeDescriptor.TryCreate(property, knownPaths);
                if (managed is null) return false;
                if (managed.Kind == ManagedTypeKind.GeneratedStruct)
                {
                    if (!IsFixedGeneratedStruct(
                            managed.ReferencedTypePath, knownPaths, knownTypes,
                            visiting, allowObjectHandles))
                        return false;
                    continue;
                }
                if (managed.Kind is ManagedTypeKind.ObjectReference && allowObjectHandles)
                    continue;
                if (managed.Kind is not (
                    ManagedTypeKind.Boolean or ManagedTypeKind.Int8 or ManagedTypeKind.UInt8 or
                    ManagedTypeKind.Int16 or ManagedTypeKind.UInt16 or ManagedTypeKind.Int32 or
                    ManagedTypeKind.UInt32 or ManagedTypeKind.Int64 or ManagedTypeKind.UInt64 or
                    ManagedTypeKind.Float or ManagedTypeKind.Double or ManagedTypeKind.Name))
                    return false;
            }
            return true;
        }
        finally
        {
            visiting.Remove(path);
        }
    }

    private static bool IsReturnParameter(PropertySnapshot parameter) =>
        (parameter.Flags & ReturnParameterFlag) != 0;

    internal static UnrealTypeKind MetadataKind(UnrealTypeSnapshot type) => type.UnrealType switch
    {
        "BoolProperty" => UnrealTypeKind.Boolean,
        "Int8Property" => UnrealTypeKind.Int8,
        "ByteProperty" => type.ReferencedTypePath is null ? UnrealTypeKind.UInt8 : UnrealTypeKind.Enum,
        "Int16Property" => UnrealTypeKind.Int16,
        "UInt16Property" => UnrealTypeKind.UInt16,
        "IntProperty" => UnrealTypeKind.Int32,
        "UInt32Property" => UnrealTypeKind.UInt32,
        "Int64Property" => UnrealTypeKind.Int64,
        "UInt64Property" => UnrealTypeKind.UInt64,
        "FloatProperty" => UnrealTypeKind.Float,
        "DoubleProperty" => UnrealTypeKind.Double,
        "EnumProperty" => UnrealTypeKind.Enum,
        "NameProperty" => UnrealTypeKind.Name,
        "StrProperty" => UnrealTypeKind.String,
        "TextProperty" => UnrealTypeKind.Text,
        "ObjectProperty" => UnrealTypeKind.Object,
        "ClassProperty" => UnrealTypeKind.Class,
        "InterfaceProperty" => UnrealTypeKind.Interface,
        "WeakObjectProperty" => UnrealTypeKind.WeakObject,
        "LazyObjectProperty" => UnrealTypeKind.LazyObject,
        "SoftObjectProperty" => UnrealTypeKind.SoftObject,
        "SoftClassProperty" => UnrealTypeKind.SoftClass,
        "StructProperty" => UnrealTypeKind.Struct,
        "ArrayProperty" => UnrealTypeKind.Array,
        "SetProperty" => UnrealTypeKind.Set,
        "MapProperty" => UnrealTypeKind.Map,
        "DelegateProperty" => UnrealTypeKind.Delegate,
        "MulticastDelegateProperty" or "MulticastInlineDelegateProperty" or
        "MulticastSparseDelegateProperty" => UnrealTypeKind.MulticastDelegate,
        "FieldPathProperty" => UnrealTypeKind.FieldPath,
        _ => UnrealTypeKind.Unknown
    };

    private static Dictionary<string, GeneratedTypeName> BuildTypeNames(
        IReadOnlyList<TypeSnapshot> types)
    {
        var result = new Dictionary<string, GeneratedTypeName>(StringComparer.Ordinal);
        foreach (var namespaceGroup in types.GroupBy(type => NamespaceFor(type.Path)))
        {
            foreach (var nameGroup in namespaceGroup.GroupBy(
                         type => type.Kind switch
                         {
                             "ScriptStruct" => "F" + Identifier(type.Name).TrimStart('F'),
                             "Enum" => "E" + Identifier(type.Name).TrimStart('E'),
                             _ => Identifier(type.Name)
                         }, StringComparer.Ordinal))
            {
                var ordered = nameGroup.OrderBy(type => type.Path, StringComparer.Ordinal).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    var name = nameGroup.Key;
                    if (ordered.Length > 1) name += "_" + StableSuffix(ordered[index].Path);
                    result.Add(ordered[index].Path, new GeneratedTypeName(namespaceGroup.Key, name));
                }
            }
        }
        return result;
    }

    private static string NamespaceFor(string path)
    {
        var moduleEnd = path.IndexOf('.', "/Script/".Length);
        var module = moduleEnd > 0 ? path["/Script/".Length..moduleEnd] : "Unknown";
        return module == "DeceiveInc" ? "Briefcase.DeceiveInc" : $"Briefcase.Unreal.{Identifier(module)}";
    }

    private static string UniqueMember(string unrealName, string suffix, HashSet<string> used)
    {
        var root = Identifier(unrealName);
        if (used.Add(root)) return root;
        var candidate = root + "_" + suffix;
        var number = 2;
        while (!used.Add(candidate)) candidate = root + "_" + suffix + number++;
        return candidate;
    }

    internal static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return "Unnamed";
        var result = new StringBuilder(value.Length + 1);
        foreach (var character in value)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        if (result.Length == 0) result.Append("Unnamed");
        if (char.IsDigit(result[0])) result.Insert(0, '_');
        return result.ToString();
    }

    private static bool CanUseExactParameterName(string value) =>
        !string.IsNullOrEmpty(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string StableSuffix(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
}

internal sealed record PlannedType(
    TypeSnapshot Snapshot,
    GeneratedTypeName Name,
    IReadOnlyList<PlannedProperty> Properties,
    IReadOnlyList<PlannedFunction> Functions,
    IReadOnlyList<PlannedMetadataProperty> MetadataProperties,
    IReadOnlyList<PlannedMetadataFunction> MetadataFunctions,
    bool UsesManagedStructRepresentation,
    bool SupportsManagedStructWrite)
{
    public string FullName => Name.Namespace + "." + Name.Name;
}

internal readonly record struct GeneratedTypeName(string Namespace, string Name);
internal sealed record PlannedProperty(
    PropertySnapshot Snapshot, ManagedTypeDescriptor Type, string MemberName,
    bool SupportsWireWrite, PlannedFunction? DelegateSignature);
internal sealed record PlannedMetadataProperty(PropertySnapshot Snapshot, string MemberName);
internal sealed record PlannedMetadataFunction(FunctionSnapshot Snapshot, string MemberName);

internal sealed record PlannedFunction(
    FunctionSnapshot Snapshot, string MemberName, IReadOnlyList<PlannedParameter> Parameters,
    bool SupportsDirectInvocation, bool SupportsPreparedInvocation,
    bool SupportsPreparedValueOutputInvocation, bool SupportsPreparedFastPath)
{
    public PlannedParameter? ReturnParameter => Parameters.SingleOrDefault(parameter => parameter.IsReturn);
    public IEnumerable<PlannedParameter> MethodParameters =>
        Parameters.Where(parameter => !parameter.IsReturn);
    public IEnumerable<PlannedParameter> InvocationInputs =>
        Parameters.Where(parameter => !parameter.IsReturn &&
                                      (!parameter.IsOutput || parameter.IsReference));
    public IEnumerable<PlannedParameter> Outputs =>
        Parameters.Where(parameter => parameter.IsOutput);
}

internal sealed record PlannedParameter(
    PropertySnapshot Snapshot, ManagedTypeDescriptor Type, bool IsReturn,
    bool IsOutput = false, bool IsReference = false);

internal sealed record ManagedTypeDescriptor(
    ManagedTypeKind Kind,
    string? ReferencedTypePath = null,
    ManagedTypeDescriptor? InnerType = null,
    ManagedTypeDescriptor? KeyType = null,
    ManagedTypeDescriptor? ValueType = null)
{
    public bool UsesGeneratedType => Kind == ManagedTypeKind.GeneratedStruct ||
        InnerType?.UsesGeneratedType == true || KeyType?.UsesGeneratedType == true ||
        ValueType?.UsesGeneratedType == true;
    public static ManagedTypeDescriptor? TryCreate(
        PropertySnapshot property,
        IReadOnlySet<string> knownPaths,
        bool allowInputContainers = false,
        bool allowReadContainers = false,
        bool allowStringProperty = false,
        bool allowTextProperty = false)
    {
        return TryCreateType(
            property.EffectiveType, property.ElementSize, knownPaths,
            allowInputContainers, allowReadContainers,
            allowStringProperty, allowTextProperty);
    }

    private static ManagedTypeDescriptor? TryCreateType(
        UnrealTypeSnapshot type,
        int fallbackSize,
        IReadOnlySet<string> knownPaths,
        bool allowInputContainers,
        bool allowReadContainers,
        bool allowString,
        bool allowText)
    {
        if (type.UnrealType == "StructProperty")
            return type.ReferencedTypePath is { } path && knownPaths.Contains(path)
                ? new ManagedTypeDescriptor(ManagedTypeKind.GeneratedStruct, path)
                : null;

        if (allowInputContainers && type.UnrealType == "ArrayProperty" &&
            type.InnerType?.UnrealType == "ByteProperty")
            return new ManagedTypeDescriptor(ManagedTypeKind.ByteArray);

        if ((allowInputContainers || allowReadContainers) &&
            type.UnrealType is "ArrayProperty" or "SetProperty")
        {
            var inner = type.InnerType is null ? null : TryCreateType(
                type.InnerType, type.InnerType.ElementSize, knownPaths,
                allowInputContainers, allowReadContainers: true,
                allowString: true, allowText: true);
            if (inner is null) return null;
            return new ManagedTypeDescriptor(
                type.UnrealType == "ArrayProperty" ? ManagedTypeKind.Array : ManagedTypeKind.Set,
                InnerType: inner);
        }
        if ((allowInputContainers || allowReadContainers) && type.UnrealType == "MapProperty")
        {
            var key = type.KeyType is null ? null : TryCreateType(
                type.KeyType, type.KeyType.ElementSize, knownPaths,
                allowInputContainers, allowReadContainers: true,
                allowString: true, allowText: true);
            var value = type.ValueType is null ? null : TryCreateType(
                type.ValueType, type.ValueType.ElementSize, knownPaths,
                allowInputContainers, allowReadContainers: true,
                allowString: true, allowText: true);
            return key is null || value is null ? null : new ManagedTypeDescriptor(
                ManagedTypeKind.Map, KeyType: key, ValueType: value);
        }

        if (allowReadContainers)
        {
            var special = type.UnrealType switch
            {
                "InterfaceProperty" => ManagedTypeKind.InterfaceReference,
                "LazyObjectProperty" => ManagedTypeKind.LazyObjectReference,
                "SoftObjectProperty" => ManagedTypeKind.SoftObjectReference,
                "SoftClassProperty" => ManagedTypeKind.SoftClassReference,
                "DelegateProperty" => ManagedTypeKind.Delegate,
                "MulticastDelegateProperty" or "MulticastInlineDelegateProperty" =>
                    ManagedTypeKind.MulticastDelegate,
                "FieldPathProperty" => ManagedTypeKind.FieldPath,
                _ => ManagedTypeKind.Unsupported
            };
            if (special != ManagedTypeKind.Unsupported)
                return new ManagedTypeDescriptor(special);
        }

        var kind = type.UnrealType switch
        {
            "StrProperty" when allowInputContainers || allowReadContainers || allowString =>
                ManagedTypeKind.String,
            "TextProperty" when allowInputContainers || allowReadContainers || allowString || allowText =>
                ManagedTypeKind.Text,
            "NameProperty" => ManagedTypeKind.Name,
            "IntProperty" => ManagedTypeKind.Int32,
            "Int8Property" => ManagedTypeKind.Int8,
            "Int16Property" => ManagedTypeKind.Int16,
            "ByteProperty" => ManagedTypeKind.UInt8,
            "UInt16Property" => ManagedTypeKind.UInt16,
            "UInt32Property" => ManagedTypeKind.UInt32,
            "Int64Property" => ManagedTypeKind.Int64,
            "UInt64Property" => ManagedTypeKind.UInt64,
            "FloatProperty" => ManagedTypeKind.Float,
            "DoubleProperty" => ManagedTypeKind.Double,
            "BoolProperty" => ManagedTypeKind.Boolean,
            "ObjectProperty" or "ClassProperty" or "WeakObjectProperty" =>
                ManagedTypeKind.ObjectReference,
            "EnumProperty" => (type.ElementSize > 0 ? type.ElementSize : fallbackSize) switch
            {
                1 => ManagedTypeKind.UInt8,
                2 => ManagedTypeKind.UInt16,
                4 => ManagedTypeKind.UInt32,
                8 => ManagedTypeKind.UInt64,
                _ => ManagedTypeKind.Unsupported
            },
            _ => ManagedTypeKind.Unsupported
        };
        return kind == ManagedTypeKind.Unsupported ? null : new ManagedTypeDescriptor(kind);
    }
}

internal enum ManagedTypeKind
{
    Unsupported,
    Boolean,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    Double,
    ObjectReference,
    GeneratedStruct,
    String,
    Text,
    Name,
    ByteArray,
    Array,
    Set,
    Map,
    InterfaceReference,
    LazyObjectReference,
    SoftObjectReference,
    SoftClassReference,
    Delegate,
    MulticastDelegate,
    FieldPath
}
