using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Briefcase.ServerApp;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(Program.DemoMode);
            if (Program.LauncherReadySignal is { } signal)
                window.Opened += (_, _) =>
                {
                    if (!OperatingSystem.IsWindows()) return;
                    try
                    {
                        using var ready = EventWaitHandle.OpenExisting(signal);
                        ready.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException) { }
                };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
