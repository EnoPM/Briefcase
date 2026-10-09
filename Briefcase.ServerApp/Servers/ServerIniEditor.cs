using System.Globalization;
using System.Text;

namespace Briefcase.ServerApp;

public enum SettingKind { Text, Secret, Integer, Decimal, Boolean, Choice }
public sealed record SettingSpec(string Key, string Label, string Category, SettingKind Kind,
    string Default, int? Minimum = null, int? Maximum = null, string[]? Choices = null);

public static class ServerIniEditor
{
    public const string Section = "/Script/DeceiveInc.TripwireServerSettings";
    public static readonly (string Id, string Label)[] Maps =
    [
        ("DI_Hardsell", "Hard Sell"), ("DI_SR", "Silver Reef"), ("DI_DS", "Diamond Spire"),
        ("DI_FS", "Fragrant Shore"), ("DI_SE", "Sound Eclipse"),
        ("DI_FSN", "Fragrant Shore (Night)"), ("DI_HSD", "Hard Sell (Morning)")
    ];
    public static readonly SettingSpec[] Fields =
    [
        new("ServerName", "Server name", "Identity", SettingKind.Text, "Deceive Inc. Server"),
        new("Password", "Join password", "Identity", SettingKind.Secret, ""),
        new("ServerRegion", "Region", "Identity", SettingKind.Choice, "eu", Choices: ["", "us-east", "us-central", "us-west", "eu", "oce", "br", "asia", "me"]),
        new("GameMode", "Game mode", "Game", SettingKind.Choice, "Solo", Choices: ["Solo", "Duo", "Trio"]),
        new("MaxPlayers", "Maximum players", "Game", SettingKind.Integer, "8", 1, 16),
        new("bIsPublic", "Public server", "Game", SettingKind.Boolean, "True"),
        new("bSandboxMode", "Sandbox mode", "Game", SettingKind.Boolean, "False"),
        new("bRandomizeMap", "Random maps", "Maps", SettingKind.Boolean, "False"),
        new("GamePort", "Game port (UDP)", "Network", SettingKind.Integer, "50000", 1024, 65535),
        new("QueryPort", "Query port (UDP)", "Network", SettingKind.Integer, "50001", 1024, 65535),
        new("bCrossplay", "Crossplay", "Network", SettingKind.Boolean, "True"),
        new("bEnableUPnP", "UPnP port mapping", "Network", SettingKind.Boolean, "True"),
        new("AutoShutdownEmptyMinutes", "Auto shutdown when empty (minutes)", "Game", SettingKind.Integer, "0", 0, 1440),
        new("bFillWithBots", "Fill with bots", "Bots", SettingKind.Boolean, "True"),
        new("BotsDifficulty", "Bot difficulty", "Bots", SettingKind.Choice, "Normal", Choices: ["Easy", "Normal", "Difficult"]),
        new("BotsAmount", "Bot count", "Bots", SettingKind.Integer, "0", 0, 16),
        new("HeatPercentDamagingCivilian", "Heat from damaging a civilian (%)", "Heat", SettingKind.Integer, "34", -1, 100),
        new("HeatPercentDamagingStaff", "Heat from damaging staff (%)", "Heat", SettingKind.Integer, "34", -1, 100),
        new("HeatPercentDamagingGuard", "Heat from damaging a guard (%)", "Heat", SettingKind.Integer, "17", -1, 100),
        new("HeatPercentDamagingTechnician", "Heat from damaging a technician (%)", "Heat", SettingKind.Integer, "34", -1, 100),
        new("HeatPercentDamagingVIP", "Heat from damaging a VIP (%)", "Heat", SettingKind.Integer, "100", -1, 100),
        new("ScoldHeatPerSecond", "Heat per second", "Heat", SettingKind.Decimal, "1.5", 0, 10),
        new("HeatDelayForSpyHit", "Heat delay after hitting a spy", "Heat", SettingKind.Decimal, "2.5", 0, 30),
        new("HeatDelayPassiveGain", "Passive heat gain delay", "Heat", SettingKind.Decimal, "5.0", 0, 30),
        new("HeatDelayAggroPostCover", "Heat delay after cover", "Heat", SettingKind.Decimal, "5.0", 0, 30),
        new("HeatDelayToDecay", "Heat decay delay", "Heat", SettingKind.Decimal, "1.0", 0, 30),
        new("HeatDecayRate", "Heat decay rate", "Heat", SettingKind.Decimal, "1.35", 0, 10)
    ];

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

    public static (Dictionary<string, string> Values, List<string> Maps) Read(string file)
    {
        var values = Fields.ToDictionary(f => f.Key, f => f.Default, StringComparer.OrdinalIgnoreCase);
        var maps = new List<string>();
        if (!File.Exists(file)) return (values, Maps.Select(x => x.Id).ToList());
        var active = false;
        var rotationSpecified = false;
        foreach (var line in File.ReadLines(file))
        {
            var raw = line.Trim();
            if (raw.StartsWith('[')) { active = raw == "[" + Section + "]"; continue; }
            if (!active || raw.StartsWith(';') || raw.StartsWith('#')) continue;
            var eq = raw.IndexOf('=');
            if (eq < 1) continue;
            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..].Trim();
            if (key == "!MapRotation") { maps.Clear(); rotationSpecified = true; continue; }
            if (key is "+MapRotation" or "MapRotation")
            {
                rotationSpecified = true;
                foreach (var map in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    maps.Add(map.Trim('"'));
            }
            else if (values.ContainsKey(key)) values[key] = value.Trim('"');
        }
        if (maps.Count == 0 && !rotationSpecified) maps.AddRange(Maps.Select(x => x.Id));
        return (values, maps);
    }

    public static void Save(string file, IReadOnlyDictionary<string, string> values, IReadOnlyList<string> maps)
    {
        foreach (var spec in Fields)
        {
            if (!values.TryGetValue(spec.Key, out var value)) throw new InvalidDataException($"Missing {spec.Label}.");
            if (value.Contains('\r') || value.Contains('\n') || value.Length > 256)
                throw new InvalidDataException($"Invalid {spec.Label}.");
            if (spec.Key == "ServerName" && value.Length > 48)
                throw new InvalidDataException("Server name must be 48 characters or fewer.");
            if (spec.Kind == SettingKind.Integer && (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ||
                    number < spec.Minimum || number > spec.Maximum))
                throw new InvalidDataException($"{spec.Label} must be between {spec.Minimum} and {spec.Maximum}.");
            if (spec.Kind == SettingKind.Decimal && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue) ||
                    !double.IsFinite(decimalValue) || decimalValue < spec.Minimum || decimalValue > spec.Maximum))
                throw new InvalidDataException($"Invalid number for {spec.Label} (allowed: {spec.Minimum}–{spec.Maximum}).");
            if (spec.Kind == SettingKind.Boolean && !bool.TryParse(value, out _))
                throw new InvalidDataException($"Invalid checkbox for {spec.Label}.");
            if (spec.Kind == SettingKind.Choice && !spec.Choices!.Contains(value, StringComparer.Ordinal))
                throw new InvalidDataException($"Invalid choice for {spec.Label}.");
        }
        if (int.Parse(values["BotsAmount"], CultureInfo.InvariantCulture) >
            int.Parse(values["MaxPlayers"], CultureInfo.InvariantCulture))
            throw new InvalidDataException("Bot count cannot exceed maximum players.");
        if (values["GamePort"] == values["QueryPort"]) throw new InvalidDataException("Game and query ports must differ.");
        if (maps.Count == 0 || maps.Any(map => !Maps.Any(item => item.Id == map)))
            throw new InvalidDataException("Select at least one supported map.");
        var original = File.Exists(file) ? File.ReadAllText(file) : "";
        var lines = original.Split('\n').Select(x => x.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(x => x.Trim() == "[" + Section + "]");
        if (start < 0) { if (lines.Count > 0 && lines[^1] != "") lines.Add(""); start = lines.Count; lines.Add("[" + Section + "]"); }
        var end = lines.FindIndex(start + 1, x => x.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var i = end - 1; i > start; i--)
        {
            var raw = lines[i].Trim();
            var eq = raw.IndexOf('=');
            if (eq < 1) continue;
            var key = raw[..eq].Trim().TrimStart('+', '!', '-', '.');
            if (key == "MapRotation" || Fields.Any(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                lines.RemoveAt(i);
        }
        var entries = Fields.Select(f => f.Key + "=" + values[f.Key]).ToList();
        entries.Add("!MapRotation=ClearArray");
        entries.AddRange(maps.Select(map => "+MapRotation=" + map));
        lines.InsertRange(start + 1, entries);
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
