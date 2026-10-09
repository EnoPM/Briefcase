using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Briefcase.ServerApp;

public sealed partial class SettingsPanel : UserControl
{
    private bool _customVersionPath;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LanguageChoice.Items.Clear();
            foreach (var language in AppTranslations.AvailableLanguages())
                LanguageChoice.Items.Add(new ComboBoxItem { Content = language.Name, Tag = language.Code });
            var saved = DataContext is MainViewModel model ? model.Registry.Language : "en";
            LanguageChoice.SelectedIndex = LanguageChoice.Items.Cast<ComboBoxItem>().ToList()
                .FindIndex(item => string.Equals(item.Tag as string, saved, StringComparison.OrdinalIgnoreCase));
            if (LanguageChoice.SelectedIndex < 0) LanguageChoice.SelectedIndex = 0;
        };
        Loaded += (_, _) => DataDirectoryPath.Text =
            DataContext is MainViewModel model ? model.Registry.DataDirectory : AppDataStore.Root;
        Loaded += (_, _) => UpdateVersionPath();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel model) model.RunningDataServers();
        };
    }

    public event EventHandler? CloseRequested;
    public Func<string, IReadOnlyList<string>, Task<bool>>? ConfirmStopServersAsync { get; set; }

    private void CloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void BranchChanged(object? sender, TextChangedEventArgs e)
    {
        _customVersionPath = false;
        UpdateVersionPath();
    }

    private void UpdateVersionPath()
    {
        if (VersionInstallPath is null || BranchNameInput is null) return;
        var data = DataContext is MainViewModel model ? model.Registry.DataDirectory : AppDataStore.Root;
        var branch = (BranchNameInput.Text ?? "").Trim().ToLowerInvariant();
        var installed = (DataContext as MainViewModel)?.Installations.FirstOrDefault(item => item.Branch == branch);
        if (ChooseVersionDirectoryButton is not null)
            ChooseVersionDirectoryButton.IsEnabled = installed is null;
        if (installed is not null)
        {
            _customVersionPath = false;
            VersionInstallPath.Text = installed.InstallRoot;
            return;
        }
        if (_customVersionPath) return;
        try { VersionInstallPath.Text = AppDataStore.VersionInstallRoot(data, branch); }
        catch (InvalidDataException) { VersionInstallPath.Text = Path.Combine(data, "game", "versions"); }
    }

    private async void ChooseVersionDirectoryClicked(object? sender, RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = AppTranslations.Get("versions.choose_directory"), AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            _customVersionPath = true;
            VersionInstallPath.Text = path;
        }
    }

    private async void InstallVersionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        await model.InstallSteamVersionAsync(BranchNameInput.Text ?? "", BetaPasswordInput.Text,
            VersionInstallPath.Text);
        BetaPasswordInput.Text = "";
        VersionList.SelectedItem = model.Installations.FirstOrDefault(item => item.Branch ==
            (BranchNameInput.Text ?? "").Trim().ToLowerInvariant());
        UpdateVersionButton.IsEnabled = VersionList.SelectedItem is not null;
        UpdateVersionPath();
    }

    private void VersionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (UpdateVersionButton is not null)
            UpdateVersionButton.IsEnabled = VersionList.SelectedItem is ServerInstallation;
    }

    private async void UpdateVersionClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || VersionList.SelectedItem is not ServerInstallation version)
            return;
        await model.InstallSteamVersionAsync(version.Branch, BetaPasswordInput.Text, version.InstallRoot);
        BetaPasswordInput.Text = "";
        VersionList.SelectedItem = model.Installations.FirstOrDefault(item => item.Id == version.Id);
        UpdateVersionButton.IsEnabled = VersionList.SelectedItem is not null;
    }

    private async void ChooseDataDirectoryClicked(object? sender, RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = AppTranslations.Get("settings.choose_directory"),
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            DataDirectoryPath.Text = path;
    }

    private async void MoveDataClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || string.IsNullOrWhiteSpace(DataDirectoryPath.Text)) return;
        ChooseDataDirectoryButton.IsEnabled = false;
        MoveDataButton.IsEnabled = false;
        try
        {
            var target = Path.GetFullPath(DataDirectoryPath.Text);
            var source = model.Registry.DataDirectory;
            var stopServers = false;
            if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                AppDataStore.ValidateMoveDestination(source, target);
                var running = model.RunningDataServers();
                if (running.Count > 0)
                {
                    if (ConfirmStopServersAsync is null ||
                        !await ConfirmStopServersAsync(target, running.Select(row => row.Name).ToArray())) return;
                    stopServers = true;
                }
            }
            if (await model.ChangeDataDirectoryAsync(target, stopServers))
            {
                DataDirectoryPath.Text = model.Registry.DataDirectory;
                UpdateVersionPath();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        { model.Report(ex.Message); }
        finally
        {
            ChooseDataDirectoryButton.IsEnabled = true;
            MoveDataButton.IsEnabled = true;
        }
    }

    private void SaveLanguageClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model) return;
        if (LanguageChoice.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        if (model.Registry.Language == language) return;
        try
        {
            model.Registry.Language = language;
            model.Registry.Save();
            model.Report(AppTranslations.Get("ui.language_saved_restart"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { model.Report(ex.Message); }
    }

    private void AddCatalogClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model) model.AddCatalog();
    }

    private void RemoveCatalogClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && CatalogList.SelectedItem is string url)
            model.RemoveCatalog(url);
    }
}
