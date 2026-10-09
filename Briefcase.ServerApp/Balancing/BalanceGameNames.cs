namespace Briefcase.ServerApp;

// The English names are compiled into the application; users can edit the matching
// game.characters.* entries in Translations/en.json and Translations/fr.json.
public static class BalanceGameNames
{
    public static string Character(string character) =>
        AppTranslations.Get($"game.characters.{character}.name", character);

    public static string? Variant(string character, string category, string variant)
    {
        if (variant is not ("Base" or "Mod1" or "Mod2") ||
            category is not ("Weapons" or "Passives" or "Expertises")) return null;
        var key = $"game.characters.{character}.{category}.{variant}";
        var value = AppTranslations.Get(key);
        return value == key ? null : value;
    }
}
