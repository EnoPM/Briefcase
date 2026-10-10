namespace Briefcase.ServerApp;

// Screenshot fixtures live entirely in memory. Nothing here names an existing installation.
public sealed record DemoServerProfile(ServerRow Row, ServerSnapshot Snapshot, IReadOnlyList<ModRow> Mods);

public static class DemoData
{
    public static IReadOnlyList<DemoServerProfile> CreateProfiles()
    {
        var atlas = Row("demo-atlas", "Atlas", "In match", "5 / 12");
        var nightshift = Row("demo-nightshift", "Nightshift", "Lobby", "2 / 8");
        nightshift.NeedsRestart = true;
        var sandbox = Row("demo-sandbox", "Sandbox", "Offline", "—");
        return
        [
            new(atlas, new ServerSnapshot("Atlas", "In match", "Silver Reef", "Trio", 5, 12,
                [
                    DemoPlayer("Aurora", "demo1", "PC", "sample-eos-001", "Ace", 43, 18,
                        "Drone", "Shield") with { Team = 0, Dead = false, Health = 82, MaxHealth = 100 },
                    DemoPlayer("Cipher", "demo2", "PC", "sample-eos-002", "Larcin", 27, 12,
                        "Jammer", "Eye") with { Team = 0, Dead = true, Health = 0, MaxHealth = 100 },
                    DemoPlayer("Juniper", "demo3", "Xbox", "sample-xbox-003", "Red", 61, 24,
                        "Mimic", "Glasses") with { Team = 0, Dead = false, Health = 120, MaxHealth = 120 },
                    DemoPlayer("Marlow", "demo4", "PC", "sample-eos-004", "Hans", 15, 7,
                        "Turret", "HackTrap") with { Team = 1 },
                    DemoPlayer("Vesper", "demo5", "PlayStation", "sample-ps-005", "Yumi", 39, 16,
                        "Soundbox", "BouncingMat") with { Team = 1 }
                ], "Local preview", true), ModsFor(atlas)),
            new(nightshift, new ServerSnapshot("Nightshift", "Lobby", "Hard Sell", "Solo", 2, 8,
                [
                    new("Kestrel", "demo6", "PC", "sample-eos-006", "", "Chavez", 21, 9),
                    new("Nox", "demo7", "PC", "sample-eos-007", "", "Squire", 34, 13)
                ], "Local preview", true), ModsFor(nightshift)),
            new(sandbox, new ServerSnapshot("Sandbox", "Offline", "—", "Duo", 0, 12,
                [], "No server process", false), ModsFor(sandbox))
        ];
    }

    public static (Dictionary<string, string> Values, List<string> Maps) Configuration(string id)
    {
        var values = ServerIniEditor.Fields.ToDictionary(field => field.Key, field => field.Default,
            StringComparer.OrdinalIgnoreCase);
        values["ServerName"] = id switch
        {
            "demo-nightshift" => "Nightshift",
            "demo-sandbox" => "Sandbox",
            _ => "Atlas"
        };
        values["GameMode"] = id switch
        {
            "demo-nightshift" => "Solo",
            "demo-sandbox" => "Duo",
            _ => "Trio"
        };
        values["MaxPlayers"] = id == "demo-nightshift" ? "8" : "12";
        values["bFillWithBots"] = "False";
        values["bEnableUPnP"] = "False";
        values["ServerRegion"] = "eu";
        values["GamePort"] = id == "demo-nightshift" ? "50100" : "50000";
        values["QueryPort"] = id == "demo-nightshift" ? "50101" : "50001";
        var maps = id == "demo-nightshift"
            ? new List<string> { "DI_Hardsell", "DI_SE", "DI_Hardsell" }
            : new List<string> { "DI_SR", "DI_DS", "DI_FS", "DI_SR" };
        return (values, maps);
    }

    public static IReadOnlyList<(string Label, string Value, bool Boolean)> ModConfiguration(string name) => name switch
    {
        "PlayerCap" => [("Solo maximum", "8", false), ("Duo maximum", "10", false),
            ("Trio maximum", "12", false)],
        "PregameTimer" => [("Lobby duration (seconds)", "30", false)],
        "SuspicionControl" => [("Suspicion multiplier", "0", false), ("Diagnostics", "False", true)],
        _ => []
    };

    public static IReadOnlyList<MarketplaceMod> Marketplace() =>
    [
        Market("PlayerCap", "Adjust the player limit for each game mode.", "1.3.0", "Current"),
        Market("PregameTimer", "Set a shorter or longer pregame lobby.", "1.2.0", "Current"),
        Market("SuspicionControl", "Tune the suspicion gained during a match.", "1.1.0", "Update available")
    ];

    private static ServerRow Row(string id, string name, string state, string players) =>
        new(id, Path.Combine("C:\\GameServers", name, "DeceiveInc", "Binaries", "Win64"), true)
        { State = state, Players = players };

    private static PlayerRow DemoPlayer(string name, string token, string platform,
        string networkId, string character, int accountLevel, int characterLevel,
        string firstGadget, string secondGadget) =>
        new(name, token, platform, networkId, "", character, accountLevel, characterLevel,
            [new PlayerGadget(firstGadget), new PlayerGadget(secondGadget)],
            [new PlayerUpgrade("Civilian", "Maxup_Health", true),
             new PlayerUpgrade("Staff", "Boost_HackSpeed", true),
             new PlayerUpgrade("Guard", "Maxup_Ammo", true),
             new PlayerUpgrade("Technician", "Boost_Expertise"),
             new PlayerUpgrade("VIP", "Maxup_Intel")],
            [new PlayerLoadoutItem(character, "Weapons", "Base"),
             new PlayerLoadoutItem(character, "Expertises", "Mod1"),
             new PlayerLoadoutItem(character, "Passives", "Mod2")]);

    private static IReadOnlyList<ModRow> ModsFor(ServerRow row) =>
    [
        Mod(row, "PlayerCap", "Enabled"),
        Mod(row, "PregameTimer", "Enabled"),
        Mod(row, "SuspicionControl", "Enabled")
    ];

    private static ModRow Mod(ServerRow row, string name, string state) =>
        new(name, state, "Has configuration", Path.Combine(row.Win64, "ue4ss", "Mods", name, "Data", "config.json"));

    private static MarketplaceMod Market(string name, string description, string version, string status) =>
        new(name, name, description, version, new Uri("https://example.invalid/" + name + ".zip"),
            new string('0', 64), "Briefcase community catalog", status)
        { IsInstalled = true, CanApply = status == "Update available" };
}
