using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Briefcase.ClientModApi;
using Briefcase.AvaloniaUi;
using Briefcase.ManagedHost;
using Briefcase.ModApi;
using ModScope = Briefcase.ManagedHost.ConfigurationRegistry.ModScope;

namespace Briefcase.AvaloniaMenu;

internal sealed class AvaloniaConfigurationView : UserControl, IDisposable
{
    private const string FrameworkTabId = "$briefcase";
    private static readonly IBrush Accent =
        new SolidColorBrush(Color.Parse("#48DBB8"));
    private static readonly IBrush Muted =
        new SolidColorBrush(Color.Parse("#98A2B3"));
    private static readonly IBrush Card =
        new SolidColorBrush(Color.Parse("#B5222933"));
    private static readonly IBrush Border =
        new SolidColorBrush(Color.Parse("#394351"));

    private readonly ConfigurationRegistry _registry;
    private readonly StackPanel _navigation = new() { Spacing = 5 };
    private readonly ContentControl _content = new();
    private readonly AvaloniaComponentRenderer _componentRenderer;
    private readonly List<RenderedComponent> _renderedComponents = [];
    private readonly DispatcherTimer _refreshTimer;
    private long _observedUiRevision;
    private string _selectedId = FrameworkTabId;
    private bool _serverView;
    private bool _disposed;

    public AvaloniaConfigurationView(ConfigurationRegistry registry)
    {
        _registry = registry;
        _componentRenderer = new AvaloniaComponentRenderer(registry.ReportClientUiError);
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _refreshTimer.Tick += (_, _) => RefreshLiveComponents();
        AttachedToVisualTree += (_, _) => _refreshTimer.Start();
        DetachedFromVisualTree += (_, _) => _refreshTimer.Stop();
        _serverView = string.Equals(
            registry._document.Window.SelectedView,
            "Server",
            StringComparison.OrdinalIgnoreCase);
        _selectedId = string.IsNullOrWhiteSpace(
            registry._document.Window.SelectedModId)
            ? FrameworkTabId
            : registry._document.Window.SelectedModId;

        Content = BuildRoot();
        RefreshAll();
    }

    private Control BuildRoot()
    {
        var client = MakeNavigationButton("Client", () =>
        {
            _serverView = false;
            _registry.RememberSelectedView("Client");
            RefreshAll();
        }, UiIcon.Client);
        var server = MakeNavigationButton("Server", () =>
        {
            _serverView = true;
            _registry.RememberSelectedView("Server");
            RefreshAll();
        }, UiIcon.Server);

        var viewSwitch = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(16, 8, 16, 10),
            Children = { client, server }
        };

        var sidebar = new ScrollViewer
        {
            Content = _navigation,
            Padding = new Thickness(10),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        var body = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("230,*"),
            Margin = new Thickness(16, 0, 16, 16)
        };
        body.Children.Add(new Border
        {
            Background = Card,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sidebar
        });
        var contentBorder = new Border
        {
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(20),
            Background = Card,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _content
        };
        Grid.SetColumn(contentBorder, 1);
        body.Children.Add(contentBorder);

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        root.Children.Add(viewSwitch);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    private void RefreshAll()
    {
        if (_disposed) return;
        foreach (var rendered in _renderedComponents) rendered.Dispose();
        _renderedComponents.Clear();
        _observedUiRevision = _registry.UiRevision;
        _navigation.Children.Clear();
        if (_serverView)
        {
            AddNavigationButton("Server administration", "$server");
            ShowServerPreview();
            return;
        }

        AddNavigationButton("Mods", FrameworkTabId);
        ModScope[] scopes;
        lock (_registry._gate)
        {
            scopes = _registry._scopes.Values
                .Where(scope => scope.ShouldShowClientTab)
                .OrderBy(scope => scope.Info.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        foreach (var scope in scopes)
            AddNavigationButton(scope.Info.Name, scope.Info.Id);

        if (!string.Equals(_selectedId, FrameworkTabId, StringComparison.OrdinalIgnoreCase) &&
            !scopes.Any(scope => string.Equals(
                scope.Info.Id, _selectedId, StringComparison.OrdinalIgnoreCase)))
            _selectedId = FrameworkTabId;
        ShowSelectedClientPage();
    }

    private void AddNavigationButton(string title, string id)
    {
        var icon = id switch
        {
            FrameworkTabId => UiIcon.Mods,
            "$server" => UiIcon.Server,
            _ => (UiIcon?)null
        };
        var button = MakeNavigationButton(title, () =>
        {
            _selectedId = id;
            if (id != "$server") _registry.RememberSelectedMod(id);
            RefreshAll();
        }, icon);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.MinHeight = 42;
        button.Opacity = string.Equals(
            id, _selectedId, StringComparison.OrdinalIgnoreCase) ? 1 : 0.76;
        _navigation.Children.Add(button);
    }

    private void ShowSelectedClientPage()
    {
        if (_serverView)
        {
            ShowServerPreview();
            return;
        }
        if (string.Equals(_selectedId, FrameworkTabId, StringComparison.OrdinalIgnoreCase))
        {
            _content.Content = BuildFrameworkPage();
            return;
        }

        ModScope? scope;
        lock (_registry._gate)
            _registry._scopes.TryGetValue(_selectedId, out scope);
        _content.Content = scope is null
            ? Message("This mod is no longer loaded.")
            : BuildModPage(scope);
    }

    private Control BuildFrameworkPage()
    {
        var stack = Page(
            "Mods",
            "Installed mods and automatically generated settings");
        stack.Children.Add(SectionTitle("Mod library"));
        stack.Children.Add(new TextBlock
        {
            Text = "Add or update mods in this folder. Avalonia uses the same lifecycle and dependency checks as the current menu.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted
        });
        stack.Children.Add(new TextBlock
        {
            Text = _registry._modControl?.ModsDirectory ?? "The mod controller is not ready.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 4, 0, 6)
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        actions.Children.Add(MakeButton("Refresh", () =>
        {
            _registry._modControl?.Refresh();
            RefreshAll();
        }, UiIcon.Refresh));
        actions.Children.Add(MakeButton("Open Mods folder", () =>
        {
            if (_registry._modControl is { } control)
                _registry.OpenModsDirectory(control.ModsDirectory);
        }, UiIcon.Folder));
        stack.Children.Add(actions);
        stack.Children.Add(SectionTitle("Installed mods"));

        var control = _registry._modControl;
        if (control is null)
        {
            stack.Children.Add(Message("The managed mod controller is not ready."));
            return new ScrollViewer { Content = stack };
        }

        ModScope[] scopes;
        lock (_registry._gate) scopes = _registry._scopes.Values.ToArray();

        var mods = control.SnapshotInstalledMods();
        if (mods.Length == 0)
            stack.Children.Add(Message("No mod DLL is installed."));
        foreach (var mod in mods)
        {
            var scope = scopes.FirstOrDefault(candidate => string.Equals(
                candidate.Info.Id, mod.Id, StringComparison.OrdinalIgnoreCase));
            stack.Children.Add(BuildModCard(control, mod, scope));
        }
        return new ScrollViewer { Content = stack };
    }

    private Control BuildModCard(
        IFrameworkModControl control,
        ManagedModStatus mod,
        ModScope? scope)
    {
        var enabled = new CheckBox
        {
            IsChecked = mod.Enabled,
            Content = mod.DisplayName,
            FontWeight = FontWeight.SemiBold,
            IsEnabled = !(mod.Enabled && !mod.CanStop)
        };
        if (!enabled.IsEnabled && !string.IsNullOrWhiteSpace(mod.StopBlockReason))
            ToolTip.SetTip(enabled, mod.StopBlockReason);
        enabled.Click += (_, _) => RunModAction(() =>
            control.SetEnabled(mod.FileName, enabled.IsChecked == true));

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(enabled);
        var state = new TextBlock
        {
            Text = mod.LastError is not null
                ? "Error"
                : mod.Loaded ? "Loaded" : "Unloaded",
            Foreground = mod.LastError is not null
                ? Brushes.OrangeRed
                : mod.Loaded ? Accent : Muted
        };
        Grid.SetColumn(state, 1);
        heading.Children.Add(state);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(heading);
        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(mod.Version)
                ? mod.FileName
                : $"{mod.FileName}  |  {mod.Version}",
            Foreground = Muted
        });
        if (mod.LastError is not null)
            stack.Children.Add(new TextBlock
            {
                Text = mod.LastError,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.OrangeRed
            });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        if (mod.Loaded)
        {
            buttons.Children.Add(ModActionButton(
                "Reload", mod.CanStop, mod.StopBlockReason,
                () => control.Reload(mod.FileName)));
            buttons.Children.Add(ModActionButton(
                "Unload", mod.CanStop, mod.StopBlockReason,
                () => control.Unload(mod.FileName)));
        }
        else
        {
            buttons.Children.Add(ModActionButton(
                "Load", true, null, () => control.Load(mod.FileName)));
        }
        stack.Children.Add(buttons);
        if (mod.Dependencies.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = $"Requires: {string.Join(", ", mod.Dependencies)}",
                Foreground = Muted
            });
        if (!string.IsNullOrWhiteSpace(mod.Description))
            stack.Children.Add(new TextBlock
            {
                Text = mod.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted
            });

        if (scope is not null && !scope.ShouldShowClientTab &&
            scope.SnapshotEntries() is { Length: > 0 } entries)
            stack.Children.Add(BuildGeneratedConfiguration(entries));

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#7F111720")),
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack
        };

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

    private Control BuildGeneratedConfiguration(IConfigurationEntry[] entries)
    {
        var content = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(4, 8, 0, 0)
        };
        foreach (var section in entries.GroupBy(
                     entry => entry.Section,
                     StringComparer.OrdinalIgnoreCase))
        {
            content.Children.Add(new TextBlock
            {
                Text = section.Key,
                FontWeight = FontWeight.SemiBold,
                Foreground = Accent,
                Margin = new Thickness(0, 8, 0, 2)
            });
            foreach (var entry in section) content.Children.Add(BuildEntry(entry));
        }

        return new Expander
        {
            Header = $"Configuration ({entries.Length})",
            IsExpanded = false,
            Margin = new Thickness(0, 5, 0, 0),
            Content = content
        };
    }

    private Control BuildModPage(ModScope scope)
    {
        var stack = Page(
            scope.Info.Name,
            $"{scope.Info.Author}  |  {scope.Info.Version}  |  {scope.Info.Id}");
        if (!string.IsNullOrWhiteSpace(scope.Info.Description))
            stack.Children.Add(new TextBlock
            {
                Text = scope.Info.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                Margin = new Thickness(0, 0, 0, 8)
            });

        var entries = scope.SnapshotEntries();
        foreach (var section in entries.GroupBy(
                     entry => entry.Section,
                     StringComparer.OrdinalIgnoreCase))
        {
            stack.Children.Add(SectionTitle(section.Key));
            foreach (var entry in section)
                stack.Children.Add(BuildEntry(entry));
        }

        var componentPanels = scope.SnapshotUiPanels();
        foreach (var panel in componentPanels)
            stack.Children.Add(RenderComponents(panel.Content));

        if (entries.Length == 0 && componentPanels.Length == 0)
            stack.Children.Add(Message("This mod does not expose configurable settings."));
        return new ScrollViewer { Content = stack };
    }

    private Control BuildEntry(IConfigurationEntry entry)
    {
        var stack = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 12)
        };
        stack.Children.Add(new TextBlock
        {
            Text = entry.Key,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(CreateEditor(entry));
        if (!string.IsNullOrWhiteSpace(entry.Description))
            stack.Children.Add(new TextBlock
            {
                Text = entry.Description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 12
            });
        return stack;
    }

    private static Control CreateEditor(IConfigurationEntry entry)
    {
        if (entry.ValueType == typeof(bool))
        {
            var control = new CheckBox
            {
                IsChecked = (bool)entry.BoxedValue,
                Content = (bool)entry.BoxedValue ? "Enabled" : "Disabled"
            };
            control.Click += (_, _) =>
            {
                var value = control.IsChecked == true;
                entry.BoxedValue = value;
                control.Content = value ? "Enabled" : "Disabled";
            };
            return control;
        }
        if (entry.ValueType == typeof(int))
        {
            var control = Numeric(
                Convert.ToDecimal(entry.BoxedValue, CultureInfo.InvariantCulture),
                entry.Minimum is null ? decimal.MinValue : Convert.ToDecimal(entry.Minimum),
                entry.Maximum is null ? decimal.MaxValue : Convert.ToDecimal(entry.Maximum),
                1, "0");
            control.ValueChanged += (_, _) =>
            {
                if (control.Value is { } value) entry.BoxedValue = decimal.ToInt32(value);
            };
            return control;
        }
        if (entry.ValueType == typeof(float) || entry.ValueType == typeof(double))
        {
            var control = Numeric(
                Convert.ToDecimal(entry.BoxedValue, CultureInfo.InvariantCulture),
                entry.Minimum is null ? decimal.MinValue : Convert.ToDecimal(entry.Minimum),
                entry.Maximum is null ? decimal.MaxValue : Convert.ToDecimal(entry.Maximum),
                0.1m, "0.###");
            control.ValueChanged += (_, _) =>
            {
                if (control.Value is not { } value) return;
                entry.BoxedValue = entry.ValueType == typeof(float)
                    ? (object)decimal.ToSingle(value)
                    : decimal.ToDouble(value);
            };
            return control;
        }
        if (entry.ValueType == typeof(string))
        {
            if (entry.Secret)
            {
                var password = new TextBox
                {
                    Text = (string)entry.BoxedValue,
                    PasswordChar = '\u2022'
                };
                password.LostFocus += (_, _) =>
                    entry.BoxedValue = password.Text ?? "";
                return password;
            }

            var text = new TextBox { Text = (string)entry.BoxedValue };
            text.LostFocus += (_, _) => entry.BoxedValue = text.Text ?? "";
            return text;
        }
        if (entry.ValueType.IsEnum)
        {
            var values = Enum.GetValues(entry.ValueType).Cast<object>().ToArray();
            var combo = new ComboBox
            {
                ItemsSource = values,
                SelectedItem = entry.BoxedValue,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is { } value) entry.BoxedValue = value;
            };
            return combo;
        }
        return Message($"Unsupported setting type: {entry.ValueType.Name}");
    }

    private void ShowServerPreview()
    {
        _selectedId = "$server";
        var stack = Page(
            "Server administration",
            "Remote Briefcase server management");

        ModScope[] scopes;
        lock (_registry._gate) scopes = _registry._scopes.Values.ToArray();
        var componentPanels = scopes
            .SelectMany(scope => scope.SnapshotServerUiPanels())
            .OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var panel in componentPanels)
        {
            if (componentPanels.Length > 1) stack.Children.Add(SectionTitle(panel.Name));
            stack.Children.Add(RenderComponents(panel.Content));
        }

        if (componentPanels.Length == 0)
            stack.Children.Add(Message("The Briefcase server administration service is unavailable."));
        _content.Content = new ScrollViewer { Content = stack };
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
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeight.SemiBold,
            Foreground = Accent
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = Muted,
            Margin = new Thickness(0, -4, 0, 8)
        });
        return stack;
    }

    private static TextBlock SectionTitle(string title) => new()
    {
        Text = title,
        FontSize = 16,
        FontWeight = FontWeight.SemiBold,
        Margin = new Thickness(0, 12, 0, 4)
    };

    private static TextBlock Message(string text) => new()
    {
        Text = text,
        Foreground = Muted,
        TextWrapping = TextWrapping.Wrap
    };

    private static Button MakeButton(
        string text,
        Action action,
        UiIcon? icon = null)
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
                Children = { AvaloniaIcons.Create(vectorIcon), content }
            };
        }
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(14, 7)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button MakeNavigationButton(
        string text,
        Action action,
        UiIcon? icon = null)
    {
        var button = MakeButton(text, action, icon);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        return button;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        foreach (var rendered in _renderedComponents) rendered.Dispose();
        _renderedComponents.Clear();
        _content.Content = null;
        Content = null;
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
}
