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
    private const string HomeId = "$home";
    private const string ServersId = "$servers";
    private const string ModsId = "$mods";
    private const string ServerDirectoryModId = "briefcase.server-browser.client";
    private const string ServerPanelPrefix = "$server-panel:";
    private const string InstalledModPrefix = "$installed-mod:";

    private readonly ConfigurationRegistry _registry;
    private readonly StackPanel _navigation = new() { Spacing = 4 };
    private readonly ContentControl _content = new();
    private readonly AvaloniaComponentRenderer _componentRenderer;
    private readonly List<RenderedComponent> _renderedComponents = [];
    private readonly DispatcherTimer _refreshTimer;
    private readonly IDisposable _serverWorkspaceSubscription;
    private long _observedUiRevision;
    private string _selectedId;
    private string? _selectedModId;
    private string? _selectedServerPanelId;
    private bool _disposed;

    public AvaloniaConfigurationView(ConfigurationRegistry registry)
    {
        _registry = registry;
        _componentRenderer = new AvaloniaComponentRenderer(registry.ReportClientUiError);
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshLiveComponents();
        AttachedToVisualTree += (_, _) => _refreshTimer.Start();
        DetachedFromVisualTree += (_, _) => _refreshTimer.Stop();

        _selectedId = NormalizeSelectedView(registry._document.Window.SelectedView);
        if (_selectedId == ModsId &&
            !string.IsNullOrWhiteSpace(registry._document.Window.SelectedModId))
            _selectedModId = registry._document.Window.SelectedModId;
        _serverWorkspaceSubscription = ServerWorkspace.Subscribe(notification =>
        {
            if (!notification.OpenAdministration) return;
            Dispatcher.UIThread.Post(OpenFirstServerAdministration);
        });

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

        var pages = SnapshotClientPages();
        var serverPanels = SnapshotServerPanels();
        if (_selectedModId is not null && !pages.Any(page => string.Equals(
                page.Id, _selectedModId, StringComparison.OrdinalIgnoreCase)))
            _selectedModId = null;
        if (_selectedServerPanelId is not null && !serverPanels.Any(panel => string.Equals(
                panel.Id, _selectedServerPanelId, StringComparison.OrdinalIgnoreCase)))
            _selectedServerPanelId = null;

        AddNavigationButton(
            "Home", HomeId, UiIcon.Briefcase, root: true,
            () => SelectTopLevelPage(HomeId));
        AddNavigationButton(
            "Servers", ServersId, UiIcon.Server, root: true,
            () => SelectTopLevelPage(ServersId));
        AddNavigationButton(
            "Mods", ModsId, UiIcon.Mods, root: true,
            () => SelectTopLevelPage(ModsId));

        ShowSelectedPage(pages, serverPanels);
    }

    private void SelectTopLevelPage(string id)
    {
        _selectedId = id;
        if (id == ServersId) _selectedServerPanelId = null;
        if (id == ModsId) _selectedModId = null;
        _registry.RememberSelectedView(id switch
        {
            ServersId => "Servers",
            ModsId => "Mods",
            _ => "Home"
        });
        RefreshAll();
    }

    private static string NormalizeSelectedView(string? view) => view switch
    {
        "Server" or "Servers" => ServersId,
        "Mods" => ModsId,
        _ => HomeId
    };

    private void AddNavigationButton(
        string title,
        string id,
        UiIcon icon,
        bool root,
        Action action)
    {
        var active = string.Equals(
            id, _selectedId, StringComparison.OrdinalIgnoreCase);
        var label = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Children =
            {
                AvaloniaIcons.Create(
                    icon,
                    root ? 21 : 17,
                    active ? BriefcaseTheme.Accent : BriefcaseTheme.Muted),
                label
            }
        };
        Grid.SetColumn(label, 1);
        label.Margin = new Thickness(10, 0, 0, 0);

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        button.Classes.Add("briefcase-navigation");
        if (root) button.Classes.Add("briefcase-navigation-root");
        if (active) button.Classes.Add("briefcase-navigation-active");
        button.Click += (_, _) => action();
        _navigation.Children.Add(button);
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
                    scope.Info.Id,
                    panel.Name,
                    panel.Content)))
            .OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ShowSelectedPage(
        IReadOnlyList<ClientPage> pages,
        IReadOnlyList<ServerPanel> serverPanels)
    {
        _content.Content = _selectedId switch
        {
            ServersId => BuildServersPage(serverPanels),
            ModsId => BuildFrameworkPage(pages),
            _ => BuildHomePage(pages, serverPanels)
        };
    }

    private Control BuildHomePage(
        IReadOnlyCollection<ClientPage> pages,
        IReadOnlyCollection<ServerPanel> serverPanels)
    {
        var statuses = _registry._modControl?.SnapshotInstalledMods() ?? [];
        var loaded = statuses.Count(status => status.Loaded);
        var errors = statuses.Count(status => status.LastError is not null);
        var serverDirectoryAvailable = serverPanels.Any(panel => panel.IsDirectory);
        var administrationAvailable = serverPanels.Any(panel => !panel.IsDirectory);
        var cards = new ResponsiveCardPanel();

        cards.Children.Add(BriefcaseControls.Card(
            "Briefcase is ready",
            "The framework is running and ready to manage your servers and client mods.",
            new Control[]
            {
                StatusBadge(errors == 0 ? "No mod errors" : $"{errors} mod error(s)",
                    errors == 0 ? UiStatusTone.Success : UiStatusTone.Error),
                DetailLine("Installed version", FrameworkVersion()),
                DetailLine("Active mods", $"{loaded} of {statuses.Length}")
            }));

        cards.Children.Add(BriefcaseControls.Card(
            "Servers",
            "Save community servers, join them quickly, and open authenticated administration.",
            new Control[]
            {
                StatusBadge(
                    serverDirectoryAvailable ? "Server directory ready" : "Server directory unavailable",
                    serverDirectoryAvailable ? UiStatusTone.Success : UiStatusTone.Warning),
                DetailLine("Remote administration",
                    administrationAvailable ? "Available" : "Unavailable"),
                MakeButton("Manage servers", () => SelectTopLevelPage(ServersId), UiIcon.Server)
            }));

        cards.Children.Add(BriefcaseControls.Card(
            "Client mods",
            "Install, configure, enable, reload, and disable client mods from one controlled page.",
            new Control[]
            {
                DetailLine("Installed mods", statuses.Length.ToString(CultureInfo.InvariantCulture)),
                DetailLine("Available configuration", pages.Count.ToString(CultureInfo.InvariantCulture)),
                MakeButton("Manage mods", () => SelectTopLevelPage(ModsId), UiIcon.Mods)
            }));

        var stack = Page("Briefcase", "Mod and community-server management for Deceive Inc.");
        stack.Children.Add(cards);
        return ScrollPage(stack);
    }

    private Control BuildServersPage(IReadOnlyList<ServerPanel> panels)
    {
        if (_selectedServerPanelId is not null)
        {
            var selected = panels.FirstOrDefault(panel => string.Equals(
                panel.Id, _selectedServerPanelId, StringComparison.OrdinalIgnoreCase));
            if (selected is not null) return BuildServerPanelPage(selected);
            _selectedServerPanelId = null;
        }

        var stack = Page(
            "Servers",
            "Save, join, and administer community servers from one workspace.");
        var directoryPanels = panels.Where(panel => panel.IsDirectory).ToArray();
        if (directoryPanels.Length == 0)
        {
            stack.Children.Add(MessageCard(
                "Server directory unavailable",
                "The Briefcase server-directory component is not loaded."));
        }
        else
        {
            foreach (var directory in directoryPanels)
                stack.Children.Add(RenderComponents(directory.Content));
        }

        var administration = panels.Where(panel => !panel.IsDirectory).ToArray();
        if (administration.Length > 0)
        {
            var actions = administration.Select(panel => (Control)MakeButton(
                administration.Length == 1 ? "Open administration" : panel.Name,
                () => OpenServerPanel(panel.Id),
                UiIcon.Settings)).ToArray();
            stack.Children.Add(BriefcaseControls.Card(
                "Remote administration",
                "Select a saved server's Configure action to use its endpoint and credentials, " +
                "or open the current administration target directly.",
                actions));
        }

        return ScrollPage(stack);
    }

    private Control BuildServerPanelPage(ServerPanel panel)
    {
        var stack = Page(panel.Name, "Remote Briefcase server management");
        stack.Children.Add(MakeButton("Back to server list", () =>
        {
            _selectedServerPanelId = null;
            RefreshAll();
        }));
        stack.Children.Add(RenderComponents(panel.Content));
        return ScrollPage(stack);
    }

    private void OpenFirstServerAdministration()
    {
        if (_disposed) return;
        var panel = SnapshotServerPanels().FirstOrDefault(candidate => !candidate.IsDirectory);
        _selectedId = ServersId;
        _selectedServerPanelId = panel?.Id;
        _registry.RememberSelectedView("Servers");
        RefreshAll();
    }

    private void OpenServerPanel(string id)
    {
        _selectedId = ServersId;
        _selectedServerPanelId = id;
        _registry.RememberSelectedView("Servers");
        RefreshAll();
    }

    private Control BuildFrameworkPage(IReadOnlyList<ClientPage> pages)
    {
        if (_selectedModId is not null)
        {
            var selected = pages.FirstOrDefault(page => string.Equals(
                page.Id, _selectedModId, StringComparison.OrdinalIgnoreCase));
            if (selected?.Scope is { } scope) return BuildModPage(scope);
            if (selected?.Status is { } status) return BuildInstalledModPage(status);
            _selectedModId = null;
        }

        var stack = Page("Mods", "Install, configure, update, and control client mods");
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
        if (hasDraft)
            children.Add(StatusBadge("Unsaved settings", UiStatusTone.Warning));
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
        var configurable = scope is not null &&
                           (scope.SnapshotEntries().Length > 0 ||
                            scope.SnapshotUiPanels().Length > 0);
        if (configurable)
            buttons.Children.Add(MakeButton(
                "Configure", () => OpenMod(scope!), UiIcon.Settings));
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
        stack.Children.Add(MakeButton("Back to mods", ReturnToMods));
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
        }

        var stack = Page(
            scope.Info.Name,
            $"{scope.Info.Author}  ·  {scope.Info.Version}  ·  {scope.Info.Id}");
        stack.Children.Add(MakeButton("Back to mods", ReturnToMods));
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

    private void OpenMod(ModScope scope)
    {
        _selectedId = ModsId;
        _selectedModId = scope.Info.Id;
        _registry.RememberSelectedView("Mods");
        _registry.RememberSelectedMod(scope.Info.Id);
        RefreshAll();
    }

    private void ReturnToMods()
    {
        _selectedModId = null;
        RefreshAll();
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
        _serverWorkspaceSubscription.Dispose();
        DisposeRenderedComponents();
        _content.Content = null;
        Content = null;
    }

    private sealed record ClientPage(
        string Id,
        string Title,
        ModScope? Scope,
        ManagedModStatus? Status);

    private sealed record ServerPanel(
        string Id,
        string ScopeId,
        string Name,
        UiComponent Content)
    {
        public bool IsDirectory => string.Equals(
            ScopeId,
            ServerDirectoryModId,
            StringComparison.OrdinalIgnoreCase);
    }
}
