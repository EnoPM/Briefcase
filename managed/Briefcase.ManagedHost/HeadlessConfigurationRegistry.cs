#if BRIEFCASE_HEADLESS
using System.Text.Json;
using Briefcase.ModApi;

namespace Briefcase.ManagedHost;

/// <summary>
/// Persists server-mod settings without creating a window, input hook, or
/// client UI callback. It deliberately exposes the same scope contract as the
/// client registry so the managed mod loader stays target agnostic.
/// </summary>
internal sealed class ConfigurationRegistry : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Action<string> _warning;
    private readonly Action<string> _error;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModScope> _scopes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saveTimer;
    private SettingsDocument _document;
    private bool _disposed;

    public ConfigurationRegistry(
        string path,
        Action<string> info,
        Action<string> warning,
        Action<string> error)
    {
        _path = path;
        _warning = warning;
        _error = error;
        _document = Load();
        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        info("Headless configuration registry started; rendering and the F1 menu are disabled.");
    }

    public ModScope RegisterMod(ModInfo info)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_scopes.ContainsKey(info.Id))
                throw new InvalidOperationException($"Configuration already exists for '{info.Id}'.");
            if (!_document.Mods.TryGetValue(info.Id, out var persisted))
            {
                persisted = new ModSettingsDocument();
                _document.Mods.Add(info.Id, persisted);
                ScheduleSave();
            }
            var scope = new ModScope(this, info, persisted);
            _scopes.Add(info.Id, scope);
            return scope;
        }
    }

    public void AttachModControl(IFrameworkModControl _) { }

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
        ModScope[] scopes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            scopes = _scopes.Values.ToArray();
            _scopes.Clear();
        }
        foreach (var scope in scopes) scope.DisposeFromOwner();
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();
        _saveTimer.Dispose();
    }

    private SettingsDocument Load()
    {
        try
        {
            if (!File.Exists(_path)) return new SettingsDocument();
            return JsonSerializer.Deserialize<SettingsDocument>(
                       File.ReadAllText(_path), JsonOptions) ?? new SettingsDocument();
        }
        catch (Exception exception)
        {
            _warning($"Could not load Briefcase settings: {exception.Message}");
            return new SettingsDocument();
        }
    }

    private void EntryChanged(ModScope scope, IConfigurationEntry entry)
    {
        lock (_gate)
        {
            if (_disposed) return;
            scope.Persisted.Values[scope.EntryId(entry.Section, entry.Key)] =
                JsonSerializer.SerializeToElement(entry.BoxedValue, entry.ValueType, JsonOptions);
            ScheduleSave();
        }
    }

    private void Remove(ModScope scope)
    {
        lock (_gate) _scopes.Remove(scope.Info.Id);
    }

    private void ScheduleSave() =>
        _saveTimer.Change(TimeSpan.FromMilliseconds(350), Timeout.InfiniteTimeSpan);

    private void SaveNow()
    {
        try
        {
            string json;
            lock (_gate) json = JsonSerializer.Serialize(_document, JsonOptions);
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

    internal sealed class ModScope : IModConfigurationScope, IDisposable
    {
        private readonly ConfigurationRegistry _owner;
        private readonly object _gate = new();
        private readonly Dictionary<string, IConfigurationEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);

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
                        $"Configuration entry '{id}' was already bound as {existing.ValueType.Name}.");
                }

                var value = LoadPersisted(id, defaultValue);
                entry = new ConfigEntry<T>(
                    section, key, description?.Trim() ?? "", value, range, secret,
                    candidate => Normalize(candidate, range),
                    changed => _owner.EntryChanged(this, changed));
                _entries.Add(id, entry);
                mustPersistDefault = !Persisted.Values.ContainsKey(id);
            }
            if (mustPersistDefault) _owner.EntryChanged(this, entry);
            return entry;
        }

        /// <summary>
        /// Adds persistent configuration to a server-mod context. The headless scope
        /// intentionally has no client UI extension.
        /// </summary>
        public ModContext AttachTo(ModContext context) =>
            context.WithConfiguration(this);

        public IConfigurationEntry[] SnapshotEntries()
        {
            lock (_gate) return _entries.Values.ToArray();
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
                IsDisposed = true;
                _entries.Clear();
            }
        }

        private T LoadPersisted<T>(string id, T fallback) where T : notnull
        {
            if (!Persisted.Values.TryGetValue(id, out var element)) return fallback;
            try { return element.Deserialize<T>(JsonOptions) ?? fallback; }
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
                throw new NotSupportedException($"Configuration type {type.FullName} is not supported.");
            if (secret && type != typeof(string))
                throw new ArgumentException("Only string settings can be secret.", nameof(secret));
        }

        private static void ValidateRange<T>(ConfigurationRange<T>? range) where T : notnull
        {
            if (range is null) return;
            if (typeof(T) != typeof(int) && typeof(T) != typeof(float) && typeof(T) != typeof(double))
                throw new ArgumentException("Ranges are supported only for numeric settings.");
            if (Comparer<T>.Default.Compare(range.Minimum, range.Maximum) > 0)
                throw new ArgumentException("The minimum cannot exceed the maximum.");
        }

        private static T Normalize<T>(T value, ConfigurationRange<T>? range) where T : notnull
        {
            if (range is null) return value;
            if (Comparer<T>.Default.Compare(value, range.Minimum) < 0) return range.Minimum;
            if (Comparer<T>.Default.Compare(value, range.Maximum) > 0) return range.Maximum;
            return value;
        }
    }

    internal sealed class SettingsDocument
    {
        // Window is retained only to read settings files produced by a client
        // install; the headless host never creates or updates UI state.
        public JsonElement? Window { get; set; }
        public Dictionary<string, ModSettingsDocument> Mods { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool> EnabledMods { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ModSettingsDocument
    {
        public Dictionary<string, JsonElement> Values { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
#endif
