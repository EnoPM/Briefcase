#if !BRIEFCASE_HEADLESS
using System.Text.Json;
using Briefcase.ClientModApi;
using Briefcase.ModApi;

namespace Briefcase.ManagedHost;

/// <summary>
/// Owns the framework settings document and the F1 configuration window.
/// A ModScope is created before each mod loads, so Bind can restore values
/// before the mod starts doing work. Disposing the scope also removes every
/// callback that could otherwise keep a collectible mod assembly alive.
/// </summary>
internal sealed partial class ConfigurationRegistry : IDisposable
{
    internal const string FrameworkTabId = "$briefcase";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Action<string> _info;
    private readonly Action<string> _warning;
    private readonly Action<string> _error;
    internal readonly object _gate = new();
    internal readonly Dictionary<string, ModScope> _scopes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saveTimer;
    internal FrameworkSettingsDocument _document;
    private bool _disposed;
    private long _uiRevision;
    internal IFrameworkModControl? _modControl;

    internal long UiRevision => Volatile.Read(ref _uiRevision);

    internal void ReportClientUiError(Exception exception) =>
        _error($"Client UI component failed: {exception}");

    private void NotifyUiChanged() => Interlocked.Increment(ref _uiRevision);

    public ConfigurationRegistry(
        string path,
        Action<string> info,
        Action<string> warning,
        Action<string> error)
    {
        _path = path;
        _info = info;
        _warning = warning;
        _error = error;
        _document = LoadDocument();
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ModScope RegisterMod(ModInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        ModScope scope;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scopes.ContainsKey(info.Id))
                throw new InvalidOperationException(
                    $"A configuration tab is already registered for mod '{info.Id}'.");

            if (!_document.Mods.TryGetValue(info.Id, out var persisted))
            {
                persisted = new ModSettingsDocument();
                _document.Mods.Add(info.Id, persisted);
                ScheduleSave();
            }

            scope = new ModScope(this, info, persisted);
            _scopes.Add(info.Id, scope);
        }
        NotifyUiChanged();
        return scope;
    }

    public void AttachModControl(IFrameworkModControl modControl) =>
        _modControl = modControl ?? throw new ArgumentNullException(nameof(modControl));

    public bool IsModEnabled(string fileName)
    {
        lock (_gate)
            return !_document.EnabledMods.TryGetValue(fileName, out var enabled) || enabled;
    }

    public void SetModEnabled(string fileName, bool enabled)
    {
        lock (_gate)
        {
            _document.EnabledMods[fileName] = enabled;
            ScheduleSave();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var scope in _scopes.Values.ToArray())
                scope.DisposeFromOwner();
            _scopes.Clear();
        }
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();
        _saveTimer.Dispose();
    }

    internal void TryModAction(Action action)
    {
        try { action(); }
        catch (Exception exception) { _error(exception.Message); }
    }

    internal void OpenModsDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _error($"Could not open the Mods directory: {exception.Message}");
        }
    }

    internal void RememberSelectedMod(string modId)
    {
        lock (_gate)
        {
            if (string.Equals(
                    _document.Window.SelectedModId,
                    modId,
                    StringComparison.OrdinalIgnoreCase))
                return;
            _document.Window.SelectedModId = modId;
            ScheduleSave();
        }
    }

    internal void RememberSelectedView(string view)
    {
        lock (_gate)
        {
            if (string.Equals(_document.Window.SelectedView, view,
                    StringComparison.OrdinalIgnoreCase)) return;
            _document.Window.SelectedView = view;
            ScheduleSave();
        }
    }

    private FrameworkSettingsDocument LoadDocument()
    {
        try
        {
            if (!File.Exists(_path)) return new FrameworkSettingsDocument();
            var document = JsonSerializer.Deserialize<FrameworkSettingsDocument>(
                               File.ReadAllText(_path), JsonOptions)
                           ?? new FrameworkSettingsDocument();
            document.Window ??= new FrameworkWindowSettings();
            var mods = new Dictionary<string, ModSettingsDocument>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in document.Mods ?? [])
            {
                var mod = pair.Value ?? new ModSettingsDocument();
                mod.Values = new Dictionary<string, JsonElement>(
                    mod.Values ?? [], StringComparer.OrdinalIgnoreCase);
                mods[pair.Key] = mod;
            }
            document.Mods = mods;
            document.EnabledMods = new Dictionary<string, bool>(
                document.EnabledMods ?? [], StringComparer.OrdinalIgnoreCase);
            return document;
        }
        catch (Exception exception)
        {
            _warning($"Could not load Briefcase settings; defaults will be used: {exception.Message}");
            return new FrameworkSettingsDocument();
        }
    }

    private void EntryChanged(ModScope scope, IConfigurationEntry entry)
    {
        lock (_gate)
        {
            if (_disposed || scope.IsDisposed) return;
            scope.Persisted.Values[scope.EntryId(entry.Section, entry.Key)] =
                JsonSerializer.SerializeToElement(
                    entry.BoxedValue, entry.ValueType, JsonOptions);
            ScheduleSave();
        }
    }

    private void Remove(ModScope scope)
    {
        var removed = false;
        lock (_gate)
        {
            if (_scopes.TryGetValue(scope.Info.Id, out var registered) &&
                ReferenceEquals(scope, registered))
                removed = _scopes.Remove(scope.Info.Id);
        }
        if (removed)
        {
            RemoveConfigurationDraft(scope.Info.Id);
            NotifyUiChanged();
        }
    }

    private void ScheduleSave() =>
        _saveTimer.Change(TimeSpan.FromMilliseconds(350), Timeout.InfiniteTimeSpan);

    private void SaveNow()
    {
        try
        {
            string json;
            lock (_gate)
                json = JsonSerializer.Serialize(_document, JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, _path, true);
        }
        catch (Exception exception)
        {
            _error($"Could not save Briefcase settings: {exception.Message}");
        }
    }

    private static bool NearlyEqual(float left, float right) =>
        Math.Abs(left - right) < 0.5f;

    internal sealed class ModScope : IModConfigurationScope, IClientUiScope, IDisposable
    {
        private readonly ConfigurationRegistry _owner;
        private readonly object _gate = new();
        private readonly Dictionary<string, IConfigurationEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<UiPanelRegistration> _uiPanels = [];
        private readonly List<ServerUiPanelRegistration> _serverUiPanels = [];

        public ModScope(
            ConfigurationRegistry owner,
            ModInfo info,
            ModSettingsDocument persisted)
        {
            _owner = owner;
            Info = info;
            Persisted = persisted;
        }

        public ModInfo Info { get; }
        public ModSettingsDocument Persisted { get; }
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Legacy signal retained for compatibility tests and earlier menu hosts.
        /// The current framework-owned menu renders registered content inside the
        /// selected mod's detail view in the central Mods page.
        /// </summary>
        public bool ShouldShowClientTab
        {
            get
            {
                lock (_gate)
                    return Info.ShowConfigurationTab &&
                           _uiPanels.Any(panel => panel.IsActive);
            }
        }

        public ConfigEntry<T> Bind<T>(
            string section,
            string key,
            T defaultValue,
            string description,
            ConfigurationRange<T>? range,
            bool secret)
            where T : notnull
        {
            section = string.IsNullOrWhiteSpace(section) ? "General" : section.Trim();
            key = string.IsNullOrWhiteSpace(key)
                ? throw new ArgumentException("A configuration key cannot be empty.", nameof(key))
                : key.Trim();
            description = description?.Trim() ?? "";
            ValidateType<T>(secret);
            ValidateRange(range);
            var id = EntryId(section, key);

            ConfigEntry<T> entry;
            var mustPersistDefault = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                if (_entries.TryGetValue(id, out var existing))
                {
                    if (existing is ConfigEntry<T> typed) return typed;
                    throw new InvalidOperationException(
                        $"Configuration entry '{section}/{key}' was already bound as " +
                        $"{existing.ValueType.Name}, not {typeof(T).Name}.");
                }

                var value = LoadPersisted(id, defaultValue);
                entry = new ConfigEntry<T>(
                    section, key, description, value, range, secret,
                    candidate => Normalize(candidate, range),
                    changed => _owner.EntryChanged(this, changed));
                _entries.Add(id, entry);
                mustPersistDefault = !Persisted.Values.ContainsKey(id);
            }
            // Do not acquire the registry lock while holding the scope lock.
            // Registry disposal intentionally takes those locks in the opposite
            // order so it can atomically stop all tabs.
            if (mustPersistDefault) _owner.EntryChanged(this, entry);
            return entry;
        }

        IDisposable IClientUiScope.RegisterPanel(UiComponent content)
        {
            ArgumentNullException.ThrowIfNull(content);
            UiPanelRegistration registration;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                registration = new UiPanelRegistration(this, content);
                _uiPanels.Add(registration);
            }
            _owner.NotifyUiChanged();
            return registration;
        }

        IDisposable IClientUiScope.RegisterServerPanel(
            string name,
            UiComponent content,
            UiComponent? toolbar,
            UiComponent? footer)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(content);
            ServerUiPanelRegistration registration;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                registration = new ServerUiPanelRegistration(
                    this, name.Trim(), content, toolbar, footer);
                _serverUiPanels.Add(registration);
            }
            _owner.NotifyUiChanged();
            return registration;
        }

        /// <summary>
        /// Adds both persistent configuration and the client-only UI capability to a
        /// mod context. Keeping the two together prevents alternate loading paths from
        /// creating a client mod context that cannot register its panel.
        /// </summary>
        public ModContext AttachTo(ModContext context) =>
            context.WithConfiguration(this).WithExtension(this);

        public IConfigurationEntry[] SnapshotEntries()
        {
            lock (_gate) return _entries.Values.ToArray();
        }

        public UiPanelRegistration[] SnapshotUiPanels()
        {
            lock (_gate) return _uiPanels.Where(panel => panel.IsActive).ToArray();
        }

        public ServerUiPanelRegistration[] SnapshotServerUiPanels()
        {
            lock (_gate) return _serverUiPanels.Where(panel => panel.IsActive).ToArray();
        }

        public string EntryId(string section, string key) => $"{section}/{key}";

        public void Dispose()
        {
            DisposeFromOwner();
            _owner.Remove(this);
        }

        public void DisposeFromOwner()
        {
            lock (_gate)
            {
                if (IsDisposed) return;
                IsDisposed = true;
                foreach (var panel in _uiPanels.ToArray()) panel.DisposeFromOwner();
                foreach (var panel in _serverUiPanels.ToArray()) panel.DisposeFromOwner();
                _uiPanels.Clear();
                _serverUiPanels.Clear();
                _entries.Clear();
            }
        }

        private void Remove(UiPanelRegistration panel)
        {
            lock (_gate) _uiPanels.Remove(panel);
            _owner.NotifyUiChanged();
        }

        private void Remove(ServerUiPanelRegistration panel)
        {
            lock (_gate) _serverUiPanels.Remove(panel);
            _owner.NotifyUiChanged();
        }

        private T LoadPersisted<T>(string id, T fallback) where T : notnull
        {
            if (!Persisted.Values.TryGetValue(id, out var element)) return fallback;
            try
            {
                return element.Deserialize<T>(JsonOptions) ?? fallback;
            }
            catch (Exception exception)
            {
                _owner._warning(
                    $"Ignoring invalid setting {Info.Id}/{id}: {exception.Message}");
                return fallback;
            }
        }

        private static void ValidateType<T>(bool secret) where T : notnull
        {
            var type = typeof(T);
            if (type != typeof(bool) && type != typeof(int) && type != typeof(float) &&
                type != typeof(double) && type != typeof(string) && !type.IsEnum)
                throw new NotSupportedException(
                    $"Configuration type {type.FullName} is not supported. " +
                    "Use bool, int, float, double, string, or an enum.");
            if (secret && type != typeof(string))
                throw new ArgumentException("Only string settings can use secret input.", nameof(secret));
        }

        private static void ValidateRange<T>(ConfigurationRange<T>? range) where T : notnull
        {
            if (range is null) return;
            if (typeof(T) != typeof(int) && typeof(T) != typeof(float) && typeof(T) != typeof(double))
                throw new ArgumentException("Ranges are supported only for numeric settings.", nameof(range));
            if (Comparer<T>.Default.Compare(range.Minimum, range.Maximum) > 0)
                throw new ArgumentException("The minimum cannot exceed the maximum.", nameof(range));
        }

        private static T Normalize<T>(T value, ConfigurationRange<T>? range) where T : notnull
        {
            if (range is null) return value;
            if (Comparer<T>.Default.Compare(value, range.Minimum) < 0) return range.Minimum;
            if (Comparer<T>.Default.Compare(value, range.Maximum) > 0) return range.Maximum;
            return value;
        }

        internal abstract class ComponentRegistration : IDisposable
        {
            private int _active = 1;
            protected ComponentRegistration(ModScope owner, UiComponent content)
            {
                Owner = owner;
                Content = content;
            }

            protected ModScope Owner { get; }
            public UiComponent Content { get; }
            public bool IsActive => Volatile.Read(ref _active) != 0;
            protected bool Deactivate() => Interlocked.Exchange(ref _active, 0) != 0;
            public void DisposeFromOwner() => Interlocked.Exchange(ref _active, 0);
            public abstract void Dispose();
        }

        internal sealed class UiPanelRegistration(ModScope owner, UiComponent content)
            : ComponentRegistration(owner, content)
        {
            public override void Dispose()
            {
                if (Deactivate()) Owner.Remove(this);
            }
        }

        internal sealed class ServerUiPanelRegistration(
            ModScope owner,
            string name,
            UiComponent content,
            UiComponent? toolbar,
            UiComponent? footer) : ComponentRegistration(owner, content)
        {
            public string Name { get; } = name;
            public UiComponent? Toolbar { get; } = toolbar;
            public UiComponent? Footer { get; } = footer;
            public override void Dispose()
            {
                if (Deactivate()) Owner.Remove(this);
            }
        }

    }

    internal sealed class FrameworkSettingsDocument
    {
        public FrameworkWindowSettings Window { get; set; } = new();
        public Dictionary<string, ModSettingsDocument> Mods { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> EnabledMods { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class FrameworkWindowSettings
    {
        public float X { get; set; } = 80;
        public float Y { get; set; } = 80;
        public float Width { get; set; } = 780;
        public float Height { get; set; } = 560;
        public string SelectedModId { get; set; } = "";
        public string SelectedView { get; set; } = "Client";
    }

    internal sealed class ModSettingsDocument
    {
        public Dictionary<string, JsonElement> Values { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
#endif
