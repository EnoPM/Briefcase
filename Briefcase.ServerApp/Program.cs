using Avalonia;

namespace Briefcase.ServerApp;

internal static class Program
{
    internal static bool DemoMode { get; private set; }
    internal static string? LauncherReadySignal { get; private set; }

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (await ElevatedUpdateService.RunMaintenanceAsync(args) is { } result) return result;
        DemoMode = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        LauncherReadySignal = args.FirstOrDefault(arg => arg.StartsWith("--launcher-ready=", StringComparison.Ordinal))?
            ["--launcher-ready=".Length..];
        AppTranslations.Select(ServerRegistry.ReadSavedLanguage());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        return 0;
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
