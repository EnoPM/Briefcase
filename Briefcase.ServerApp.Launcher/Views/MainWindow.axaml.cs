using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Briefcase.ServerApp;

namespace Briefcase.ServerApp.Launcher;

public sealed partial class MainWindow : Window
{
    private enum SplashState { Loading, Failed, Ready }

    private readonly AppProcessController _process = new();
    private readonly LauncherUpdateService _updates = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _started;
    private bool _closing;
    private Task? _runTask;

    public MainWindow()
    {
        InitializeComponent();
        SetVisualState(SplashState.Loading);
        StageText.Text = AppTranslations.Get("launcher.checking");
        Opened += (_, _) =>
        {
            if (_started) return;
            _started = true;
            _runTask = RunAsync();
        };
        Closed += (_, _) => { _shutdown.Cancel(); _process.Dispose(); };
    }

    private void BackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private async void CloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        CloseButton.IsEnabled = false;
        _shutdown.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask; }
            catch (OperationCanceledException) { }
        }
        CompleteProgress();
        Close();
    }

    private async Task RunAsync()
    {
        var root = AppContext.BaseDirectory;
        InstalledApp? installed;
        try { installed = AppUpdateStore.Current(root); }
        catch (Exception ex) { ShowError(ex); return; }

        var candidate = installed;
        try
        {
            StageText.Text = AppTranslations.Get("launcher.checking");
            var repository = LauncherUpdateService.ReadRepository(root);
            if (repository is not null)
            {
                var latest = await _updates.LatestAsync(repository, _shutdown.Token);
                if (installed is null || LauncherUpdateService.IsNewer(latest.Version, installed.Version))
                {
                    StageText.Text = AppTranslations.Get("launcher.downloading");
                    var archive = await _updates.DownloadAsync(latest, root, new Progress<double>(), _shutdown.Token);
                    try
                    {
                        StageText.Text = AppTranslations.Get("launcher.installing");
                        try
                        {
                            candidate = await Task.Run(() => AppUpdateStore.Prepare(root, archive, latest.Version), _shutdown.Token);
                        }
                        catch (Exception ex) when (LauncherElevation.AccessDenied(ex))
                        {
                            await LauncherElevation.PrepareAsync(archive, latest.Version, latest.Sha256, repository,
                                _shutdown.Token);
                            candidate = new InstalledApp(latest.Version,
                                Path.Combine(root, "App", "versions", latest.Version));
                            if (!File.Exists(candidate.Executable))
                                throw new IOException("The application update did not install its executable.");
                        }
                    }
                    finally { if (File.Exists(archive)) File.Delete(archive); }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (installed is null) { ShowError(ex); return; }
            candidate = installed;
            StageText.Text = AppTranslations.Get("launcher.update_failed");
        }

        _shutdown.Token.ThrowIfCancellationRequested();
        if (candidate is null)
        {
            StageText.Text = AppTranslations.Get("launcher.missing_app");
            SetVisualState(SplashState.Failed);
            CompleteProgress();
            return;
        }

        try
        {
            StageText.Text = AppTranslations.Get("launcher.starting");
            var opened = false;
            try { opened = await _process.LaunchAndWaitAsync(candidate, _shutdown.Token); }
            catch (Exception) when (installed is not null && candidate != installed && !_shutdown.IsCancellationRequested)
            {
                StageText.Text = AppTranslations.Get("launcher.fallback");
            }
            if (opened)
            {
                if (candidate != installed && candidate.Version != "0.0.0")
                {
                    try { AppUpdateStore.Activate(root, candidate); }
                    catch (Exception ex) when (LauncherElevation.AccessDenied(ex))
                    {
                        try { await LauncherElevation.ActivateAsync(candidate.Version, _shutdown.Token); }
                        catch (Exception) { /* Keep the previous version selected if approval is cancelled. */ }
                    }
                    catch (Exception) { /* The app is already open; the previous version remains selected. */ }
                }
                await FinishAsync();
                return;
            }
            if (installed is not null && candidate != installed)
            {
                StageText.Text = AppTranslations.Get("launcher.fallback");
                if (await _process.LaunchAndWaitAsync(installed, _shutdown.Token))
                {
                    await FinishAsync();
                    return;
                }
            }
            StageText.Text = AppTranslations.Get("launcher.start_failed");
            SetVisualState(SplashState.Failed);
            CompleteProgress();
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task FinishAsync()
    {
        StageText.Text = AppTranslations.Get("launcher.running");
        SetVisualState(SplashState.Ready);
        CompleteProgress();
        await Task.Delay(350, _shutdown.Token);
        Close();
    }

    private void CompleteProgress()
    {
        LoadingProgress.Value = 100;
        LoadingProgress.IsIndeterminate = false;
    }

    private void ShowError(Exception ex)
    {
        StageText.Text = ex.Message;
        SetVisualState(SplashState.Failed);
        CompleteProgress();
    }

    private void SetVisualState(SplashState state)
    {
        var (background, border, text, progress, track) = state switch
        {
            SplashState.Failed => ("#992C1520", "#B7D66B7C", "#FFD5DC", "#EE7589", "#68502C3A"),
            SplashState.Ready => ("#99152B22", "#A77ACF9C", "#DAF9E8", "#71DBA4", "#68405748"),
            _ => ("#9919102B", "#A0805BC7", "#E9DBFF", "#AE82F5", "#68463972")
        };
        StatusPanel.Background = new SolidColorBrush(Color.Parse(background));
        StatusPanel.BorderBrush = new SolidColorBrush(Color.Parse(border));
        StageText.Foreground = new SolidColorBrush(Color.Parse(text));
        LoadingProgress.Foreground = new SolidColorBrush(Color.Parse(progress));
        LoadingProgress.Background = new SolidColorBrush(Color.Parse(track));
    }
}
