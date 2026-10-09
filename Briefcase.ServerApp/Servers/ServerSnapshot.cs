using System.Text.Json;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Briefcase.ServerApp;

public sealed record PlayerGadget(string Id)
{
    public string CanonicalId => Id switch
    {
        "BounceMat" => "BouncingMat",
        "RemoteTurret" => "AutoTurret",
        "Shieldbrella" => "UmbrellaShield",
        "SoundMachine" => "Soundbox",
        _ => Id
    };
    public Bitmap? Icon => BalanceIcons.Gadget(CanonicalId);
    public bool IconUnavailable => Icon is null;
    public string Label => AppTranslations.Get("game.gadgets." + CanonicalId, CanonicalId);
}

public sealed record PlayerUpgrade(string Tier, string Kind, bool Owned = false)
{
    private static readonly IReadOnlyDictionary<string, IBrush> TierBrushes =
        new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase)
        {
            ["Civilian"] = new SolidColorBrush(Color.Parse("#E9DEC0")),
            ["Staff"] = new SolidColorBrush(Color.Parse("#5AD345")),
            ["Guard"] = new SolidColorBrush(Color.Parse("#3D81C7")),
            ["Technician"] = new SolidColorBrush(Color.Parse("#E554F8")),
            ["VIP"] = new SolidColorBrush(Color.Parse("#FFB400"))
        };

    public Bitmap? Icon => BalanceIcons.Upgrade(Kind);
    public bool IconUnavailable => Icon is null;
    public IBrush Tint => TierBrushes.GetValueOrDefault(Tier) ?? Brushes.Gray;
    public double Opacity => Owned ? 1.0 : 0.43;
    public string Label => AppTranslations.Get("game.upgrades." + Kind, Kind);
    public string Detail => Tier + " · " + Label + " · " +
        AppTranslations.Get(Owned ? "player.upgrade_owned" : "player.upgrade_selected");
}

public sealed record PlayerLoadoutItem(string Character, string Category, string Variant)
{
    public Bitmap? Icon => BalanceIcons.Loadout(Character, Category, Variant);
    public bool IconUnavailable => Icon is null;
    public string Detail => AppTranslations.Get("balance.category." + Category) + " · " +
        (BalanceGameNames.Variant(Character, Category, Variant) ?? Variant);
}

public sealed record PlayerRow(string Name, string Token, string Platform, string NetworkId, string PlatformId,
    string Character = "", int? AccountLevel = null, int? CharacterLevel = null,
    IReadOnlyList<PlayerGadget>? Gadgets = null, IReadOnlyList<PlayerUpgrade>? Upgrades = null,
    IReadOnlyList<PlayerLoadoutItem>? Loadout = null, int? Team = null,
    bool? Dead = null, double? Health = null, double? MaxHealth = null)
{
    private static readonly IBrush BasicLevelBackground = new SolidColorBrush(Color.Parse("#302A37"));
    private static readonly IBrush BasicLevelBorder = new SolidColorBrush(Color.Parse("#D8D2DF"));
    private static readonly IBrush BasicLevelForeground = new SolidColorBrush(Color.Parse("#F5F3F8"));
    private static readonly IBrush GoldLevelBackground = new SolidColorBrush(Color.Parse("#382B17"));
    private static readonly IBrush GoldLevelBorder = new SolidColorBrush(Color.Parse("#C69B46"));
    private static readonly IBrush GoldLevelForeground = new SolidColorBrush(Color.Parse("#FFD36A"));
    private static readonly IBrush LiveHealthForeground = new SolidColorBrush(Color.Parse("#69D5A0"));
    private static readonly IBrush DeadHealthForeground = new SolidColorBrush(Color.Parse("#918A9B"));

    public bool IsDead => Dead == true;
    public Bitmap? Portrait => string.IsNullOrWhiteSpace(Character) ? null : BalanceIcons.CharacterPortrait(Character, IsDead);
    public bool HasPortrait => Portrait is not null;
    public bool PortraitUnavailable => !HasPortrait;
    public bool HasHealth => Health is >= 0 && MaxHealth is > 0;
    public double HealthPercent => HasHealth ? Math.Clamp(Health!.Value / MaxHealth!.Value * 100, 0, 100) : 0;
    public double HealthBarWidth => 96 * HealthPercent / 100;
    public IBrush HealthForeground => IsDead ? DeadHealthForeground : LiveHealthForeground;
    public string HealthText => HasHealth
        ? Health!.Value.ToString("0.#", CultureInfo.CurrentCulture) + "/" +
          MaxHealth!.Value.ToString("0.#", CultureInfo.CurrentCulture) : "";
    public bool HasCharacterLevel => !string.IsNullOrWhiteSpace(Character) && CharacterLevel is > 0;
    public bool IsGoldCharacterLevel => CharacterLevel is > 10;
    public int? DisplayCharacterLevel => CharacterLevel is > 10 ? CharacterLevel - 10 : CharacterLevel;
    public string CharacterLevelBadgeText => DisplayCharacterLevel is { } level
        ? AppTranslations.Format("player.character_level_badge", "Level {0}", level) : "";
    public IBrush CharacterLevelBadgeBackground => IsGoldCharacterLevel ? GoldLevelBackground : BasicLevelBackground;
    public IBrush CharacterLevelBadgeBorder => IsGoldCharacterLevel ? GoldLevelBorder : BasicLevelBorder;
    public IBrush CharacterLevelBadgeForeground => IsGoldCharacterLevel ? GoldLevelForeground : BasicLevelForeground;
    public IReadOnlyList<PlayerGadget> GadgetItems => Gadgets ?? [];
    public IReadOnlyList<PlayerUpgrade> UpgradeItems => Upgrades ?? [];
    public IReadOnlyList<PlayerLoadoutItem> LoadoutItems => Loadout ?? [];
    public bool HasGadgets => GadgetItems.Count > 0;
    public bool HasUpgrades => UpgradeItems.Count > 0;
    public bool GadgetsUnavailable => !HasGadgets;
    public bool UpgradesUnavailable => !HasUpgrades;
    public bool LoadoutUnavailable => LoadoutItems.Count == 0;
    public bool HasNetworkId => !string.IsNullOrWhiteSpace(NetworkId);
    public string CharacterSummary => string.IsNullOrWhiteSpace(Character) ? AppTranslations.Get("player.character_unknown")
        : AppTranslations.Format("player.character_name", "Character: {0}", BalanceGameNames.Character(Character));
    public string AccountLevelText => AppTranslations.Format("player.account_level", "Account level: {0}", AccountLevel?.ToString() ?? "—");
    public string Identity => !HasNetworkId ? "" : string.IsNullOrWhiteSpace(Platform)
        ? NetworkId : Platform.Trim() + " · " + NetworkId;
}

public sealed record ServerSnapshot(
    string Name, string State, string Map, string GameMode,
    int? PlayerCount, int? MaxPlayers, IReadOnlyList<PlayerRow> Players,
    string Source, bool HasPlayerRoster);

public enum LiveChannelStatus { Disconnected, Connected, InvalidData, Stale }

public static class ServerSnapshotReader
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(45);

    public static ServerSnapshot Read(ServerLocation location, int? runningPid, ServerSnapshot? live,
        LiveChannelStatus channel = LiveChannelStatus.Disconnected, TimeSpan? processUptime = null,
        TimeSpan? connectionAge = null)
    {
        if (runningPid is null)
            return new("—", "Offline", "—", "—", null, null, [], "No server process", false);
        var settings = ServerIniEditor.Read(location.ConfigFile).Values;
        var name = settings.GetValueOrDefault("ServerName", "—");
        var mode = settings.GetValueOrDefault("GameMode", "—");
        int? max = int.TryParse(settings.GetValueOrDefault("MaxPlayers"), out var number) ? number : null;
        if (live is null)
        {
            var uptime = processUptime ?? TimeSpan.Zero;
            var (state, source) = uptime < StartupGrace
                ? ("Starting", "Starting dedicated server process")
                : channel switch
                {
                    LiveChannelStatus.Connected => ("Waiting for data", "Local channel connected; awaiting server data"),
                    LiveChannelStatus.InvalidData => ("Incompatible data", "Local server data rejected"),
                    LiveChannelStatus.Stale => ("Data interrupted", "Local server data stopped updating"),
                    _ when (connectionAge ?? uptime) < ConnectionTimeout => ("Connecting", "Connecting to local server channel"),
                    _ => ("Connection unavailable", "Local server channel unavailable")
                };
            return new(name, state, "—", mode, null, max, [], source, false);
        }
        return live with
        {
            Name = name,
            GameMode = mode,
            MaxPlayers = max
        };
    }

    public static ServerSnapshot? ParseLive(string line, int expectedPid, string? expectedInstanceId = null)
    {
        if (line.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 ||
                root.GetProperty("type").GetString() != "snapshot" ||
                root.GetProperty("processId").GetInt32() != expectedPid)
                return null;
            if (expectedInstanceId is not null &&
                (!root.TryGetProperty("instanceId", out var identity) ||
                 identity.ValueKind != JsonValueKind.String ||
                 !string.Equals(identity.GetString(), expectedInstanceId, StringComparison.Ordinal)))
                return null;
            var state = Text(root, "state");
            var map = Text(root, "map");
            if (state.Length > 64 || map.Length > 128) return null;
            var players = new List<PlayerRow>();
            foreach (var player in root.GetProperty("players").EnumerateArray())
            {
                if (players.Count >= 128) return null;
                var name = player.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128) return null;
                var token = player.GetProperty("token").GetString();
                var platform = player.GetProperty("platform").GetString() ?? "Unknown";
                var networkId = player.GetProperty("networkId").GetString() ?? "";
                var platformId = player.TryGetProperty("platformId", out var platformValue)
                    ? platformValue.GetString() ?? "" : "";
                var character = player.TryGetProperty("character", out var characterValue) &&
                    characterValue.ValueKind == JsonValueKind.String ? characterValue.GetString() ?? "" : "";
                var accountLevel = OptionalLevel(player, "accountLevel");
                var characterLevel = OptionalLevel(player, "characterLevel");
                var gadgets = ParseGadgets(player);
                var upgrades = ParseUpgrades(player);
                var loadout = ParseLoadout(player, character);
                var team = player.TryGetProperty("team", out var teamValue) &&
                    teamValue.ValueKind == JsonValueKind.Number && teamValue.TryGetInt32(out var parsedTeam) &&
                    parsedTeam is >= 0 and < 32 ? parsedTeam : (int?)null;
                var dead = player.TryGetProperty("dead", out var deadValue) ? deadValue.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => (bool?)null
                } : null;
                var health = OptionalHealth(player, "health");
                var maxHealth = OptionalHealth(player, "maxHealth");
                if (string.IsNullOrWhiteSpace(token) || token.Length > 64 ||
                    !token.All(char.IsAsciiLetterOrDigit) || platform.Length > 32 ||
                    networkId.Length > 256 || platformId.Length > 256 || character.Length > 64 ||
                    character.Any(char.IsControl)) return null;
                players.Add(new PlayerRow(name, token, platform, networkId, platformId,
                    character, accountLevel, characterLevel, gadgets, upgrades, loadout, team,
                    dead, health, maxHealth));
            }
            return new("—", state, map, "—", players.Count, null, players,
                "Live server connection", true);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) ? value.GetString() ?? "—" : "—";

    private static int? OptionalLevel(JsonElement player, string key) =>
        player.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var level) && level is >= 1 and <= 10000 ? level : null;

    private static double? OptionalHealth(JsonElement player, string key) =>
        player.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var health) && double.IsFinite(health) && health is >= 0 and <= 10000
            ? health : null;

    private static IReadOnlyList<PlayerGadget> ParseGadgets(JsonElement player)
    {
        if (!player.TryGetProperty("gadgets", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<PlayerGadget>(2);
        foreach (var value in values.EnumerateArray())
        {
            if (result.Count == 2) break;
            var id = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (ValidAssetId(id)) result.Add(new PlayerGadget(id!));
        }
        return result;
    }

    private static IReadOnlyList<PlayerUpgrade> ParseUpgrades(JsonElement player)
    {
        if (!player.TryGetProperty("upgrades", out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<PlayerUpgrade>(5);
        foreach (var value in values.EnumerateArray())
        {
            if (result.Count == 5) break;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("tier", out var tierValue) ||
                !value.TryGetProperty("kind", out var kindValue) ||
                tierValue.ValueKind != JsonValueKind.String ||
                kindValue.ValueKind != JsonValueKind.String) continue;
            var tier = tierValue.GetString();
            var kind = kindValue.GetString();
            var owned = value.TryGetProperty("owned", out var ownedValue) &&
                        ownedValue.ValueKind == JsonValueKind.True;
            if (tier is not ("Civilian" or "Staff" or "Guard" or "Technician" or "VIP") ||
                !ValidAssetId(kind)) continue;
            result.Add(new PlayerUpgrade(tier, kind!, owned));
        }
        return result;
    }

    private static IReadOnlyList<PlayerLoadoutItem> ParseLoadout(JsonElement player, string character)
    {
        if (!ValidAssetId(character) ||
            !player.TryGetProperty("loadout", out var values) || values.ValueKind != JsonValueKind.Object)
            return [];
        var result = new List<PlayerLoadoutItem>(3);
        foreach (var (field, category) in new[] {
                     ("weapon", "Weapons"), ("expertise", "Expertises"), ("passive", "Passives") })
        {
            if (!values.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out var index) || index is < 0 or > 2) continue;
            result.Add(new PlayerLoadoutItem(character, category,
                index == 0 ? "Base" : "Mod" + index));
        }
        return result;
    }

    private static bool ValidAssetId(string? id) => id is { Length: > 0 and <= 48 } &&
        id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
}
