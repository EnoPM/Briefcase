using System.Security.Cryptography;
using System.Text;

namespace Briefcase.SdkEmitter;

/// <summary>
/// Converts a raw Unreal snapshot into the exact public surface that can be
/// represented by the current managed ABI. Both the emitter and validator use
/// this immutable plan, so a skipped member can never be mistaken for emitted
/// API.
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

    public static SdkEmissionPlan Create(SdkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = snapshot.Types
            .Where(type => type.Kind is "Class" or "ScriptStruct" &&
                           type.Path.StartsWith("/Script/", StringComparison.Ordinal))
            .GroupBy(type => type.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(type => type.Path, StringComparer.Ordinal)
            .ToArray();

        var usable = candidates
            .Where(type => type.Kind == "Class" || type.Size is > 0 and <= 65_535)
            .ToArray();
        var names = BuildTypeNames(usable);
        var knownPaths = names.Keys.ToHashSet(StringComparer.Ordinal);
        var planned = new List<PlannedType>(usable.Length);
        var skippedProperties = 0;
        var skippedFunctions = 0;

        foreach (var type in usable)
        {
            var used = type.Kind == "Class"
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    "Handle", "Name", "Path", "ClassHandle", "StaticClass", "Properties",
                    "Functions", "UnrealPath", "FromObject", "Read", "ReadString", "ReadText",
                    "InvokeText", "IsA", names[type.Path].Name
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    "UnrealPath", "NativeSize", names[type.Path].Name
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

            planned.Add(new PlannedType(type, names[type.Path], properties, functions));
        }

        var byPath = planned.ToDictionary(type => type.Snapshot.Path, StringComparer.Ordinal);
        return new SdkEmissionPlan(
            snapshot, planned, byPath, candidates.Length - usable.Length,
            skippedProperties, skippedFunctions);
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
            // UE represents `const FStruct&` as ConstParm | OutParm |
            // ReferenceParm. It is still an input value in the ProcessEvent
            // buffer, so it is safe to emit. A writable out parameter remains
            // excluded until generated methods can return multiple values.
            var isConstReference =
                (parameter.Flags & (ConstParameterFlag | ReferenceParameterFlag)) ==
                (ConstParameterFlag | ReferenceParameterFlag);
            if (!isReturn && (parameter.Flags & OutParameterFlag) != 0 &&
                !isConstReference)
                return null;
            // Unreal marks native `const FStruct&` inputs as ReferenceParm even
            // though ProcessEvent still stores the complete value inline in
            // the reflected parameter buffer. Generated blittable structures
            // can therefore use the same value signature as ordinary struct
            // inputs. Writable OutParm values remain excluded above until the
            // SDK can model multiple return values explicitly.
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

    private static Dictionary<string, GeneratedTypeName> BuildTypeNames(
        IReadOnlyList<TypeSnapshot> types)
    {
        var result = new Dictionary<string, GeneratedTypeName>(StringComparer.Ordinal);
        foreach (var namespaceGroup in types.GroupBy(type => NamespaceFor(type.Path)))
        {
            foreach (var nameGroup in namespaceGroup.GroupBy(
                         type => type.Kind == "ScriptStruct"
                             ? "F" + Identifier(type.Name).TrimStart('F')
                             : Identifier(type.Name), StringComparer.Ordinal))
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

    private static bool CanUseExactParameterName(string value) =>
        !string.IsNullOrEmpty(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return "Unnamed";
        var result = new StringBuilder(value.Length + 1);
        foreach (var character in value)
            result.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        if (result.Length == 0) result.Append("Unnamed");
        if (char.IsDigit(result[0])) result.Insert(0, '_');
        return result.ToString();
    }

    private static string StableSuffix(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
}

internal sealed record PlannedType(
    TypeSnapshot Snapshot,
    GeneratedTypeName Name,
    IReadOnlyList<PlannedProperty> Properties,
    IReadOnlyList<PlannedFunction> Functions)
{
    public string FullName => Name.Namespace + "." + Name.Name;
}

internal readonly record struct GeneratedTypeName(string Namespace, string Name);

internal sealed record PlannedProperty(
    PropertySnapshot Snapshot, ManagedTypeDescriptor Type, string MemberName);

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
        if (property.UnrealType == "StructProperty")
            return property.ReferencedTypePath is { } path && knownPaths.Contains(path)
                ? new ManagedTypeDescriptor(ManagedTypeKind.GeneratedStruct, path)
                : null;

        var kind = property.UnrealType switch
        {
            "StrProperty" when allowInputContainers || allowStringProperty =>
                ManagedTypeKind.String,
            "TextProperty" when allowInputContainers || allowStringProperty || allowTextProperty =>
                ManagedTypeKind.Text,
            "NameProperty" when allowInputContainers => ManagedTypeKind.Name,
            "ArrayProperty" when allowInputContainers &&
                                 property.InnerUnrealType == "ByteProperty" =>
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
