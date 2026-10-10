using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public enum FormFieldKind { Text, Secret, Integer, Decimal, Boolean, Choice }

public sealed record FormTab(string Id, string Label, string? Feature, string LabelKey);
public sealed record FormGroup(string Id, string Tab, string Label, string LabelKey);
public sealed record FormChoice(string Value, string Label, string LabelKey);
public sealed record FormField(string Key, string Label, string Group, string Section,
    FormFieldKind Kind, string Default, decimal? Minimum, decimal? Maximum, string? MaximumReference,
    int? MaxLength, FormChoice[]? Choices, string Display, decimal Step, string? NotEqualTo, string? Pattern, string LabelKey)
{
    public decimal SliderMaximum(decimal maximum)
    {
        var baseValue = Minimum ?? 0;
        return baseValue + decimal.Floor((maximum - baseValue) / Step) * Step;
    }

    public string FormatSliderValue(double value)
    {
        var baseValue = Minimum ?? 0;
        var snapped = baseValue + decimal.Round(((decimal)value - baseValue) / Step, 0,
            MidpointRounding.AwayFromZero) * Step;
        return snapped.ToString(CultureInfo.InvariantCulture);
    }
}

// Form layout and validation are independent of the INI reader. Other editors can use this schema later.
public sealed class FormSchema
{
    private const int MaximumBaseBytes = 512 * 1024;
    private const int MaximumOverrideBytes = 64 * 1024;
    private const string ResourceName = "Briefcase.ServerApp.Fallback.ServerConfiguration.json";
    private readonly JsonObject _document;
    private readonly Dictionary<string, string> _translations;

    public string Section { get; }
    public IReadOnlyList<FormTab> Tabs { get; }
    public IReadOnlyList<FormGroup> Groups { get; }
    public IReadOnlyList<FormField> Fields { get; }

    private FormSchema(JsonObject document, Dictionary<string, string>? translations = null)
    {
        _document = document;
        _translations = translations is null ? new Dictionary<string, string>(StringComparer.Ordinal) :
            new Dictionary<string, string>(translations, StringComparer.Ordinal);
        if (Integer(document, "schemaVersion") != 1) throw new InvalidDataException("Unsupported form schema version.");
        Section = Required(document, "section");
        if (!Regex.IsMatch(Section, "^[A-Za-z0-9_./-]{1,200}$"))
            throw new InvalidDataException("Invalid form section.");
        Tabs = Items(document, "tabs", "id").Select(item => new FormTab(
            Identifier(item, "id"), Required(item, "label"), Optional(item, "feature"),
            Optional(item, "labelKey") ?? "config.tab." + Identifier(item, "id"))).ToArray();
        Groups = Items(document, "groups", "id").Select(item => new FormGroup(
            Identifier(item, "id"), Identifier(item, "tab"), Required(item, "label"),
            Optional(item, "labelKey") ?? "config.category." + Identifier(item, "id"))).ToArray();
        Fields = Items(document, "fields", "key").Select(ParseField).ToArray();
        if (Tabs.Count is < 1 or > 20 || Groups.Count > 64 || Fields.Count is < 1 or > 256 ||
            Groups.Any(group => !Tabs.Any(tab => tab.Id == group.Tab)) ||
            Fields.Any(field => !Groups.Any(group => group.Id == field.Group)))
            throw new InvalidDataException("Form schema contains an unknown tab or group.");
        foreach (var field in Fields)
        {
            if (field.MaximumReference is not { } reference) continue;
            if (reference == field.Key || !Fields.Any(other => other.Key == reference &&
                    other.Kind is FormFieldKind.Integer or FormFieldKind.Decimal))
                throw new InvalidDataException("Invalid maximum reference for " + field.Key + ".");
        }
        if (Fields.Any(field => field.NotEqualTo is { } other && !Fields.Any(candidate => candidate.Key == other)))
            throw new InvalidDataException("Form schema contains an unknown comparison reference.");
        Validate(Defaults());
    }

    public static FormSchema LoadBase(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppContext.BaseDirectory, "Schemas", "ServerConfiguration.json");
        byte[] bytes;
        if (File.Exists(path)) bytes = ReadLimited(path, MaximumBaseBytes);
        else
        {
            using var fallback = typeof(FormSchema).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidDataException("Bundled form schema is missing.");
            using var output = new MemoryStream();
            fallback.CopyTo(output);
            bytes = output.ToArray();
        }
        return Parse(bytes);
    }

    public static FormSchema LoadForServer(ServerLocation location, string? directory = null)
    {
        var schema = LoadBase(directory);
        foreach (var mod in ModCatalog.Read(location).Where(mod => mod.IsEnabled && mod.HasNativeBinary))
        {
            var path = Path.Combine(location.ModsRoot, mod.Name, "Data", "server-configuration.override.json");
            if (!File.Exists(path)) continue;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Mod form override cannot be a link: " + mod.Name);
            schema = schema.ApplyOverride(ReadLimited(path, MaximumOverrideBytes));
            var languageRoot = Path.Combine(location.ModsRoot, mod.Name, "Data", "FormTranslations");
            schema.AddTranslations(languageRoot, "en");
            if (AppTranslations.Language != "en") schema.AddTranslations(languageRoot, AppTranslations.Language);
        }
        return schema;
    }

    public static FormSchema Parse(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumBaseBytes) throw new InvalidDataException("Invalid form schema size.");
        try { return new FormSchema(JsonNode.Parse(bytes)?.AsObject()
            ?? throw new InvalidDataException("Form schema must be an object.")); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Invalid form schema JSON.", error); }
    }

    public FormSchema ApplyOverride(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaximumOverrideBytes) throw new InvalidDataException("Invalid form override size.");
        try
        {
            var patch = JsonNode.Parse(bytes)?.AsObject()
                ?? throw new InvalidDataException("Form override must be an object.");
            if (Integer(patch, "schemaVersion") != 1 || patch.Any(item => item.Key is not
                    ("schemaVersion" or "tabs" or "groups" or "fields")))
                throw new InvalidDataException("Invalid form override header.");
            var merged = (JsonObject)_document.DeepClone();
            Merge(merged, patch, "tabs", "id");
            Merge(merged, patch, "groups", "id");
            Merge(merged, patch, "fields", "key");
            return new FormSchema(merged);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Invalid form override JSON.", error); }
    }

    public Dictionary<string, string> Defaults() => Fields.ToDictionary(field => field.Key,
        field => field.Default, StringComparer.OrdinalIgnoreCase);

    public string Text(string key, string fallback) => _translations.TryGetValue(key, out var translated)
        ? translated : AppTranslations.Get(key, fallback);

    public decimal? EffectiveMaximum(FormField field, IReadOnlyDictionary<string, string> values)
    {
        if (field.MaximumReference is not { } reference) return field.Maximum;
        if (!values.TryGetValue(reference, out var value) ||
            !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var bound))
            return field.Maximum;
        return field.Maximum is { } ceiling ? Math.Min(ceiling, bound) : bound;
    }

    public void Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var field in Fields)
        {
            if (!values.TryGetValue(field.Key, out var value) || value.Contains('\r') ||
                value.Contains('\n') || value.Length > 256)
                throw new InvalidDataException("Invalid " + field.Label + ".");
            if (field.MaxLength is { } maxLength && value.Length > maxLength)
                throw new InvalidDataException(field.Label + " must have at most " + maxLength + " characters.");
            if (field.Pattern is { } pattern)
            {
                try
                {
                    if (!Regex.IsMatch(value, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                        throw new InvalidDataException("Invalid " + field.Label + ".");
                }
                catch (RegexMatchTimeoutException error)
                { throw new InvalidDataException("Validation timed out for " + field.Label + ".", error); }
            }
            var maximum = EffectiveMaximum(field, values);
            if (field.Kind is FormFieldKind.Integer or FormFieldKind.Decimal)
            {
                var valid = field.Kind == FormFieldKind.Integer
                    ? long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    : decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                if (!valid || !decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    number < field.Minimum || number > maximum)
                    throw new InvalidDataException(field.Label + " must be between " + field.Minimum + " and " + maximum + ".");
                if (field.Display == "slider" && (number - (field.Minimum ?? 0)) % field.Step != 0)
                    throw new InvalidDataException(field.Label + " must use increments of " + field.Step + ".");
            }
            if (field.Kind == FormFieldKind.Boolean && !bool.TryParse(value, out _))
                throw new InvalidDataException("Invalid checkbox for " + field.Label + ".");
            if (field.Kind == FormFieldKind.Choice && !field.Choices!.Any(choice => choice.Value == value))
                throw new InvalidDataException("Invalid choice for " + field.Label + ".");
            if (field.NotEqualTo is { } other && values.TryGetValue(other, out var otherValue) && value == otherValue)
                throw new InvalidDataException(field.Label + " must differ from " + other + ".");
        }
    }

    private static FormField ParseField(JsonObject item)
    {
        var key = Identifier(item, "key");
        if (!Enum.TryParse<FormFieldKind>(Required(item, "kind"), true, out var kind))
            throw new InvalidDataException("Invalid field kind for " + key + ".");
        var minimum = Number(item, "min");
        var maximum = Number(item, "max");
        var maxReference = Optional(item, "maxReference");
        var maxLength = Integer(item, "maxLength", optional: true);
        var choices = item["choices"] is JsonArray array ? array.Select(node => ParseChoice(node, key)).ToArray() : null;
        var display = Optional(item, "display") ?? "input";
        var step = Number(item, "step") ?? (kind == FormFieldKind.Integer ? 1m : 0.1m);
        var section = Optional(item, "section") ?? "";
        if (section.Length > 0 && !Regex.IsMatch(section, "^[A-Za-z0-9_./-]{1,200}$") ||
            minimum > maximum || maxLength is < 1 or > 256 ||
            kind == FormFieldKind.Choice && (choices is null || choices.Length is < 1 or > 64) ||
            choices is not null && choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal).Count() != choices.Length ||
            display is not ("input" or "slider") || display == "slider" &&
                kind is not (FormFieldKind.Integer or FormFieldKind.Decimal) ||
            display == "slider" && maximum is null && maxReference is null ||
            step is <= 0 or > 1000000 || kind == FormFieldKind.Integer && step != decimal.Truncate(step))
            throw new InvalidDataException("Invalid field definition for " + key + ".");
        if (Optional(item, "pattern") is { Length: > 200 } pattern)
            throw new InvalidDataException("Validation pattern is too long: " + key);
        if (Optional(item, "pattern") is { } expression)
        {
            try { _ = new Regex(expression, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException error) { throw new InvalidDataException("Invalid validation pattern: " + key, error); }
        }
        return new FormField(key, Required(item, "label"), Identifier(item, "group"), section,
            kind, Required(item, "default", allowEmpty: true), minimum, maximum, maxReference,
            maxLength, choices, display, step, Optional(item, "notEqualTo"), Optional(item, "pattern"),
            Optional(item, "labelKey") ?? "config.field." + key);
    }

    private static FormChoice ParseChoice(JsonNode? node, string fieldKey)
    {
        if (node is JsonObject option)
        {
            var value = Required(option, "value", allowEmpty: true);
            return new FormChoice(value, Optional(option, "label") ?? value,
                Optional(option, "labelKey") ?? "config.choice." + fieldKey + "." + (value.Length == 0 ? "default" : value));
        }
        var text = node?.GetValue<string>() ?? throw new InvalidDataException("Invalid form choice.");
        return new FormChoice(text, text, "config.choice." + fieldKey + "." + (text.Length == 0 ? "default" : text));
    }

    private static void Merge(JsonObject target, JsonObject patch, string name, string key)
    {
        if (patch[name] is not JsonArray additions) return;
        var existing = target[name] as JsonArray ?? throw new InvalidDataException("Missing " + name + ".");
        foreach (var node in additions)
        {
            var change = node as JsonObject ?? throw new InvalidDataException("Invalid " + name + " entry.");
            var id = Identifier(change, key);
            var prior = existing.OfType<JsonObject>().FirstOrDefault(item => Optional(item, key) == id);
            if (prior is null) existing.Add(change.DeepClone());
            else foreach (var property in change)
                if (property.Key != key) prior[property.Key] = property.Value?.DeepClone();
        }
    }

    private void AddTranslations(string directory, string language)
    {
        var path = Path.Combine(directory, language + ".json");
        if (!File.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Form translation cannot be a link: " + path);
        using var json = JsonDocument.Parse(ReadLimited(path, MaximumOverrideBytes));
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Form translation must be a JSON object: " + path);
        foreach (var item in json.RootElement.EnumerateObject())
        {
            if (item.Name.Length is < 1 or > 128 || item.Value.ValueKind != JsonValueKind.String ||
                item.Value.GetString() is not { Length: > 0 and <= 256 } value)
                throw new InvalidDataException("Invalid form translation: " + path);
            _translations[item.Name] = value;
        }
    }

    private static IReadOnlyList<JsonObject> Items(JsonObject root, string property, string key)
    {
        var array = root[property] as JsonArray ?? throw new InvalidDataException("Missing " + property + ".");
        var items = array.Select(node => node as JsonObject ??
            throw new InvalidDataException("Invalid " + property + " entry.")).ToArray();
        if (items.Select(item => Identifier(item, key)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new InvalidDataException("Duplicate " + property + " identifier.");
        return items;
    }

    private static string Identifier(JsonObject item, string name)
    {
        var value = Required(item, name);
        if (!Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]{0,99}$"))
            throw new InvalidDataException("Invalid " + name + ": " + value);
        return value;
    }

    private static string Required(JsonObject item, string name, bool allowEmpty = false)
    {
        var value = item[name]?.GetValue<string>() ?? throw new InvalidDataException("Missing " + name + ".");
        if (value.Length > 256 || !allowEmpty && value.Length == 0)
            throw new InvalidDataException("Invalid " + name + ".");
        return value;
    }

    private static string? Optional(JsonObject item, string name) => item[name]?.GetValue<string>();
    private static decimal? Number(JsonObject item, string name) => item[name] is { } value ? value.GetValue<decimal>() : null;
    private static int? Integer(JsonObject item, string name, bool optional = false) =>
        item[name] is { } value ? value.GetValue<int>() : optional ? null : throw new InvalidDataException("Missing " + name + ".");
    private static byte[] ReadLimited(string path, int maximum)
    {
        var file = new FileInfo(path);
        if (file.Length is < 1 || file.Length > maximum) throw new InvalidDataException("Invalid form schema size: " + path);
        return File.ReadAllBytes(path);
    }
}
