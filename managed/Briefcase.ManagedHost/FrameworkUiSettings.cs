using System.Text.Json;

namespace Briefcase.ManagedHost;

internal enum AvaloniaMenuLifetime
{
    Cached,
    PerOpen
}

internal readonly record struct AvaloniaMenuMargins(
    double HorizontalPercent,
    double VerticalPercent)
{
    public static AvaloniaMenuMargins Default { get; } = new(12.5, 8);
}

internal static class FrameworkUiSettings
{
    public static bool ReadGameWindowChrome(string path, Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        try
        {
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("gameWindowChrome", out var value))
                return false;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
            warning("gameWindowChrome must be true or false; keeping the game borderless.");
            return false;
        }
        catch (Exception exception)
        {
            warning($"Could not read gameWindowChrome from loader.json; keeping the game borderless: {exception.Message}");
            return false;
        }
    }

    public static AvaloniaMenuLifetime ReadAvaloniaMenuLifetime(
        string path,
        Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        try
        {
            if (!File.Exists(path)) return AvaloniaMenuLifetime.Cached;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("avaloniaMenuLifetime", out var value) ||
                value.ValueKind != JsonValueKind.String)
                return AvaloniaMenuLifetime.Cached;
            return value.GetString()?.Trim().ToLowerInvariant() switch
            {
                null or "" or "cached" => AvaloniaMenuLifetime.Cached,
                "per-open" or "peropen" => AvaloniaMenuLifetime.PerOpen,
                var unknown => WarnAndUseCachedMenu(unknown, warning)
            };
        }
        catch (Exception exception)
        {
            warning($"Could not read avaloniaMenuLifetime from loader.json; using cached: {exception.Message}");
            return AvaloniaMenuLifetime.Cached;
        }
    }

    public static AvaloniaMenuMargins ReadAvaloniaMenuMargins(
        string path,
        Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        try
        {
            if (!File.Exists(path)) return AvaloniaMenuMargins.Default;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("avaloniaMenuMargins", out var value))
                return AvaloniaMenuMargins.Default;
            if (value.ValueKind != JsonValueKind.Object ||
                !TryPercentage(value, "horizontalPercent", out var horizontal) ||
                !TryPercentage(value, "verticalPercent", out var vertical))
            {
                warning("avaloniaMenuMargins requires horizontalPercent and verticalPercent between 0 and 45; using defaults.");
                return AvaloniaMenuMargins.Default;
            }
            return new AvaloniaMenuMargins(horizontal, vertical);
        }
        catch (Exception exception)
        {
            warning($"Could not read avaloniaMenuMargins from loader.json; using defaults: {exception.Message}");
            return AvaloniaMenuMargins.Default;
        }
    }

    private static bool TryPercentage(JsonElement parent, string name, out double result)
    {
        result = 0;
        return parent.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetDouble(out result) &&
               double.IsFinite(result) && result is >= 0 and <= 45;
    }

    private static AvaloniaMenuLifetime WarnAndUseCachedMenu(
        string unknown,
        Action<string> warning)
    {
        warning($"Unknown avaloniaMenuLifetime value '{unknown}'; using cached.");
        return AvaloniaMenuLifetime.Cached;
    }
}
