using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace Briefcase.ServerApp;

public sealed record ModConfigValue(string Label, string Path, JsonNode Parent, string? Key, int Index,
    JsonValueKind Kind, string Value);

public sealed class ModConfigEditor
{
    public string FilePath { get; }
    public JsonNode Root { get; }
    public IReadOnlyList<ModConfigValue> Values { get; }

    public ModConfigEditor(string file, string? draftJson = null)
    {
        FilePath = file;
        if (draftJson is null && new FileInfo(file).Length > 1_048_576)
            throw new InvalidDataException("The mod configuration is too large for the visual editor.");
        Root = JsonNode.Parse(draftJson ?? File.ReadAllText(file)) ??
            throw new InvalidDataException("The mod configuration is empty.");
        var fields = new List<ModConfigValue>();
        Visit(Root, "", fields, 0, LoadTranslations(Path.GetDirectoryName(file)!));
        if (fields.Count == 0) throw new InvalidDataException("This mod has no editable settings.");
        Values = fields;
    }

    private static Dictionary<string, string> LoadTranslations(string dataDirectory)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in AppTranslations.Language == "en"
                     ? new[] { "en" } : new[] { "en", AppTranslations.Language })
        {
            var path = Path.Combine(dataDirectory, "FormTranslations", language + ".json");
            if (!File.Exists(path)) continue;
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 65_536)
                throw new InvalidDataException("Invalid mod translation file.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Mod translations must be an object.");
            foreach (var item in document.RootElement.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.String && item.Value.GetString() is { Length: > 0 and <= 256 } text)
                    values[item.Name] = text;
        }
        return values;
    }

    private static void Visit(JsonNode node, string prefix, List<ModConfigValue> values, int depth,
        IReadOnlyDictionary<string, string> translations)
    {
        if (depth > 12 || values.Count > 512) throw new InvalidDataException("The mod configuration is too complex for the visual editor.");
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
                Add(pair.Value, obj, pair.Key, -1, string.IsNullOrEmpty(prefix) ? pair.Key : prefix + " · " + pair.Key,
                    values, depth + 1, translations);
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                Add(array[i], array, null, i, prefix + " [" + (i + 1) + "]", values, depth + 1, translations);
        }
    }

    private static void Add(JsonNode? node, JsonNode parent, string? key, int index,
        string label, List<ModConfigValue> values, int depth,
        IReadOnlyDictionary<string, string> translations)
    {
        if (node is null) return;
        if (node is JsonObject or JsonArray) { Visit(node, label, values, depth, translations); return; }
        var kind = node.GetValueKind();
        if (kind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
        {
            var path = label.Replace(" · ", ".", StringComparison.Ordinal);
            var translated = translations.TryGetValue("mod.config." + path, out var text) ? text : label;
            values.Add(new ModConfigValue(translated, path, parent, key, index, kind,
                kind == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString()));
        }
    }

    public string Render(IReadOnlyDictionary<ModConfigValue, string> changes)
    {
        foreach (var (entry, raw) in changes)
        {
            if (raw.Length > 4096) throw new InvalidDataException($"Value too long: {entry.Label}.");
            JsonNode replacement = entry.Kind switch
            {
                JsonValueKind.String => JsonValue.Create(raw)!,
                JsonValueKind.True or JsonValueKind.False when bool.TryParse(raw, out var boolean) => JsonValue.Create(boolean)!,
                JsonValueKind.Number when decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => JsonValue.Create(number)!,
                _ => throw new InvalidDataException($"Invalid value for {entry.Label}.")
            };
            if (entry.Parent is JsonObject obj) obj[entry.Key!] = replacement;
            else ((JsonArray)entry.Parent)[entry.Index] = replacement;
        }
        return Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void Save(IReadOnlyDictionary<ModConfigValue, string> changes) => SaveRaw(FilePath, Render(changes));

    public static void SaveRaw(string file, string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) > 1_048_576)
            throw new InvalidDataException("The mod configuration exceeds 1 MiB.");
        var document = JsonNode.Parse(raw) ?? throw new InvalidDataException("The mod configuration is empty.");
        if (document is not JsonObject and not JsonArray)
            throw new InvalidDataException("The mod configuration must contain a JSON object or array.");
        var temp = file + ".briefcase-tmp";
        var backup = file + ".briefcase-backup";
        try
        {
            File.WriteAllText(temp, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Copy(file, backup, true);
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
