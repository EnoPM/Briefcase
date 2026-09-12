using Briefcase.ManagedHost;

namespace Briefcase.Core.Tests;

public sealed class FrameworkUiSettingsTests
{
    [Theory]
    [InlineData("cached", 0)]
    [InlineData("PER-OPEN", 1)]
    [InlineData("peropen", 1)]
    public void Reads_avalonia_menu_lifetime(string configured, int expected)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, $$"""{ "avaloniaMenuLifetime": "{{configured}}" }""");
        var warnings = new List<string>();

        var actual = FrameworkUiSettings.ReadAvaloniaMenuLifetime(path, warnings.Add);

        Assert.Equal((AvaloniaMenuLifetime)expected, actual);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Invalid_avalonia_menu_lifetime_uses_cached()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, """{ "avaloniaMenuLifetime": "session" }""");
        var warnings = new List<string>();

        Assert.Equal(
            AvaloniaMenuLifetime.Cached,
            FrameworkUiSettings.ReadAvaloniaMenuLifetime(path, warnings.Add));
        Assert.Single(warnings);
    }

    [Fact]
    public void Reads_relative_avalonia_menu_margins()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, """
            { "avaloniaMenuMargins": { "horizontalPercent": 15, "verticalPercent": 10 } }
            """);
        var warnings = new List<string>();

        var margins = FrameworkUiSettings.ReadAvaloniaMenuMargins(path, warnings.Add);

        Assert.Equal(15, margins.HorizontalPercent);
        Assert.Equal(10, margins.VerticalPercent);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"avaloniaMenuMargins\": { \"horizontalPercent\": 46, \"verticalPercent\": 8 } }")]
    public void Missing_or_invalid_menu_margins_use_defaults(string json)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, json);
        var warnings = new List<string>();

        var margins = FrameworkUiSettings.ReadAvaloniaMenuMargins(path, warnings.Add);

        Assert.Equal(AvaloniaMenuMargins.Default, margins);
        Assert.Equal(json == "{}" ? 0 : 1, warnings.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reads_game_window_chrome_boolean(bool configured)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, $$"""{ "gameWindowChrome": {{configured.ToString().ToLowerInvariant()}} }""");
        var warnings = new List<string>();

        Assert.Equal(
            configured,
            FrameworkUiSettings.ReadGameWindowChrome(path, warnings.Add));
        Assert.Empty(warnings);
    }

    [Fact]
    public void Invalid_game_window_chrome_keeps_borderless_mode()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "loader.json");
        File.WriteAllText(path, """{ "gameWindowChrome": "yes" }""");
        var warnings = new List<string>();

        Assert.False(FrameworkUiSettings.ReadGameWindowChrome(path, warnings.Add));
        Assert.Single(warnings);
    }
}
