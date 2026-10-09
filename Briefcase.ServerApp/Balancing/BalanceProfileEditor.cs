using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public sealed class BalanceField
{
    public required string Table { get; init; }
    public required string Row { get; init; }
    public required string Field { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required string AllowedRange { get; init; }
    public required JsonValueKind Kind { get; init; }
    public required string Value { get; set; }
    public required string OriginalValue { get; init; }
    public bool IsBoolean => Kind is JsonValueKind.True or JsonValueKind.False;
    public bool IsNumber => Kind == JsonValueKind.Number;
    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }
    public string SourceKey => Table + " · " + Row + " · " + Field;
    public string RangeHint => AllowedRange.Length == 0 ? "" :
        AppTranslations.Format("balance.allowed", "Allowed: {0}", AllowedRange);
    public bool HasRange => AllowedRange.Length > 0;
    public string Key => MakeKey(Table, Row, Field);
    public static string MakeKey(string table, string row, string field) => table + "\u001f" + row + "\u001f" + field;
}

public sealed class BalanceProfileEditor
{
    private const int MaximumBytes = 1_048_576;
    private const int MaximumEntries = 2_048;
    private static readonly Regex RangePattern = new(@"^\s*(-?\d+(?:\.\d+)?)\s+to\s+(-?\d+(?:\.\d+)?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string _profilePath;
    private readonly JsonObject? _activeProfile;
    public IReadOnlyList<BalanceField> Fields { get; }
    public bool HasTemplate { get; }

    private BalanceProfileEditor(string profilePath, JsonObject? activeProfile, bool hasTemplate,
        IReadOnlyList<BalanceField> fields)
    {
        _profilePath = profilePath;
        _activeProfile = activeProfile;
        HasTemplate = hasTemplate;
        Fields = fields;
    }

    public static BalanceProfileEditor Load(ServerLocation location)
    {
        var template = ReadProfile(location.BalanceTemplateFile, optional: true);
        var active = ReadProfile(location.BalanceProfileFile, optional: true);
        var fields = new Dictionary<string, BalanceField>(StringComparer.Ordinal);
        foreach (var source in new[] { template, active })
        {
            if (source?["overrides"] is not JsonArray overrides) continue;
            foreach (var element in overrides)
            {
                if (element is not JsonObject item) continue;
                var table = RequiredName(item, "table");
                var row = RequiredName(item, "row");
                var field = RequiredName(item, "field");
                if (item["value"] is not JsonValue value) continue;
                var kind = ValueKind(value);
                if (kind is not (JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)) continue;
                var key = BalanceField.MakeKey(table, row, field);
                var previous = fields.GetValueOrDefault(key);
                fields[key] = new BalanceField
                {
                    Table = table,
                    Row = row,
                    Field = field,
                    Label = Humanize(field),
                    Description = item["info"]?.GetValue<string>() ?? previous?.Description ?? "",
                    AllowedRange = previous is { AllowedRange.Length: > 0 }
                        ? previous.AllowedRange : item["allowedRange"]?.GetValue<string>() ?? "",
                    Kind = previous?.Kind ?? kind,
                    Value = value.ToJsonString(),
                    OriginalValue = value.ToJsonString()
                };
            }
        }
        return new BalanceProfileEditor(location.BalanceProfileFile, active, template is not null,
            fields.Values.OrderBy(item => item.Table, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Row, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Field, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public void Save()
    {
        if (Fields.Count == 0) throw new InvalidDataException("No balance settings are available for this installation.");
        var root = (JsonObject?)_activeProfile?.DeepClone() ?? new JsonObject
        {
            ["format"] = "DeceiveCommunityBalanceProfile",
            ["schemaVersion"] = 1,
            ["profileName"] = "Briefcase ServerApp",
            ["overrides"] = new JsonArray()
        };
        var overrides = root["overrides"] as JsonArray ?? throw new InvalidDataException("Invalid balance profile overrides.");
        var existing = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var element in overrides)
        {
            if (element is not JsonObject item) continue;
            existing[BalanceField.MakeKey(RequiredName(item, "table"), RequiredName(item, "row"),
                RequiredName(item, "field"))] = item;
        }
        foreach (var field in Fields)
        {
            if (!existing.ContainsKey(field.Key) && field.Value == field.OriginalValue) continue;
            var value = ParseValue(field);
            if (existing.TryGetValue(field.Key, out var item)) item["value"] = value;
            else
            {
                var added = new JsonObject
                {
                    ["table"] = field.Table, ["row"] = field.Row, ["field"] = field.Field,
                    ["value"] = value
                };
                overrides.Add((JsonNode)added);
                existing[field.Key] = added;
            }
        }
        if (overrides.Count > MaximumEntries) throw new InvalidDataException("The profile exceeds the game's 2,048-setting limit.");
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException("The profile exceeds the game's 1 MiB size limit.");
        var directory = Path.GetDirectoryName(_profilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = _profilePath + ".briefcase-tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(_profilePath)) File.Copy(_profilePath, _profilePath + ".briefcase-backup", true);
            File.Move(temporary, _profilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static JsonNode ParseValue(BalanceField field)
    {
        if (field.Kind is JsonValueKind.True or JsonValueKind.False)
        {
            if (!bool.TryParse(field.Value, out var value))
                throw new InvalidDataException($"{field.Label} must be checked or unchecked.");
            return JsonValue.Create(value)!;
        }
        if (!double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number))
            throw new InvalidDataException($"{field.Label} must be a finite number.");
        if (Math.Abs(number) > 100_000_000)
            throw new InvalidDataException($"{field.Label} exceeds the game's numeric limit.");
        var range = RangePattern.Match(field.AllowedRange);
        if (range.Success)
        {
            var minimum = double.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
            var maximum = double.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
            if (number < minimum || number > maximum)
                throw new InvalidDataException($"{field.Label} must be between {minimum:G} and {maximum:G}.");
        }
        if (field.Value.IndexOfAny(['.', 'e', 'E']) < 0 &&
            long.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return JsonValue.Create(integer)!;
        return JsonValue.Create(number)!;
    }

    public static string Humanize(string name)
    {
        const string behaviorPrefix = "DamageBehaviors[BehaviorName=";
        if (name.StartsWith(behaviorPrefix, StringComparison.Ordinal) && name.IndexOf("].", StringComparison.Ordinal) is var split && split > 0)
            return Humanize(name[(split + 2)..]) + " (" + Humanize(name[behaviorPrefix.Length..split]) + ")";
        var value = Regex.Replace(name.Replace('_', ' '), @"(?<=[a-z0-9])(?=[A-Z])", " ");
        value = Regex.Replace(value, @"(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return value.Replace("Maxium", "Maximum", StringComparison.Ordinal);
    }

    private static JsonObject? ReadProfile(string path, bool optional)
    {
        if (!File.Exists(path) && optional) return null;
        if (new FileInfo(path).Length > MaximumBytes)
            throw new InvalidDataException($"Balance file exceeds 1 MiB: {Path.GetFileName(path)}.");
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ??
            throw new InvalidDataException($"Invalid balance file: {Path.GetFileName(path)}.");
        if (root["format"]?.GetValue<string>() != "DeceiveCommunityBalanceProfile" ||
            root["schemaVersion"]?.GetValue<int>() != 1 || root["overrides"] is not JsonArray)
            throw new InvalidDataException($"Unsupported balance file format: {Path.GetFileName(path)}.");
        return root;
    }

    private static string RequiredName(JsonObject item, string name)
    {
        var value = item[name]?.GetValue<string>() ?? "";
        if (value.Length is < 1 or > 128) throw new InvalidDataException($"Invalid balance {name}.");
        return value;
    }

    private static JsonValueKind ValueKind(JsonValue value)
    {
        using var json = JsonDocument.Parse(value.ToJsonString());
        return json.RootElement.ValueKind;
    }
}
