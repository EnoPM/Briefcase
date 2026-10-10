using System.Text.Json;
using Avalonia;
using Briefcase.ServerApp;

namespace Briefcase.ServerApp.Launcher;

internal static class Program
{
    internal static int RestartWaitPid { get; private set; }
    internal static string? RestartAppDirectory { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        if (LauncherElevation.RunMaintenance(args) is { } result) return result;
        var waitPid = args.FirstOrDefault(arg => arg.StartsWith("--restart-wait-pid=", StringComparison.Ordinal))?
            ["--restart-wait-pid=".Length..];
        RestartWaitPid = int.TryParse(waitPid, out var pid) && pid > 0 ? pid : 0;
        var appDirectory = args.FirstOrDefault(arg => arg.StartsWith("--restart-app-directory=", StringComparison.Ordinal))?
            ["--restart-app-directory=".Length..];
        RestartAppDirectory = appDirectory is not null && Path.IsPathFullyQualified(appDirectory)
            ? Path.GetFullPath(appDirectory) : null;
        AppTranslations.Select(ReadLanguage());
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static string ReadLanguage()
    {
        try
        {
            var file = Path.Combine(AppDataStore.Root, "settings.json");
            if (!File.Exists(file) || new FileInfo(file).Length > 1024 * 1024) return "en";
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.TryGetProperty("language", out var value) && value.ValueKind == JsonValueKind.String
                ? AppTranslations.NormalizeLanguage(value.GetString()) : "en";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or InvalidDataException)
        { return "en"; }
    }
}
