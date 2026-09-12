using Avalonia.Media;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Shared visual tokens for Briefcase's retained client UI. Keeping colors and
/// dimensions here prevents the menu shell and toolkit-neutral renderer from
/// drifting into separate themes.
/// </summary>
internal static class BriefcaseTheme
{
    public static FontFamily FontFamily { get; } = new(
        "avares://Briefcase.AvaloniaUi/Fonts/Inter-Variable.ttf#Inter");

    public static IBrush Background { get; } = Brush("#0E0B12");
    public static IBrush Navigation { get; } = Brush("#E6151019");
    public static IBrush Surface { get; } = Brush("#E619141F");
    public static IBrush Card { get; } = Brush("#EB241A2B");
    public static IBrush CardHover { get; } = Brush("#F02E2037");
    public static IBrush Border { get; } = Brush("#72493455");
    public static IBrush Accent { get; } = Brush("#A45BE0");
    public static IBrush AccentHover { get; } = Brush("#B96CF0");
    public static IBrush Text { get; } = Brush("#F6F1F8");
    public static IBrush Muted { get; } = Brush("#B4A9B8");
    public static IBrush Disabled { get; } = Brush("#706A77");
    public static IBrush Success { get; } = Brush("#59D69D");
    public static IBrush Warning { get; } = Brush("#F2B765");
    public static IBrush Error { get; } = Brush("#FF647C");

    public const double SidebarWidth = 252;
    public const double CardSpacing = 16;
    public const double MinimumCardWidth = 380;

    private static SolidColorBrush Brush(string value) =>
        new(Color.Parse(value));
}
