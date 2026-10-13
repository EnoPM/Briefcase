using System.Globalization;
using System.Text;

namespace Briefcase.ServerApp;

public static class ServerIniEditor
{
    public const string Section = "/Script/DeceiveInc.TripwireServerSettings";
    public static readonly (string Id, string Label)[] Maps =
    [
        ("DI_Hardsell", "Hard Sell"), ("DI_SR", "Silver Reef"), ("DI_DS", "Diamond Spire"),
        ("DI_FS", "Fragrant Shore"), ("DI_SE", "Sound Eclipse"),
        ("DI_HSD", "Hard Sell (Morning)"), ("DI_FSN", "Fragrant Shore (Night)")
    ];
    private static string NativeMapName(string id) => id switch
    {
        "DI_Hardsell" => "Hardsell",
        "DI_SR" => "Silverreef",
        "DI_DS" => "Diamondspire",
        "DI_FS" => "FragrantShore",
        "DI_SE" => "SoundEclipse",
        "DI_HSD" => "Hardsell_Day",
        "DI_FSN" => "FragrantShore_Night",
        _ => throw new InvalidDataException("Unsupported map: " + id)
    };
    public static IReadOnlyList<FormField> Fields => FormSchema.LoadBase().Fields;

    public static void WriteDefault(string file, string? name = null, ServerLocation? installation = null)
    {
        var schema = installation is null ? FormSchema.LoadBase() : FormSchema.LoadForInstallation(installation);
        var values = schema.Defaults();
        values["ServerName"] = name is null ? values["ServerName"] : AppDataStore.ValidateServerName(name);
        var lines = new List<string>
        {
            "[" + Section + "]",
            "ServerName=" + values["ServerName"],
            "MaxPlayers=" + values["MaxPlayers"],
            "ServerRegion=" + values["ServerRegion"],
            "GameMode=" + values["GameMode"]
        };
        lines.AddRange(RotationEntries(Maps.Select(map => map.Id)));
        lines.AddRange([
            "Password=" + values["Password"],
            "bCrossplay=" + values["bCrossplay"],
            "Platform=PC",
            "bIsPublic=" + values["bIsPublic"],
            "bIsOfficial=False",
            "GamePort=" + values["GamePort"],
            "QueryPort=" + values["QueryPort"],
            "bEnableUPnP=" + values["bEnableUPnP"],
            "AdminPassword=",
            "AutoShutdownEmptyMinutes=" + values["AutoShutdownEmptyMinutes"],
            "bSandboxMode=" + values["bSandboxMode"],
            "bFillWithBots=" + values["bFillWithBots"],
            "BotsDifficulty=" + values["BotsDifficulty"],
            "bRandomizeMap=" + values["bRandomizeMap"],
            "BotsAmount=" + values["BotsAmount"],
            "HeatPercentDamagingCivilian=" + values["HeatPercentDamagingCivilian"],
            "HeatPercentDamagingStaff=" + values["HeatPercentDamagingStaff"],
            "HeatPercentDamagingGuard=" + values["HeatPercentDamagingGuard"],
            "HeatPercentDamagingTechnician=" + values["HeatPercentDamagingTechnician"],
            "HeatPercentDamagingVIP=" + values["HeatPercentDamagingVIP"],
            "ScoldHeatPerSecond=" + values["ScoldHeatPerSecond"],
            "HeatDelayForSpyHit=" + values["HeatDelayForSpyHit"],
            "HeatDelayPassiveGain=" + values["HeatDelayPassiveGain"],
            "HeatDelayAggroPostCover=" + values["HeatDelayAggroPostCover"],
            "HeatDelayToDecay=" + values["HeatDelayToDecay"],
            "HeatDecayRate=" + values["HeatDecayRate"]
        ]);
        foreach (var field in schema.Fields.Where(field => field.Key is
                     "MinimumDeployedPlayersToStart" or "bTournamentMode"))
            lines.Add(field.Key + "=" + values[field.Key]);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    public static string? ReadConfiguredServerName(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var active = false;
            string? name = null;
            foreach (var line in File.ReadLines(file))
            {
                var raw = line.Trim();
                if (raw.StartsWith('['))
                {
                    active = raw.Equals("[" + Section + "]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!active || raw.StartsWith(';') || raw.StartsWith('#')) continue;
                var eq = raw.IndexOf('=');
                if (eq < 1 || !raw[..eq].Trim().Equals("ServerName", StringComparison.OrdinalIgnoreCase)) continue;
                name = raw[(eq + 1)..].Trim().Trim('"').Trim();
            }
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void SaveServerName(string file, string name)
    {
        name = AppDataStore.ValidateServerName(name);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var section = "[" + Section + "]";
        var start = lines.FindIndex(line => line.Trim().Equals(section, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            start = lines.Count;
            lines.Add(section);
        }
        var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var index = end - 1; index > start; index--)
        {
            var raw = lines[index].Trim();
            var equals = raw.IndexOf('=');
            if (equals > 0 && raw[..equals].Trim().Equals("ServerName", StringComparison.OrdinalIgnoreCase))
                lines.RemoveAt(index);
        }
        lines.Insert(start + 1, "ServerName=" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".briefcase-tmp";
        File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
        if (File.Exists(file)) File.Copy(file, file + ".briefcase-backup", true);
        File.Move(temporary, file, true);
    }

    public static (Dictionary<string, string> Values, List<string> Maps) Read(string file, FormSchema? schema = null)
        => ReadText(File.Exists(file) ? File.ReadAllText(file) : null, schema);

    public static (Dictionary<string, string> Values, List<string> Maps) ReadText(string? content,
        FormSchema? schema = null)
    {
        schema ??= FormSchema.LoadBase();
        var values = schema.Defaults();
        var maps = new List<string>();
        if (content is null) return (values, Maps.Select(x => x.Id).ToList());
        var activeSection = "";
        var rotationSpecified = false;
        foreach (var line in content.Split('\n'))
        {
            var raw = line.Trim();
            if (raw.StartsWith('[')) { activeSection = raw.Trim('[', ']'); continue; }
            if (activeSection.Length == 0 || raw.StartsWith(';') || raw.StartsWith('#')) continue;
            var eq = raw.IndexOf('=');
            if (eq < 1) continue;
            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            if (activeSection.Equals(schema.Section, StringComparison.OrdinalIgnoreCase) &&
                key.TrimStart('!', '+', '-', '.').Equals("MapRotation", StringComparison.OrdinalIgnoreCase))
            {
                rotationSpecified = true;
                if (key[0] == '!') { maps.Clear(); continue; }
                foreach (var map in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var id = map.Trim('"');
                    id = AppTranslations.TryResolveMapAlias(id, out var alias) ? alias : id;
                    if (key[0] == '-') maps.RemoveAll(existing => existing.Equals(id, StringComparison.OrdinalIgnoreCase));
                    else if (key[0] != '+' || !maps.Contains(id, StringComparer.OrdinalIgnoreCase)) maps.Add(id);
                }
            }
            else if (schema.Fields.Any(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                         (field.Section.Length == 0 ? schema.Section : field.Section) == activeSection))
                values[key] = value.Trim('"');
        }
        if (maps.Count == 0 && !rotationSpecified) maps.AddRange(Maps.Select(x => x.Id));
        return (values, maps);
    }

    public static void Save(string file, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> maps,
        FormSchema? schema = null)
    {
        var original = File.Exists(file) ? File.ReadAllText(file) : "";
        var updated = RenderText(original, values, maps, schema);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".briefcase-tmp";
        var backup = file + ".briefcase-backup";
        File.WriteAllText(temporary, updated, new UTF8Encoding(false));
        if (File.Exists(file)) File.Copy(file, backup, true);
        File.Move(temporary, file, true);
    }

    public static string RenderText(string original, IReadOnlyDictionary<string, string> values,
        IReadOnlyList<string> maps, FormSchema? schema = null)
    {
        schema ??= FormSchema.LoadBase();
        schema.Validate(values);
        if (maps.Count == 0 || maps.Any(map => !Maps.Any(item => item.Id == map)))
            throw new InvalidDataException("Select at least one supported map.");
        var lines = original.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
        foreach (var section in schema.Fields.Select(field => field.Section.Length == 0 ? schema.Section : field.Section)
                     .Append(schema.Section).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fields = schema.Fields.Where(field => (field.Section.Length == 0 ? schema.Section : field.Section)
                .Equals(section, StringComparison.OrdinalIgnoreCase)).ToArray();
            var start = lines.FindIndex(line => line.Trim().Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase));
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1] != "") lines.Add("");
                start = lines.Count;
                lines.Add("[" + section + "]");
            }
            var end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            for (var i = end - 1; i > start; i--)
            {
                var raw = lines[i].Trim();
                var eq = raw.IndexOf('=');
                if (eq < 1) continue;
                var key = raw[..eq].Trim().TrimStart('+', '!', '-', '.');
                if (section.Equals(schema.Section, StringComparison.OrdinalIgnoreCase) &&
                    key.Equals("MapRotation", StringComparison.OrdinalIgnoreCase) ||
                    fields.Any(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    lines.RemoveAt(i);
            }
            var entries = fields.Select(field => field.Key + "=" + values[field.Key]).ToList();
            if (section.Equals(schema.Section, StringComparison.OrdinalIgnoreCase))
            {
                entries.AddRange(RotationEntries(maps));
            }
            lines.InsertRange(start + 1, entries);
        }
        return string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n";
    }

    public static string RenderChangedText(string original,
        IReadOnlyDictionary<string, string> changes, FormSchema schema)
    {
        var (values, maps) = ReadText(original, schema);
        foreach (var (key, value) in changes)
        {
            if (!values.ContainsKey(key))
                throw new InvalidDataException("Unknown server configuration field: " + key);
            values[key] = value;
        }
        return RenderText(original, values, maps, schema);
    }

    public static void SaveMapRotation(string file, IReadOnlyList<string> maps)
    {
        var original = File.Exists(file) ? File.ReadAllText(file) : "";
        var updated = RenderMapRotationText(original, maps);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".briefcase-tmp";
        var backup = file + ".briefcase-backup";
        File.WriteAllText(temporary, updated, new UTF8Encoding(false));
        if (File.Exists(file)) File.Copy(file, backup, true);
        File.Move(temporary, file, true);
    }

    public static string RenderMapRotationText(string original, IReadOnlyList<string> maps)
    {
        if (maps.Count == 0 || maps.Any(map => !Maps.Any(item => item.Id == map)))
            throw new InvalidDataException("Select at least one supported map.");

        var lines = original.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(x => x.Trim().Equals("[" + Section + "]", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1] != "") lines.Add("");
            start = lines.Count;
            lines.Add("[" + Section + "]");
        }
        var end = lines.FindIndex(start + 1, x => x.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var i = end - 1; i > start; i--)
        {
            var raw = lines[i].Trim();
            var eq = raw.IndexOf('=');
            if (eq < 1) continue;
            var key = raw[..eq].Trim().TrimStart('+', '!', '-', '.');
            if (key.Equals("MapRotation", StringComparison.OrdinalIgnoreCase)) lines.RemoveAt(i);
        }
        var entries = RotationEntries(maps);
        lines.InsertRange(start + 1, entries);

        return string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n";
    }

    private static List<string> RotationEntries(IEnumerable<string> maps)
    {
        var entries = new List<string> { "!MapRotation=ClearArray" };
        entries.AddRange(maps.Select(map => ".MapRotation=" + NativeMapName(map)));
        return entries;
    }
}
