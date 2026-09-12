namespace Briefcase.Updater;

internal static class ReleaseVersion
{
    public static bool TryParse(string? value, out Version version, out string normalized)
    {
        version = new Version(0, 0, 0);
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return false;

        var candidate = value.Trim();
        if (candidate.StartsWith('v')) candidate = candidate[1..];
        var components = candidate.Split('.');
        if (components.Length != 3) return false;

        var parsed = new int[3];
        for (var index = 0; index < components.Length; index++)
        {
            var component = components[index];
            if (component.Length == 0 ||
                component.Any(character => character is < '0' or > '9') ||
                !int.TryParse(component, out parsed[index]))
                return false;
        }

        version = new Version(parsed[0], parsed[1], parsed[2]);
        normalized = $"{parsed[0]}.{parsed[1]}.{parsed[2]}";
        return true;
    }
}
