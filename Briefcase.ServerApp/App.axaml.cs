using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Net.Sockets;

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
                window.Opened += async (_, _) =>
                {
                    if (OperatingSystem.IsWindows())
                    {
                        try
                        {
                            using var ready = EventWaitHandle.OpenExisting(signal);
                            ready.Set();
                        }
                        catch (WaitHandleCannotBeOpenedException) { }
                    }
                    else if (OperatingSystem.IsLinux() && signal.StartsWith("/tmp/briefcase-ready-", StringComparison.Ordinal))
                    {
                        try
                        {
                            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                            await socket.ConnectAsync(new UnixDomainSocketEndPoint(signal));
                            await socket.SendAsync(new byte[] { 1 });
                        }
                        catch (Exception ex) when (ex is SocketException or IOException) { }
                    }
                };
            if (Program.LauncherUpdateVersion is { } updateVersion && Program.LauncherProcessId > 0)
                window.Opened += async (_, _) =>
                {
                    try
                    {
                        await Task.Run(() => AppLauncherUpdate.ApplyAsync(updateVersion,
                            Program.LauncherProcessId, CancellationToken.None));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                                  InvalidDataException or System.ComponentModel.Win32Exception or
                                                  OperationCanceledException or System.Text.Json.JsonException)
                    {
                        if (window.DataContext is MainViewModel model)
                            model.Report(AppTranslations.Format("status.launcher_update_failed",
                                "Unable to update the launcher: {0}", error.Message));
                    }
                };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
