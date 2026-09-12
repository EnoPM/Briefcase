using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Briefcase.AvaloniaUi;
using Briefcase.ClientModApi;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using ModScope = Briefcase.ManagedHost.ConfigurationRegistry.ModScope;

namespace Briefcase.AvaloniaMenu;

internal sealed class AvaloniaConfigurationView : UserControl, IDisposable
{
    private const string ClientHomeId = "$client-home";
    private const string FrameworkTabId = "$briefcase";
    private const string ServerHomeId = "$server-home";
    private const string ServerPanelPrefix = "$server-panel:";
    private const string InstalledModPrefix = "$installed-mod:";

    private readonly ConfigurationRegistry _registry;
    private readonly StackPanel _navigation = new() { Spacing = 4 };
    private readonly ContentControl _content = new();
    private readonly AvaloniaComponentRenderer _componentRenderer;
    private readonly List<RenderedComponent> _renderedComponents = [];
    private readonly Dictionary<string, TextBlock> _draftIndicators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private long _observedUiRevision;
    private string _selectedId;
    private bool _serverView;
    private bool _disposed;

    public AvaloniaConfigurationView(ConfigurationRegistry registry)
    {
        _registry = registry;
        _componentRenderer = new AvaloniaComponentRenderer(registry.ReportClientUiError);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshLiveComponents();
        AttachedToVisualTree += (_, _) => _refreshTimer.Start();
        DetachedFromVisualTree += (_, _) => _refreshTimer.Stop();

        _serverView = string.Equals(
            registry._document.Window.SelectedView,
            "Server",
            StringComparison.OrdinalIgnoreCase);
        _selectedId = _serverView
            ? ServerHomeId
            : string.IsNullOrWhiteSpace(registry._document.Window.SelectedModId)
                ? ClientHomeId
                : registry._document.Window.SelectedModId;

        Content = BuildRoot();
        RefreshAll();
    }

    private Control BuildRoot()
    {
        var sidebar = new ScrollViewer
        {
            Content = _navigation,
            Padding = new Thickness(12, 10),
            HorizontalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var navigationHost = new Border
        {
            Background = BriefcaseTheme.Navigation,
            BorderBrush = BriefcaseTheme.Border,
            BorderThickness = new Thickness(0, 1, 1, 0),
            Child = sidebar
        };

        var contentHost = new Border
        {
            Padding = new Thickness(24, 20, 24, 0),
            Background = BriefcaseTheme.Surface,
            Child = _content
        };
        Grid.SetColumn(contentHost, 1);

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions(
                $"{BriefcaseTheme.SidebarWidth},*"),
            Children = { navigationHost, contentHost }
        };
    }

    private void RefreshAll()
    {
        if (_disposed) return;
        DisposeRenderedComponents();
        _observedUiRevision = _registry.UiRevision;
        _navigation.Children.Clear();
        _draftIndicators.Clear();

        if (_serverView)
            RefreshServerNavigation();
        else
            RefreshClientNavigation();
    }

    private void RefreshClientNavigation()
    {
        var pages = SnapshotClientPages();
        if (_selectedId != ClientHomeId && _selectedId != FrameworkTabId &&
            !pages.Any(page => string.Equals(
                page.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
            _selectedId = FrameworkTabId;

        AddNavigationButton(
            "Client", ClientHomeId, UiIcon.Client, root: true,
            () => SelectClientPage(ClientHomeId),
            activeOverride: true);
        AddNavigationButton(
            "Server", ServerHomeId, UiIcon.Server, root: true,
            () =>
            {
                _serverView = true;
                _selectedId = ServerHomeId;
                _registry.RememberSelectedView("Server");
                RefreshAll();
            }, activeOverride: false);
        AddNavigationButton(
            "Mods", FrameworkTabId, UiIcon.Mods, root: false,
            () => SelectClientPage(FrameworkTabId));

        if (pages.Length > 0)
        {
            AddNavigationGroup("MODS CLIENT");
            foreach (var page in pages)
            {
                AddNavigationButton(
                    page.Title, page.Id, UiIcon.Settings, root: false,
                    () => SelectClientPage(page.Id),
                    indented: true,
                    draftId: page.Scope?.Info.Id);
            }
        }

        ShowSelectedClientPage(pages);
    }

    private void RefreshServerNavigation()
    {
        var panels = SnapshotServerPanels();
        if (_selectedId != ServerHomeId && !panels.Any(panel => string.Equals(
                panel.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
            _selectedId = ServerHomeId;

        AddNavigationButton(
            "Client", ClientHomeId, UiIcon.Client, root: true,
            () =>
            {
                _serverView = false;
                _selectedId = ClientHomeId;
                _registry.RememberSelectedView("Client");
                RefreshAll();
            }, activeOverride: false);
        AddNavigationButton(
            "Server", ServerHomeId, UiIcon.Server, root: true,
            () => SelectServerPage(ServerHomeId),
            activeOverride: true);
        if (panels.Length > 0)
        {
            AddNavigationGroup("SERVER");
            foreach (var panel in panels)
            {
                AddNavigationButton(
                    panel.Name, panel.Id, UiIcon.Settings, root: false,
                    () => SelectServerPage(panel.Id),
                    indented: true);
            }
        }

        ShowSelectedServerPage(panels);
    }

    private void SelectClientPage(string id)
    {
        _serverView = false;
        _selectedId = id;
        _registry.RememberSelectedView("Client");
        if (id != ClientHomeId) _registry.RememberSelectedMod(id);
        RefreshAll();
    }

    private void SelectServerPage(string id)
    {
        _serverView = true;
        _selectedId = id;
        _registry.RememberSelectedView("Server");
        RefreshAll();
    }

    private void AddNavigationGroup(string title)
    {
        _navigation.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = BriefcaseTheme.Muted,
            Margin = new Thickness(12, 16, 8, 4),
            LetterSpacing = 1.1
        });
    }

    private void AddNavigationButton(
        string title,
        string id,
        UiIcon icon,
        bool root,
        Action action,
        bool indented = false,
        string? draftId = null,
        bool? activeOverride = null)
    {
        var active = activeOverride ?? string.Equals(
            id, _selectedId, StringComparison.OrdinalIgnoreCase);
        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var dirty = new TextBlock
        {
            Text = "●",
            FontSize = 10,
            Foreground = BriefcaseTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = draftId is not null && _registry.HasConfigurationDraft(draftId)
        };
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Children =
            {
                AvaloniaIcons.Create(
                    icon,
                    root ? 21 : 17,
                    active ? BriefcaseTheme.Accent : BriefcaseTheme.Muted),
                label,
                dirty
            }
        };
        Grid.SetColumn(label, 1);
        label.Margin = new Thickness(10, 0, 8, 0);
        Grid.SetColumn(dirty, 2);

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = indented ? new Thickness(10, 0, 0, 0) : default
        };
        button.Classes.Add("briefcase-navigation");
        if (root) button.Classes.Add("briefcase-navigation-root");
        if (active) button.Classes.Add("briefcase-navigation-active");
        button.Click += (_, _) => action();
        _navigation.Children.Add(button);
        if (draftId is not null) _draftIndicators[draftId] = dirty;
    }

    private ClientPage[] SnapshotClientPages()
    {
        ModScope[] scopes;
        lock (_registry._gate)
            scopes = _registry._scopes.Values.ToArray();
        var scopesById = scopes.ToDictionary(
            scope => scope.Info.Id,
            StringComparer.OrdinalIgnoreCase);
        var result = new List<ClientPage>();
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var status in _registry._modControl?.SnapshotInstalledMods() ?? [])
        {
            ModScope? scope = null;
            if (!string.IsNullOrWhiteSpace(status.Id))
                scopesById.TryGetValue(status.Id, out scope);
            var id = !string.IsNullOrWhiteSpace(status.Id)
                ? status.Id
                : InstalledModPrefix + status.FileName;
            result.Add(new ClientPage(id, status.DisplayName, scope, status));
            if (scope is not null) represented.Add(scope.Info.Id);
        }

        foreach (var scope in scopes.Where(scope => !represented.Contains(scope.Info.Id)))
            result.Add(new ClientPage(scope.Info.Id, scope.Info.Name, scope, null));

        return result
            .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private ServerPanel[] SnapshotServerPanels()
    {
        ModScope[] scopes;
        lock (_registry._gate) scopes = _registry._scopes.Values.ToArray();
        return scopes
            .SelectMany(scope => scope.SnapshotServerUiPanels().Select(panel =>
                new ServerPanel(
                    ServerPanelPrefix + scope.Info.Id + ":" + panel.Name,
                    panel.Name,
                    panel.Content)))
            .OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ShowSelectedClientPage(IReadOnlyList<ClientPage> pages)
    {
        if (_selectedId == ClientHomeId)
        {
            _content.Content = BuildClientHomePage(pages);
            return;
        }
        if (_selectedId == FrameworkTabId)
        {
            _content.Content = BuildFrameworkPage();
            return;
        }

        var page = pages.FirstOrDefault(candidate => string.Equals(
            candidate.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
        _content.Content = page?.Scope is { } scope
            ? BuildModPage(scope)
            : page?.Status is { } status
                ? BuildInstalledModPage(status)
                : MessageCard("Unavailable", "This mod is no longer installed.");
    }

    private void ShowSelectedServerPage(IReadOnlyList<ServerPanel> panels)
    {
        if (_selectedId == ServerHomeId)
        {
            _content.Content = BuildServerHomePage(panels);
            return;
        }
        var panel = panels.FirstOrDefault(candidate => string.Equals(
            candidate.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
        if (panel is null)
        {
            _content.Content = MessageCard(
                "Server tools unavailable",
                "No Briefcase server administration panel is currently registered.");
            return;
        }

        var stack = Page(panel.Name, "Remote Briefcase server management");
        stack.Children.Add(RenderComponents(panel.Content));
        _content.Content = ScrollPage(stack);
    }

    private Control BuildClientHomePage(IReadOnlyCollection<ClientPage> pages)
    {
        var statuses = _registry._modControl?.SnapshotInstalledMods() ?? [];
        var loaded = statuses.Count(status => status.Loaded);
        var errors = statuses.Count(status => status.LastError is not null);
        var cards = new ResponsiveCardPanel();

        cards.Children.Add(BriefcaseControls.Card(
            "Briefcase is ready",
            "The client framework is running and ready to manage your mods.",
            new Control[]
            {
                StatusBadge(errors == 0 ? "No mod errors" : $"{errors} mod error(s)",
                    errors == 0 ? UiStatusTone.Success : UiStatusTone.Error),
                DetailLine("Installed version", FrameworkVersion()),
                DetailLine("Active mods", $"{loaded} of {statuses.Length}"),
                MakeButton("Manage mods", () => SelectClientPage(FrameworkTabId), UiIcon.Mods)
            }));

        cards.Children.Add(BriefcaseControls.Card(
            "Client mods",
            "Every installed mod has a dedicated page, including mods using automatic configuration.",
            new Control[]
            {
                DetailLine("Available pages", pages.Count.ToString(CultureInfo.InvariantCulture)),
                DetailLine("Configuration", "Drafts are applied one page at a time"),
                new TextBlock
                {
                    Text = "A violet dot in the navigation marks a page with unapplied changes.",
                    Foreground = BriefcaseTheme.Muted,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5
                }
            }));

        cards.Children.Add(BriefcaseControls.Card(
            "Server management",
            "Open the server workspace to connect, configure players and manage server-side mods.",
            new Control[]
            {
                DetailLine("Workspace", "Remote administration"),
                MakeButton("Open server workspace", () =>
                {
                    _serverView = true;
                    _selectedId = ServerHomeId;
                    _registry.RememberSelectedView("Server");
                    RefreshAll();
                }, UiIcon.Server)
            }));

        var stack = Page("Client", "Your Briefcase client workspace");
        stack.Children.Add(cards);
        return ScrollPage(stack);
    }

    private Control BuildServerHomePage(IReadOnlyList<ServerPanel> panels)
    {
        var cards = new ResponsiveCardPanel();
        var available = panels.Count > 0;
        cards.Children.Add(BriefcaseControls.Card(
            "Remote administration",
            "Manage a Briefcase-enabled dedicated server from the game client.",
            new Control[]
            {
                StatusBadge(
                    available ? "Administration tools available" : "Waiting for server tools",
                    available ? UiStatusTone.Success : UiStatusTone.Warning),
                DetailLine("Available sections", panels.Count.ToString(CultureInfo.InvariantCulture)),
                available
                    ? MakeButton("Open administration", () => SelectServerPage(panels[0].Id),
                        UiIcon.Server)
                    : new TextBlock
                    {
                        Text = "Load the server administration client mod to expose this workspace.",
                        Foreground = BriefcaseTheme.Muted,
                        TextWrapping = TextWrapping.Wrap
                    }
            }));

        cards.Children.Add(BriefcaseControls.Card(
            "Server workspace",
            "Server configuration, players and server mods stay separate from local client settings.",
            new Control[]
            {
                DetailLine("Client settings", "Unchanged"),
                DetailLine("Remote changes", "Applied by the selected server tool"),
                new TextBlock
                {
                    Text = "Connection and authentication details are owned by the server administration mod.",
                    Foreground = BriefcaseTheme.Muted,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5
                }
            }));

        var stack = Page("Server", "Remote dedicated-server workspace");
        stack.Children.Add(cards);
        return ScrollPage(stack);
    }

    private Control BuildFrameworkPage()
    {
        var stack = Page("Mods", "Install, update and control client mods");
        var control = _registry._modControl;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                MakeButton("Refresh", () =>
                {
                    control?.Refresh();
                    RefreshAll();
                }, UiIcon.Refresh),
                MakeButton("Open Mods folder", () =>
                {
                    if (control is not null)
                        _registry.OpenModsDirectory(control.ModsDirectory);
                }, UiIcon.Folder)
            }
        };
        stack.Children.Add(BriefcaseControls.Card(
            "Mod library",
            "Add or update managed mod DLLs in this directory.",
            new Control[]
            {
                new TextBlock
                {
                    Text = control?.ModsDirectory ?? "The mod controller is not ready.",
                    Foreground = BriefcaseTheme.Muted,
                    TextWrapping = TextWrapping.Wrap
                },
                actions
            }));

        if (control is null)
        {
            stack.Children.Add(MessageCard(
                "Mod controller unavailable",
                "The managed mod controller is not ready yet."));
            return ScrollPage(stack);
        }

        ModScope[] scopes;
        lock (_registry._gate) scopes = _registry._scopes.Values.ToArray();
        var mods = control.SnapshotInstalledMods();
        if (mods.Length == 0)
        {
            stack.Children.Add(MessageCard(
                "No mods installed",
                "Copy a compatible managed mod DLL into the Mods directory, then select Refresh."));
            return ScrollPage(stack);
        }

        var cards = new ResponsiveCardPanel();
        foreach (var mod in mods)
        {
            var scope = scopes.FirstOrDefault(candidate => string.Equals(
                candidate.Info.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            cards.Children.Add(BuildModCard(control, mod, scope));
        }
        stack.Children.Add(cards);
        return ScrollPage(stack);
    }

    private Control BuildModCard(
        IFrameworkModControl control,
        ManagedModStatus mod,
        ModScope? scope)
    {
        var hasDraft = scope is not null &&
                       _registry.HasConfigurationDraft(scope.Info.Id);
        var stopAllowed = mod.CanStop && !hasDraft;
        var stopReason = hasDraft
            ? "Apply or cancel this mod's pending configuration changes first."
            : mod.StopBlockReason;
        var enabled = new ToggleSwitch
        {
            IsChecked = mod.Enabled,
            Content = mod.DisplayName,
            FontWeight = FontWeight.SemiBold,
            IsEnabled = !(mod.Enabled && !stopAllowed)
        };
        if (!enabled.IsEnabled && !string.IsNullOrWhiteSpace(stopReason))
            ToolTip.SetTip(enabled, stopReason);
        enabled.IsCheckedChanged += (_, _) => RunModAction(() =>
            control.SetEnabled(mod.FileName, enabled.IsChecked == true));

        var state = mod.LastError is not null
            ? StatusBadge("Error", UiStatusTone.Error)
            : mod.Loaded
                ? StatusBadge("Loaded", UiStatusTone.Success)
                : StatusBadge("Unloaded", UiStatusTone.Neutral);
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(enabled);
        Grid.SetColumn(state, 1);
        heading.Children.Add(state);

        var children = new List<Control>
        {
            heading,
            new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(mod.Version)
                    ? mod.FileName
                    : $"{mod.FileName}  ·  {mod.Version}",
                Foreground = BriefcaseTheme.Muted,
                FontSize = 12.5
            }
        };
        if (mod.LastError is not null)
        {
            children.Add(new TextBlock
            {
                Text = mod.LastError,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BriefcaseTheme.Error,
                FontSize = 12.5
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (mod.Loaded)
        {
            buttons.Children.Add(ModActionButton(
                "Reload", stopAllowed, stopReason, () => control.Reload(mod.FileName)));
            buttons.Children.Add(ModActionButton(
                "Unload", stopAllowed, stopReason, () => control.Unload(mod.FileName)));
        }
        else
        {
            buttons.Children.Add(ModActionButton(
                "Load", true, null, () => control.Load(mod.FileName)));
        }
        children.Add(buttons);

        if (mod.Dependencies.Count > 0)
            children.Add(DetailLine("Requires", string.Join(", ", mod.Dependencies)));
        if (!string.IsNullOrWhiteSpace(mod.Description))
            children.Add(new TextBlock
            {
                Text = mod.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BriefcaseTheme.Muted,
                FontSize = 12.5
            });

        return BriefcaseControls.Card(null, null, children);

        Button ModActionButton(
            string label,
            bool available,
            string? reason,
            Action action)
        {
            var icon = label switch
            {
                "Load" => UiIcon.Play,
                "Reload" => UiIcon.Reload,
                "Unload" => UiIcon.Power,
                _ => (UiIcon?)null
            };
            var button = MakeButton(label, () => RunModAction(action), icon);
            button.IsEnabled = available;
            if (!available && !string.IsNullOrWhiteSpace(reason))
                ToolTip.SetTip(button, reason);
            return button;
        }
    }

    private Control BuildInstalledModPage(ManagedModStatus status)
    {
        var stack = Page(status.DisplayName, "Installed client mod");
        if (_registry._modControl is { } control)
            stack.Children.Add(BuildModCard(control, status, null));
        stack.Children.Add(MessageCard(
            "Configuration unavailable",
            status.Loaded
                ? "This mod does not currently expose configuration."
                : "Load the mod to make its configuration and custom interface available."));
        return ScrollPage(stack);
    }

    private Control BuildModPage(ModScope scope)
    {
        var draft = _registry.GetConfigurationDraft(scope);
        Border? actionBar = null;
        void DraftChanged()
        {
            if (actionBar is not null) actionBar.IsVisible = draft.IsDirty;
            UpdateDraftIndicators();
        }

        var stack = Page(
            scope.Info.Name,
            $"{scope.Info.Author}  ·  {scope.Info.Version}  ·  {scope.Info.Id}");
        if (!string.IsNullOrWhiteSpace(scope.Info.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = scope.Info.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = BriefcaseTheme.Muted,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        var entries = scope.SnapshotEntries();
        if (entries.Length > 0)
        {
            var cards = new ResponsiveCardPanel();
            foreach (var section in entries.GroupBy(
                         entry => entry.Section,
                         StringComparer.OrdinalIgnoreCase))
            {
                var controls = section
                    .Select(entry => BuildEntry(entry, draft, DraftChanged))
                    .ToArray();
                cards.Children.Add(BriefcaseControls.Card(section.Key, null, controls));
            }
            stack.Children.Add(cards);
        }

        var componentPanels = scope.SnapshotUiPanels();
        foreach (var panel in componentPanels)
            stack.Children.Add(RenderComponents(panel.Content));

        if (entries.Length == 0 && componentPanels.Length == 0)
            stack.Children.Add(MessageCard(
                "No settings",
                "This mod does not expose configurable settings."));

        actionBar = BuildActionBar(draft);
        actionBar.IsVisible = draft.IsDirty;
        var page = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        page.Children.Add(ScrollPage(stack));
        Grid.SetRow(actionBar, 1);
        page.Children.Add(actionBar);
        return page;
    }

    private Border BuildActionBar(ConfigurationPageDraft draft)
    {
        var text = new TextBlock
        {
            Text = "This page has unapplied changes.",
            Foreground = BriefcaseTheme.Text,
            VerticalAlignment = VerticalAlignment.Center
        };
        var error = new TextBlock
        {
            Foreground = BriefcaseTheme.Error,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        var cancel = MakeButton("Cancel", () =>
        {
            draft.Cancel();
            RefreshAll();
        });
        var apply = MakeButton("Apply changes", () =>
        {
            try
            {
                draft.Apply();
                RefreshAll();
            }
            catch (Exception exception)
            {
                _registry.ReportClientUiError(exception);
                error.Text = exception.GetBaseException().Message;
                error.IsVisible = true;
                UpdateDraftIndicators();
            }
        }, UiIcon.Save, primary: true);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, apply }
        };
        var feedback = new StackPanel
        {
            Spacing = 3,
            Children = { text, error }
        };
        Grid.SetColumn(actions, 1);
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { feedback, actions }
        };
        var bar = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Child = layout
        };
        bar.Classes.Add("briefcase-actionbar");
        return bar;
    }

    private static Control BuildEntry(
        IConfigurationEntry entry,
        ConfigurationPageDraft draft,
        Action changed) =>
        BriefcaseControls.SettingRow(
            entry.Key,
            entry.Description,
            CreateEditor(entry, draft, changed));

    private static Control CreateEditor(
        IConfigurationEntry entry,
        ConfigurationPageDraft draft,
        Action changed)
    {
        if (entry.ValueType == typeof(bool))
        {
            var control = new ToggleSwitch
            {
                IsChecked = (bool)draft.GetValue(entry),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            control.IsCheckedChanged += (_, _) =>
            {
                draft.SetValue(entry, control.IsChecked == true);
                changed();
            };
            return control;
        }
        if (entry.ValueType == typeof(int))
        {
            var control = Numeric(
                Convert.ToDecimal(draft.GetValue(entry), CultureInfo.InvariantCulture),
                entry.Minimum is null ? decimal.MinValue : Convert.ToDecimal(entry.Minimum),
                entry.Maximum is null ? decimal.MaxValue : Convert.ToDecimal(entry.Maximum),
                1,
                "0");
            control.ValueChanged += (_, _) =>
            {
                if (control.Value is not { } value) return;
                draft.SetValue(entry, decimal.ToInt32(value));
                changed();
            };
            return control;
        }
        if (entry.ValueType == typeof(float) || entry.ValueType == typeof(double))
        {
            var control = Numeric(
                Convert.ToDecimal(draft.GetValue(entry), CultureInfo.InvariantCulture),
                entry.Minimum is null ? decimal.MinValue : Convert.ToDecimal(entry.Minimum),
                entry.Maximum is null ? decimal.MaxValue : Convert.ToDecimal(entry.Maximum),
                0.1m,
                "0.###");
            control.ValueChanged += (_, _) =>
            {
                if (control.Value is not { } value) return;
                draft.SetValue(entry, entry.ValueType == typeof(float)
                    ? decimal.ToSingle(value)
                    : decimal.ToDouble(value));
                changed();
            };
            return control;
        }
        if (entry.ValueType == typeof(string))
        {
            var text = new TextBox
            {
                Text = (string)draft.GetValue(entry),
                PasswordChar = entry.Secret ? '\u2022' : default
            };
            text.TextChanged += (_, _) =>
            {
                draft.SetValue(entry, text.Text ?? "");
                changed();
            };
            return text;
        }
        if (entry.ValueType.IsEnum)
        {
            var values = Enum.GetValues(entry.ValueType).Cast<object>().ToArray();
            var combo = new ComboBox
            {
                ItemsSource = values,
                SelectedItem = draft.GetValue(entry),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not { } value) return;
                draft.SetValue(entry, value);
                changed();
            };
            return combo;
        }
        return Message($"Unsupported setting type: {entry.ValueType.Name}");
    }

    private Control RenderComponents(UiComponent content)
    {
        var rendered = _componentRenderer.Build(content);
        rendered.Refresh();
        _renderedComponents.Add(rendered);
        return rendered.Control;
    }

    private void RefreshLiveComponents()
    {
        if (_disposed) return;
        if (_registry.UiRevision != _observedUiRevision)
        {
            RefreshAll();
            return;
        }
        foreach (var rendered in _renderedComponents.ToArray()) rendered.Refresh();
    }

    private void UpdateDraftIndicators()
    {
        foreach (var pair in _draftIndicators)
            pair.Value.IsVisible = _registry.HasConfigurationDraft(pair.Key);
    }

    private void RunModAction(Action action)
    {
        _registry.TryModAction(action);
        RefreshAll();
    }

    private static StackPanel Page(string title, string subtitle)
    {
        var stack = new StackPanel { Spacing = BriefcaseTheme.CardSpacing };
        stack.Children.Add(BriefcaseControls.PageHeader(title, subtitle));
        return stack;
    }

    private static ScrollViewer ScrollPage(Control content) => new()
    {
        Content = content,
        Padding = new Thickness(0, 0, 8, 22),
        HorizontalScrollBarVisibility =
            Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };

    private static Border MessageCard(string title, string message) =>
        BriefcaseControls.Card(title, message, Array.Empty<Control>());

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        Foreground = BriefcaseTheme.Muted,
        TextWrapping = TextWrapping.Wrap
    };

    private static Grid DetailLine(string label, string value)
    {
        var left = new TextBlock
        {
            Text = label,
            Foreground = BriefcaseTheme.Muted
        };
        var right = new TextBlock
        {
            Text = value,
            Foreground = BriefcaseTheme.Text,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(right, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { left, right }
        };
    }

    private static Border StatusBadge(string text, UiStatusTone tone)
    {
        var badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold
            }
        };
        badge.Classes.Add(UiClasses.Status);
        badge.Classes.Add(tone switch
        {
            UiStatusTone.Information => UiClasses.StatusInformation,
            UiStatusTone.Success => UiClasses.StatusSuccess,
            UiStatusTone.Warning => UiClasses.StatusWarning,
            UiStatusTone.Error => UiClasses.StatusError,
            _ => UiClasses.Status
        });
        return badge;
    }

    private static Button MakeButton(
        string text,
        Action action,
        UiIcon? icon = null,
        bool primary = false)
    {
        Control content = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (icon is { } vectorIcon)
        {
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children =
                {
                    AvaloniaIcons.Create(vectorIcon, 16),
                    content
                }
            };
        }
        var button = new Button { Content = content };
        button.Classes.Add(primary ? UiClasses.Primary : UiClasses.Secondary);
        button.Click += (_, _) => action();
        return button;
    }

    private static NumericUpDown Numeric(
        decimal value,
        decimal minimum,
        decimal maximum,
        decimal increment,
        string format) => new()
    {
        Value = value,
        Minimum = minimum,
        Maximum = maximum,
        Increment = increment,
        FormatString = format,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static string FrameworkVersion() =>
        typeof(AvaloniaConfigurationView).Assembly.GetName().Version?.ToString(3)
        ?? "development";

    private void DisposeRenderedComponents()
    {
        foreach (var rendered in _renderedComponents) rendered.Dispose();
        _renderedComponents.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        DisposeRenderedComponents();
        _content.Content = null;
        Content = null;
    }

    private sealed record ClientPage(
        string Id,
        string Title,
        ModScope? Scope,
        ManagedModStatus? Status);

    private sealed record ServerPanel(string Id, string Name, UiComponent Content);
}
