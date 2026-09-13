using System.Windows.Input;

namespace Briefcase.ClientModApi;

/// <summary>Visual emphasis understood by every Briefcase client UI backend.</summary>
public enum UiTextTone
{
    Normal,
    Muted,
    Accent,
    Warning,
    Error
}

/// <summary>Controls when an editable value is sent back to the mod.</summary>
public enum UiCommitMode
{
    Immediate,
    OnCommit
}

/// <summary>Semantic state displayed by a status component.</summary>
public enum UiStatusTone
{
    Neutral,
    Information,
    Success,
    Warning,
    Error
}

/// <summary>
/// Toolkit-neutral vector icon identifiers. Avalonia renders these as scalable
/// geometries; immediate-mode backends use a compact text fallback.
/// </summary>
public enum UiIcon
{
    Briefcase,
    Mods,
    Client,
    Server,
    Settings,
    Refresh,
    Folder,
    Play,
    Pause,
    Reload,
    Power,
    Check,
    Warning,
    Information,
    Search,
    Save,
    Delete,
    Download,
    Upload,
    User,
    Users,
    Plug,
    Disconnect,
    Close,
    ChevronRight,
    ChevronLeft
}

/// <summary>
/// Stable style classes understood by Briefcase renderers. Mods may also attach
/// their own class names for a future optional Avalonia stylesheet.
/// </summary>
public static class UiClasses
{
    public const string Primary = "briefcase-primary";
    public const string Secondary = "briefcase-secondary";
    public const string Danger = "briefcase-danger";
    public const string Compact = "briefcase-compact";
    public const string Card = "briefcase-card";
    public const string FlatCard = "briefcase-flat-card";
    public const string ActionBar = "briefcase-actionbar";
    public const string Tabs = "briefcase-tabs";
    public const string Tab = "briefcase-tab";
    public const string Status = "briefcase-status";
    public const string StatusInformation = "briefcase-status-information";
    public const string StatusSuccess = "briefcase-status-success";
    public const string StatusWarning = "briefcase-status-warning";
    public const string StatusError = "briefcase-status-error";
}

/// <summary>Base class for toolkit-neutral client UI components.</summary>
public abstract class UiComponent
{
    private Func<bool>? _visible;
    private Func<bool>? _enabled;
    private Func<string?>? _tooltip;
    private readonly List<Func<Action, IDisposable>> _subscriptions = [];
    private readonly List<string> _styleClasses = [];

    /// <summary>A stable identifier used by immediate-mode renderers.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public bool IsVisible => _visible?.Invoke() ?? true;
    public bool IsEnabled => _enabled?.Invoke() ?? true;
    public string? Tooltip => _tooltip?.Invoke();
    public string? Description { get; private set; }
    public IReadOnlyList<string> StyleClasses => _styleClasses;

    public UiComponent WithClass(string styleClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleClass);
        styleClass = styleClass.Trim();
        if (styleClass.Any(char.IsWhiteSpace))
            throw new ArgumentException(
                "A UI style class cannot contain whitespace.", nameof(styleClass));
        if (!_styleClasses.Contains(styleClass, StringComparer.Ordinal))
            _styleClasses.Add(styleClass);
        return this;
    }

    public UiComponent WithClasses(params string[] styleClasses)
    {
        ArgumentNullException.ThrowIfNull(styleClasses);
        foreach (var styleClass in styleClasses) WithClass(styleClass);
        return this;
    }

    /// <summary>Adds plain-language help rendered below the component label.</summary>
    public UiComponent WithDescription(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        Description = description.Trim();
        return this;
    }

    public UiComponent VisibleWhen(Func<bool> predicate)
    {
        _visible = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    public UiComponent VisibleWhen(UiBinding<bool> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _visible = () => binding.Value;
        ObserveBinding(binding);
        return this;
    }

    public UiComponent EnabledWhen(Func<bool> predicate)
    {
        _enabled = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    public UiComponent EnabledWhen(UiBinding<bool> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _enabled = () => binding.Value;
        ObserveBinding(binding);
        return this;
    }

    public UiComponent WithTooltip(string text) => WithTooltip(() => text);

    public UiComponent WithTooltip(Func<string?> text)
    {
        _tooltip = text ?? throw new ArgumentNullException(nameof(text));
        return this;
    }

    public UiComponent WithTooltip(UiBinding<string?> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _tooltip = () => binding.Value;
        ObserveBinding(binding);
        return this;
    }

    internal void ObserveBinding<T>(UiBinding<T> binding)
    {
        if (binding.CanNotify) _subscriptions.Add(binding.Subscribe);
    }

    internal void ObserveInvalidation(Func<Action, IDisposable> subscribe) =>
        _subscriptions.Add(subscribe ?? throw new ArgumentNullException(nameof(subscribe)));

    internal IDisposable SubscribeInvalidated(Action invalidated)
    {
        if (_subscriptions.Count == 0) return SubscriptionGroup.Empty;
        return new SubscriptionGroup(
            _subscriptions.Select(subscribe => subscribe(invalidated)).ToArray());
    }

    private sealed class SubscriptionGroup(IReadOnlyList<IDisposable> subscriptions)
        : IDisposable
    {
        public static readonly SubscriptionGroup Empty = new([]);
        private IReadOnlyList<IDisposable>? _subscriptions = subscriptions;

        public void Dispose()
        {
            var subscriptions = Interlocked.Exchange(ref _subscriptions, null);
            if (subscriptions is null) return;
            foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }
}

public sealed class UiGroup : UiComponent
{
    internal UiGroup(bool horizontal, IReadOnlyList<UiComponent> children)
    {
        Horizontal = horizontal;
        Children = children;
    }

    public bool Horizontal { get; }
    public IReadOnlyList<UiComponent> Children { get; }
}

public sealed class UiSection : UiComponent
{
    internal UiSection(string title, IReadOnlyList<UiComponent> children)
    {
        Title = title;
        Children = children;
    }

    public string Title { get; }
    public IReadOnlyList<UiComponent> Children { get; }
}

/// <summary>A themed container for a reusable group of controls.</summary>
public sealed class UiCard : UiComponent
{
    internal UiCard(string? title, IReadOnlyList<UiComponent> children)
    {
        Title = title;
        Children = children;
        WithClass(UiClasses.Card);
    }

    public string? Title { get; }
    public IReadOnlyList<UiComponent> Children { get; }
}

/// <summary>A group that the user can expand and collapse.</summary>
public sealed class UiExpander : UiComponent
{
    internal UiExpander(
        string title,
        bool initiallyExpanded,
        IReadOnlyList<UiComponent> children)
    {
        Title = title;
        InitiallyExpanded = initiallyExpanded;
        Children = children;
    }

    public string Title { get; }
    public bool InitiallyExpanded { get; }
    public IReadOnlyList<UiComponent> Children { get; }
}

/// <summary>One named page inside a horizontal tab set.</summary>
public sealed class UiTab
{
    internal UiTab(string id, string label, UiComponent content)
    {
        Id = id;
        Label = label;
        Content = content;
    }

    public string Id { get; }
    public string Label { get; }
    public UiComponent Content { get; }
}

/// <summary>
/// A toolkit-neutral tab set. The selected identifier is writable so the host
/// and the mod keep one source of truth when the user changes pages.
/// </summary>
public sealed class UiTabs : UiComponent
{
    internal UiTabs(UiBinding<string> selection, IReadOnlyList<UiTab> items)
    {
        Selection = selection;
        Items = items;
        WithClass(UiClasses.Tabs);
        ObserveBinding(selection);
    }

    public UiBinding<string> Selection { get; }
    public IReadOnlyList<UiTab> Items { get; }
    public string SelectedId => Selection.Value;
    public void Select(string id) => Selection.SetValue(id);
}

/// <summary>A compact, themed status badge.</summary>
public sealed class UiStatus : UiComponent
{
    internal UiStatus(UiBinding<string> binding, UiStatusTone tone)
    {
        Binding = binding;
        Value = () => binding.Value;
        Tone = tone;
        WithClasses(UiClasses.Status, tone switch
        {
            UiStatusTone.Information => UiClasses.StatusInformation,
            UiStatusTone.Success => UiClasses.StatusSuccess,
            UiStatusTone.Warning => UiClasses.StatusWarning,
            UiStatusTone.Error => UiClasses.StatusError,
            _ => UiClasses.Status
        });
        ObserveBinding(binding);
    }

    public UiBinding<string> Binding { get; }
    public Func<string> Value { get; }
    public UiStatusTone Tone { get; }
}

public sealed class UiText : UiComponent
{
    internal UiText(UiBinding<string> binding, UiTextTone tone, bool wrap)
    {
        Binding = binding;
        Value = () => binding.Value;
        Tone = tone;
        Wrap = wrap;
        ObserveBinding(binding);
    }

    public UiBinding<string> Binding { get; }
    public Func<string> Value { get; }
    public UiTextTone Tone { get; }
    public bool Wrap { get; }
}

public sealed class UiIconView : UiComponent
{
    internal UiIconView(UiIcon icon, double size, UiTextTone tone)
    {
        Icon = icon;
        Size = size;
        Tone = tone;
    }

    public UiIcon Icon { get; }
    public double Size { get; }
    public UiTextTone Tone { get; }
}

public sealed class UiButton : UiComponent
{
    internal UiButton(UiBinding<string> label, ICommand command, UiIcon? icon)
    {
        LabelBinding = label;
        Label = () => label.Value;
        Command = command;
        Icon = icon;
        ObserveBinding(label);
        ObserveInvalidation(invalidated => new CommandSubscription(command, invalidated));
    }

    public UiBinding<string> LabelBinding { get; }
    public Func<string> Label { get; }
    public ICommand Command { get; }
    public UiIcon? Icon { get; }
    public bool CanExecute => Command.CanExecute(null);
    public void Execute() => Command.Execute(null);

    private sealed class CommandSubscription : IDisposable
    {
        private ICommand? _command;
        private readonly EventHandler _handler;

        public CommandSubscription(ICommand command, Action invalidated)
        {
            _command = command;
            _handler = (_, _) => invalidated();
            command.CanExecuteChanged += _handler;
        }

        public void Dispose()
        {
            var command = Interlocked.Exchange(ref _command, null);
            if (command is not null) command.CanExecuteChanged -= _handler;
        }
    }
}

public sealed class UiToggle : UiComponent
{
    internal UiToggle(string label, UiBinding<bool> binding)
    {
        Label = label;
        Binding = binding;
        Value = () => binding.Value;
        Changed = binding.SetValue;
        ObserveBinding(binding);
    }

    public string Label { get; }
    public UiBinding<bool> Binding { get; }
    public Func<bool> Value { get; }
    public Action<bool> Changed { get; }
}

public sealed class UiTextField : UiComponent
{
    internal UiTextField(
        string label,
        UiBinding<string> binding,
        bool secret,
        int maximumLength,
        string? hint,
        UiCommitMode commitMode)
    {
        Label = label;
        Binding = binding;
        Value = () => binding.Value;
        Changed = binding.SetValue;
        Secret = secret;
        MaximumLength = maximumLength;
        Hint = hint;
        CommitMode = commitMode;
        ObserveBinding(binding);
    }

    public string Label { get; }
    public UiBinding<string> Binding { get; }
    public Func<string> Value { get; }
    public Action<string> Changed { get; }
    public bool Secret { get; }
    public int MaximumLength { get; }
    public string? Hint { get; }
    public UiCommitMode CommitMode { get; }
}

public sealed class UiNumberField : UiComponent
{
    internal UiNumberField(
        string label,
        UiBinding<double> binding,
        double minimum,
        double maximum,
        double step,
        string format,
        bool integer,
        UiCommitMode commitMode)
    {
        Label = label;
        Binding = binding;
        Value = () => binding.Value;
        Changed = binding.SetValue;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Format = format;
        Integer = integer;
        CommitMode = commitMode;
        ObserveBinding(binding);
    }

    public string Label { get; }
    public UiBinding<double> Binding { get; }
    public Func<double> Value { get; }
    public Action<double> Changed { get; }
    public double Minimum { get; }
    public double Maximum { get; }
    public double Step { get; }
    public string Format { get; }
    public bool Integer { get; }
    public UiCommitMode CommitMode { get; }
}

public sealed class UiChoice : UiComponent
{
    internal UiChoice(string label, IReadOnlyList<UiChoiceItem> items)
    {
        Label = label;
        Items = items;
    }

    public string Label { get; }
    public IReadOnlyList<UiChoiceItem> Items { get; }
}

public sealed class UiChoiceItem
{
    internal UiChoiceItem(string label, Func<bool> selected, Action select)
    {
        Label = label;
        Selected = selected;
        Select = select;
    }

    public string Label { get; }
    public Func<bool> Selected { get; }
    public Action Select { get; }
}

public sealed class UiSeparator : UiComponent;
public sealed class UiSpacer : UiComponent;

/// <summary>
/// A subtree whose shape can change at runtime. The revision is optional; when
/// supplied, retained renderers rebuild the subtree only when it changes.
/// </summary>
public sealed class UiDynamic : UiComponent
{
    internal UiDynamic(
        Func<IReadOnlyList<UiComponent>> children,
        Func<long>? revision)
    {
        Children = children;
        Revision = revision;
    }

    public Func<IReadOnlyList<UiComponent>> Children { get; }
    public Func<long>? Revision { get; }
}

/// <summary>
/// Factory methods used by client mods to describe one panel. Components own
/// no Avalonia objects and therefore work with either backend.
/// </summary>
public static partial class Ui
{
    public static UiGroup Column(params UiComponent[] children) =>
        new(false, Validate(children));

    public static UiGroup Row(params UiComponent[] children) =>
        new(true, Validate(children));

    public static UiSection Section(string title, params UiComponent[] children) =>
        new(RequireText(title, nameof(title)), Validate(children));

    public static UiCard Card(params UiComponent[] children) =>
        new(null, Validate(children));

    public static UiCard Card(string title, params UiComponent[] children) =>
        new(RequireText(title, nameof(title)), Validate(children));

    public static UiExpander Expander(
        string title,
        bool initiallyExpanded = false,
        params UiComponent[] children) =>
        new(RequireText(title, nameof(title)), initiallyExpanded, Validate(children));

    public static UiTab Tab(string id, string label, UiComponent content) => new(
        RequireText(id, nameof(id)),
        RequireText(label, nameof(label)),
        content ?? throw new ArgumentNullException(nameof(content)));

    public static UiTabs Tabs(
        Func<string> selectedId,
        Action<string> changed,
        params UiTab[] items) => Tabs(
        UiBinding<string>.Create(
            selectedId ?? throw new ArgumentNullException(nameof(selectedId)),
            changed ?? throw new ArgumentNullException(nameof(changed))),
        items);

    public static UiTabs Tabs(UiBinding<string> selection, params UiTab[] items)
    {
        selection = RequireWritable(selection);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Length == 0 || items.Any(item => item is null))
            throw new ArgumentException(
                "A tab set needs at least one non-null tab.", nameof(items));
        if (items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException(
                "Every tab identifier must be unique.", nameof(items));
        return new UiTabs(selection, items);
    }

    public static UiStatus Status(
        string value,
        UiStatusTone tone = UiStatusTone.Neutral) =>
        Status(UiBinding<string>.Create(() => value), tone);

    public static UiStatus Status(
        Func<string> value,
        UiStatusTone tone = UiStatusTone.Neutral) =>
        Status(UiBinding<string>.Create(
            value ?? throw new ArgumentNullException(nameof(value))), tone);

    public static UiStatus Status(
        UiBinding<string> binding,
        UiStatusTone tone = UiStatusTone.Neutral) =>
        new(binding ?? throw new ArgumentNullException(nameof(binding)), tone);

    public static UiText Text(
        string value,
        UiTextTone tone = UiTextTone.Normal,
        bool wrap = true) => Text(() => value, tone, wrap);

    public static UiText Text(
        Func<string> value,
        UiTextTone tone = UiTextTone.Normal,
        bool wrap = true) => new(
            UiBinding<string>.Create(value ?? throw new ArgumentNullException(nameof(value))),
            tone,
            wrap);

    public static UiText Text(
        UiBinding<string> binding,
        UiTextTone tone = UiTextTone.Normal,
        bool wrap = true) => new(
            binding ?? throw new ArgumentNullException(nameof(binding)),
            tone,
            wrap);

    public static UiIconView Icon(
        UiIcon icon,
        double size = 18,
        UiTextTone tone = UiTextTone.Normal)
    {
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        return new UiIconView(icon, size, tone);
    }

    public static UiButton Button(string label, Action clicked) =>
        Button(label, new UiCommand(clicked));

    public static UiButton Button(string label, UiIcon icon, Action clicked) =>
        Button(label, new UiCommand(clicked), icon);

    public static UiButton Button(Func<string> label, Action clicked) =>
        new(
            UiBinding<string>.Create(label ?? throw new ArgumentNullException(nameof(label))),
            new UiCommand(clicked ?? throw new ArgumentNullException(nameof(clicked))),
            null);

    public static UiButton Button(
        string label,
        ICommand command,
        UiIcon? icon = null) => new(
            UiBinding<string>.Create(() => RequireText(label, nameof(label))),
            command ?? throw new ArgumentNullException(nameof(command)),
            icon);

    public static UiButton Button(
        UiBinding<string> label,
        ICommand command,
        UiIcon? icon = null) => new(
            label ?? throw new ArgumentNullException(nameof(label)),
            command ?? throw new ArgumentNullException(nameof(command)),
            icon);

    public static UiToggle Toggle(string label, Func<bool> value, Action<bool> changed) =>
        Toggle(label, UiBinding<bool>.Create(
            value ?? throw new ArgumentNullException(nameof(value)),
            changed ?? throw new ArgumentNullException(nameof(changed))));

    public static UiToggle Toggle(string label, UiBinding<bool> binding) =>
        new(RequireText(label, nameof(label)), RequireWritable(binding));

    public static UiTextField TextField(
        string label,
        Func<string> value,
        Action<string> changed,
        bool secret = false,
        int maximumLength = 1024,
        string? hint = null,
        UiCommitMode commitMode = UiCommitMode.Immediate)
    {
        if (maximumLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        return TextField(
            label,
            UiBinding<string>.Create(
                value ?? throw new ArgumentNullException(nameof(value)),
                changed ?? throw new ArgumentNullException(nameof(changed))),
            secret,
            maximumLength,
            hint,
            commitMode);
    }

    public static UiTextField TextField(
        string label,
        UiBinding<string> binding,
        bool secret = false,
        int maximumLength = 1024,
        string? hint = null,
        UiCommitMode commitMode = UiCommitMode.Immediate)
    {
        if (maximumLength < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        return new UiTextField(
            RequireText(label, nameof(label)),
            RequireWritable(binding),
            secret,
            maximumLength,
            hint,
            commitMode);
    }

    public static UiNumberField Number(
        string label,
        Func<int> value,
        Action<int> changed,
        int minimum,
        int maximum,
        int step = 1,
        string format = "0",
        UiCommitMode commitMode = UiCommitMode.Immediate) => Number(
            label,
            UiBinding<int>.Create(
                value ?? throw new ArgumentNullException(nameof(value)),
                changed ?? throw new ArgumentNullException(nameof(changed))),
            minimum,
            maximum,
            step,
            format,
            commitMode);

    public static UiNumberField Number(
        string label,
        UiBinding<int> binding,
        int minimum,
        int maximum,
        int step = 1,
        string format = "0",
        UiCommitMode commitMode = UiCommitMode.Immediate) => new(
            RequireText(label, nameof(label)),
            ConvertBinding(
                RequireWritable(binding),
                value => value,
                candidate => checked((int)Math.Round(candidate))),
            minimum,
            maximum,
            Math.Max(1, step),
            format,
            true,
            commitMode);

    public static UiNumberField Number(
        string label,
        Func<float> value,
        Action<float> changed,
        float minimum,
        float maximum,
        float step = 0.1f,
        string format = "0.###",
        UiCommitMode commitMode = UiCommitMode.Immediate) => Number(
            label,
            UiBinding<float>.Create(
                value ?? throw new ArgumentNullException(nameof(value)),
                changed ?? throw new ArgumentNullException(nameof(changed))),
            minimum,
            maximum,
            step,
            format,
            commitMode);

    public static UiNumberField Number(
        string label,
        UiBinding<float> binding,
        float minimum,
        float maximum,
        float step = 0.1f,
        string format = "0.###",
        UiCommitMode commitMode = UiCommitMode.Immediate) => new(
            RequireText(label, nameof(label)),
            ConvertBinding(
                RequireWritable(binding),
                value => value,
                candidate => (float)candidate),
            minimum,
            maximum,
            step,
            format,
            false,
            commitMode);

    public static UiNumberField Number(
        string label,
        Func<double> value,
        Action<double> changed,
        double minimum,
        double maximum,
        double step = 0.1,
        string format = "0.###",
        UiCommitMode commitMode = UiCommitMode.Immediate) => Number(
            label,
            UiBinding<double>.Create(
                value ?? throw new ArgumentNullException(nameof(value)),
                changed ?? throw new ArgumentNullException(nameof(changed))),
            minimum,
            maximum,
            step,
            format,
            commitMode);

    public static UiNumberField Number(
        string label,
        UiBinding<double> binding,
        double minimum,
        double maximum,
        double step = 0.1,
        string format = "0.###",
        UiCommitMode commitMode = UiCommitMode.Immediate) => new(
            RequireText(label, nameof(label)),
            RequireWritable(binding),
            minimum,
            maximum,
            step,
            format,
            false,
            commitMode);

    public static UiChoice Choice<T>(
        string label,
        Func<T> value,
        Action<T> changed,
        IEnumerable<T> choices,
        Func<T, string>? display = null,
        IEqualityComparer<T>? comparer = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(changed);
        return Choice(
            label,
            UiBinding<T>.Create(value, changed),
            choices,
            display,
            comparer);
    }

    public static UiChoice Choice<T>(
        string label,
        UiBinding<T> binding,
        IEnumerable<T> choices,
        Func<T, string>? display = null,
        IEqualityComparer<T>? comparer = null)
    {
        binding = RequireWritable(binding);
        ArgumentNullException.ThrowIfNull(choices);
        display ??= candidate => candidate?.ToString() ?? "";
        comparer ??= EqualityComparer<T>.Default;
        var items = choices.Select(candidate => new UiChoiceItem(
                display(candidate),
                () => comparer.Equals(binding.Value, candidate),
                () => binding.SetValue(candidate)))
            .ToArray();
        if (items.Length == 0)
            throw new ArgumentException("A choice component needs at least one item.", nameof(choices));
        var choice = new UiChoice(RequireText(label, nameof(label)), items);
        choice.ObserveBinding(binding);
        return choice;
    }

    public static UiDynamic Dynamic(
        Func<IReadOnlyList<UiComponent>> children,
        Func<long>? revision = null) =>
        new(children ?? throw new ArgumentNullException(nameof(children)), revision);

    public static UiSeparator Separator() => new();
    public static UiSpacer Spacer() => new();

    private static UiBinding<T> RequireWritable<T>(UiBinding<T> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!binding.CanWrite)
            throw new ArgumentException("An editable control requires a writable binding.", nameof(binding));
        return binding;
    }

    private static UiBinding<double> ConvertBinding<T>(
        UiBinding<T> binding,
        Func<T, double> read,
        Func<double, T> write) => UiBinding<double>.Create(
            () => read(binding.Value),
            value => binding.SetValue(write(value)),
            binding.CanNotify ? binding.Subscribe : null);

    private static UiComponent[] Validate(UiComponent[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Any(child => child is null))
            throw new ArgumentException("A UI child cannot be null.", nameof(children));
        return children;
    }

    private static string RequireText(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("The value cannot be empty.", parameter)
            : value.Trim();
}
