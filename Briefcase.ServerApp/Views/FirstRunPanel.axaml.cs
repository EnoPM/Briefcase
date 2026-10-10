using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Briefcase.ServerApp;

public sealed partial class FirstRunPanel : UserControl
{
    private readonly MainViewModel? _model;
    private bool _installing;

    public FirstRunPanel() : this(null) { }

    public FirstRunPanel(MainViewModel? model)
    {
        InitializeComponent();
        _model = model!;
        DataContext = model;
        ShowStep(model?.Installations.Any(installation =>
            installation.Branch == "public" && File.Exists(installation.Shipping)) == true
            ? Step.Finished : Step.Offer);
    }

    public event Action? LaterRequested;
    public event Action? CreateRequested;
    public event Action? ImportRequested;

    public void TryDismiss()
    {
        if (!_installing) LaterRequested?.Invoke();
    }

    private async void InstallClicked(object? sender, RoutedEventArgs e)
    {
        if (_installing || _model is null) return;
        _installing = true;
        ShowStep(Step.Installing);
        try
        {
            var installed = await _model.InstallSteamVersionAsync("public", null);
            ShowStep(installed ? Step.Finished : Step.Failed);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _model.Report(error.Message);
            ShowStep(Step.Failed);
        }
        finally { _installing = false; }
    }

    private void LaterClicked(object? sender, RoutedEventArgs e) => TryDismiss();
    private void CreateClicked(object? sender, RoutedEventArgs e) => CreateRequested?.Invoke();
    private void ImportClicked(object? sender, RoutedEventArgs e) => ImportRequested?.Invoke();

    private void ShowStep(Step step)
    {
        OfferSection.IsVisible = step == Step.Offer;
        InstallingSection.IsVisible = step == Step.Installing;
        FinishedSection.IsVisible = step == Step.Finished;
        FailedSection.IsVisible = step == Step.Failed;
        CloseButton.IsEnabled = step != Step.Installing;
    }

    private enum Step { Offer, Installing, Finished, Failed }
}
