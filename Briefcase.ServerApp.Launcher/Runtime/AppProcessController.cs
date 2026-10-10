using System.ComponentModel;
using System.Diagnostics;

namespace Briefcase.ServerApp.Launcher;

/// <summary>Starts one application instance and waits for its main window to open.</summary>
public sealed class AppProcessController : IDisposable
{
    private Process? _owned;

    public async Task<bool> LaunchAndWaitAsync(InstalledApp app, CancellationToken cancellationToken,
        string? launcherUpdateVersion = null)
    {
        var useDotnet = app.Version == "0.0.0" && !File.Exists(app.Executable) &&
                        File.Exists(Path.Combine(app.Directory, "Briefcase.ServerApp.dll"));
        if (!File.Exists(app.Executable) && !useDotnet)
            throw new FileNotFoundException("Briefcase.ServerApp.exe is missing.", app.Executable);

        var signalName = "Local\\Briefcase.ServerApp.Ready." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signalName);
        _owned?.Dispose();
        var start = new ProcessStartInfo(useDotnet ? "dotnet" : app.Executable)
        {
            WorkingDirectory = app.Directory,
            UseShellExecute = false
        };
        if (useDotnet) start.ArgumentList.Add(Path.Combine(app.Directory, "Briefcase.ServerApp.dll"));
        start.ArgumentList.Add("--launcher-ready=" + signalName);
        if (launcherUpdateVersion is not null)
        {
            start.ArgumentList.Add("--launcher-update=" + launcherUpdateVersion);
            start.ArgumentList.Add("--launcher-pid=" + Environment.ProcessId);
        }
        _owned = Process.Start(start) ?? throw new Win32Exception("Briefcase ServerApp could not be started.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (ready.WaitOne(0)) return true;
                if (_owned.HasExited) return false;
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_owned is { HasExited: false })
            {
                _owned.Kill();
                await _owned.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        catch (OperationCanceledException) { }

        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        if (_owned is { HasExited: false })
        {
            try { _owned.CloseMainWindow(); }
            catch (InvalidOperationException) { }
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _owned.WaitForExitAsync(grace.Token); }
            catch (OperationCanceledException)
            {
                if (!_owned.HasExited)
                {
                    _owned.Kill();
                    await _owned.WaitForExitAsync(CancellationToken.None);
                }
            }
        }
        return false;
    }

    public void Dispose() => _owned?.Dispose();
}
