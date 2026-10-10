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
        Loaded += (_, _) => LoadProfiles();
        Loaded += (_, _) => UpdateVersionPath();
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel model) model.RunningDataServers();
        };
    }

    public event EventHandler? CloseRequested;
    public event Action<string>? RestartRequired;
    public Func<string, IReadOnlyList<string>, Task<bool>>? ConfirmStopServersAsync { get; set; }

    private void CloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void LoadProfiles()
    {
        var store = AppProfileStore.Current;
        ProfileChoice.Items.Clear();
        foreach (var profile in store.Profiles)
            ProfileChoice.Items.Add(new ComboBoxItem
            {
                Content = profile.Id == "default" ? AppTranslations.Get("settings.default_profile") : profile.Name,
                Tag = profile.Id
            });
        ProfileChoice.SelectedItem = ProfileChoice.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == store.NextActiveId);
        var editable = DataContext is MainViewModel { IsDemo: false };
        ProfileChoice.IsEnabled = editable;
        NewProfileName.IsEnabled = editable;
        CreateProfileButton.IsEnabled = editable;
        SwitchProfileButton.IsEnabled = editable &&
            ProfileChoice.SelectedItem is ComboBoxItem { Tag: string id } && id != store.NextActiveId;
        ProfilePendingNote.IsVisible = store.HasPendingActivation;
        ProfileWarning.Text = store.LoadError;
        ProfileWarning.IsVisible = store.LoadError.Length > 0;
    }

    private void ProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SwitchProfileButton is null) return;
        SwitchProfileButton.IsEnabled = DataContext is MainViewModel { IsDemo: false, IsBusy: false } &&
            ProfileChoice.SelectedItem is ComboBoxItem { Tag: string id } &&
            id != AppProfileStore.Current.NextActiveId;
    }

    private void CreateProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsDemo: false, IsBusy: false } model) return;
        var store = AppProfileStore.Current;
        try
        {
            store.CreateAndScheduleActivation(NewProfileName.Text ?? "");
            NewProfileName.Text = "";
            LoadProfiles();
            RestartRequired?.Invoke(AppTranslations.Get("settings.restart_reason_profile"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                                      ArgumentException or InvalidOperationException)
        {
            model.Report(error.Message);
            LoadProfiles();
        }
    }

    private void SwitchProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel { IsDemo: false, IsBusy: false } model ||
            ProfileChoice.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        var store = AppProfileStore.Current;
        if (id == store.NextActiveId) return;
        try
        {
            store.ScheduleActivation(id);
            LoadProfiles();
            if (store.HasPendingActivation)
                RestartRequired?.Invoke(AppTranslations.Get("settings.restart_reason_profile"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                                      ArgumentException or InvalidOperationException)
        {
            model.Report(error.Message);
            LoadProfiles();
        }
    }

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
            var previous = model.Registry.Language;
            model.Registry.Language = language;
            try { model.Registry.Save(); }
            catch { model.Registry.Language = previous; throw; }
            RestartRequired?.Invoke(AppTranslations.Get("settings.restart_reason_language"));
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
