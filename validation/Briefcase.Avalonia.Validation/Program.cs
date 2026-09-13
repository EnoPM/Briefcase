using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Briefcase.AvaloniaUi;
using Briefcase.ClientModApi;
using Briefcase.ModApi;
using Briefcase.Rendering;

if (args.Contains("--construction-only", StringComparer.Ordinal))
    return ValidateControlConstruction();
return Run();

static int ValidateControlConstruction()
{
    try
    {
        var row = BriefcaseControls.SettingRow(
            "Example setting",
            "Construction must not depend on unsupported GridLength syntax.",
            new CheckBox { IsChecked = true });
        if (row.Child is not Grid layout || layout.ColumnDefinitions.Count != 2)
            return 10;
        Console.WriteLine("Avalonia reusable-control construction passed.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Avalonia reusable-control construction failed: {exception}");
        return 11;
    }
}

[SupportedOSPlatform("windows")]
static int Run()
{
    var messages = new List<string>();
    var startup = new FrameworkStartupProgress();
    var created = 0;
    var disposed = 0;
    using var host = new AvaloniaOverlayHost(
        GetDesktopWindow(),
        () => BuildContent(ref created, () => Interlocked.Increment(ref disposed)),
        visible => messages.Add($"Visible={visible}"),
        null,
        null,
        message => messages.Add($"Info: {message}"),
        message => messages.Add($"Error: {message}"),
        startup,
        recreateMenuOnClose: true);

    host.Start();
    SpinWait.SpinUntil(() => host.IsReady || host.HasFailed, TimeSpan.FromSeconds(5));
    if (!host.IsReady) return Report(messages, 2);

    SpinWait.SpinUntil(() => host.IsStartupVisible, TimeSpan.FromSeconds(3));
    if (!host.IsStartupVisible || host.IsMenuCreated) return Report(messages, 3);

    startup.Report(0.6, "Loading validation", "Checking vector geometry...");
    Thread.Sleep(100);
    startup.Complete();
    SpinWait.SpinUntil(() => !host.IsStartupVisible, TimeSpan.FromSeconds(3));
    Thread.Sleep(250);
    if (host.IsMenuCreated || created != 0) return Report(messages, 4);

    // Passive mod drawings are intentionally absent from this Avalonia
    // lifecycle test. They are rendered inside the game's D3D11 swap chain.

    for (var opening = 1; opening <= 2; opening++)
    {
        host.SetVisible(true);
        SpinWait.SpinUntil(
            () => host.IsVisible && host.IsMenuCreated,
            TimeSpan.FromSeconds(3));
        if (!host.IsVisible || created != opening)
            return Report(messages, 5);
        Thread.Sleep(250);
        host.SetVisible(false);
        SpinWait.SpinUntil(
            () => !host.IsVisible && !host.IsMenuCreated,
            TimeSpan.FromSeconds(3));
        Thread.Sleep(200);
        if (disposed != opening)
            return Report(messages, 6);
    }

    foreach (var message in messages) Console.WriteLine(message);
    Console.WriteLine("Avalonia startup/menu lifecycle smoke test passed.");
    return 0;
}

static int Report(IEnumerable<string> messages, int code)
{
    foreach (var message in messages) Console.Error.WriteLine(message);
    return code;
}

static Control BuildContent(ref int created, Action disposed)
{
    Interlocked.Increment(ref created);
    var icons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
    foreach (var icon in Enum.GetValues<UiIcon>())
        icons.Children.Add(AvaloniaIcons.Create(icon, 14));
    return new ValidationContent(disposed)
    {
        Margin = new Thickness(24),
        Spacing = 12,
        Children =
        {
            icons,
            new TextBlock { Text = "Retained-mode Briefcase controls", FontSize = 22 },
            BriefcaseControls.SettingRow(
                "Example Boolean setting",
                "Built on Avalonia's UI thread during the lifecycle smoke test.",
                new CheckBox { IsChecked = true }),
            BuildTabs(),
            new NumericUpDown { Value = 12, Minimum = 1, Maximum = 24, Increment = 1 },
            new ComboBox
            {
                SelectedIndex = 1,
                ItemsSource = new[] { "Automatic", "Hitscan", "Projectile" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            }
        }
    };
}

static Control BuildTabs()
{
    static TabItem Item(string title, Control content)
    {
        var item = new TabItem { Header = title, Content = content };
        item.Classes.Add("briefcase-tab");
        return item;
    }

    var tabs = new TabControl
    {
        Height = 150,
        ItemsSource = new[]
        {
            Item("Home", new TextBlock { Text = "Home content" }),
            Item("Servers", BriefcaseControls.Card(
                "Saved server",
                "Flat list item validation",
                Array.Empty<Control>(),
                showAccent: false)),
            Item("Mods", new TextBlock { Text = "Mods content" })
        }
    };
    tabs.Classes.Add("briefcase-tabs");
    return tabs;
}

[DllImport("user32.dll")]
static extern nint GetDesktopWindow();

sealed class ValidationContent(Action disposed) : StackPanel, IDisposable
{
    public void Dispose() => disposed();
}
