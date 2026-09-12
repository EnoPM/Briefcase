using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Briefcase.ClientModApi;
using Briefcase.AvaloniaUi;

namespace Briefcase.AvaloniaMenu;

/// <summary>
/// Converts the client-only Briefcase component tree into Avalonia controls.
/// Reactive bindings are dispatched to Avalonia while legacy delegates retain
/// their periodic compatibility refresh.
/// </summary>
internal sealed class AvaloniaComponentRenderer(Action<Exception> reportError)
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#48DBB8"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#98A2B3"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#F5C56B"));

    public RenderedComponent Build(UiComponent component)
    {
        var rendered = Wrap(component, BuildCore(component));
        rendered.Observe(component);
        return rendered;
    }

    private RenderedComponent BuildCore(UiComponent component) => component switch
    {
        UiGroup group => BuildGroup(group),
        UiSection section => BuildSection(section),
        UiCard card => BuildCard(card),
        UiExpander expander => BuildExpander(expander),
        UiStatus status => BuildStatus(status),
        UiText text => BuildText(text),
        UiIconView icon => BuildIcon(icon),
        UiButton button => BuildButton(button),
        UiToggle toggle => BuildToggle(toggle),
        UiTextField field => BuildTextField(field),
        UiNumberField number => BuildNumber(number),
        UiChoice choice => BuildChoice(choice),
        UiSeparator => Static(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#394351")),
            Margin = new Thickness(0, 5)
        }),
        UiSpacer => Static(new Border { Height = 8 }),
        UiDynamic dynamic => BuildDynamic(dynamic),
        _ => Static(new TextBlock
        {
            Text = $"Unsupported Briefcase UI component: {component.GetType().Name}",
            Foreground = Brushes.OrangeRed
        })
    };

    private RenderedComponent BuildGroup(UiGroup group)
    {
        var children = group.Children.Select(Build).ToArray();
        var stack = new StackPanel
        {
            Orientation = group.Horizontal ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = group.Horizontal ? 8 : 7
        };
        foreach (var child in children) stack.Children.Add(child.Control);
        return new RenderedComponent(stack, () =>
        {
            foreach (var child in children) child.Refresh();
        }, reportError, () => DisposeAll(children));
    }

    private RenderedComponent BuildSection(UiSection section)
    {
        var children = section.Children.Select(Build).ToArray();
        var stack = new StackPanel { Spacing = 7 };
        stack.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 12, 0, 4)
        });
        foreach (var child in children) stack.Children.Add(child.Control);
        return new RenderedComponent(stack, () =>
        {
            foreach (var child in children) child.Refresh();
        }, reportError, () => DisposeAll(children));
    }

    private RenderedComponent BuildCard(UiCard card)
    {
        var children = card.Children.Select(Build).ToArray();
        var content = new StackPanel { Spacing = 7 };
        if (!string.IsNullOrWhiteSpace(card.Title))
            content.Children.Add(new TextBlock
            {
                Text = card.Title,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold
            });
        foreach (var child in children) content.Children.Add(child.Control);
        return new RenderedComponent(
            new Border { Child = content },
            () =>
            {
                foreach (var child in children) child.Refresh();
            },
            reportError,
            () => DisposeAll(children));
    }

    private RenderedComponent BuildExpander(UiExpander expander)
    {
        var children = expander.Children.Select(Build).ToArray();
        var content = new StackPanel { Spacing = 7 };
        foreach (var child in children) content.Children.Add(child.Control);
        return new RenderedComponent(
            new Expander
            {
                Header = expander.Title,
                IsExpanded = expander.InitiallyExpanded,
                Content = content
            },
            () =>
            {
                foreach (var child in children) child.Refresh();
            },
            reportError,
            () => DisposeAll(children));
    }

    private RenderedComponent BuildStatus(UiStatus status)
    {
        var text = new TextBlock();
        return new RenderedComponent(
            new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = text
            },
            () => text.Text = status.Value(),
            reportError);
    }

    private RenderedComponent BuildText(UiText text)
    {
        var control = new TextBlock
        {
            TextWrapping = text.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Foreground = text.Tone switch
            {
                UiTextTone.Muted => Muted,
                UiTextTone.Accent => Accent,
                UiTextTone.Warning => Warning,
                UiTextTone.Error => Brushes.OrangeRed,
                _ => null
            }
        };
        return new RenderedComponent(control, () => control.Text = text.Value(), reportError);
    }

    private static RenderedComponent BuildIcon(UiIconView icon) => Static(
        AvaloniaIcons.Create(icon.Icon, icon.Size, ToneBrush(icon.Tone)));

    private RenderedComponent BuildButton(UiButton button)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        Control content = label;
        if (button.Icon is { } icon)
        {
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Children = { AvaloniaIcons.Create(icon), label }
            };
        }
        var control = new Button
        {
            Padding = new Thickness(14, 7),
            Content = content
        };
        control.Click += (_, _) => Invoke(button.Execute);
        return new RenderedComponent(control, () =>
        {
            label.Text = button.Label();
            control.IsEnabled = button.CanExecute;
        }, reportError);
    }

    private RenderedComponent BuildToggle(UiToggle toggle)
    {
        var control = new CheckBox { Content = toggle.Label };
        var updating = false;
        control.IsCheckedChanged += (_, _) =>
        {
            if (!updating) Invoke(() => toggle.Changed(control.IsChecked == true));
        };
        return new RenderedComponent(control, () =>
        {
            updating = true;
            try { control.IsChecked = toggle.Value(); }
            finally { updating = false; }
        }, reportError);
    }

    private RenderedComponent BuildTextField(UiTextField field)
    {
        var input = new TextBox
        {
            PasswordChar = field.Secret ? '\u2022' : default,
            MaxLength = field.MaximumLength,
            PlaceholderText = field.Hint,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var updating = false;
        if (field.CommitMode == UiCommitMode.Immediate)
        {
            input.TextChanged += (_, _) =>
            {
                if (!updating) Invoke(() => field.Changed(input.Text ?? ""));
            };
        }
        else
        {
            input.LostFocus += (_, _) => Invoke(() => field.Changed(input.Text ?? ""));
        }
        var content = Labeled(field.Label, input);
        return new RenderedComponent(content, () =>
        {
            if (input.IsFocused) return;
            var value = field.Value();
            if (string.Equals(input.Text, value, StringComparison.Ordinal)) return;
            updating = true;
            try { input.Text = value; }
            finally { updating = false; }
        }, reportError);
    }

    private RenderedComponent BuildNumber(UiNumberField number)
    {
        var input = new NumericUpDown
        {
            Minimum = ToDecimal(number.Minimum),
            Maximum = ToDecimal(number.Maximum),
            Increment = Math.Max(0.000001m, Math.Abs(ToDecimal(number.Step))),
            FormatString = number.Format,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var updating = false;
        if (number.CommitMode == UiCommitMode.Immediate)
        {
            input.ValueChanged += (_, _) =>
            {
                if (!updating && input.Value is { } value)
                    Invoke(() => number.Changed(decimal.ToDouble(value)));
            };
        }
        else
        {
            input.LostFocus += (_, _) =>
            {
                if (input.Value is { } value)
                    Invoke(() => number.Changed(decimal.ToDouble(value)));
            };
        }
        var content = Labeled(number.Label, input);
        return new RenderedComponent(content, () =>
        {
            if (input.IsFocused) return;
            var value = ToDecimal(Math.Clamp(number.Value(), number.Minimum, number.Maximum));
            if (input.Value == value) return;
            updating = true;
            try { input.Value = value; }
            finally { updating = false; }
        }, reportError);
    }

    private RenderedComponent BuildChoice(UiChoice choice)
    {
        var input = new ComboBox
        {
            ItemsSource = choice.Items.Select(item => item.Label).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var updating = false;
        input.SelectionChanged += (_, _) =>
        {
            if (updating || input.SelectedIndex < 0 ||
                input.SelectedIndex >= choice.Items.Count) return;
            Invoke(choice.Items[input.SelectedIndex].Select);
        };
        var content = Labeled(choice.Label, input);
        return new RenderedComponent(content, () =>
        {
            var index = -1;
            for (var candidate = 0; candidate < choice.Items.Count; candidate++)
            {
                if (!choice.Items[candidate].Selected()) continue;
                index = candidate;
                break;
            }
            if (input.SelectedIndex == index) return;
            updating = true;
            try { input.SelectedIndex = index; }
            finally { updating = false; }
        }, reportError);
    }

    private RenderedComponent BuildDynamic(UiDynamic dynamic)
    {
        var stack = new StackPanel { Spacing = 7 };
        RenderedComponent[] children = [];
        var revision = long.MinValue;

        void Rebuild()
        {
            DisposeAll(children);
            stack.Children.Clear();
            children = dynamic.Children().Select(Build).ToArray();
            foreach (var child in children) stack.Children.Add(child.Control);
        }

        return new RenderedComponent(stack, () =>
        {
            var current = dynamic.Revision?.Invoke() ?? unchecked(revision + 1);
            if (current != revision)
            {
                revision = current;
                Rebuild();
            }
            foreach (var child in children) child.Refresh();
        }, reportError, () => DisposeAll(children));
    }

    private RenderedComponent Wrap(UiComponent component, RenderedComponent rendered)
    {
        foreach (var styleClass in component.StyleClasses)
            rendered.Control.Classes.Add(styleClass);
        return new RenderedComponent(rendered.Control, () =>
        {
            var visible = component.IsVisible;
            rendered.Control.IsVisible = visible;
            rendered.Control.IsEnabled = component.IsEnabled;
            ToolTip.SetTip(rendered.Control, component.Tooltip);
            if (visible) rendered.Refresh();
        }, reportError, rendered.Dispose);
    }

    private static StackPanel Labeled(string label, Control editor)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(editor);
        return stack;
    }

    private static RenderedComponent Static(Control control) => new(control, null, null);

    private static void DisposeAll(IEnumerable<RenderedComponent> components)
    {
        foreach (var component in components) component.Dispose();
    }

    private void Invoke(Action action)
    {
        try { action(); }
        catch (Exception exception) { reportError(exception); }
    }

    private static IBrush? ToneBrush(UiTextTone tone) => tone switch
    {
        UiTextTone.Muted => Muted,
        UiTextTone.Accent => Accent,
        UiTextTone.Warning => Warning,
        UiTextTone.Error => Brushes.OrangeRed,
        _ => null
    };

    private static decimal ToDecimal(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value >= (double)decimal.MaxValue) return decimal.MaxValue;
        if (value <= (double)decimal.MinValue) return decimal.MinValue;
        return (decimal)value;
    }
}

internal sealed class RenderedComponent : IDisposable
{
    private readonly Action? _refresh;
    private readonly Action<Exception>? _reportError;
    private readonly Action? _disposeOwned;
    private IDisposable? _subscription;
    private string? _lastError;
    private int _refreshQueued;
    private int _disposed;

    public RenderedComponent(
        Control control,
        Action? refresh,
        Action<Exception>? reportError,
        Action? disposeOwned = null)
    {
        Control = control;
        _refresh = refresh;
        _reportError = reportError;
        _disposeOwned = disposeOwned;
    }

    public Control Control { get; }

    public void Observe(UiComponent component)
    {
        _subscription = component.SubscribeInvalidated(QueueRefresh);
    }

    public void Refresh()
    {
        if (_refresh is null || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _refresh();
            _lastError = null;
        }
        catch (Exception exception)
        {
            if (!string.Equals(_lastError, exception.ToString(), StringComparison.Ordinal))
            {
                _lastError = exception.ToString();
                _reportError?.Invoke(exception);
            }
        }
    }

    private void QueueRefresh()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                Volatile.Write(ref _refreshQueued, 0);
                Refresh();
            }, DispatcherPriority.Background);
        }
        catch
        {
            Volatile.Write(ref _refreshQueued, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        _disposeOwned?.Invoke();
    }
}