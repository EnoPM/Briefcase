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
    public static IReadOnlyList<FormField> Fields => FormSchema.LoadBase().Fields;

    public static void WriteDefault(string file, string? name = null)
    {
        var values = FormSchema.LoadBase().Defaults();
        values["ServerName"] = name is null ? values["ServerName"] : AppDataStore.ValidateServerName(name);
        var lines = new List<string>
        {
            "[" + Section + "]",
            "ServerName=" + values["ServerName"],
            "MaxPlayers=" + values["MaxPlayers"],
            "ServerRegion=" + values["ServerRegion"],
            "GameMode=" + values["GameMode"]
        };
        lines.AddRange(new[] { "Hardsell", "Silverreef", "Diamondspire", "FragrantShore",
            "SoundEclipse", "Hardsell_Day", "FragrantShore_Night" }.Select(map => "MapRotation=" + map));
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
    {
        schema ??= FormSchema.LoadBase();
        var values = schema.Defaults();
        var maps = new List<string>();
        if (!File.Exists(file)) return (values, Maps.Select(x => x.Id).ToList());
        var activeSection = "";
        var rotationSpecified = false;
        foreach (var line in File.ReadLines(file))
        {
            var raw = line.Trim();
            if (raw.StartsWith('[')) { activeSection = raw.Trim('[', ']'); continue; }
            if (activeSection.Length == 0 || raw.StartsWith(';') || raw.StartsWith('#')) continue;
            var eq = raw.IndexOf('=');
            if (eq < 1) continue;
            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            if (activeSection == schema.Section && key == "!MapRotation")
            { maps.Clear(); rotationSpecified = true; continue; }
            if (activeSection == schema.Section && key is "+MapRotation" or "MapRotation")
            {
                rotationSpecified = true;
                foreach (var map in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var id = map.Trim('"');
                    maps.Add(AppTranslations.TryResolveMapAlias(id, out var alias) ? alias : id);
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
        schema ??= FormSchema.LoadBase();
        schema.Validate(values);
        if (maps.Count == 0 || maps.Any(map => !Maps.Any(item => item.Id == map)))
            throw new InvalidDataException("Select at least one supported map.");
        var original = File.Exists(file) ? File.ReadAllText(file) : "";
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
                if (section.Equals(schema.Section, StringComparison.OrdinalIgnoreCase) && key == "MapRotation" ||
                    fields.Any(field => field.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                    lines.RemoveAt(i);
            }
            var entries = fields.Select(field => field.Key + "=" + values[field.Key]).ToList();
            if (section.Equals(schema.Section, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add("!MapRotation=ClearArray");
                entries.AddRange(maps.Select(map => "+MapRotation=" + map));
            }
            lines.InsertRange(start + 1, entries);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".briefcase-tmp";
        var backup = file + ".briefcase-backup";
        File.WriteAllText(temporary, string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n", new UTF8Encoding(false));
        if (File.Exists(file)) File.Copy(file, backup, true);
        File.Move(temporary, file, true);
    }

    public static void SaveMapRotation(string file, IReadOnlyList<string> maps)
    {
        if (maps.Count == 0 || maps.Any(map => !Maps.Any(item => item.Id == map)))
            throw new InvalidDataException("Select at least one supported map.");

        var original = File.Exists(file) ? File.ReadAllText(file) : "";
        var lines = original.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(x => x.Trim() == "[" + Section + "]");
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
        var entries = new List<string> { "!MapRotation=ClearArray" };
        entries.AddRange(maps.Select(map => "+MapRotation=" + map));
        lines.InsertRange(start + 1, entries);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".briefcase-tmp";
        var backup = file + ".briefcase-backup";
        File.WriteAllText(temporary, string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n", new UTF8Encoding(false));
        if (File.Exists(file)) File.Copy(file, backup, true);
        File.Move(temporary, file, true);
    }
}
