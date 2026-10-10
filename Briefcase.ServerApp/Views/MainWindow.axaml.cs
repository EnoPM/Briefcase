using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    private readonly MainViewModel _model;
    private readonly DispatcherTimer _reconciliationTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, SchemaFieldControl> _settingControls = new(StringComparer.OrdinalIgnoreCase);
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
        _model.LiveStateChanged += OnLiveStateChanged;
        _model.PlayerTeams.CollectionChanged += (_, _) => CancelPlayerCardDrag();
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.SelectedServer))
            {
                CancelPlayerCardDrag();
                if (ModEditorCard.IsVisible) HideModal();
                CaptureModDraft();
                ClearConfigurationFields();
                ModConfigurationFields.Children.Clear();
                _settingControls.Clear();
                _modInputs.Clear();
                _modEditor = null;
                _selectedModPath = null;
                SaveModButton.IsEnabled = false;
                SelectedModTitle.Text = AppTranslations.Get("mod.select_title");
                SelectedModPath.Text = "";
                CoreModHint.IsVisible = false;
                ModJsonEditor.Text = "";
                var selected = _model.SelectedServer;
                if (_model.IsDemo || selected is null)
                {
                    LoadSettings();
                    BalancingView.Load(selected, _model.IsDemo, _model.Report);
                }
                else Dispatcher.UIThread.Post(() => _ = LoadSelectedServerAsync(selected));
            }
            if (args.PropertyName == nameof(MainViewModel.IsRunning) && !_model.IsRunning &&
                ServerTabs.SelectedItem == PlayersTab) ServerTabs.SelectedIndex = 0;
        };
        LoadSettings();
        BalancingView.Load(_model.SelectedServer, _model.IsDemo, _model.Report);
        BalancingView.Saved += async (_, _) => await _model.RefreshAsync();
        _reconciliationTimer.Tick += async (_, _) => await _model.RefreshAsync();
        Opened += async (_, _) =>
        {
            if (!_model.IsDemo) _reconciliationTimer.Start();
            await _model.RefreshAsync();
            if (!_model.IsDemo)
            {
                _ = _model.CheckUpdatesAsync();
                _ = RefreshMarketplaceAndSettingsAsync();
                if (!_model.Registry.OnboardingCompleted && _model.Servers.Count == 0)
                    Dispatcher.UIThread.Post(ShowFirstRun);
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
            if (ModalContent.Content is not FirstRunPanel) _dismissModal?.Invoke();
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
            if (_model.SelectedServer == selected)
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            _model.CompleteSelectionLoading(selected);
            if (_model.SelectedServer == selected)
            {
                _ = _model.CheckUpdatesAsync();
                _ = RefreshMarketplaceAndSettingsAsync();
            }
        }
    }

    private async void AddServerClicked(object? sender, RoutedEventArgs e) =>
        await OpenAddServerAsync();

    private async Task OpenAddServerAsync(bool preferExisting = false)
    {
        if (_model.IsDemo) { _model.Report(AppTranslations.Get("status.demo_no_add")); return; }
        if (_model.IsBusy || ModalLayer.IsVisible) return;
        var result = new TaskCompletionSource<ServerAddChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(ServerAddChoice? choice)
        {
            if (!result.TrySetResult(choice)) return;
            HideModal();
        }
        var panel = new AddServerPanel(_model.Installations, preferExisting);
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

    private void ShowFirstRun()
    {
        if (_model.IsDemo || _model.Registry.OnboardingCompleted || _model.Servers.Count > 0 || ModalLayer.IsVisible)
            return;
        var panel = new FirstRunPanel(_model);
        void Finish(bool openServerDialog, bool preferExisting = false)
        {
            try { _model.Registry.CompleteOnboarding(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _model.Report(error.Message);
                return;
            }
            HideModal();
            if (openServerDialog) _ = OpenAddServerAsync(preferExisting);
        }
        panel.LaterRequested += () => Finish(false);
        panel.CreateRequested += () => Finish(true);
        panel.ImportRequested += () => Finish(true, preferExisting: true);
        ShowModal(panel, 590, panel.TryDismiss);
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
    private void OpenServerConfigurationClicked(object? sender, RoutedEventArgs e) => OpenConfigurationTab("Server");
    private void OpenPlayersClicked(object? sender, RoutedEventArgs e) => ServerTabs.SelectedItem = PlayersTab;
    private void OpenMapsConfigurationClicked(object? sender, RoutedEventArgs e) => OpenConfigurationTab("Maps");
    private void OpenGameModeConfigurationClicked(object? sender, RoutedEventArgs e) => OpenConfigurationTab("GameMode");

    private void OpenConfigurationTab(string id)
    {
        ConfigurationTabs.SelectedItem = ConfigurationTabs.Items.OfType<TabItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, id, StringComparison.OrdinalIgnoreCase))
            ?? ConfigurationTabs.Items.OfType<TabItem>().FirstOrDefault();
        ServerTabs.SelectedItem = ConfigurationTab;
    }

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
                        var control = new SchemaFieldControl(schema, spec, values[spec.Key], values);
                        control.ValueChanged += (_, _) => UpdateReferencedBounds(schema);
                        _settingControls[spec.Key] = control;
                        panel.Children.Add(control);
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
            foreach (var (key, control) in _settingControls) values[key] = control.Value;
            var referencedFields = schema.Fields.Where(field =>
                field.MaximumReference is not null || field.MaximumByChoice is not null).ToArray();
            for (var pass = 0; pass < referencedFields.Length; pass++)
            {
                var changed = false;
                foreach (var field in referencedFields)
                {
                    var bound = schema.EffectiveMaximum(field, values);
                    if (bound is null || !_settingControls.TryGetValue(field.Key, out var control)) continue;
                    control.SetMaximum(bound.Value);
                    var current = control.Value;
                    if (values[field.Key] == current) continue;
                    values[field.Key] = current;
                    changed = true;
                }
                if (!changed) break;
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
                _model.ReportTransient(AppTranslations.Get(_model.IsDemo ? "status.demo_preview_only" : "status.rotation_saved"));
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
                values[spec.Key] = _settingControls[spec.Key].Value;
            ServerIniEditor.Save(location!.ConfigFile, values, _mapRotationDraft,
                FormSchema.LoadForServer(location));
            _model.ReportTransient(AppTranslations.Get("status.config_saved"));
            await _model.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or KeyNotFoundException)
        { _model.Report(ex.Message); }
    }

    private void OpenInstalledModSettingsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModRow mod } || !mod.HasConfiguration || ModalLayer.IsVisible) return;
        CaptureModDraft();
        LoadModEditor(mod);
        ModalCard.IsVisible = false;
        ModEditorCard.IsVisible = true;
        ModalLayer.IsVisible = true;
        _dismissModal = HideModal;
        Dispatcher.UIThread.Post(() => ModalLayer.Focus());
    }

    private void CloseInstalledModSettingsClicked(object? sender, RoutedEventArgs e) => HideModal();

    private void LoadModEditor(ModRow mod)
    {
        _modEditor = null;
        _modInputs.Clear();
        ModConfigurationFields.Children.Clear();
        ModEditorError.Text = "";
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
                        if (pending.TryGetValue(entry.Path, out var raw) && input is TextBox text) text.Text = raw;
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
            Control input;
            Control display;
            if (entry.Slider is { } range)
            {
                var value = decimal.TryParse(entry.Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var parsed) ? parsed : range.Maximum;
                var slider = new Slider
                {
                    Minimum = (double)range.Minimum, Maximum = (double)range.Maximum,
                    TickFrequency = (double)range.Step, IsSnapToTickEnabled = true,
                    Value = (double)range.Snap(value),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                var current = new TextBlock
                {
                    Text = range.Snap((decimal)slider.Value).ToString(CultureInfo.InvariantCulture),
                    MinWidth = 28, TextAlignment = Avalonia.Media.TextAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                slider.ValueChanged += (_, _) => current.Text =
                    range.Snap((decimal)slider.Value).ToString(CultureInfo.InvariantCulture);
                var sliderLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                sliderLayout.Children.Add(slider);
                Grid.SetColumn(current, 1);
                sliderLayout.Children.Add(current);
                input = slider;
                display = sliderLayout;
            }
            else
                input = display = entry.Kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                    ? new CheckBox { IsChecked = entry.Value == "true" }
                    : new TextBox { Text = entry.Value };
            _modInputs[entry] = input;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 16 };
            line.Children.Add(new TextBlock { Text = entry.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            Grid.SetColumn(display, 1);
            line.Children.Add(display);
            ModConfigurationFields.Children.Add(line);
        }
    }

    private IReadOnlyDictionary<ModConfigValue, string> FormValues() => _modInputs.ToDictionary(pair => pair.Key,
        pair => pair.Value switch
        {
            CheckBox box => box.IsChecked == true ? "true" : "false",
            Slider slider when pair.Key.Slider is { } range =>
                range.Snap((decimal)slider.Value).ToString(CultureInfo.InvariantCulture),
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
            _invalidFormDrafts[_selectedModPath] = _modInputs.ToDictionary(pair => pair.Key.Path,
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
            var savedPath = _selectedModPath;
            HideModal();
            _modDrafts.Remove(savedPath);
            _invalidFormDrafts.Remove(savedPath);
            _model.ReportTransient(AppTranslations.Get("status.mod_saved"));
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
    private async Task RefreshMarketplaceAndSettingsAsync(bool announce = false)
    {
        var selected = _model.SelectedServer;
        var revision = _model.FormAssetsRevision;
        await _model.RefreshMarketplaceAsync(announce);
        if (_model.FormAssetsRevision != revision && _model.SelectedServer == selected)
            LoadSettings();
    }
    private async void RefreshMarketplaceClicked(object? sender, RoutedEventArgs e) =>
        await RefreshMarketplaceAndSettingsAsync(announce: true);
    private void OpenModDocumentationClicked(object? sender, RoutedEventArgs e) => OpenModLink(sender, mod => mod.DocumentationUrl);
    private void OpenModDiscordClicked(object? sender, RoutedEventArgs e) => OpenModLink(sender, mod => mod.DiscordUrl);
    private void OpenModHomepageClicked(object? sender, RoutedEventArgs e) => OpenModLink(sender, mod => mod.HomepageUrl);
    private void OpenInstalledModDocsClicked(object? sender, RoutedEventArgs e) => OpenInstalledModLink(sender, mod => mod.DocumentationUrl);
    private void OpenInstalledModDiscordClicked(object? sender, RoutedEventArgs e) => OpenInstalledModLink(sender, mod => mod.DiscordUrl);
    private void OpenInstalledModHomepageClicked(object? sender, RoutedEventArgs e) => OpenInstalledModLink(sender, mod => mod.HomepageUrl);
    private void OpenInstalledModLink(object? sender, Func<ModRow, string?> selector)
    {
        if (sender is Button { DataContext: ModRow mod }) OpenExternalUrl(selector(mod));
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
        if (sender is not Button { DataContext: MarketplaceMod mod } || !mod.CanApply) return;
        await ApplyMarketplaceModAsync(mod);
    }

    private async void UpdateInstalledModClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModRow { UpdateCandidate: { } candidate } installed } ||
            !_model.Mods.Contains(installed) || !_model.CanManageMods) return;
        await ApplyMarketplaceModAsync(candidate);
    }

    private async Task ApplyMarketplaceModAsync(MarketplaceMod mod)
    {
        if (!_model.CanManageMods)
        {
            if (_model.IsRunning) _model.Report(AppTranslations.Get("mod.stop_to_manage"));
            return;
        }
        if (!ServerLocation.TryCreate(_model.SelectedServer, out var location, out var error))
        { _model.Report(error); return; }
        var selected = _model.SelectedServer;
        if (!ModMarketplace.BriefcaseRuntimeInstalled(location!))
        {
            if (!await ConfirmBriefcaseForModAsync(mod)) return;
            if (_model.SelectedServer != selected || !_model.CanManageMods) return;
            await _model.InstallBriefcaseAsync();
            if (!ModMarketplace.BriefcaseRuntimeInstalled(location!)) return;
            await _model.RefreshBriefcaseUpdateAsync();
        }
        if (_model.SelectedServer != selected || !_model.CanManageMods) return;
        await _model.InstallMarketplaceModAsync(mod);
        LoadSettings();
    }

    private async Task<bool> ConfirmBriefcaseForModAsync(MarketplaceMod mod)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(bool confirmed)
        {
            if (!result.TrySetResult(confirmed)) return;
            HideModal();
        }
        var cancel = new Button { Content = AppTranslations.Get("dialog.cancel") };
        var install = new Button { Content = AppTranslations.Get("updates.install_briefcase") };
        install.Classes.Add("success");
        cancel.Click += (_, _) => Finish(false);
        install.Click += (_, _) => Finish(true);
        var content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("mod.briefcase_required_title"), FontSize = 21,
                    FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = AppTranslations.Format("mod.briefcase_required_explanation",
                    "Briefcase is required to install {0}. Install Briefcase on this server and then continue with the mod?",
                    mod.Name), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, install } }
            }
        };
        if (!ShowModal(content, 480, () => Finish(false))) return false;
        return await result.Task;
    }

    private async void EnableModClicked(object? sender, RoutedEventArgs e) => await ChangeModStateAsync(sender, true);
    private async void DisableModClicked(object? sender, RoutedEventArgs e) => await ChangeModStateAsync(sender, false);

    private async Task ChangeModStateAsync(object? sender, bool enabled)
    {
        if (sender is not Button { DataContext: ModRow mod } || !_model.CanManageMods || !mod.CanToggle) return;
        await _model.SetModEnabledAsync(mod, enabled);
        LoadSettings();
    }

    private async void UninstallModClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ModRow mod } || !_model.CanManageMods || !mod.CanToggle) return;
        if (!await ConfirmModUninstallAsync(mod)) return;
        _modDrafts.Remove(mod.ConfigPath);
        _invalidFormDrafts.Remove(mod.ConfigPath);
        await _model.UninstallModAsync(mod);
        LoadSettings();
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
        panel.ConfirmDeleteProfileAsync = ConfirmProfileRemovalAsync;
        panel.RestartRequired += ShowRestartConfirmation;
        ShowModal(panel, 840, HideModal, tall: true);
    }

    private void ShowRestartConfirmation(string reason)
    {
        if (ConfirmationLayer.IsVisible) return;
        void Dismiss()
        {
            ConfirmationLayer.IsVisible = false;
            ConfirmationContent.Content = null;
            _dismissConfirmation = null;
            if (ModalLayer.IsVisible) ModalLayer.Focus();
        }
        var later = new Button { Content = AppTranslations.Get("settings.restart_later") };
        var now = new Button { Content = AppTranslations.Get("settings.restart_now") };
        now.Classes.Add("primary");
        later.Click += (_, _) => Dismiss();
        now.Click += (_, _) =>
        {
            Dismiss();
            try
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
                    throw new InvalidOperationException("The desktop application cannot be restarted.");
                AppRestartService.StartLauncher();
                desktop.Shutdown();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          InvalidOperationException or System.ComponentModel.Win32Exception)
            { _model.Report(error.Message); }
        };
        ConfirmationContent.Content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("settings.restart_title"), FontSize = 21,
                    FontWeight = FontWeight.Bold },
                new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = AppTranslations.Get("settings.restart_explanation"),
                    TextWrapping = TextWrapping.Wrap, Foreground = Brush("#ADA3B6") },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { later, now } }
            }
        };
        _dismissConfirmation = Dismiss;
        ConfirmationLayer.IsVisible = true;
        Dispatcher.UIThread.Post(() => ConfirmationLayer.Focus());
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

    private async Task<ProfileRemovalChoice> ConfirmProfileRemovalAsync(AppProfile profile)
    {
        if (ConfirmationLayer.IsVisible) return ProfileRemovalChoice.Cancel;
        var result = new TaskCompletionSource<ProfileRemovalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Finish(ProfileRemovalChoice choice)
        {
            if (!result.TrySetResult(choice)) return;
            ConfirmationLayer.IsVisible = false;
            ConfirmationContent.Content = null;
            _dismissConfirmation = null;
            if (ModalLayer.IsVisible) ModalLayer.Focus();
        }

        var cancel = new Button { Content = AppTranslations.Get("dialog.cancel") };
        var keep = new Button { Content = AppTranslations.Get("settings.keep_profile_files") };
        var delete = new Button { Content = AppTranslations.Get("settings.delete_profile_files") };
        delete.Classes.Add("danger");
        cancel.Click += (_, _) => Finish(ProfileRemovalChoice.Cancel);
        keep.Click += (_, _) => Finish(ProfileRemovalChoice.KeepFiles);
        delete.Click += (_, _) => Finish(ProfileRemovalChoice.DeleteFiles);
        ConfirmationContent.Content = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = AppTranslations.Get("settings.delete_profile_title"), FontSize = 21,
                    FontWeight = FontWeight.Bold },
                new TextBlock { Text = AppTranslations.Format("settings.delete_profile_explanation",
                    "Remove profile {0} from Briefcase? Choose whether to keep its data folder.", profile.Name),
                    TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = profile.DataDirectory, TextWrapping = TextWrapping.Wrap,
                    Foreground = Brush("#ADA3B6") },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, keep, delete } }
            }
        };
        _dismissConfirmation = () => Finish(ProfileRemovalChoice.Cancel);
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
        ModalCard.IsVisible = true;
        ModEditorCard.IsVisible = false;
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
        if (ModEditorCard.IsVisible)
        {
            CaptureModDraft();
            _selectedModPath = null;
            _modEditor = null;
            _modInputs.Clear();
        }
        ModalLayer.IsVisible = false;
        ModEditorCard.IsVisible = false;
        ModalCard.IsVisible = true;
        ModalContent.Content = null;
        _dismissModal = null;
    }

    private async void CopyPlayerIdClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PlayerRow player } || !player.HasNetworkId) return;
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(player.NetworkId);
            _model.ReportTransient(AppTranslations.Get("status.network_copied"));
        }
    }

    private async void CopyNetworkAddressClicked(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanCopyNetworkAddress || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(_model.NetworkStatus.Address);
        _model.ReportTransient(AppTranslations.Get("network.address_copied"));
    }
}
