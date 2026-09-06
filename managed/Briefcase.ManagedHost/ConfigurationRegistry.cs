#if !BRIEFCASE_HEADLESS
using System.Numerics;
using System.Text.Json;
using Briefcase.ModApi;
using ImGuiNET;

namespace Briefcase.ManagedHost;

/// <summary>
/// Owns the framework settings document and the F1 configuration window.
/// A ModScope is created before each mod loads, so Bind can restore values
/// before the mod starts doing work. Disposing the scope also removes every
/// callback that could otherwise keep a collectible mod assembly alive.
/// </summary>
internal sealed class ConfigurationRegistry : IDisposable
{
    private const string FrameworkTabId = "$briefcase";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly Action<string> _info;
    private readonly Action<string> _warning;
    private readonly Action<string> _error;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModScope> _scopes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _saveTimer;
    private FrameworkSettingsDocument _document;
    private bool _placementApplied;
    private bool _disposed;
    private IFrameworkModControl? _modControl;

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

            var scope = new ModScope(this, info, persisted);
            _scopes.Add(info.Id, scope);
            return scope;
        }
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

    /// <summary>Draws the only window controlled by the framework F1 key.</summary>
    public bool Draw(RenderFrame frame)
    {
        ModScope[] scopes;
        FrameworkWindowSettings window;
        lock (_gate)
        {
            if (_disposed) return false;
            scopes = _scopes.Values
                .OrderBy(scope => scope.Info.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            window = _document.Window;
        }

        ApplySavedPlacement(frame, window);
        ImGui.SetNextWindowSizeConstraints(new Vector2(520, 360), new Vector2(
            Math.Max(520, frame.Width), Math.Max(360, frame.Height)));

        var open = true;
        var visible = ImGui.Begin(
            "Briefcase configuration###Briefcase.Framework.Configuration",
            ref open,
            ImGuiNET.ImGuiWindowFlags.NoSavedSettings);
        try
        {
            RememberPlacement();
            if (!visible) return open;

            ImGui.TextColored(new Vector4(72 / 255f, 219 / 255f, 184 / 255f, 1),
                "BRIEFCASE");
            ImGui.SameLine();
            ImGui.TextDisabled("Mod configuration");
            ImGui.Separator();

            var serverPanels = scopes
                .SelectMany(scope => scope.SnapshotServerPanels())
                .OrderBy(panel => panel.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var serverView = string.Equals(
                window.SelectedView, "Server", StringComparison.OrdinalIgnoreCase);
            if (ImGui.RadioButton("Client", !serverView))
            {
                serverView = false;
                RememberSelectedView("Client");
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Server", serverView))
            {
                serverView = true;
                RememberSelectedView("Server");
            }
            ImGui.Separator();

            if (serverView)
            {
                DrawServerView(frame, serverPanels);
                return open;
            }

            var clientScopes = scopes.Where(scope => scope.Info.ShowConfigurationTab).ToArray();
            var frameworkSelected = string.Equals(
                window.SelectedModId, FrameworkTabId, StringComparison.OrdinalIgnoreCase);
            var selectedScope = clientScopes.FirstOrDefault(scope =>
                                    string.Equals(
                                        scope.Info.Id,
                                        window.SelectedModId,
                                        StringComparison.OrdinalIgnoreCase))
                                ?? clientScopes.FirstOrDefault();
            if (!frameworkSelected && selectedScope is null)
            {
                frameworkSelected = true;
                RememberSelectedMod(FrameworkTabId);
            }

            var available = ImGui.GetContentRegionAvail();
            var navigationWidth = Math.Clamp(available.X * 0.27f, 190, 260);
            var navigationVisible = ImGui.BeginChild(
                "Briefcase.ModNavigation",
                new Vector2(navigationWidth, 0),
                ImGuiNET.ImGuiChildFlags.Borders);
            try
            {
                if (navigationVisible)
                {
                    if (ImGui.Selectable(
                            "Briefcase###Briefcase.FrameworkTab",
                            frameworkSelected,
                            ImGuiNET.ImGuiSelectableFlags.None,
                            new Vector2(0, 46)))
                    {
                        frameworkSelected = true;
                        RememberSelectedMod(FrameworkTabId);
                    }
                    ImGui.Separator();
                    foreach (var scope in clientScopes)
                    {
                        var selected = !frameworkSelected && ReferenceEquals(scope, selectedScope);
                        if (ImGui.Selectable(
                                $"{scope.Info.Name}###{scope.Info.Id}",
                                selected,
                                ImGuiNET.ImGuiSelectableFlags.None,
                                new Vector2(0, 42)))
                        {
                            frameworkSelected = false;
                            selectedScope = scope;
                            RememberSelectedMod(scope.Info.Id);
                        }
                    }
                }
            }
            finally
            {
                ImGui.EndChild();
            }
            ImGui.SameLine();
            var contentVisible = ImGui.BeginChild(
                "Briefcase.ModContent",
                Vector2.Zero,
                ImGuiNET.ImGuiChildFlags.Borders);
            try
            {
                if (contentVisible)
                {
                    if (frameworkSelected)
                        DrawFrameworkContent();
                    else if (selectedScope is not null)
                        DrawModContent(selectedScope, frame);
                }
            }
            finally
            {
                ImGui.EndChild();
            }
        }
        finally
        {
            ImGui.End();
        }
        return open;
    }

    private void DrawServerView(
        RenderFrame frame,
        ModScope.ServerPanelRegistration[] panels)
    {
        var visible = ImGui.BeginChild(
            "Briefcase.ServerContent", Vector2.Zero, ImGuiNET.ImGuiChildFlags.Borders);
        try
        {
            if (!visible) return;
            if (panels.Length == 0)
            {
                ImGui.TextDisabled("The Briefcase server administration service is unavailable.");
                return;
            }

            if (panels.Length == 1)
            {
                InvokeServerPanel(panels[0], frame);
                return;
            }

            if (!ImGui.BeginTabBar("Briefcase.ServerPanels")) return;
            try
            {
                foreach (var panel in panels)
                {
                    if (!ImGui.BeginTabItem(panel.Name)) continue;
                    InvokeServerPanel(panel, frame);
                    ImGui.EndTabItem();
                }
            }
            finally { ImGui.EndTabBar(); }
        }
        finally { ImGui.EndChild(); }
    }

    private void InvokeServerPanel(
        ModScope.ServerPanelRegistration panel,
        RenderFrame frame)
    {
        try { panel.Invoke(new ConfigurationPanelContext(frame)); }
        catch (Exception exception)
        {
            _error($"Server panel '{panel.Name}' failed: {exception}");
            ImGui.TextColored(new Vector4(1, 0.35f, 0.35f, 1),
                "The server panel raised an exception. See Briefcase.log.");
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

    private void DrawModContent(ModScope scope, RenderFrame frame)
    {
        ImGui.PushID(scope.Info.Id);
        try
        {
            ImGui.TextColored(
                new Vector4(72 / 255f, 219 / 255f, 184 / 255f, 1),
                scope.Info.Name);
            ImGui.TextDisabled(
                $"{scope.Info.Author}  |  {scope.Info.Version}  |  {scope.Info.Id}");
            if (!string.IsNullOrWhiteSpace(scope.Info.Description))
                ImGui.TextWrapped(scope.Info.Description);
            ImGui.Separator();

            var entries = scope.SnapshotEntries();
            foreach (var section in entries
                         .GroupBy(entry => entry.Section, StringComparer.OrdinalIgnoreCase))
            {
                ImGui.SeparatorText(section.Key);
                foreach (var entry in section)
                    DrawEntry(entry);
                ImGui.Spacing();
            }

            var panels = scope.SnapshotPanels();
            if (entries.Length == 0 && panels.Length == 0)
                ImGui.TextDisabled("This mod does not expose configurable settings.");
            foreach (var panel in panels)
            {
                try
                {
                    panel.Invoke(new ConfigurationPanelContext(frame));
                }
                catch (Exception exception)
                {
                    _error(
                        $"Configuration panel failed for {scope.Info.Name}: {exception}");
                    ImGui.TextColored(new Vector4(1, 0.35f, 0.35f, 1),
                        "This mod's configuration panel raised an exception. See Briefcase.log.");
                }
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private void DrawFrameworkContent()
    {
        ImGui.PushID(FrameworkTabId);
        try
        {
            ImGui.TextColored(
                new Vector4(72 / 255f, 219 / 255f, 184 / 255f, 1),
                "Briefcase");
            ImGui.TextDisabled("Framework and installed mod management");
            ImGui.Separator();

            var control = _modControl;
            if (control is null)
            {
                ImGui.TextDisabled("The managed mod controller is not ready.");
                return;
            }

            ImGui.SeparatorText("Mod library");
            ImGui.TextWrapped(
                "Add a mod by copying its DLL into this directory. Replacing a loaded DLL " +
                "updates it through Briefcase hot reload.");
            ImGui.TextDisabled(control.ModsDirectory);
            if (ImGui.Button("Refresh")) control.Refresh();
            ImGui.SameLine();
            if (ImGui.Button("Open Mods folder")) OpenModsDirectory(control.ModsDirectory);

            ImGui.Spacing();
            ImGui.SeparatorText("Installed mods");
            var mods = control.SnapshotInstalledMods();
            if (mods.Length == 0)
            {
                ImGui.TextDisabled("No mod DLL is installed.");
                return;
            }

            foreach (var mod in mods)
            {
                ImGui.PushID(mod.FileName);
                try
                {
                    var enabled = mod.Enabled;
                    var disableBlocked = enabled && !mod.CanStop;
                    if (disableBlocked) ImGui.BeginDisabled();
                    if (ImGui.Checkbox("##Enabled", ref enabled))
                        TryModAction(() => control.SetEnabled(mod.FileName, enabled));
                    if (disableBlocked) ImGui.EndDisabled();
                    DrawBlockedTooltip(disableBlocked, mod.StopBlockReason);
                    ImGui.SameLine();
                    ImGui.Text(mod.DisplayName);
                    ImGui.TextDisabled(
                        string.IsNullOrWhiteSpace(mod.Version)
                            ? mod.FileName
                            : $"{mod.FileName}  |  {mod.Version}");

                    if (mod.LastError is not null)
                        ImGui.TextColored(new Vector4(1, 0.35f, 0.35f, 1),
                            $"Error: {mod.LastError}");
                    else
                        ImGui.TextColored(
                            mod.Loaded
                                ? new Vector4(0.35f, 0.9f, 0.55f, 1)
                                : new Vector4(0.75f, 0.75f, 0.75f, 1),
                            mod.Loaded ? "Loaded" : "Unloaded");

                    if (mod.Loaded)
                    {
                        if (!mod.CanStop) ImGui.BeginDisabled();
                        if (ImGui.Button("Reload"))
                            TryModAction(() => control.Reload(mod.FileName));
                        if (!mod.CanStop) ImGui.EndDisabled();
                        DrawBlockedTooltip(!mod.CanStop, mod.StopBlockReason);
                        ImGui.SameLine();
                        if (!mod.CanStop) ImGui.BeginDisabled();
                        if (ImGui.Button("Unload"))
                            TryModAction(() => control.Unload(mod.FileName));
                        if (!mod.CanStop) ImGui.EndDisabled();
                        DrawBlockedTooltip(!mod.CanStop, mod.StopBlockReason);
                    }
                    else if (ImGui.Button("Load"))
                    {
                        TryModAction(() => control.Load(mod.FileName));
                    }

                    if (mod.Dependencies.Count > 0)
                        ImGui.TextDisabled(
                            $"Requires: {string.Join(", ", mod.Dependencies)}");
                    if (!string.IsNullOrWhiteSpace(mod.Description))
                        ImGui.TextWrapped(mod.Description);
                    ImGui.Separator();
                }
                finally
                {
                    ImGui.PopID();
                }
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private void TryModAction(Action action)
    {
        try { action(); }
        catch (Exception exception) { _error(exception.Message); }
    }

    private static void DrawBlockedTooltip(bool blocked, string? reason)
    {
        if (!blocked || string.IsNullOrWhiteSpace(reason) ||
            !ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420);
        ImGui.TextUnformatted(reason);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private void OpenModsDirectory(string directory)
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

    private void RememberSelectedMod(string modId)
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

    private void RememberSelectedView(string view)
    {
        lock (_gate)
        {
            if (string.Equals(_document.Window.SelectedView, view,
                    StringComparison.OrdinalIgnoreCase)) return;
            _document.Window.SelectedView = view;
            ScheduleSave();
        }
    }

    private static void DrawEntry(IConfigurationEntry entry)
    {
        ImGui.PushID($"{entry.Section}/{entry.Key}");
        try
        {
            switch (entry.ValueType)
            {
                case var type when type == typeof(bool):
                {
                    var value = (bool)entry.BoxedValue;
                    if (ImGui.Checkbox(entry.Key, ref value)) entry.BoxedValue = value;
                    break;
                }
                case var type when type == typeof(int):
                {
                    var value = (int)entry.BoxedValue;
                    var changed = entry.Minimum is int minimum && entry.Maximum is int maximum
                        ? ImGui.SliderInt(entry.Key, ref value, minimum, maximum)
                        : ImGui.InputInt(entry.Key, ref value);
                    if (changed) entry.BoxedValue = value;
                    break;
                }
                case var type when type == typeof(float):
                {
                    var value = (float)entry.BoxedValue;
                    var changed = entry.Minimum is float minimum && entry.Maximum is float maximum
                        ? ImGui.SliderFloat(entry.Key, ref value, minimum, maximum)
                        : ImGui.InputFloat(entry.Key, ref value);
                    if (changed) entry.BoxedValue = value;
                    break;
                }
                case var type when type == typeof(double):
                {
                    var value = (double)entry.BoxedValue;
                    if (ImGui.InputDouble(entry.Key, ref value)) entry.BoxedValue = value;
                    break;
                }
                case var type when type == typeof(string):
                {
                    var value = (string)entry.BoxedValue;
                    var flags = entry.Secret
                        ? ImGuiNET.ImGuiInputTextFlags.Password
                        : ImGuiNET.ImGuiInputTextFlags.None;
                    if (ImGui.InputText(entry.Key, ref value, 1024, flags))
                        entry.BoxedValue = value;
                    break;
                }
                case var type when type.IsEnum:
                    DrawEnum(entry, type);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                ImGui.Indent();
                ImGui.TextDisabled(entry.Description);
                ImGui.Unindent();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static void DrawEnum(IConfigurationEntry entry, Type enumType)
    {
        var current = entry.BoxedValue;
        if (!ImGui.BeginCombo(entry.Key, current.ToString())) return;
        try
        {
            foreach (var value in Enum.GetValues(enumType))
            {
                var selected = Equals(value, current);
                if (ImGui.Selectable(value.ToString(), selected))
                    entry.BoxedValue = value;
                if (selected) ImGui.SetItemDefaultFocus();
            }
        }
        finally
        {
            ImGui.EndCombo();
        }
    }

    private void ApplySavedPlacement(RenderFrame frame, FrameworkWindowSettings window)
    {
        if (_placementApplied) return;
        _placementApplied = true;

        var width = Math.Clamp(window.Width, 520, Math.Max(520, frame.Width));
        var height = Math.Clamp(window.Height, 360, Math.Max(360, frame.Height));
        var x = Math.Clamp(window.X, 0, Math.Max(0, frame.Width - 80));
        var y = Math.Clamp(window.Y, 0, Math.Max(0, frame.Height - 60));
        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
    }

    private void RememberPlacement()
    {
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            !float.IsFinite(size.X) || !float.IsFinite(size.Y) ||
            size.X < 1 || size.Y < 1)
            return;

        lock (_gate)
        {
            var current = _document.Window;
            if (NearlyEqual(current.X, position.X) && NearlyEqual(current.Y, position.Y) &&
                NearlyEqual(current.Width, size.X) && NearlyEqual(current.Height, size.Y))
                return;
            current.X = position.X;
            current.Y = position.Y;
            current.Width = size.X;
            current.Height = size.Y;
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
        lock (_gate)
        {
            if (_scopes.TryGetValue(scope.Info.Id, out var registered) &&
                ReferenceEquals(scope, registered))
                _scopes.Remove(scope.Info.Id);
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

    internal sealed class ModScope : IModConfigurationScope, IDisposable
    {
        private readonly ConfigurationRegistry _owner;
        private readonly object _gate = new();
        private readonly Dictionary<string, IConfigurationEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PanelRegistration> _panels = [];
        private readonly List<ServerPanelRegistration> _serverPanels = [];

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

        public IDisposable RegisterPanel(Action<ConfigurationPanelContext> draw)
        {
            ArgumentNullException.ThrowIfNull(draw);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                var registration = new PanelRegistration(this, draw);
                _panels.Add(registration);
                return registration;
            }
        }

        public IDisposable RegisterServerPanel(
            string name,
            Action<ConfigurationPanelContext> draw)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(draw);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                var registration = new ServerPanelRegistration(this, name.Trim(), draw);
                _serverPanels.Add(registration);
                return registration;
            }
        }

        public IConfigurationEntry[] SnapshotEntries()
        {
            lock (_gate) return _entries.Values.ToArray();
        }

        public PanelRegistration[] SnapshotPanels()
        {
            lock (_gate) return _panels.Where(panel => panel.IsActive).ToArray();
        }

        public ServerPanelRegistration[] SnapshotServerPanels()
        {
            lock (_gate) return _serverPanels.Where(panel => panel.IsActive).ToArray();
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
                foreach (var panel in _panels.ToArray()) panel.DisposeFromOwner();
                foreach (var panel in _serverPanels.ToArray()) panel.DisposeFromOwner();
                _panels.Clear();
                _serverPanels.Clear();
                _entries.Clear();
            }
        }

        private void Remove(PanelRegistration panel)
        {
            lock (_gate) _panels.Remove(panel);
        }

        private void Remove(ServerPanelRegistration panel)
        {
            lock (_gate) _serverPanels.Remove(panel);
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

        internal sealed class PanelRegistration : IDisposable
        {
            private readonly ModScope _owner;
            private readonly object _invocationGate = new();
            private int _active = 1;

            public PanelRegistration(ModScope owner, Action<ConfigurationPanelContext> draw)
            {
                _owner = owner;
                Draw = draw;
            }

            public Action<ConfigurationPanelContext> Draw { get; }
            public bool IsActive => Volatile.Read(ref _active) != 0;

            public void Invoke(ConfigurationPanelContext context)
            {
                lock (_invocationGate)
                {
                    if (!IsActive) return;
                    Draw(context);
                }
            }

            public void Dispose()
            {
                lock (_invocationGate)
                {
                    if (Interlocked.Exchange(ref _active, 0) == 0) return;
                }
                _owner.Remove(this);
            }

            public void DisposeFromOwner()
            {
                lock (_invocationGate) Interlocked.Exchange(ref _active, 0);
            }
        }


        internal sealed class ServerPanelRegistration : IDisposable
        {
            private readonly ModScope _owner;
            private readonly object _invocationGate = new();
            private int _active = 1;

            public ServerPanelRegistration(
                ModScope owner,
                string name,
                Action<ConfigurationPanelContext> draw)
            {
                _owner = owner;
                Name = name;
                Draw = draw;
            }

            public string Name { get; }
            private Action<ConfigurationPanelContext> Draw { get; }
            public bool IsActive => Volatile.Read(ref _active) != 0;

            public void Invoke(ConfigurationPanelContext context)
            {
                lock (_invocationGate)
                {
                    if (IsActive) Draw(context);
                }
            }

            public void Dispose()
            {
                lock (_invocationGate)
                {
                    if (Interlocked.Exchange(ref _active, 0) == 0) return;
                }
                _owner.Remove(this);
            }

            public void DisposeFromOwner()
            {
                lock (_invocationGate) Interlocked.Exchange(ref _active, 0);
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
