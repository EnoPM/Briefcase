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
        IEnumerable<Control> children)
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

        var accent = new Border
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        accent.Classes.Add("briefcase-card-accent");
        var contentHost = new Border
        {
            Padding = new Thickness(16, 14),
            Child = content
        };
        Grid.SetColumn(contentHost, 1);
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("4,*"),
            Children = { accent, contentHost }
        };
        var card = new Border { Child = layout };
        card.Classes.Add("briefcase-card");
        return card;
    }

    public static Grid SettingRow(
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
            Margin = new Thickness(24, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Child = editor
        };
        Grid.SetColumn(editorHost, 1);
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,minmax(210,330)"),
            MinHeight = 46,
            Children = { information, editorHost }
        };
        row.Classes.Add("briefcase-setting-row");
        return row;
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
