namespace Briefcase.ServerApp;

public sealed record BalancePlacement(string[] Path, string? Character, string? IconCategory,
    string? IconVariant, string? Gadget);

public static class BalanceGrouping
{
    public static readonly string[] Characters =
    [
        "Ace", "Cavaliere", "Chavez", "Hans", "Larcin", "Octo", "Sasori",
        "Socialite", "Squire", "Vigil", "Xiu", "Yumi"
    ];

    private static readonly Dictionary<string, string> Gadgets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BouncingMat"] = "Bouncing Mat", ["Drone"] = "Drone", ["DEye"] = "Digital Eye",
        ["AutoTurret"] = "Auto Turret", ["GooPod"] = "Goo Pod", ["HackTrap"] = "Hack Trap",
        ["Jammer"] = "Jammer", ["Scrambler"] = "Scrambler", ["Mimic"] = "Mimic",
        ["Shield"] = "Shield", ["UmbrellaShield"] = "Umbrella Shield",
        ["Soundbox"] = "Soundbox", ["Tripwire"] = "Tripwire"
    };

    public static BalancePlacement Classify(BalanceField entry)
    {
        var prefix = entry.Row.Split('_')[0];
        var character = Characters.FirstOrDefault(name => name.Equals(prefix, StringComparison.OrdinalIgnoreCase));
        if (character is not null)
        {
            var type = entry.Row.Contains("Passive", StringComparison.OrdinalIgnoreCase) ||
                entry.Table.Contains("PassivesBalancing", StringComparison.OrdinalIgnoreCase) ? "Passives" :
                entry.Row.Contains("Active", StringComparison.OrdinalIgnoreCase) ||
                entry.Table.Contains("ActivesBalancing", StringComparison.OrdinalIgnoreCase) ? "Expertises" :
                entry.Row.Contains("Weapon", StringComparison.OrdinalIgnoreCase) ||
                entry.Row.Contains("Projectile", StringComparison.OrdinalIgnoreCase) ||
                entry.Row.Contains("Dart", StringComparison.OrdinalIgnoreCase) ||
                entry.Row.Contains("Melee", StringComparison.OrdinalIgnoreCase) ? "Weapons" : "Other";
            var iconType = type switch { "Weapons" => "Weapon", "Passives" => "Passive",
                "Expertises" => "Expertise", _ => null };
            var (variantName, iconVariant) = Variant(entry.Row, type);
            var displayCharacter = character == "Socialite" ? "Red" : character == "Xiu" ? "Madame Xiu" : character;
            return new BalancePlacement(["Characters", displayCharacter, type, variantName],
                character, iconType, iconVariant, null);
        }

        if (entry.Table.Equals("DT_Gadgets_Balancing", StringComparison.OrdinalIgnoreCase) ||
            Gadgets.ContainsKey(prefix))
        {
            var name = Gadgets.GetValueOrDefault(prefix) ?? BalanceProfileEditor.Humanize(entry.Row);
            return new BalancePlacement(["Gadgets", name], null, null, null, prefix);
        }

        if (entry.Table.StartsWith("DT_SpyShared_", StringComparison.OrdinalIgnoreCase))
            return new BalancePlacement(["Game", "Shared systems", BalanceProfileEditor.Humanize(entry.Row)],
                null, null, null, null);

        if (prefix is "Guard" or "SuperGuard" or "UltraGuard" or "Staff" or "Civilian" or
            "Technician" or "VIP" || prefix.EndsWith("Guard", StringComparison.OrdinalIgnoreCase))
            return new BalancePlacement(["Game", "NPCs", BalanceProfileEditor.Humanize(entry.Row)],
                null, null, null, null);

        return new BalancePlacement(["Game", "Other", BalanceProfileEditor.Humanize(entry.Row)],
            null, null, null, null);
    }

    private static (string Name, string? IconVariant) Variant(string row, string type)
    {
        var singular = type switch { "Weapons" => "Weapon", "Passives" => "Passive",
            "Expertises" => "Expertise", _ => "Variant" };
        if (row.Contains("_Base", StringComparison.OrdinalIgnoreCase)) return (singular + " 1", "Base");
        if (row.Contains("_Mod1", StringComparison.OrdinalIgnoreCase)) return (singular + " 2", "Mod1");
        if (row.Contains("_Mod2", StringComparison.OrdinalIgnoreCase)) return (singular + " 3", "Mod2");
        return (BalanceProfileEditor.Humanize(row), null);
    }
}
