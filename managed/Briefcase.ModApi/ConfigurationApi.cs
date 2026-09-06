namespace Briefcase.ModApi;

/// <summary>
/// A value owned and persisted by Briefcase. Mods keep the returned entry and
/// read <see cref="Value"/> whenever behavior depends on the setting.
/// Assigning Value from mod code uses the same validation and persistence path
/// as a change made in the F1 configuration menu.
/// </summary>
public sealed class ConfigEntry<T> : IConfigurationEntry where T : notnull
{
    private readonly object _gate = new();
    private readonly Func<T, T> _normalize;
    private readonly Action<IConfigurationEntry> _changed;
    private T _value;

    internal ConfigEntry(
        string section,
        string key,
        string description,
        T value,
        ConfigurationRange<T>? range,
        bool secret,
        Func<T, T> normalize,
        Action<IConfigurationEntry> changed)
    {
        Section = section;
        Key = key;
        Description = description;
        Range = range;
        Secret = secret;
        _normalize = normalize;
        _changed = changed;
        _value = normalize(value);
    }

    public string Section { get; }
    public string Key { get; }
    public string Description { get; }
    public ConfigurationRange<T>? Range { get; }
    public bool Secret { get; }

    public T Value
    {
        get
        {
            lock (_gate) return _value;
        }
        set
        {
            value = _normalize(value);
            lock (_gate)
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
            }
            _changed(this);
            ValueChanged?.Invoke(value);
        }
    }

    /// <summary>Raised after the value has changed and been queued for saving.</summary>
    public event Action<T>? ValueChanged;

    Type IConfigurationEntry.ValueType => typeof(T);
    object IConfigurationEntry.BoxedValue
    {
        get => Value;
        set => Value = (T)value;
    }
    object? IConfigurationEntry.Minimum => Range is null ? null : Range.Minimum;
    object? IConfigurationEntry.Maximum => Range is null ? null : Range.Maximum;
}

/// <summary>Optional inclusive limits for numeric configuration values.</summary>
public sealed record ConfigurationRange<T>(T Minimum, T Maximum) where T : notnull;

/// <summary>
/// The configuration surface scoped to the mod currently being loaded.
/// Settings appear automatically in that mod's tab in the F1 menu.
/// </summary>
public readonly struct ConfigurationApi
{
    private readonly IModConfigurationScope? _scope;

    internal ConfigurationApi(IModConfigurationScope? scope) => _scope = scope;

    public bool IsAvailable => _scope is not null;

    /// <summary>
    /// Declares a persistent setting. Supported types are bool, int, float,
    /// double, string, and enums. A repeated section/key pair returns the same
    /// entry when its type is unchanged.
    /// </summary>
    public ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        string description = "",
        ConfigurationRange<T>? range = null,
        bool secret = false)
        where T : notnull
    {
        if (_scope is null)
            throw new InvalidOperationException(
                "Configuration is available only to mods loaded by the managed Briefcase host.");
        return _scope.Bind(section, key, defaultValue, description, range, secret);
    }

    /// <summary>
    /// Adds rich ImGui content after the automatically generated controls in
    /// this mod's tab. The callback runs on Briefcase's rendering thread while
    /// an ImGui frame is active and must be disposed during Unload.
    /// </summary>
    public IDisposable RegisterPanel(Action<ConfigurationPanelContext> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (_scope is null)
            throw new InvalidOperationException(
                "Configuration is available only to mods loaded by the managed Briefcase host.");
        return _scope.RegisterPanel(draw);
    }

    /// <summary>
    /// Adds a page to the framework's Server view. This is intended for
    /// server-oriented features such as balancing editors; the framework owns
    /// connection settings and the remote mod lifecycle page.
    /// </summary>
    public IDisposable RegisterServerPanel(
        string name,
        Action<ConfigurationPanelContext> draw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(draw);
        if (_scope is null)
            throw new InvalidOperationException(
                "Configuration is available only to mods loaded by the managed Briefcase host.");
        return _scope.RegisterServerPanel(name.Trim(), draw);
    }
}

/// <summary>Frame information supplied to a mod's custom configuration panel.</summary>
public readonly record struct ConfigurationPanelContext(RenderFrame Frame)
{
    public ImGuiApi ImGui => Frame.ImGui;
}

internal interface IConfigurationEntry
{
    string Section { get; }
    string Key { get; }
    string Description { get; }
    Type ValueType { get; }
    object BoxedValue { get; set; }
    object? Minimum { get; }
    object? Maximum { get; }
    bool Secret { get; }
}

internal interface IModConfigurationScope
{
    ConfigEntry<T> Bind<T>(
        string section,
        string key,
        T defaultValue,
        string description,
        ConfigurationRange<T>? range,
        bool secret)
        where T : notnull;

    IDisposable RegisterPanel(Action<ConfigurationPanelContext> draw);
    IDisposable RegisterServerPanel(string name, Action<ConfigurationPanelContext> draw);
}
