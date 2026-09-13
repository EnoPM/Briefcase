using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Small retained-mode building blocks used by the menu and by the renderer of
/// toolkit-neutral mod components. Mods consume these indirectly through
/// Briefcase.ClientModApi, so their assemblies never depend on Avalonia.
/// </summary>
internal static class BriefcaseControls
{
    public static Border Card(
        string? title,
        string? description,
        IEnumerable<Control> children,
        bool showAccent = true)
    {
        var content = new StackPanel { Spacing = 10 };
        if (!string.IsNullOrWhiteSpace(title))
        {
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Foreground = BriefcaseTheme.Text
            });
        }
        if (!string.IsNullOrWhiteSpace(description))
        {
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12.5,
                Foreground = BriefcaseTheme.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -5, 0, 2)
            });
        }
        foreach (var child in children) content.Children.Add(child);

        var contentHost = new Border
        {
            Padding = showAccent
                ? new Thickness(16, 14)
                : new Thickness(4, 12),
            Child = content
        };
        var card = new Border();
        card.Classes.Add("briefcase-card");
        if (!showAccent)
        {
            card.Classes.Add("briefcase-flat-card");
            card.Child = contentHost;
            return card;
        }

        var accent = new Border
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        accent.Classes.Add("briefcase-card-accent");
        Grid.SetColumn(contentHost, 1);
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("4,*"),
            Children = { accent, contentHost }
        };
        card.Child = layout;
        return card;
    }

    public static Border SettingRow(
        string title,
        string? description,
        Control editor)
    {
        var information = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        information.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            Foreground = BriefcaseTheme.Text,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(description))
        {
            information.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = BriefcaseTheme.Muted,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var editorHost = new Border
        {
            MinWidth = 210,
            MaxWidth = 330,
            Margin = new Thickness(20, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = editor
        };
        Grid.SetColumn(editorHost, 1);
        var row = new Grid
        {
            // Avalonia's GridLength parser supports Auto, pixels, and star
            // sizing. CSS-style minmax() is not valid here; the editor host
            // already carries the desired minimum and maximum widths.
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 46,
            Children = { information, editorHost }
        };
        var container = new Border { Child = row };
        container.Classes.Add("briefcase-setting-row");
        return container;
    }

    public static StackPanel PageHeader(string title, string subtitle)
    {
        var header = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 14)
        };
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = BriefcaseTheme.Text
        });
        header.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 13,
            Foreground = BriefcaseTheme.Muted,
            TextWrapping = TextWrapping.Wrap
        });
        return header;
    }
}
