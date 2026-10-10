using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Diagnostics;
using System.Globalization;

namespace Briefcase.ServerApp;

public sealed partial class MainWindow : Window
{
    private sealed record ServerAddChoice(string? Name, ServerInstallation? Installation, string? Executable);
    private sealed record FormChoiceOption(string Value, string Label)
    {
        public override string ToString() => Label;
    }
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _reconciliationTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, Control> _settingControls = new(StringComparer.OrdinalIgnoreCase);
    private FormSchema? _configurationSchema;
    private bool _updatingReferencedBounds;
    private List<string> _mapRotationDraft = [];
    private WrapPanel? _mapRotationPreview;
    private Action? _dismissModal;
    private Action? _dismissConfirmation;
    private ModConfigEditor? _modEditor;
    private readonly Dictionary<ModConfigValue, Control> _modInputs = new();
    private readonly Dictionary<string, string> _modDrafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _invalidFormDrafts = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedModPath;
    private bool _jsonMode;
    private PlayerCardDrag? _playerCardDrag;

    public MainWindow() : this(false) { }

    public MainWindow(bool demo)
    {
        // Rider's XAML preview creates a real MainWindow. Never let that preview
        // discover servers or occupy the live control pipe used by the app.
        _model = new MainViewModel(demo || Design.IsDesignMode);
        InitializeComponent();
        if (demo) Title += " [" + AppTranslations.Get("ui.demo") + "]";
        DataContext = _model;
        if (_model.MarketplaceMods.Count > 0) MarketplaceList.SelectedIndex = 0;
        _model.MarketplaceMods.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (MarketplaceList.SelectedItem is null && _model.MarketplaceMods.Count > 0)
                    MarketplaceList.SelectedIndex = 0;
            });
        _model.LiveStateChanged += OnLiveStateChanged;
        _model.PlayerTeams.CollectionChanged += (_, _) => CancelPlayerCardDrag();
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedServer))
            {
                CancelPlayerCardDrag();
                CaptureModDraft();
                ClearConfigurationFields();
                ModConfigurationFields.Children.Clear();
                _settingControls.Clear();
                _modInputs.Clear();
                _modEditor = null;
                _selectedModPath = null;
                SaveModButton.IsEnabled = false;
                EnableModButton.IsVisible = false;
                DisableModButton.IsVisible = false;
                UninstallModButton.IsVisible = false;
                InstalledModDocsButton.IsVisible = false;
                InstalledModDiscordButton.IsVisible = false;
                InstalledModHomepageButton.IsVisible = false;
                SelectedModTitle.Text = AppTranslations.Get("mod.select_title");
                SelectedModPath.Text = "";
                CoreModHint.IsVisible = false;
                ModJsonEditor.Text = "";
                var selected = _model.SelectedServer;
                if (_model.IsDemo || selected is null)
                {
                    LoadSettings();
                    BalancingView.Load(selected, _model.IsDemo, _model.Report);
                    if (_model.IsDemo) Dispatcher.UIThread.Post(() => ModsList.SelectedIndex = 0);
                }
                else Dispatcher.UIThread.Post(() => _ = LoadSelectedServerAsync(selected));
            }
            if (args.PropertyName == nameof(MainViewModel.IsRunning) && !_model.IsRunning &&
                ServerTabs.SelectedItem == PlayersTab) ServerTabs.SelectedIndex = 0;
        };
        LoadSettings();
        BalancingView.Load(_model.SelectedServer, _model.IsDemo, _model.Report);
        BalancingView.Saved += async (_, _) => await _model.RefreshAsync();
        if (demo) ModsList.SelectedIndex = 0;
        _reconciliationTimer.Tick += async (_, _) => await _model.RefreshAsync();
        Opened += async (_, _) =>
        {
            if (!_model.IsDemo) _reconciliationTimer.Start();
            await _model.RefreshAsync();
            if (!_model.IsDemo)
            {
                _ = _model.CheckUpdatesAsync();
                _ = _model.RefreshMarketplaceAsync();
            }
        };
        KeyDown += (_, args) =>
        {
            if (ConfirmationLayer.IsVisible && args.Key == Key.Escape)
            {
                _dismissConfirmation?.Invoke();
                args.Handled = true;
            }
            else if (ModalLayer.IsVisible && args.Key == Key.Escape)
            {
                _dismissModal?.Invoke();
                args.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _reconciliationTimer.Stop();
            _model.LiveStateChanged -= OnLiveStateChanged;
            _dismissConfirmation?.Invoke();
            _dismissModal?.Invoke();
            _model.Dispose();
        };
    }

    private void PlayerCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_model.CanArrangeTeams || sender is not Border { DataContext: PlayerRow player } card ||
            !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source).OfType<Button>().Any()) return;
        var origin = card.GetVisualAncestors().OfType<Border>().FirstOrDefault(IsTeamSlot);
        if (origin?.DataContext is not PlayerTeamSlot { Player: not null }) return;
        var slots = PlayerTeamList.GetVisualDescendants().OfType<Border>()
            .Where(IsTeamSlot)
            .Select(surface => new PlayerSlotSurface(surface,
                (PlayerTeamSlot)surface.DataContext!,
                surface.GetVisualDescendants().OfType<Border>().FirstOrDefault(IsPlayerCard),
                surface.GetVisualAncestors().OfType<Border>().First(IsTeamGroup)))
            .ToArray();
        _playerCardDrag = new PlayerCardDrag(player, card, origin, slots, e.Pointer,
            e.GetPosition(PlayerDragLayer));
        e.Pointer.Capture(card);
        e.Handled = true;
    }

    private void PlayerCardMoved(object? sender, PointerEventArgs e)
    {
        if (_playerCardDrag is not { } drag || sender != drag.Card || e.Pointer != drag.Pointer) return;
        var position = e.GetPosition(PlayerDragLayer);
        var delta = position - drag.Start;
        if (!drag.Started && Math.Abs(delta.X) + Math.Abs(delta.Y) < 5) return;
        if (!drag.Started)
        {
            drag.Started = true;
            drag.PreviewOrigin = drag.Card.TranslatePoint(default, PlayerDragLayer) ?? default;
            PlayerDragPreview.Content = drag.Player;
            PlayerDragPreview.Width = drag.Card.Bounds.Width;
            PlayerDragPreview.Height = drag.Card.Bounds.Height;
            PlayerDragPreview.IsVisible = true;
            drag.Card.Opacity = 0;
        }
        Canvas.SetLeft(PlayerDragPreview, drag.PreviewOrigin.X + delta.X);
        Canvas.SetTop(PlayerDragPreview, drag.PreviewOrigin.Y + delta.Y);
        var target = drag.Slots.FirstOrDefault(slot => slot.Surface != drag.Origin &&
            slot.Slot.Team != drag.Player.Team && IsInside(slot.Surface, e.GetPosition(slot.Surface)));
        if (target == drag.Target) return;
        ResetPlayerTarget(drag);
        drag.Target = target;
        if (target is not { } found) return;
        found.Surface.Background = Brush("#392C49");
        found.Surface.BorderBrush = Brush("#A875E7");
        found.Group.ZIndex = 90;
        if (found.Card is { } other)
        {
            var from = found.Card.TranslatePoint(default, this);
            var to = drag.Card.TranslatePoint(default, this);
            if (from is not null && to is not null)
            {
                other.Transitions = CardMovementTransitions();
                other.RenderTransform = CardOffset(to.Value.X - from.Value.X, to.Value.Y - from.Value.Y);
            }
        }
        e.Handled = true;
    }

    private async void PlayerCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_playerCardDrag is not { } drag || sender != drag.Card || e.Pointer != drag.Pointer) return;
        _playerCardDrag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        var target = drag.Target;
        RestorePlayerCardDrag(drag);
        try
        {
            if (drag.Started && target is { } destination && _model.CanArrangeTeams)
                await _model.ChangePlayerTeamAsync(drag.Player, destination.Slot.Team, destination.Slot.Player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { _model.Report(ex.Message); }
    }

    private void PlayerCardCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_playerCardDrag is { } drag && sender == drag.Card) CancelPlayerCardDrag();
    }

    private void CancelPlayerCardDrag()
    {
        if (_playerCardDrag is not { } drag) return;
        _playerCardDrag = null;
        drag.Pointer.Capture(null);
        RestorePlayerCardDrag(drag);
    }

    private void RestorePlayerCardDrag(PlayerCardDrag drag)
    {
        ResetPlayerTarget(drag);
        PlayerDragPreview.IsVisible = false;
        PlayerDragPreview.Content = null;
        drag.Card.Opacity = 1;
    }

    private static void ResetPlayerTarget(PlayerCardDrag drag)
    {
        if (drag.Target is not { } target) return;
        target.Card?.SetCurrentValue(Border.RenderTransformProperty, CardOffset(0, 0));
        target.Surface.Background = Brush("#28232F");
        target.Surface.BorderBrush = Brush("#473A55");
        target.Group.ZIndex = 0;
        drag.Target = null;
    }

    private static bool IsInside(Border border, Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X < border.Bounds.Width && point.Y < border.Bounds.Height;
    private static bool IsTeamSlot(Border border) => border.Classes.Contains("team-slot");
    private static bool IsTeamGroup(Border border) => border.Classes.Contains("team-group");
    private static bool IsPlayerCard(Border border) => border.Classes.Contains("player-card");
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
    private static TransformOperations CardOffset(double x, double y) => TransformOperations.Parse(
        "translate(" + x.ToString(CultureInfo.InvariantCulture) + "px, " +
        y.ToString(CultureInfo.InvariantCulture) + "px)");
    private static Transitions CardMovementTransitions() =>
    [
        new TransformOperationsTransition
        {
            Property = Border.RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new CubicEaseOut()
        }
    ];

    private sealed record PlayerSlotSurface(Border Surface, PlayerTeamSlot Slot, Border? Card, Border Group);
    private sealed class PlayerCardDrag(PlayerRow player, Border card, Border origin,
        PlayerSlotSurface[] slots, IPointer pointer, Point start)
    {
        public PlayerRow Player { get; } = player;
        public Border Card { get; } = card;
        public Border Origin { get; } = origin;
        public PlayerSlotSurface[] Slots { get; } = slots;
        public IPointer Pointer { get; } = pointer;
        public Point Start { get; } = start;
        public Point PreviewOrigin { get; set; }
        public bool Started { get; set; }
        public PlayerSlotSurface? Target { get; set; }
    }

    private void OnLiveStateChanged() =>
        Dispatcher.UIThread.Post(() => _ = _model.RefreshAsync(waitForSelection: true));

    private async Task LoadSelectedServerAsync(ServerRow selected)
    {
        await _model.RefreshAsync(waitForSelection: true);
        if (_model.SelectedServer != selected) return;
        try
        {
            LoadSettings();
            BalancingView.Load(selected, false, _model.Report);
        }
        finally
        {
            _model.CompleteSelectionLoading(selected);
            if (_model.SelectedServer == selected)
            {
                _ = _model.CheckUpdatesAsync();
                _ = _model.RefreshMarketplaceAsync();
            }
        }
    }

    private async void AddServerClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDemo) { _model.Report(AppTranslations.Get("status.demo_no_add")); return; }
        if (_model.IsBusy || ModalLayer.IsVisible) return;
        var result = new TaskCompletionSource<ServerAddChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(ServerAddChoice? choice)
        {
            if (!result.TrySetResult(choice)) return;
            HideModal();
        }
        var panel = new AddServerPanel(_model.Installations);
        panel.InstallRequested += (name, installation) => Finish(new ServerAddChoice(name, installation, null));
        panel.ExistingRequested += executable => Finish(new ServerAddChoice(null, null, executable));
        panel.CancelRequested += () => Finish(null);
        if (!ShowModal(panel, 620, () => Finish(null))) return;
        if (await result.Task is not { } selected) return;
        try
        {
            if (selected.Executable is { } executable)
            {
                _model.AddServer(executable);
                await _model.RefreshAsync();
            }
            else if (selected.Name is { } name)
            {
                if (selected.Installation is null) throw new InvalidDataException("Choose an installed server version.");
                _model.CreateServerFromInstallation(selected.Installation, name);
                await _model.RefreshAsync();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or ArgumentException)
        { _model.Report(ex.Message); }
    }
    private async void DeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDemo) { _model.Report(AppTranslations.Get("status.demo_no_remove")); return; }
        if (_model.IsBusy || _model.SelectedServer is not { } row) return;
        var choice = await ConfirmRemovalAsync(row);
        if (choice == 0) return;
        await _model.RemoveServerAsync(row, choice == 2);
    }

    private async Task<int> ConfirmRemovalAsync(ServerRow row)
    {
        var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(int choice)
        {
            if (!result.TrySetResult(choice)) return;
            HideModal();
        }
        var cancel = new Button { Content = AppTranslations.Get("dialog.cancel") };
        var remove = new Button { Content = AppTranslations.Get("dialog.remove_from_list") };
        var delete = new Button { Content = AppTranslations.Get("dialog.stop_delete") };
        delete.Classes.Add("danger");
        cancel.Click += (_, _) => Finish(0);
        remove.Click += (_, _) => Finish(1);
        delete.Click += (_, _) => Finish(2);
        var content = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("dialog.remove_server"), FontSize = 21, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = row.InstanceRoot.Length > 0 ? row.InstanceRoot :
                    Path.GetFullPath(Path.Combine(row.Win64, "../../..")), TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ADA3B6")) },
                new TextBlock { Text = AppTranslations.Get("dialog.remove_explanation"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, remove, delete } }
            }
        };
        if (!ShowModal(content, 560, () => Finish(0))) return 0;
        return await result.Task;
    }
    private async void StartClicked(object? sender, RoutedEventArgs e)
    {
        var proxy = Path.Combine(_model.Win64Path, "version.dll");
        var wasPresent = File.Exists(proxy);
        await _model.StartAsync();
        if (wasPresent && !File.Exists(proxy) && File.Exists(proxy + ".disabled")) ShowProxyDisabledNotice();
    }
    private async void MigrateInstallationClicked(object? sender, RoutedEventArgs e) =>
        await _model.MigrateSelectedServerAsync();
    private async void StopClicked(object? sender, RoutedEventArgs e) => await _model.StopAsync();
    private void AutomaticUpdatesChanged(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDemo || _model.SelectedServer is null) return;
        try { _model.Registry.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _model.Report(ex.Message); }
    }
    private async void RestartClicked(object? sender, RoutedEventArgs e)
    {
        var proxy = Path.Combine(_model.Win64Path, "version.dll");
        var wasPresent = File.Exists(proxy);
        await _model.RestartAsync();
        if (wasPresent && !File.Exists(proxy) && File.Exists(proxy + ".disabled")) ShowProxyDisabledNotice();
    }

    private void ShowProxyDisabledNotice()
    {
        var close = new Button { Content = AppTranslations.Get("dialog.close", "Close") };
        close.Click += (_, _) => HideModal();
        var content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("ui.proxy_disabled_title"), FontSize = 20,
                    FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = AppTranslations.Get("status.proxy_disabled_notice"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { close } }
            }
        };
        ShowModal(content, 500, HideModal);
    }

    private void LoadSettingsClicked(object? sender, RoutedEventArgs e) => LoadSettings();
    private void ClearConfigurationFields()
    {
        ConfigurationTabs.Items.Clear();
        _configurationSchema = null;
        _mapRotationPreview = null;
        _mapRotationDraft = [];
    }

    private void LoadSettings()
    {
        var selectedTabId = (ConfigurationTabs.SelectedItem as TabItem)?.Tag as string;
        ClearConfigurationFields();
        _settingControls.Clear();
        if (_model.SelectedServer is not { } row) return;
        ServerLocation? location = null;
        if (!_model.IsDemo && !ServerLocation.TryCreate(row, out location, out var error))
        { _model.Report(error); return; }
        try
        {
            var schema = _model.IsDemo ? FormSchema.LoadBase() : FormSchema.LoadForServer(location!);
            _configurationSchema = schema;
            var (values, maps) = _model.IsDemo
                ? DemoData.Configuration(row.Id)
                : ServerIniEditor.Read(location!.ConfigFile, schema);
            _mapRotationDraft = maps.ToList();
            foreach (var tab in schema.Tabs)
            {
                var tabPanel = new StackPanel { Spacing = 9, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
                foreach (var group in schema.Groups.Where(group => group.Tab == tab.Id))
                {
                    var panel = new StackPanel { Spacing = 7 };
                    if (schema.Groups.Count(item => item.Tab == tab.Id) > 1) panel.Children.Add(new TextBlock
                    {
                        Text = schema.Text(group.LabelKey, group.Label), FontSize = 16,
                        FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(0, 2, 0, 3)
                    });
                    foreach (var spec in schema.Fields.Where(field => field.Group == group.Id))
                    {
                        Control input;
                        Control? inputDisplay = null;
                        var value = values[spec.Key];
                        if (spec.Kind == FormFieldKind.Boolean)
                            input = new CheckBox { IsChecked = value.Equals("True", StringComparison.OrdinalIgnoreCase) || value == "1" };
                        else if (spec.Kind == FormFieldKind.Choice)
                        {
                            var options = spec.Choices!.Select(choice => new FormChoiceOption(choice.Value,
                                schema.Text(choice.LabelKey, choice.Label))).ToArray();
                            input = new ComboBox { ItemsSource = options,
                                SelectedItem = options.FirstOrDefault(option => option.Value == value) };
                        }
                        else if (spec.Display == "slider")
                        {
                            var maximum = (double)spec.SliderMaximum(schema.EffectiveMaximum(spec, values) ?? 100);
                            var selected = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out var configuredValue) ? Math.Clamp(configuredValue, spec.Minimum ?? 0, (decimal)maximum) :
                                decimal.Parse(spec.Default, CultureInfo.InvariantCulture);
                            var slider = new Slider
                            {
                                Minimum = (double)(spec.Minimum ?? 0), Maximum = maximum, Value = (double)selected,
                                TickFrequency = (double)spec.Step, IsSnapToTickEnabled = true,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                            };
                            var valueLabel = new TextBlock
                            {
                                Text = selected.ToString(CultureInfo.InvariantCulture), MinWidth = 22,
                                TextAlignment = Avalonia.Media.TextAlignment.Right,
                                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                            };
                            slider.ValueChanged += (_, _) =>
                            {
                                valueLabel.Text = spec.FormatSliderValue(slider.Value);
                                UpdateReferencedBounds(schema);
                            };
                            var sliderDisplay = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                            sliderDisplay.Children.Add(slider);
                            Grid.SetColumn(valueLabel, 1);
                            sliderDisplay.Children.Add(valueLabel);
                            input = slider;
                            inputDisplay = sliderDisplay;
                        }
                        else if (spec.Kind is FormFieldKind.Integer or FormFieldKind.Decimal)
                        {
                            var parsed = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out var number) ? number : decimal.Parse(spec.Default, CultureInfo.InvariantCulture);
                            var numeric = new NumericUpDown
                            {
                                Minimum = spec.Minimum ?? -1000000, Maximum = schema.EffectiveMaximum(spec, values) ?? 1000000,
                                Value = parsed, Increment = spec.Step,
                                FormatString = spec.Kind == FormFieldKind.Integer ? "0" : "0.##",
                                ParsingNumberStyle = spec.Kind == FormFieldKind.Integer ? NumberStyles.Integer : NumberStyles.Float,
                                ClipValueToMinMax = true, ShowButtonSpinner = false
                            };
                            numeric.ValueChanged += (_, _) => UpdateReferencedBounds(schema);
                            input = numeric;
                        }
                        else
                            input = new TextBox { Text = value,
                                MaxLength = spec.MaxLength ?? 0,
                                PasswordChar = spec.Kind == FormFieldKind.Secret ? '●' : '\0' };
                        inputDisplay ??= input;
                        _settingControls[spec.Key] = input;
                        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 10 };
                        line.Children.Add(new TextBlock { Text = schema.Text(spec.LabelKey, spec.Label),
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                        Grid.SetColumn(inputDisplay, 1);
                        line.Children.Add(inputDisplay);
                        panel.Children.Add(line);
                    }
                    tabPanel.Children.Add(new Border
                    {
                        Background = Avalonia.Media.Brushes.Transparent,
                        BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#493A57")),
                        BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(6),
                        Padding = new Avalonia.Thickness(12, 9), Child = panel
                    });
                }
                if (tab.Feature == "mapRotation")
                {
                    var panel = new StackPanel { Spacing = 7 };
                    var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
                    heading.Children.Add(new TextBlock
                    {
                        Text = AppTranslations.Get("ui.map_rotation_cycle"), FontSize = 15,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    });
                    var edit = new Button
                    {
                        Content = AppTranslations.Get("ui.edit_map_rotation"),
                        MinHeight = 28, Padding = new Avalonia.Thickness(8, 4), FontSize = 12
                    };
                    edit.Click += EditMapRotationClicked;
                    Grid.SetColumn(edit, 1);
                    heading.Children.Add(edit);
                    panel.Children.Add(heading);
                    panel.Children.Add(new TextBlock
                    {
                        Text = AppTranslations.Get("ui.map_rotation_save_hint"),
                        Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#ADA3B6")),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    });
                    _mapRotationPreview = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
                    panel.Children.Add(_mapRotationPreview);
                    tabPanel.Children.Add(new Border
                    {
                        Background = Avalonia.Media.Brushes.Transparent,
                        BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#493A57")),
                        BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(6),
                        Padding = new Avalonia.Thickness(12, 9), Child = panel
                    });
                    RenderMapRotationPreview();
                }
                ConfigurationTabs.Items.Add(new TabItem
                {
                    Header = schema.Text(tab.LabelKey, tab.Label),
                    Tag = tab.Id,
                    Content = new ScrollViewer { Content = tabPanel }
                });
            }
            ConfigurationTabs.SelectedItem = ConfigurationTabs.Items.OfType<TabItem>()
                .FirstOrDefault(item => item.Tag as string == selectedTabId) ?? ConfigurationTabs.Items.OfType<TabItem>().First();
            UpdateReferencedBounds(schema);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { _model.Report(ex.Message); }
    }

    private void RenderMapRotationPreview()
    {
        if (_mapRotationPreview is null) return;
        _mapRotationPreview.Children.Clear();
        if (_mapRotationDraft.Count == 0)
        {
            _mapRotationPreview.Children.Add(new TextBlock
            {
                Text = AppTranslations.Get("ui.map_rotation_empty"), Foreground = Avalonia.Media.Brushes.IndianRed
            });
            return;
        }
        for (var index = 0; index < _mapRotationDraft.Count; index++)
        {
            var id = _mapRotationDraft[index];
            var label = GameMapNames.Display(id);
            var item = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6, Margin = new Avalonia.Thickness(0, 0, 7, 4) };
            item.Children.Add(new Border
            {
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#30263F")),
                BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#58456C")),
                BorderThickness = new Avalonia.Thickness(1), CornerRadius = new Avalonia.CornerRadius(5),
                Padding = new Avalonia.Thickness(7, 3),
                Child = new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
            });
            if (index < _mapRotationDraft.Count - 1)
                item.Children.Add(new TextBlock { Text = "→", FontSize = 17, Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B79AD8")),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            _mapRotationPreview.Children.Add(item);
        }
    }

    private void UpdateReferencedBounds(FormSchema schema)
    {
        if (_updatingReferencedBounds) return;
        _updatingReferencedBounds = true;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, control) in _settingControls)
            {
                var field = schema.Fields.FirstOrDefault(candidate => candidate.Key == key);
                values[key] = control switch
                {
                    Slider slider when field is not null => field.FormatSliderValue(slider.Value),
                    NumericUpDown numeric => numeric.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
                    _ => ""
                };
            }
            foreach (var field in schema.Fields.Where(field => field.MaximumReference is not null))
            {
                var bound = schema.EffectiveMaximum(field, values);
                if (bound is null || !_settingControls.TryGetValue(field.Key, out var control)) continue;
                if (control is Slider slider)
                {
                    slider.Maximum = (double)field.SliderMaximum(bound.Value);
                    if (slider.Value > slider.Maximum) slider.Value = slider.Maximum;
                }
                else if (control is NumericUpDown numeric)
                {
                    numeric.Maximum = bound.Value;
                    if (numeric.Value > numeric.Maximum) numeric.Value = numeric.Maximum;
                }
            }
        }
        finally { _updatingReferencedBounds = false; }
    }

    private void EditMapRotationClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.SelectedServer is not { } row || ModalLayer.IsVisible) return;
        ServerLocation? location = null;
        if (!_model.IsDemo && !ServerLocation.TryCreate(row, out location, out var error))
        { _model.Report(error); return; }
        var editor = new MapRotationEditor();
        editor.Load(_mapRotationDraft);
        editor.RotationChanged += rotation =>
        {
            try
            {
                if (!_model.IsDemo) ServerIniEditor.SaveMapRotation(location!.ConfigFile, rotation);
                _mapRotationDraft = rotation.ToList();
                RenderMapRotationPreview();
                if (_model.IsRunning) row.NeedsRestart = true;
                _model.Report(AppTranslations.Get(_model.IsDemo ? "status.demo_preview_only" : "status.rotation_saved"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                editor.Load(_mapRotationDraft);
                _model.Report(ex.Message);
            }
        };
        var close = new IconButton
        {
            IconData = Avalonia.Media.Geometry.Parse("M 4,4 L 16,16 M 16,4 L 4,16"),
            IconStroke = Avalonia.Media.Brushes.LightGray,
            IconStrokeThickness = 1.8,
            Width = 32, Height = 32, IconSize = 17
        };
        close.Classes.Add("icon-button");
        ToolTip.SetTip(close, AppTranslations.Get("ui.close", "Close"));
        close.Click += (_, _) => HideModal();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(new TextBlock
        {
            Text = AppTranslations.Get("ui.edit_map_rotation"), FontSize = 21,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };
        content.Children.Add(header);
        Grid.SetRow(editor, 1);
        content.Children.Add(editor);
        ShowModal(content, 520, HideModal, tall: true);
    }

    private async void SaveSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDemo) { _model.Report(AppTranslations.Get("status.demo_no_config_save")); return; }
        if (_model.SelectedServer is not { } row) return;
        if (!ServerLocation.TryCreate(row, out var location, out var error)) { _model.Report(error); return; }
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in (_configurationSchema ?? FormSchema.LoadBase()).Fields)
            {
                var input = _settingControls[spec.Key];
                values[spec.Key] = input switch
                {
                    CheckBox box => box.IsChecked == true ? "True" : "False",
                    ComboBox combo => combo.SelectedItem is FormChoiceOption choice ? choice.Value : "",
                    Slider slider => spec.FormatSliderValue(slider.Value),
                    NumericUpDown number => number.Value?.ToString(CultureInfo.InvariantCulture) ?? "",
                    TextBox text => text.Text ?? "",
                    _ => ""
                };
            }
            ServerIniEditor.Save(location!.ConfigFile, values, _mapRotationDraft,
                FormSchema.LoadForServer(location));
            _model.Report(AppTranslations.Get("status.config_saved"));
            await _model.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException)
        { _model.Report(ex.Message); }
    }

    private void ModSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        CaptureModDraft();
        _modEditor = null;
        _modInputs.Clear();
        ModConfigurationFields.Children.Clear();
        ModEditorError.Text = "";
        if (ModsList.SelectedItem is not ModRow mod)
        {
            _selectedModPath = null;
            SelectedModTitle.Text = AppTranslations.Get("mod.select_title");
            SelectedModPath.Text = "";
            CoreModHint.IsVisible = false;
            SaveModButton.IsEnabled = false;
            ModJsonEditor.Text = "";
            EnableModButton.IsVisible = false;
            DisableModButton.IsVisible = false;
            UninstallModButton.IsVisible = false;
            InstalledModDocsButton.IsVisible = false;
            InstalledModDiscordButton.IsVisible = false;
            InstalledModHomepageButton.IsVisible = false;
            return;
        }
        EnableModButton.IsVisible = mod.CanToggle && !mod.IsEnabled;
        DisableModButton.IsVisible = mod.CanToggle && mod.IsEnabled;
        UninstallModButton.IsVisible = mod.CanToggle;
        InstalledModDocsButton.IsVisible = mod.HasDocumentation;
        InstalledModDiscordButton.IsVisible = mod.HasDiscord;
        InstalledModHomepageButton.IsVisible = !string.IsNullOrWhiteSpace(mod.HomepageUrl);
        _selectedModPath = mod.ConfigPath;
        CoreModHint.IsVisible = mod.IsCore;
        SelectedModTitle.Text = mod.Title;
        SelectedModPath.Text = mod.ConfigPath;
        ToolTip.SetTip(SelectedModPath, mod.ConfigPath);
        SaveModButton.IsEnabled = !_model.IsDemo && mod.HasConfiguration;
        if (_model.IsDemo)
        {
            foreach (var (label, value, boolean) in DemoData.ModConfiguration(mod.Name))
            {
                Control input = boolean
                    ? new CheckBox { IsChecked = value.Equals("True", StringComparison.OrdinalIgnoreCase), IsEnabled = false }
                    : new TextBox { Text = value, IsReadOnly = true };
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 16 };
                line.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                Grid.SetColumn(input, 1);
                line.Children.Add(input);
                ModConfigurationFields.Children.Add(line);
            }
            ModJsonEditor.Text = "{}";
            ModJsonEditor.IsReadOnly = true;
            return;
        }
        if (!File.Exists(mod.ConfigPath))
        {
            ModConfigurationFields.Children.Add(new TextBlock { Text = AppTranslations.Get("status.mod_no_config") });
            ModJsonEditor.Text = "";
            ModJsonEditor.IsReadOnly = true;
            return;
        }
        try
        {
            ModJsonEditor.IsReadOnly = false;
            ModJsonEditor.Text = _modDrafts.GetValueOrDefault(mod.ConfigPath) ?? File.ReadAllText(mod.ConfigPath);
            if (!_jsonMode)
            {
                BuildModForm(mod.ConfigPath, ModJsonEditor.Text);
                if (_invalidFormDrafts.TryGetValue(mod.ConfigPath, out var pending))
                    foreach (var (entry, input) in _modInputs)
                        if (pending.TryGetValue(entry.Label, out var raw) && input is TextBox text) text.Text = raw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { ModEditorError.Text = ex.Message; _model.Report(ex.Message); }
    }

    private void BuildModForm(string path, string? json)
    {
        var editor = new ModConfigEditor(path, json);
        _modEditor = editor;
        _modInputs.Clear();
        ModConfigurationFields.Children.Clear();
        foreach (var entry in editor.Values)
        {
            Control input = entry.Kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? new CheckBox { IsChecked = entry.Value == "true" }
                : new TextBox { Text = entry.Value };
            _modInputs[entry] = input;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 16 };
            line.Children.Add(new TextBlock { Text = entry.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            Grid.SetColumn(input, 1);
            line.Children.Add(input);
            ModConfigurationFields.Children.Add(line);
        }
    }

    private IReadOnlyDictionary<ModConfigValue, string> FormValues() => _modInputs.ToDictionary(pair => pair.Key,
        pair => pair.Value switch
        {
            CheckBox box => box.IsChecked == true ? "true" : "false",
            TextBox text => text.Text ?? "",
            _ => ""
        });

    private void CaptureModDraft()
    {
        if (_selectedModPath is null || _model.IsDemo || !File.Exists(_selectedModPath)) return;
        try
        {
            _modDrafts[_selectedModPath] = _jsonMode || _modEditor is null
                ? ModJsonEditor.Text ?? "" : _modEditor.Render(FormValues());
            _invalidFormDrafts.Remove(_selectedModPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            _invalidFormDrafts[_selectedModPath] = _modInputs.ToDictionary(pair => pair.Key.Label,
                pair => pair.Value is TextBox text ? text.Text ?? "" :
                    pair.Value is CheckBox box && box.IsChecked == true ? "true" : "false");
            ModEditorError.Text = ex.Message;
            _model.Report(ex.Message);
        }
    }

    private void FormModeClicked(object? sender, RoutedEventArgs e)
    {
        if (!_jsonMode) return;
        if (_selectedModPath is not null && !_model.IsDemo && File.Exists(_selectedModPath))
        {
            try { BuildModForm(_selectedModPath, ModJsonEditor.Text); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            { ModEditorError.Text = ex.Message; return; }
        }
        SetModEditorMode(false);
    }

    private void JsonModeClicked(object? sender, RoutedEventArgs e)
    {
        if (_jsonMode) return;
        if (_modEditor is not null && !_model.IsDemo)
        {
            try { ModJsonEditor.Text = _modEditor.Render(FormValues()); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            { ModEditorError.Text = ex.Message; return; }
        }
        SetModEditorMode(true);
    }

    private void SetModEditorMode(bool json)
    {
        _jsonMode = json;
        ModFormScroll.IsVisible = !json;
        ModJsonEditor.IsVisible = json;
        FormModeButton.Classes.Set("primary", !json);
        JsonModeButton.Classes.Set("primary", json);
        ModEditorError.Text = "";
    }

    private async void SaveModSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (_model.IsDemo) { _model.Report(AppTranslations.Get("status.demo_no_mod_save")); return; }
        if (_selectedModPath is null || !File.Exists(_selectedModPath)) return;
        try
        {
            if (_jsonMode) ModConfigEditor.SaveRaw(_selectedModPath, ModJsonEditor.Text ?? "");
            else if (_modEditor is not null) _modEditor.Save(FormValues());
            else return;
            _modDrafts.Remove(_selectedModPath);
            _invalidFormDrafts.Remove(_selectedModPath);
            ModEditorError.Text = "";
            _model.Report(AppTranslations.Get("status.mod_saved"));
            await _model.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { ModEditorError.Text = ex.Message; _model.Report(ex.Message); }
    }

    private async void RefreshGameClicked(object? sender, RoutedEventArgs e) => await _model.RefreshGameUpdateAsync();
    private async void RefreshBriefcaseClicked(object? sender, RoutedEventArgs e) => await _model.RefreshBriefcaseUpdateAsync();
    private async void RefreshRuntimeClicked(object? sender, RoutedEventArgs e) => await _model.RefreshRuntimeUpdateAsync();
    private async void UpdateGameClicked(object? sender, RoutedEventArgs e) { await _model.UpdateGameAsync(); await _model.RefreshGameUpdateAsync(); }
    private async void InstallBriefcaseClicked(object? sender, RoutedEventArgs e) { await _model.InstallBriefcaseAsync(); await _model.RefreshBriefcaseUpdateAsync(); }
    private async void InstallCppClicked(object? sender, RoutedEventArgs e) { await _model.InstallCppRuntimeAsync(); await _model.RefreshRuntimeUpdateAsync(); }
    private async void RefreshMarketplaceClicked(object? sender, RoutedEventArgs e) => await _model.RefreshMarketplaceAsync();
    private void OpenModDocumentationClicked(object? sender, RoutedEventArgs e) => OpenModLink(sender, mod => mod.DocumentationUrl);
    private void OpenModDiscordClicked(object? sender, RoutedEventArgs e) => OpenModLink(sender, mod => mod.DiscordUrl);
    private void OpenModHomepageClicked(object? sender, RoutedEventArgs e) => OpenModLink(sender, mod => mod.HomepageUrl);
    private void OpenInstalledModDocsClicked(object? sender, RoutedEventArgs e) => OpenInstalledModLink(mod => mod.DocumentationUrl);
    private void OpenInstalledModDiscordClicked(object? sender, RoutedEventArgs e) => OpenInstalledModLink(mod => mod.DiscordUrl);
    private void OpenInstalledModHomepageClicked(object? sender, RoutedEventArgs e) => OpenInstalledModLink(mod => mod.HomepageUrl);
    private void OpenInstalledModLink(Func<ModRow, string?> selector)
    {
        if (ModsList.SelectedItem is ModRow selected) OpenExternalUrl(selector(selected));
    }
    private void OpenModLink(object? sender, Func<MarketplaceMod, string?> selector)
    {
        if (sender is not Button { DataContext: MarketplaceMod mod }) return;
        OpenExternalUrl(selector(mod));
    }
    private void OpenExternalUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { _model.Report(error.Message); }
    }
    private async void InstallMarketplaceClicked(object? sender, RoutedEventArgs e)
    {
        if (MarketplaceList.SelectedItem is MarketplaceMod mod)
        {
            await _model.InstallMarketplaceModAsync(mod);
            LoadSettings();
        }
    }

    private async void EnableModClicked(object? sender, RoutedEventArgs e) => await ChangeModStateAsync(true);
    private async void DisableModClicked(object? sender, RoutedEventArgs e) => await ChangeModStateAsync(false);

    private async Task ChangeModStateAsync(bool enabled)
    {
        if (ModsList.SelectedItem is not ModRow mod || !_model.CanManageMods || !mod.CanToggle) return;
        await _model.SetModEnabledAsync(mod, enabled);
        SelectMod(mod.Name);
        LoadSettings();
    }

    private async void UninstallModClicked(object? sender, RoutedEventArgs e)
    {
        if (ModsList.SelectedItem is not ModRow mod || !_model.CanManageMods || !mod.CanToggle) return;
        if (!await ConfirmModUninstallAsync(mod)) return;
        _modDrafts.Remove(mod.ConfigPath);
        _invalidFormDrafts.Remove(mod.ConfigPath);
        await _model.UninstallModAsync(mod);
        LoadSettings();
    }

    private void SelectMod(string name)
    {
        var index = _model.Mods.ToList().FindIndex(mod => mod.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) ModsList.SelectedIndex = index;
    }

    private async Task<bool> ConfirmModUninstallAsync(ModRow mod)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(bool choice)
        {
            if (!result.TrySetResult(choice)) return;
            HideModal();
        }
        var cancel = new Button { Content = AppTranslations.Get("dialog.cancel") };
        var uninstall = new Button { Content = AppTranslations.Get("mod.uninstall") };
        uninstall.Classes.Add("danger");
        cancel.Click += (_, _) => Finish(false);
        uninstall.Click += (_, _) => Finish(true);
        var content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("dialog.uninstall_mod"), FontSize = 21,
                    FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = mod.Name, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = AppTranslations.Get("dialog.uninstall_mod_explanation"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, uninstall } }
            }
        };
        if (!ShowModal(content, 460, () => Finish(false))) return false;
        return await result.Task;
    }
    private void SettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (ModalLayer.IsVisible) return;
        var panel = new SettingsPanel { DataContext = _model };
        panel.CloseRequested += (_, _) => HideModal();
        panel.ConfirmStopServersAsync = ConfirmDataMoveAsync;
        ShowModal(panel, 840, HideModal, tall: true);
    }

    private async Task<bool> ConfirmDataMoveAsync(string destination, IReadOnlyList<string> serverNames)
    {
        if (ConfirmationLayer.IsVisible) return false;
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(bool accepted)
        {
            if (!result.TrySetResult(accepted)) return;
            ConfirmationLayer.IsVisible = false;
            ConfirmationContent.Content = null;
            _dismissConfirmation = null;
            if (ModalLayer.IsVisible) ModalLayer.Focus();
        }

        var cancel = new Button { Content = AppTranslations.Get("dialog.cancel") };
        var proceed = new Button { Content = AppTranslations.Get("settings.stop_and_move") };
        proceed.Classes.Add("danger");
        cancel.Click += (_, _) => Finish(false);
        proceed.Click += (_, _) => Finish(true);
        var names = new StackPanel { Spacing = 5 };
        foreach (var name in serverNames)
            names.Children.Add(new TextBlock { Text = "• " + name, TextWrapping = TextWrapping.Wrap });
        ConfirmationContent.Content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("settings.confirm_move_title"), FontSize = 21,
                    FontWeight = FontWeight.Bold },
                new TextBlock { Text = AppTranslations.Get("settings.confirm_move_explanation"),
                    TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { Content = names, MaxHeight = 150 },
                new TextBlock { Text = destination, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#ADA3B6")) },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, proceed } }
            }
        };
        _dismissConfirmation = () => Finish(false);
        ConfirmationLayer.IsVisible = true;
        Dispatcher.UIThread.Post(() => ConfirmationLayer.Focus());
        return await result.Task;
    }

    private async void KickClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PlayerRow player } || !_model.IsRunning) return;
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(bool confirmed)
        {
            if (!result.TrySetResult(confirmed)) return;
            HideModal();
        }
        var cancel = new Button { Content = AppTranslations.Get("dialog.cancel") };
        var kick = new Button { Content = AppTranslations.Get("dialog.kick_player") };
        kick.Classes.Add("danger");
        cancel.Click += (_, _) => Finish(false);
        kick.Click += (_, _) => Finish(true);
        var content = new StackPanel
        {
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("dialog.kick_player"), FontSize = 21, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = AppTranslations.Format("dialog.kick_question", "Disconnect {0} from this server?", player.Name),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, kick } }
            }
        };
        if (!ShowModal(content, 440, () => Finish(false))) return;
        if (await result.Task) await _model.KickPlayerAsync(player);
    }

    private bool ShowModal(Control content, double width, Action dismiss, double maxHeight = 540,
        bool expanded = false, bool tall = false)
    {
        if (ModalLayer.IsVisible) return false;
        ModalCard.Margin = expanded ? new Thickness(20) : tall ? new Thickness(0, 20) : new Thickness(0);
        ModalCard.HorizontalAlignment = expanded ? Avalonia.Layout.HorizontalAlignment.Stretch : Avalonia.Layout.HorizontalAlignment.Center;
        ModalCard.VerticalAlignment = expanded || tall ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Center;
        ModalCard.Width = expanded ? double.NaN : width;
        ModalCard.MaxHeight = expanded || tall ? double.PositiveInfinity : Math.Min(maxHeight, Math.Max(300, Bounds.Height - 48));
        ModalContent.Content = content;
        _dismissModal = dismiss;
        ModalLayer.IsVisible = true;
        Dispatcher.UIThread.Post(() => ModalLayer.Focus());
        return true;
    }

    private void HideModal()
    {
        _dismissConfirmation?.Invoke();
        ModalLayer.IsVisible = false;
        ModalContent.Content = null;
        _dismissModal = null;
    }

    private async void CopyPlayerIdClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PlayerRow player } || !player.HasNetworkId) return;
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(player.NetworkId);
            _model.Report(AppTranslations.Get("status.network_copied"));
        }
    }
}
