using System.Diagnostics;
using System.Net.Http;
using Microsoft.Win32;

namespace Briefcase.ServerApp;

public sealed class VisualCppInstaller
{
    public static string? InstalledVersion()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
            return Convert.ToInt32(key?.GetValue("Installed") ?? 0) == 1
                ? key?.GetValue("Version") as string ?? "installed" : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        { return null; }
    }
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Uri Source = new("https://aka.ms/vc14/vc_redist.x64.exe");

    public async Task<string> InstallAsync(string downloadFolder, IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(downloadFolder);
        var file = Path.Combine(downloadFolder, "vc_redist.x64.exe");
        progress.Report(AppTranslations.Get("status.downloading_runtime", "Downloading Visual C++ runtime…"));
        using (var response = await Http.GetAsync(Source, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var host = response.RequestMessage?.RequestUri?.Host ?? "";
            if (host != "aka.ms" && !host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The C++ installer redirected outside Microsoft.");
            if (response.Content.Headers.ContentLength is > 100_000_000)
                throw new InvalidDataException("The C++ installer is unexpectedly large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = File.Create(file);
            var buffer = new byte[65536];
            long count = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                count += read;
                if (count > 100_000_000) throw new InvalidDataException("The C++ installer is unexpectedly large.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (response.Content.Headers.ContentLength is > 0 and var size)
                    progress.Report(AppTranslations.Format("status.downloading_runtime_progress",
                        "Downloading Visual C++ runtime: {0}%", count * 100 / size));
            }
        }
        progress.Report(AppTranslations.Get("status.installing_runtime",
            "Installing Visual C++ runtime (Windows may request administrator approval)…"));
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = true, Verb = "runas", Arguments = "/install /passive /norestart",
        };
        using var installer = Process.Start(start) ?? throw new InvalidOperationException("The C++ installer did not start.");
        await installer.WaitForExitAsync(cancellationToken);
        var result = installer.ExitCode switch
        {
            0 => AppTranslations.Get("status.runtime_ready", "Visual C++ runtime is ready."),
            3010 => AppTranslations.Get("status.runtime_restart_windows", "Restart Windows to finish installing Visual C++ runtime."),
            1638 => AppTranslations.Get("status.runtime_newer", "A newer Visual C++ runtime is already installed."),
            _ => $"Microsoft Visual C++ installer exited with code {installer.ExitCode}."
        };
        File.Delete(file);
        return result;
    }
}
