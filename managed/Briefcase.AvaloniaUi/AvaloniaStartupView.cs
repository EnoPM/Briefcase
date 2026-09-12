using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Briefcase.Rendering;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Small startup-only view. It has no dependency on the configuration menu and
/// is released as soon as its closing animation completes.
/// </summary>
internal sealed class AvaloniaStartupView : StackPanel
{
    private readonly TextBlock _stage;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _close;

    public AvaloniaStartupView(Action close)
    {
        Spacing = 18;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        _stage = new TextBlock
        {
            Text = "Starting Briefcase",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Height = 8,
            Width = 480
        };
        _detail = new TextBlock
        {
            Text = "Preparing the managed runtime...",
            Foreground = new SolidColorBrush(Color.Parse("#AEB6C2")),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560
        };
        _close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(18, 6),
            IsVisible = false
        };
        _close.Click += (_, _) => close();

        Children.Add(AvaloniaIcons.Create(
            Briefcase.ClientModApi.UiIcon.Briefcase,
            54,
            new SolidColorBrush(Color.Parse("#48DBB8"))));
        Children.Add(_stage);
        Children.Add(_progress);
        Children.Add(_detail);
        Children.Add(_close);
    }

    public void Update(FrameworkStartupSnapshot snapshot)
    {
        _stage.Text = snapshot.Stage;
        _detail.Text = snapshot.Detail;
        _progress.Value = snapshot.Progress;
        _close.IsVisible = snapshot.HasFailed;
    }
}
