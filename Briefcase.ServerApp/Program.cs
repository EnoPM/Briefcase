using Avalonia;

namespace Briefcase.ServerApp;

internal static class Program
{
    internal static bool DemoMode { get; private set; }
    internal static string? LauncherReadySignal { get; private set; }
    internal static string? LauncherUpdateVersion { get; private set; }
    internal static int LauncherProcessId { get; private set; }

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (await AppLauncherUpdate.RunMaintenanceAsync(args) is { } launcherResult) return launcherResult;
        if (await ElevatedUpdateService.RunMaintenanceAsync(args) is { } result) return result;
        DemoMode = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        LauncherReadySignal = args.FirstOrDefault(arg => arg.StartsWith("--launcher-ready=", StringComparison.Ordinal))?
            ["--launcher-ready=".Length..];
        LauncherUpdateVersion = args.FirstOrDefault(arg => arg.StartsWith("--launcher-update=", StringComparison.Ordinal))?
            ["--launcher-update=".Length..];
        var pidText = args.FirstOrDefault(arg => arg.StartsWith("--launcher-pid=", StringComparison.Ordinal))?
            ["--launcher-pid=".Length..];
        LauncherProcessId = int.TryParse(pidText, out var pid) ? pid : 0;
        AppTranslations.Select(ServerRegistry.ReadSavedLanguage());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        return 0;
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
