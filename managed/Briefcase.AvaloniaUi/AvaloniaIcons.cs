using Avalonia.Controls;
using Avalonia.Media;
using Briefcase.ClientModApi;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Built-in 24x24 vector paths. Keeping geometry in Core makes icons sharp at
/// every game resolution and avoids image files or font glyph dependencies.
/// </summary>
internal static class AvaloniaIcons
{
    private static readonly IBrush DefaultForeground =
        new SolidColorBrush(Color.Parse("#DDE5EE"));

    public static PathIcon Create(
        UiIcon icon,
        double size = 18,
        IBrush? foreground = null) => new()
    {
        Data = StreamGeometry.Parse(Data(icon)),
        Width = size,
        Height = size,
        Foreground = foreground ?? DefaultForeground,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
    };

    private static string Data(UiIcon icon) => icon switch
    {
        UiIcon.Briefcase => "M3 7 L8 7 L8 5 C8 3.9 8.9 3 10 3 L14 3 C15.1 3 16 3.9 16 5 L16 7 L21 7 C22.1 7 23 7.9 23 9 L23 19 C23 20.1 22.1 21 21 21 L3 21 C1.9 21 1 20.1 1 19 L1 9 C1 7.9 1.9 7 3 7 M10 5 L10 7 L14 7 L14 5 Z M1 12 L10 12 L10 14 L14 14 L14 12 L23 12 L23 14 L15 14 L15 16 L9 16 L9 14 L1 14 Z",
        UiIcon.Mods => "M3 3 L10 3 L10 10 L3 10 Z M14 3 L21 3 L21 10 L14 10 Z M3 14 L10 14 L10 21 L3 21 Z M14 14 L21 14 L21 21 L14 21 Z",
        UiIcon.Client => "M3 4 L21 4 L21 17 L13 17 L13 20 L17 20 L17 22 L7 22 L7 20 L11 20 L11 17 L3 17 Z M5 6 L5 15 L19 15 L19 6 Z",
        UiIcon.Server => "M4 3 L20 3 L20 9 L4 9 Z M6 5 L8 5 L8 7 L6 7 Z M4 10 L20 10 L20 16 L4 16 Z M6 12 L8 12 L8 14 L6 14 Z M4 17 L20 17 L20 23 L4 23 Z M6 19 L8 19 L8 21 L6 21 Z",
        UiIcon.Settings => "M10 2 L14 2 L14.7 4.4 L16.4 5.1 L18.6 4 L21 6.4 L19.9 8.6 L20.6 10.3 L23 11 L23 14 L20.6 14.7 L19.9 16.4 L21 18.6 L18.6 21 L16.4 19.9 L14.7 20.6 L14 23 L10 23 L9.3 20.6 L7.6 19.9 L5.4 21 L3 18.6 L4.1 16.4 L3.4 14.7 L1 14 L1 11 L3.4 10.3 L4.1 8.6 L3 6.4 L5.4 4 L7.6 5.1 L9.3 4.4 Z M12 8 C9.8 8 8 9.8 8 12 C8 14.2 9.8 16 12 16 C14.2 16 16 14.2 16 12 C16 9.8 14.2 8 12 8 Z",
        UiIcon.Refresh or UiIcon.Reload => "M17.7 6.3 C16.2 4.8 14.2 4 12 4 C7.6 4 4 7.6 4 12 C4 16.4 7.6 20 12 20 C15.7 20 18.8 17.5 19.7 14 L17.6 14 C16.8 16.3 14.6 18 12 18 C8.7 18 6 15.3 6 12 C6 8.7 8.7 6 12 6 C13.7 6 15.2 6.7 16.3 7.7 L13 11 L21 11 L21 3 Z",
        UiIcon.Folder => "M2 5 L10 5 L12 7 L22 7 L22 20 L2 20 Z M4 9 L4 18 L20 18 L20 9 Z",
        UiIcon.Play => "M7 4 L20 12 L7 20 Z",
        UiIcon.Pause => "M6 4 L10 4 L10 20 L6 20 Z M14 4 L18 4 L18 20 L14 20 Z",
        UiIcon.Power => "M11 2 L13 2 L13 12 L11 12 Z M7.1 4.6 C4.6 6.2 3 8.9 3 12 C3 17 7 21 12 21 C17 21 21 17 21 12 C21 8.9 19.4 6.2 16.9 4.6 L15.7 6.2 C17.7 7.4 19 9.6 19 12 C19 15.9 15.9 19 12 19 C8.1 19 5 15.9 5 12 C5 9.6 6.3 7.4 8.3 6.2 Z",
        UiIcon.Check => "M3 12 L8 17 L21 4 L23 6 L8 21 L1 14 Z",
        UiIcon.Warning => "M12 2 L23 22 L1 22 Z M11 8 L13 8 L13 15 L11 15 Z M11 17 L13 17 L13 19 L11 19 Z",
        UiIcon.Information => "M12 2 C6.5 2 2 6.5 2 12 C2 17.5 6.5 22 12 22 C17.5 22 22 17.5 22 12 C22 6.5 17.5 2 12 2 M11 7 L13 7 L13 9 L11 9 Z M11 11 L13 11 L13 18 L11 18 Z",
        UiIcon.Search => "M10 3 C6.1 3 3 6.1 3 10 C3 13.9 6.1 17 10 17 C11.6 17 13.1 16.5 14.3 15.6 L20.7 22 L22 20.7 L15.6 14.3 C16.5 13.1 17 11.6 17 10 C17 6.1 13.9 3 10 3 M10 5 C12.8 5 15 7.2 15 10 C15 12.8 12.8 15 10 15 C7.2 15 5 12.8 5 10 C5 7.2 7.2 5 10 5 Z",
        UiIcon.Save => "M3 3 L18 3 L22 7 L22 21 L3 21 Z M6 5 L6 10 L17 10 L17 5 Z M7 14 L18 14 L18 19 L7 19 Z",
        UiIcon.Delete => "M5 7 L19 7 L18 22 L6 22 Z M8 9 L10 9 L10 19 L8 19 Z M14 9 L16 9 L16 19 L14 19 Z M8 3 L16 3 L17 5 L21 5 L21 7 L3 7 L3 5 L7 5 Z",
        UiIcon.Download => "M11 3 L13 3 L13 13 L17 9 L19 11 L12 18 L5 11 L7 9 L11 13 Z M3 19 L21 19 L21 22 L3 22 Z",
        UiIcon.Upload => "M12 3 L19 10 L17 12 L13 8 L13 18 L11 18 L11 8 L7 12 L5 10 Z M3 19 L21 19 L21 22 L3 22 Z",
        UiIcon.User => "M12 3 C9.2 3 7 5.2 7 8 C7 10.8 9.2 13 12 13 C14.8 13 17 10.8 17 8 C17 5.2 14.8 3 12 3 M4 22 C4 17.6 7.6 14 12 14 C16.4 14 20 17.6 20 22 Z",
        UiIcon.Users => "M9 4 C6.8 4 5 5.8 5 8 C5 10.2 6.8 12 9 12 C11.2 12 13 10.2 13 8 C13 5.8 11.2 4 9 4 M2 21 C2 16.6 5.1 13 9 13 C12.9 13 16 16.6 16 21 Z M16 5 C18.2 5 20 6.8 20 9 C20 11.2 18.2 13 16 13 C15.3 13 14.7 12.8 14.1 12.5 C15.3 11.4 16 9.8 16 8 C16 6.9 15.7 5.9 15.1 5.1 C15.4 5 15.7 5 16 5 M16.5 14 C19.7 14.3 22 17.3 22 21 L18 21 C18 18.2 17 15.8 15.4 14.2 C15.8 14.1 16.1 14 16.5 14 Z",
        UiIcon.Plug => "M8 2 L10 2 L10 7 L14 7 L14 2 L16 2 L16 7 L19 7 L19 10 C19 13.2 16.8 15.9 14 16.7 L14 22 L10 22 L10 16.7 C7.2 15.9 5 13.2 5 10 L5 7 L8 7 Z",
        UiIcon.Disconnect => "M4 3 L21 20 L19.6 21.4 L15.8 17.6 C15.2 18 14.6 18.3 14 18.5 L14 23 L10 23 L10 18.5 C7.2 17.7 5 15.1 5 12 L5 9.8 L2.6 7.4 Z M8 3 L10 3 L10 7 L11.2 7 L16.9 12.7 C17 12.3 17 11.9 17 11 L17 8 L19 8 L19 11 C19 12.2 18.7 13.4 18.2 14.4 L14.8 11 L5 11 L5 8 L8 8 Z M14 3 L16 3 L16 7 L14 7 Z",
        UiIcon.Close => "M4 5 L5 4 L12 11 L19 4 L20 5 L13 12 L20 19 L19 20 L12 13 L5 20 L4 19 L11 12 Z",
        UiIcon.ChevronRight => "M8 4 L16 12 L8 20 L10 22 L20 12 L10 2 Z",
        UiIcon.ChevronLeft => "M16 4 L8 12 L16 20 L14 22 L4 12 L14 2 Z",
        _ => "M4 4 L20 4 L20 20 L4 20 Z"
    };

    public static string TextFallback(UiIcon icon) => icon switch
    {
        UiIcon.Refresh or UiIcon.Reload => "R",
        UiIcon.Folder => "DIR",
        UiIcon.Play => ">",
        UiIcon.Pause => "II",
        UiIcon.Check => "OK",
        UiIcon.Warning => "!",
        UiIcon.Information => "i",
        UiIcon.Delete => "X",
        UiIcon.Download => "v",
        UiIcon.Upload => "^",
        UiIcon.Close => "X",
        UiIcon.ChevronLeft => "<",
        UiIcon.ChevronRight => ">",
        UiIcon.Server => "SRV",
        UiIcon.Client => "PC",
        UiIcon.Mods => "MOD",
        _ => "*"
    };
}
