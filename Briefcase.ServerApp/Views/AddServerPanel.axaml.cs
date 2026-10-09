using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Briefcase.ServerApp;

public sealed partial class AddServerPanel : UserControl
{
    public AddServerPanel() : this([]) { }

    public AddServerPanel(IEnumerable<ServerInstallation> installations)
    {
        InitializeComponent();
        VersionChoice.ItemsSource = installations.ToArray();
        VersionChoice.SelectedIndex = VersionChoice.ItemCount > 0 ? 0 : -1;
        InstallChoice.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) UpdateMode();
        };
        ExistingChoice.PropertyChanged += (_, args) =>
        {
            if (args.Property == ToggleButton.IsCheckedProperty) UpdateMode();
        };
        UpdateMode();
    }

    public event Action<string, ServerInstallation>? InstallRequested;
    public event Action<string>? ExistingRequested;
    public event Action? CancelRequested;

    private void InstallChoicePressed(object? sender, PointerPressedEventArgs e) => InstallChoice.IsChecked = true;
    private void ExistingChoicePressed(object? sender, PointerPressedEventArgs e) => ExistingChoice.IsChecked = true;

    private void UpdateMode()
    {
        var install = InstallChoice.IsChecked == true;
        InstallFields.IsVisible = install;
        ExistingFields.IsVisible = !install;
        InstallChoiceCard.Background = new SolidColorBrush(Color.Parse(install ? "#392D49" : "#2D2735"));
        InstallChoiceCard.BorderBrush = new SolidColorBrush(Color.Parse(install ? "#7350C6" : "#493B58"));
        ExistingChoiceCard.Background = new SolidColorBrush(Color.Parse(install ? "#2D2735" : "#392D49"));
        ExistingChoiceCard.BorderBrush = new SolidColorBrush(Color.Parse(install ? "#493B58" : "#7350C6"));
        ConfirmButton.Content = AppTranslations.Get(install ? "add.create_button" : "add.add_button");
        ValidationMessage.Text = "";
    }

    private async void BrowseExistingClicked(object? sender, RoutedEventArgs e)
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null) return;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = AppTranslations.Get("dialog.select_server"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(AppTranslations.Get("dialog.server_executable"))
                { Patterns = ["DeceiveIncServer.exe", "DeceiveIncServer-Win64-Shipping.exe"] }]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            ExistingExecutablePath.Text = path;
    }

    private void ConfirmClicked(object? sender, RoutedEventArgs e)
    {
        if (InstallChoice.IsChecked != true)
        {
            if (string.IsNullOrWhiteSpace(ExistingExecutablePath.Text))
            {
                ValidationMessage.Text = AppTranslations.Get("add.select_executable_first");
                return;
            }
            ExistingRequested?.Invoke(ExistingExecutablePath.Text);
            return;
        }

        string name;
        try { name = AppDataStore.ValidateServerName(ServerNameInput.Text ?? ""); }
        catch (InvalidDataException)
        {
            ValidationMessage.Text = AppTranslations.Get("install.invalid_name");
            return;
        }
        if (VersionChoice.SelectedItem is not ServerInstallation installation)
        {
            ValidationMessage.Text = AppTranslations.Get("versions.select_version_first");
            return;
        }
        InstallRequested?.Invoke(name, installation);
    }

    private void CancelClicked(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
