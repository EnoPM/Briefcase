namespace Briefcase.ServerApp;

/// <summary>Turns server map identifiers and live level names into localized display names.</summary>
public static class GameMapNames
{
    public static string Display(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        var name = value.Trim().Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = name[(name.LastIndexOf('.') + 1)..];
        var id = ServerIniEditor.Maps.FirstOrDefault(map =>
            map.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            map.Label.Equals(name, StringComparison.OrdinalIgnoreCase)).Id;
        if (id is null && !AppTranslations.TryResolveMapAlias(name, out id)) return value;
        return AppTranslations.Get("game.maps." + id, ServerIniEditor.Maps.First(map => map.Id == id).Label);
    }
}
