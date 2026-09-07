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
                    type, names[type.Path], [], [], metadataProperties, metadataFunctions));
                continue;
            }

            var used = type.Kind == "Class"
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    "Handle", "Name", "Path", "ClassHandle", "StaticClass", "Properties",
                    "Functions", "Metadata", "Reflection", "UnrealPath", "FromObject", "Read",
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
                var managedType = ManagedTypeDescriptor.TryCreate(
                    property, knownPaths, allowStringProperty: type.Kind == "Class");
                var validLayout = type.Kind == "Class" ||
                    property.Offset >= 0 && property.ElementSize > 0 &&
                    property.Offset <= type.Size - property.ElementSize;
                if (managedType is null || property.ArrayDimension != 1 || !validLayout)
                {
                    skippedProperties++;
                    continue;
                }

                properties.Add(new PlannedProperty(
                    property,
                    managedType,
                    UniqueMember(property.Name, type.Kind == "Class" ? "Property" : "Field", used)));
            }

            var functions = new List<PlannedFunction>();
            if (type.Kind == "Class")
            {
                foreach (var function in type.Functions.OrderBy(
                             function => function.Name, StringComparer.Ordinal))
                {
                    var plannedFunction = TryPlanFunction(function, knownPaths, used);
                    if (plannedFunction is null) skippedFunctions++;
                    else functions.Add(plannedFunction);
                }
            }

            planned.Add(new PlannedType(
                type, names[type.Path], properties, functions,
                metadataProperties, metadataFunctions));
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
                allowTextProperty: true);
            if (managedType is null || parameter.ArrayDimension != 1 ||
                !CanUseExactParameterName(parameter.Name))
                return null;

            // UE represents const FStruct& as ConstParm | OutParm | ReferenceParm.
            // It remains an input in the ProcessEvent buffer. Writable out/ref
            // values stay metadata-only until their ownership rules are modeled.
            var isConstReference =
                (parameter.Flags & (ConstParameterFlag | ReferenceParameterFlag)) ==
                (ConstParameterFlag | ReferenceParameterFlag);
            if (!isReturn && (parameter.Flags & OutParameterFlag) != 0 &&
                !isConstReference)
                return null;
            if (!isReturn && (parameter.Flags & ReferenceParameterFlag) != 0 &&
                managedType.Kind is not (ManagedTypeKind.String or ManagedTypeKind.Text or
                    ManagedTypeKind.ByteArray or ManagedTypeKind.GeneratedStruct))
                return null;
            planned.Add(new PlannedParameter(parameter, managedType, isReturn));
        }

        return new PlannedFunction(
            function, UniqueMember(function.Name, "Method", usedMembers), planned);
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
    IReadOnlyList<PlannedMetadataFunction> MetadataFunctions)
{
    public string FullName => Name.Namespace + "." + Name.Name;
}

internal readonly record struct GeneratedTypeName(string Namespace, string Name);
internal sealed record PlannedProperty(
    PropertySnapshot Snapshot, ManagedTypeDescriptor Type, string MemberName);
internal sealed record PlannedMetadataProperty(PropertySnapshot Snapshot, string MemberName);
internal sealed record PlannedMetadataFunction(FunctionSnapshot Snapshot, string MemberName);

internal sealed record PlannedFunction(
    FunctionSnapshot Snapshot, string MemberName, IReadOnlyList<PlannedParameter> Parameters)
{
    public PlannedParameter? ReturnParameter => Parameters.SingleOrDefault(parameter => parameter.IsReturn);
    public IEnumerable<PlannedParameter> Inputs => Parameters.Where(parameter => !parameter.IsReturn);
}

internal sealed record PlannedParameter(
    PropertySnapshot Snapshot, ManagedTypeDescriptor Type, bool IsReturn);

internal sealed record ManagedTypeDescriptor(ManagedTypeKind Kind, string? ReferencedTypePath = null)
{
    public static ManagedTypeDescriptor? TryCreate(
        PropertySnapshot property,
        IReadOnlySet<string> knownPaths,
        bool allowInputContainers = false,
        bool allowStringProperty = false,
        bool allowTextProperty = false)
    {
        var type = property.EffectiveType;
        if (type.UnrealType == "StructProperty")
            return type.ReferencedTypePath is { } path && knownPaths.Contains(path)
                ? new ManagedTypeDescriptor(ManagedTypeKind.GeneratedStruct, path)
                : null;

        var kind = type.UnrealType switch
        {
            "StrProperty" when allowInputContainers || allowStringProperty => ManagedTypeKind.String,
            "TextProperty" when allowInputContainers || allowStringProperty || allowTextProperty =>
                ManagedTypeKind.Text,
            "NameProperty" => ManagedTypeKind.Name,
            "ArrayProperty" when allowInputContainers &&
                                 type.InnerType?.UnrealType == "ByteProperty" =>
                ManagedTypeKind.ByteArray,
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
            "ObjectProperty" or "ClassProperty" => ManagedTypeKind.ObjectReference,
            "EnumProperty" => property.ElementSize switch
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
    ByteArray
}
