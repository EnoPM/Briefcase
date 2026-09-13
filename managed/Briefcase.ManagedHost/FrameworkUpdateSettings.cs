using System.Text.Json;

namespace Briefcase.ManagedHost;

internal readonly record struct FrameworkUpdateSettings(
    bool Enabled,
    string RestartMode,
    bool AutomaticModUpdates)
{
    public static FrameworkUpdateSettings Default { get; } = new(true, "auto", true);
}

internal static class FrameworkUpdateSettingsReader
{
    public static FrameworkUpdateSettings Read(string path, Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        try
        {
            if (!File.Exists(path)) return FrameworkUpdateSettings.Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var enabled = true;
            if (root.TryGetProperty("automaticUpdates", out var enabledValue))
            {
                if (enabledValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    enabled = enabledValue.GetBoolean();
                else
                    warning("automaticUpdates must be true or false; automatic updates remain enabled.");
            }

            var automaticModUpdates = true;
            if (root.TryGetProperty("automaticModUpdates", out var modUpdatesValue))
            {
                if (modUpdatesValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    automaticModUpdates = modUpdatesValue.GetBoolean();
                else
                    warning("automaticModUpdates must be true or false; mod updates remain enabled.");
            }
            var restartMode = "auto";
            if (root.TryGetProperty("updateRestartMode", out var restartValue))
            {
                if (restartValue.ValueKind == JsonValueKind.String)
                {
                    var candidate = restartValue.GetString()?.Trim().ToLowerInvariant();
                    if (candidate is "auto" or "steam" or "executable")
                        restartMode = candidate;
                    else
                        warning("updateRestartMode must be auto, steam, or executable; using auto.");
                }
                else
                {
                    warning("updateRestartMode must be a string; using auto.");
                }
            }
            return new FrameworkUpdateSettings(enabled, restartMode, automaticModUpdates);
        }
        catch (Exception exception)
        {
            warning(
                $"Could not read automatic update settings from loader.json; using defaults: {exception.Message}");
            return FrameworkUpdateSettings.Default;
        }
    }
}
