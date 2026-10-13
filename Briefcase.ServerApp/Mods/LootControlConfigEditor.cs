using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public sealed record LootPlaceKey(bool Vault, int Security, string PointType)
{
    public string Label => (Vault ? AppTranslations.Get("loot.place.vault", "Vault") + " · " : "") +
        (Security switch
        {
            0 => AppTranslations.Get("loot.place.public", "Public area"),
            1 => AppTranslations.Get("loot.place.staff", "Staff area"),
            2 => AppTranslations.Get("loot.place.guard", "Guard area"),
            3 => AppTranslations.Get("loot.place.purple", "Purple area"),
            _ => AppTranslations.Get("loot.place.security", "Security") + " " + Security
        }) + " · " + AppTranslations.Get("loot.point_type." + PointType, PointType);
}

public sealed record LootLocationItem(string ObjectType, string Weight, bool Added,
    string? VanillaWeight = null, bool UseAddition = false);

/// <summary>Edits the installed UE4SS LootControl configuration without discarding its advanced rules.</summary>
public sealed class LootControlConfigEditor
{
    private readonly JsonObject _root;
    private readonly Dictionary<(string Map, string ObjectType), int> _vanillaCounts = new();
    private readonly Dictionary<string, decimal> _vanillaOccurrence = new(StringComparer.Ordinal);
    private readonly Dictionary<(LootPlaceKey Place, string ObjectType), decimal> _vanillaWeights = new();
    private readonly IReadOnlyDictionary<string, string> _itemImages;

    public IReadOnlyList<string> ObjectTypes { get; }
    public IReadOnlyList<LootPlaceKey> Places { get; }
    public IReadOnlyList<string> Maps { get; }
    public bool Diagnostics { get; }

    public static bool IsLootControl(string configPath)
    {
        if (configPath.Equals("BriefcaseLootControl", StringComparison.OrdinalIgnoreCase)) return true;
        var data = Path.GetDirectoryName(configPath);
        var modDirectory = data is null ? null : Path.GetDirectoryName(data);
        return string.Equals(Path.GetFileName(modDirectory), "BriefcaseLootControl",
            StringComparison.OrdinalIgnoreCase);
    }

    public LootControlConfigEditor(string configPath, string json, string? remoteData = null)
    {
        if (!IsLootControl(configPath)) throw new InvalidDataException("This is not a LootControl configuration.");
        _root = JsonNode.Parse(json) as JsonObject ??
            throw new InvalidDataException("LootControl configuration must be a JSON object.");
        if (_root["schemaVersion"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Unsupported LootControl configuration version.");
        if (_root["diagnostics"] is JsonValue diagnostics)
        {
            if (!diagnostics.TryGetValue<bool>(out var enabled))
                throw new InvalidDataException("LootControl diagnostics must be a boolean.");
            Diagnostics = enabled;
        }
        else if (_root["diagnostics"] is not null)
            throw new InvalidDataException("LootControl diagnostics must be a boolean.");

        var objectTypes = new HashSet<string>(StringComparer.Ordinal);
        var places = new HashSet<LootPlaceKey>();
        var maps = new HashSet<string>(StringComparer.Ordinal);
        if (remoteData is not null)
        {
            using var remote = JsonDocument.Parse(remoteData);
            if (remote.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid remote LootControl data.");
            _itemImages = remote.RootElement.TryGetProperty("itemImages", out var imageMapping)
                ? ReadItemImages(imageMapping) : new Dictionary<string, string>(StringComparer.Ordinal);
            if (remote.RootElement.TryGetProperty("catalogs", out var catalogs))
            {
                if (catalogs.ValueKind != JsonValueKind.Array || catalogs.GetArrayLength() > 65)
                    throw new InvalidDataException("Invalid remote LootControl catalogs.");
                foreach (var catalog in catalogs.EnumerateArray())
                {
                    if (catalog.ValueKind != JsonValueKind.Object ||
                        !catalog.TryGetProperty("data", out var data))
                        throw new InvalidDataException("Invalid remote LootControl catalog.");
                    var map = catalog.TryGetProperty("map", out var mapValue) &&
                              mapValue.ValueKind == JsonValueKind.String ? mapValue.GetString() : null;
                    ReadCatalog(data, map, objectTypes, places, maps);
                }
            }
        }
        else
        {
            var dataDirectory = Path.GetDirectoryName(configPath)!;
            _itemImages = ReadItemImages(dataDirectory);
            var catalogDirectory = Path.Combine(dataDirectory, "catalogs");
            if (Directory.Exists(catalogDirectory))
                foreach (var path in Directory.EnumerateFiles(catalogDirectory, "LVL_*.json").Take(32))
                    ReadCatalog(path, Path.GetFileNameWithoutExtension(path), objectTypes, places, maps);
            var latestCatalog = Path.Combine(dataDirectory, "catalog.json");
            if (File.Exists(latestCatalog))
                ReadCatalog(latestCatalog, null, objectTypes, places, maps);
            ReadPackagedCatalogs(dataDirectory, objectTypes, places, maps);
        }

        if (_root["objects"] is JsonObject objects)
            foreach (var item in objects) objectTypes.Add(item.Key);
        if (_root["mapCounts"] is JsonArray mapCounts)
            foreach (var item in mapCounts.OfType<JsonObject>())
            {
                if (item["objectType"] is JsonValue type) objectTypes.Add(type.GetValue<string>());
                if (item["map"] is JsonValue map && map.GetValue<string>() != "*") maps.Add(map.GetValue<string>());
            }
        if (_root["presets"] is JsonArray presets)
            foreach (var item in presets.OfType<JsonObject>())
            {
                if (TryPlace(item, out var place)) places.Add(place);
                if (item["weights"] is JsonObject weights)
                    foreach (var weight in weights) objectTypes.Add(weight.Key);
                if (item["addCandidates"] is JsonArray additions)
                    foreach (var candidate in additions.OfType<JsonObject>())
                        if (candidate["objectType"] is JsonValue type) objectTypes.Add(type.GetValue<string>());
            }

        ObjectTypes = objectTypes.Where(IsObjectType).OrderBy(type => type, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Places = places.OrderBy(place => place.Vault).ThenBy(place => place.Security)
            .ThenBy(place => place.PointType, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Maps = maps.OrderBy(GameMapNames.Display, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string? Weight(LootPlaceKey place, string objectType) =>
        FindPreset(_root, place)?["weights"] is JsonObject weights && weights[objectType] is JsonNode value
            ? value.ToJsonString() : null;

    public string ImageFor(string objectType) => _itemImages.TryGetValue(objectType, out var id)
        ? id : GameImageCatalog.ForLootItem(objectType);

    private static IReadOnlyDictionary<string, string> ReadItemImages(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "item-images.json");
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path)) return images;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 65_536)
            throw new InvalidDataException("Invalid LootControl image mapping file.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return ReadItemImages(document.RootElement);
    }

    private static IReadOnlyDictionary<string, string> ReadItemImages(JsonElement root)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid LootControl image mapping.");
        foreach (var item in items.EnumerateObject())
        {
            if (images.Count >= 512 || !IsObjectType(item.Name) ||
                item.Value.ValueKind != JsonValueKind.String || item.Value.GetString() is not { } id ||
                !GameImageCatalog.Contains(id) || !images.TryAdd(item.Name, id))
                throw new InvalidDataException("Unknown bundled game image for " + item.Name + ".");
        }
        return images;
    }

    public IReadOnlyList<LootLocationItem> LocationItems(LootPlaceKey place)
    {
        var preset = FindPreset(_root, place);
        var items = new Dictionary<string, LootLocationItem>(StringComparer.Ordinal);
        var fullReplacement = preset?["weights"] is JsonObject;
        if (!fullReplacement)
            foreach (var (key, value) in _vanillaWeights)
                if (key.Place == place)
                    items[key.ObjectType] = new LootLocationItem(key.ObjectType, "", false,
                        value.ToString("0.###", CultureInfo.InvariantCulture), true);
        if (preset?["weights"] is JsonObject weights)
            foreach (var (type, value) in weights)
                if (IsObjectType(type) && value is not null)
                    items[type] = new LootLocationItem(type, value.ToJsonString(), false,
                        VanillaWeight(place, type));
        if (preset?["addCandidates"] is JsonArray additions)
            foreach (var candidate in additions.OfType<JsonObject>())
            {
                if (candidate["objectType"] is not JsonValue type ||
                    !type.TryGetValue<string>(out var name) || !IsObjectType(name) ||
                    candidate["weight"] is not JsonNode weight) continue;
                items[name] = new LootLocationItem(name, weight.ToJsonString(),
                    !_vanillaWeights.ContainsKey((place, name)), VanillaWeight(place, name), true);
            }
        return items.Values.OrderBy(item => DisplayItem(item.ObjectType),
            StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public string? VanillaWeight(LootPlaceKey place, string objectType) =>
        _vanillaWeights.TryGetValue((place, objectType), out var value)
            ? value.ToString("0.###", CultureInfo.InvariantCulture) : null;

    public string? Count(string map, string objectType) =>
        FindMapCount(_root, map, objectType)?["count"]?.ToJsonString();

    public string? Occurrence(string objectType) =>
        _root["objects"] is JsonObject objects && objects[objectType] is JsonObject item &&
        item["occurrenceFactor"] is JsonNode value ? value.ToJsonString() : null;

    public string VanillaCount(string map, string objectType)
    {
        if (map != "*")
            return _vanillaCounts.TryGetValue((map, objectType), out var count)
                ? count.ToString(CultureInfo.InvariantCulture) : "—";
        var counts = Maps.Where(id => _vanillaCounts.ContainsKey((id, objectType)))
            .Select(id => _vanillaCounts[(id, objectType)]).ToArray();
        if (counts.Length == 0) return "—";
        var minimum = counts.Min();
        var maximum = counts.Max();
        return minimum == maximum ? minimum.ToString(CultureInfo.InvariantCulture)
            : minimum.ToString(CultureInfo.InvariantCulture) + "–" + maximum.ToString(CultureInfo.InvariantCulture);
    }

    public string? VanillaCountDetails(string map, string objectType) => map == "*" && Maps.Count > 0
        ? string.Join("\n", Maps.Select(id => GameMapNames.Display(id) + ": " + VanillaCount(id, objectType)))
        : null;

    public string VanillaOccurrence(string objectType) =>
        _vanillaOccurrence.TryGetValue(objectType, out var value)
            ? value.ToString("0.###", CultureInfo.InvariantCulture) : "—";

    public static string DisplayItem(string objectType) => objectType.Replace('_', ' ');

    public string Render(IReadOnlyDictionary<(LootPlaceKey Place, string ObjectType), string> weights,
        IReadOnlyDictionary<(string Map, string ObjectType), string> counts,
        IReadOnlyDictionary<string, string> occurrences,
        IReadOnlyDictionary<(LootPlaceKey Place, string ObjectType), string>? additions = null,
        bool? diagnostics = null)
    {
        var root = (JsonObject)_root.DeepClone();
        if (diagnostics.HasValue && (_root.ContainsKey("diagnostics") || diagnostics.Value != Diagnostics))
            root["diagnostics"] = diagnostics.Value;
        var candidateDrafts = additions?.ToDictionary(item => item.Key, item => item.Value) ?? new();
        foreach (var (key, raw) in weights)
        {
            var preset = FindPreset(root, key.Place);
            if (_vanillaWeights.ContainsKey(key) && preset?["weights"] is null)
            {
                // A single "weights" entry replaces the whole vanilla pool.
                // Adjust a discovered vanilla item through addCandidates instead.
                candidateDrafts[key] = raw;
                continue;
            }
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (preset?["weights"] is JsonObject existing) existing.Remove(key.ObjectType);
                if (preset?["weights"] is JsonObject { Count: 0 }) preset.Remove("weights");
                if (preset is { Count: 3 }) (root["presets"] as JsonArray)?.Remove(preset);
                continue;
            }
            var value = ParseWeight(raw, key.ObjectType);
            preset ??= AddPreset(root, key.Place);
            if (preset["weights"] is null) preset["weights"] = new JsonObject();
            if (preset["weights"] is not JsonObject entries)
                throw new InvalidDataException("Invalid preset weights for " + key.Place.Label + ".");
            entries[key.ObjectType] = JsonValue.Create(value);
        }
        foreach (var (key, raw) in candidateDrafts)
            {
                var preset = FindPreset(root, key.Place);
                var candidates = preset?["addCandidates"] as JsonArray;
                var candidate = candidates?.OfType<JsonObject>().FirstOrDefault(item =>
                    item["objectType"]?.GetValue<string>() == key.ObjectType);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    if (candidate is not null) candidates!.Remove(candidate);
                    if (candidates is { Count: 0 }) preset!.Remove("addCandidates");
                    if (preset is { Count: 3 }) (root["presets"] as JsonArray)?.Remove(preset);
                    continue;
                }
                var value = ParseWeight(raw, key.ObjectType);
                preset ??= AddPreset(root, key.Place);
                candidates ??= EnsureArray(preset, "addCandidates");
                if (candidate is null)
                {
                    candidate = new JsonObject { ["objectType"] = key.ObjectType };
                    candidates.Add((JsonNode)candidate);
                }
                candidate["weight"] = JsonValue.Create(value);
            }
        foreach (var (key, raw) in counts)
        {
            var entry = FindMapCount(root, key.Map, key.ObjectType);
            if (string.IsNullOrWhiteSpace(raw))
            {
                if (entry is not null) ((JsonArray)root["mapCounts"]!).Remove(entry);
                continue;
            }
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value is < 0 or > 10000)
                throw new InvalidDataException("Count for " + key.ObjectType + " must be between 0 and 10000.");
            if (entry is null)
            {
                var array = EnsureArray(root, "mapCounts");
                entry = new JsonObject { ["map"] = key.Map, ["objectType"] = key.ObjectType };
                array.Add((JsonNode)entry);
            }
            entry["count"] = value;
        }
        foreach (var (objectType, raw) in occurrences)
        {
            var objects = root["objects"] as JsonObject;
            var entry = objects?[objectType] as JsonObject;
            if (string.IsNullOrWhiteSpace(raw))
            {
                entry?.Remove("occurrenceFactor");
                if (entry is { Count: 0 }) objects!.Remove(objectType);
                continue;
            }
            var value = ParseWeight(raw, objectType);
            objects ??= EnsureObject(root, "objects");
            if (entry is null)
            {
                entry = new JsonObject();
                objects[objectType] = entry;
            }
            entry["occurrenceFactor"] = JsonValue.Create(value);
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private void ReadPackagedCatalogs(string dataDirectory, HashSet<string> objectTypes, HashSet<LootPlaceKey> places,
        HashSet<string> maps)
    {
        var directory = Path.Combine(dataDirectory, "vanilla-catalogs");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "LVL_*.json")
                     .OrderBy(path => path, StringComparer.Ordinal).Take(32))
            ReadCatalog(path, Path.GetFileNameWithoutExtension(path), objectTypes, places, maps);
    }

    private void ReadCatalog(string path, string? mapName, HashSet<string> objectTypes,
        HashSet<LootPlaceKey> places, HashSet<string> maps)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).Length > 4_194_304)
            throw new InvalidDataException("Invalid LootControl catalog: " + path);
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(path));
        ReadCatalog(catalog.RootElement, mapName, objectTypes, places, maps);
    }

    private void ReadCatalog(JsonElement root, string? mapName, HashSet<string> objectTypes,
        HashSet<LootPlaceKey> places, HashSet<string> maps)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid LootControl catalog.");
        if (mapName is null && root.TryGetProperty("map", out var mapPath) && mapPath.ValueKind == JsonValueKind.String)
        {
            var match = Regex.Match(mapPath.GetString() ?? "", @"LVL_[A-Za-z0-9_]+", RegexOptions.CultureInvariant);
            if (match.Success) mapName = match.Value;
        }
        if (mapName is not null) maps.Add(mapName);
        if (root.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Array)
            foreach (var item in objects.EnumerateArray())
            {
                if (!TryString(item, "objectType", out var type) || !IsObjectType(type)) continue;
                objectTypes.Add(type);
                if (item.TryGetProperty("occurrenceFactor", out var factor) && factor.TryGetDecimal(out var value))
                    _vanillaOccurrence.TryAdd(type, value);
            }
        if (root.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Array)
            foreach (var item in counts.EnumerateArray())
            {
                if (!TryString(item, "objectType", out var type) || !IsObjectType(type)) continue;
                objectTypes.Add(type);
                if (mapName is not null && item.TryGetProperty("count", out var count) && count.TryGetInt32(out var value))
                    _vanillaCounts.TryAdd((mapName, type), value);
            }
        if (root.TryGetProperty("presets", out var presets) && presets.ValueKind == JsonValueKind.Array)
            foreach (var preset in presets.EnumerateArray())
            {
                if (preset.ValueKind != JsonValueKind.Object ||
                    !preset.TryGetProperty("vault", out var vault) ||
                    vault.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !preset.TryGetProperty("security", out var security) ||
                    !security.TryGetInt32(out var level) ||
                    !TryString(preset, "pointType", out var pointType) ||
                    !preset.TryGetProperty("candidates", out var candidates) ||
                    candidates.ValueKind != JsonValueKind.Array) continue;
                var place = new LootPlaceKey(vault.GetBoolean(), level, pointType);
                places.Add(place);
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (candidate.ValueKind != JsonValueKind.Object ||
                        !TryString(candidate, "objectType", out var type) || !IsObjectType(type) ||
                        !candidate.TryGetProperty("weight", out var weight) ||
                        !weight.TryGetDecimal(out var value) || value is < 0 or > 10000) continue;
                    objectTypes.Add(type);
                    _vanillaWeights.TryAdd((place, type), value);
                }
            }
        if (root.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
            foreach (var point in points.EnumerateArray())
            {
                if (point.ValueKind != JsonValueKind.Object ||
                    point.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True ||
                    point.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.True ||
                    !TryString(point, "originalSelection", out var selection) || selection != "Preset" ||
                    !point.TryGetProperty("vault", out var vault) || vault.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !point.TryGetProperty("security", out var security) || !security.TryGetInt32(out var level) ||
                    !TryString(point, "pointType", out var pointType)) continue;
                places.Add(new LootPlaceKey(vault.GetBoolean(), level, pointType));
            }
    }

    private static bool TryString(JsonElement obj, string key, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var node) ||
            node.ValueKind != JsonValueKind.String) return false;
        value = node.GetString() ?? "";
        return value.Length > 0;
    }

    private static bool IsObjectType(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');

    private static bool TryPlace(JsonObject obj, out LootPlaceKey place)
    {
        place = default!;
        if (obj["vault"] is not JsonValue vault || !vault.TryGetValue<bool>(out var inVault) ||
            obj["security"] is not JsonValue security || !security.TryGetValue<int>(out var level) ||
            obj["pointType"] is not JsonValue pointType || !pointType.TryGetValue<string>(out var type)) return false;
        place = new LootPlaceKey(inVault, level, type);
        return true;
    }

    private static JsonObject? FindPreset(JsonObject root, LootPlaceKey place) =>
        (root["presets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => TryPlace(item, out var existing) && existing == place);

    private static JsonObject AddPreset(JsonObject root, LootPlaceKey place)
    {
        var item = new JsonObject { ["vault"] = place.Vault, ["security"] = place.Security,
            ["pointType"] = place.PointType };
        EnsureArray(root, "presets").Add((JsonNode)item);
        return item;
    }

    private static JsonObject? FindMapCount(JsonObject root, string map, string objectType) =>
        (root["mapCounts"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => item["map"]?.GetValue<string>() == map &&
                item["objectType"]?.GetValue<string>() == objectType);

    private static JsonArray EnsureArray(JsonObject root, string key)
    {
        if (root[key] is null) root[key] = new JsonArray();
        return root[key] as JsonArray ?? throw new InvalidDataException("Invalid LootControl " + key + " section.");
    }

    private static JsonObject EnsureObject(JsonObject root, string key)
    {
        if (root[key] is null) root[key] = new JsonObject();
        return root[key] as JsonObject ?? throw new InvalidDataException("Invalid LootControl " + key + " section.");
    }

    private static decimal ParseWeight(string raw, string objectType)
    {
        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value is < 0 or > 10000)
            throw new InvalidDataException("Weight for " + objectType + " must be between 0 and 10000.");
        return value;
    }
}
