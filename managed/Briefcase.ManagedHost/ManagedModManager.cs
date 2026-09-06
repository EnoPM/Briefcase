using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Briefcase.ModApi;
#if !BRIEFCASE_HEADLESS
using ImGuiNET;
#endif

namespace Briefcase.ManagedHost;

internal sealed class ManagedModManager : IFrameworkModControl, IDisposable
{
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(600);
    private readonly ModContext _context;
    private readonly string _modsDirectory;
    private readonly string _cacheDirectory;
    private readonly Assembly? _generatedSdk;
    private readonly ConfigurationRegistry _configuration;
    private readonly Dictionary<string, LoadedMod> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstalledMod> _installed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;

    public string ModsDirectory => _modsDirectory;

    public ManagedModManager(
        ModContext context,
        string modsDirectory,
        string cacheDirectory,
        Assembly? generatedSdk,
        ConfigurationRegistry configuration)
    {
        _context = context;
        _modsDirectory = modsDirectory;
        _cacheDirectory = cacheDirectory;
        _generatedSdk = generatedSdk;
        _configuration = configuration;
    }

    public void Start()
    {
        Directory.CreateDirectory(_modsDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        var paths = Directory.EnumerateFiles(_modsDirectory, "*.dll")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var path in paths)
        {
            RegisterInstalled(path);
            ProbeInstalled(path);
        }
        LoadEnabledInDependencyOrder();

        _watcher = new FileSystemWatcher(_modsDirectory, "*.dll")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Deleted += OnDeleted;
        _watcher.EnableRaisingEvents = true;
        _context.Info($"Managed mod directory: {_modsDirectory}");
    }

    public ManagedModConfigurationEntry[] GetConfiguration(string fileName)
    {
        lock (_gate)
        {
            var path = ResolveFileName(fileName);
            if (!_loaded.TryGetValue(path, out var loaded)) return [];
            return loaded.Configuration.SnapshotEntries()
                .Where(entry => !entry.Secret)
                .OrderBy(entry => entry.Section, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(ToConfigurationEntry)
                .ToArray();
        }
    }

    public void SetConfiguration(
        string fileName,
        string section,
        string key,
        JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var path = ResolveFileName(fileName);
            if (!_loaded.TryGetValue(path, out var loaded))
                throw new InvalidOperationException(
                    $"The server mod '{fileName}' must be loaded before its settings can be changed.");
            var matches = loaded.Configuration.SnapshotEntries().Where(entry =>
                    entry.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                    entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1 || matches[0].Secret)
                throw new InvalidOperationException(
                    $"The public setting '{section}/{key}' was not found in {fileName}.");
            var entry = matches[0];
            object parsed = entry.ValueType.IsEnum
                ? Enum.Parse(entry.ValueType, value.GetString() ?? "", ignoreCase: true)
                : value.Deserialize(entry.ValueType) ??
                  throw new InvalidOperationException("The setting value cannot be null.");
            entry.BoxedValue = parsed;
        }
    }

    private static ManagedModConfigurationEntry ToConfigurationEntry(
        IConfigurationEntry entry)
    {
        var type = entry.ValueType;
        var value = type.IsEnum
            ? JsonSerializer.SerializeToElement(entry.BoxedValue.ToString())
            : JsonSerializer.SerializeToElement(entry.BoxedValue, type);
        JsonElement? minimum = entry.Minimum is null
            ? null
            : JsonSerializer.SerializeToElement(entry.Minimum, type);
        JsonElement? maximum = entry.Maximum is null
            ? null
            : JsonSerializer.SerializeToElement(entry.Maximum, type);
        return new ManagedModConfigurationEntry(
            entry.Section,
            entry.Key,
            entry.Description,
            type.IsEnum ? "enum" : Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => "bool",
                TypeCode.Int32 => "int",
                TypeCode.Single => "float",
                TypeCode.Double => "double",
                TypeCode.String => "string",
                _ => "unsupported"
            },
            value,
            minimum,
            maximum,
            type.IsEnum ? Enum.GetNames(type) : []);
    }

    // Metadata must be known before any mod Load method runs. Probing in a
    // short-lived collectible context lets us build the dependency graph while
    // preserving the simple, strongly typed ModInfo API used by mod authors.
    private void ProbeInstalled(string sourcePath)
    {
        sourcePath = NormalizePath(sourcePath);
        ManagedModLoadContext? loadContext = null;
        try
        {
            var shadowAssembly = CreateShadowCopy(sourcePath);
            loadContext = new ManagedModLoadContext(shadowAssembly, _generatedSdk);
            var assembly = loadContext.LoadFromAssemblyPath(shadowAssembly);
            var candidates = assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(BriefcaseMod).IsAssignableFrom(type))
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one BriefcaseMod implementation; found {candidates.Length}.");
            var instance = (BriefcaseMod?)Activator.CreateInstance(candidates[0]) ??
                           throw new InvalidOperationException(
                               "The BriefcaseMod class could not be constructed.");
            ValidateInfo(instance.Info);
            lock (_gate)
            {
                var installed = GetOrAddInstalled(sourcePath);
                installed.Info = instance.Info;
                installed.LastError = null;
            }
        }
        catch (Exception exception)
        {
            lock (_gate) GetOrAddInstalled(sourcePath).LastError = exception.Message;
            _context.Error($"Could not inspect {Path.GetFileName(sourcePath)}: {exception}");
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static void ValidateInfo(ModInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Id))
            throw new InvalidOperationException("ModInfo.Id cannot be empty.");
        if (info.Dependencies.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Mod dependency IDs cannot be empty.");
        if (info.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            info.Dependencies.Count)
            throw new InvalidOperationException("Mod dependency IDs must be unique.");
        if (info.Dependencies.Contains(info.Id, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Mod '{info.Id}' cannot depend on itself.");
    }

    private void OnChanged(object sender, FileSystemEventArgs eventArgs) => Schedule(eventArgs.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs eventArgs)
    {
        Remove(eventArgs.OldFullPath);
        lock (_gate) _installed.Remove(NormalizePath(eventArgs.OldFullPath));
        RegisterInstalled(eventArgs.FullPath);
        Schedule(eventArgs.FullPath);
    }

    private void OnDeleted(object sender, FileSystemEventArgs eventArgs)
    {
        Remove(eventArgs.FullPath);
        lock (_gate) _installed.Remove(NormalizePath(eventArgs.FullPath));
    }

    // Builds often replace a file through several writes. Debouncing prevents us
    // from trying to load a half-written PE image for every watcher notification.
    private void Schedule(string path)
    {
        lock (_gate)
        {
            if (_pending.Remove(path, out var previous))
                previous.Dispose();
            _pending[path] = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (_pending.Remove(path, out var timer))
                        timer.Dispose();
                }
                Reload(path);
            }, null, ReloadDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Reload(
        string sourcePath,
        bool force = false,
        bool allowReloadWithDependents = false)
    {
        sourcePath = NormalizePath(sourcePath);
        RegisterInstalled(sourcePath);
        bool needsProbe;
        lock (_gate)
            needsProbe = !_loaded.ContainsKey(sourcePath) &&
                         GetOrAddInstalled(sourcePath).Info is null;
        if (needsProbe) ProbeInstalled(sourcePath);
        try
        {
            if (!File.Exists(sourcePath))
                return;

            lock (_gate)
            {
                var alreadyLoaded = _loaded.ContainsKey(sourcePath);
                if (!force && !alreadyLoaded &&
                    !_configuration.IsModEnabled(Path.GetFileName(sourcePath)))
                    return;
                if (alreadyLoaded && !allowReloadWithDependents)
                    EnsureCanStopLocked(sourcePath, "reload");
                EnsureDependenciesLoadedLocked(sourcePath);
            }

            // Shadow copying leaves Rider/MSBuild free to replace the original
            // DLL while the previous generation is still being unloaded.
            var shadowAssembly = CreateShadowCopy(sourcePath);

            lock (_gate)
            {
                var unloadTicket = BeginUnload(sourcePath);
                if (unloadTicket is not null)
                    CompleteUnload(unloadTicket);

                var loadContext = new ManagedModLoadContext(shadowAssembly, _generatedSdk);
                PatchSet? patches = null;
                PatchRuntime? patchRuntime = null;
                ConfigurationRegistry.ModScope? configuration = null;
                BriefcaseMod? instance = null;
                var modLoaded = false;
                try
                {
                    var assembly = loadContext.LoadFromAssemblyPath(shadowAssembly);
                    var candidates = assembly.GetTypes()
                        .Where(type => !type.IsAbstract && typeof(BriefcaseMod).IsAssignableFrom(type))
                        .ToArray();
                    if (candidates.Length != 1)
                        throw new InvalidOperationException(
                            $"Expected exactly one BriefcaseMod implementation; found {candidates.Length}.");

                    instance = (BriefcaseMod?)Activator.CreateInstance(candidates[0]) ??
                               throw new InvalidOperationException("The BriefcaseMod class could not be constructed.");
                    var info = instance.Info;
                    ValidateInfo(info);
                    EnsureUniqueIdLocked(sourcePath, info.Id);
                    EnsureDependenciesLoadedLocked(sourcePath, info);
                    if ((info.RequiredCapabilities & ~_context.Capabilities) != 0)
                        throw new InvalidOperationException("The framework does not expose every required capability.");

                    configuration = _configuration.RegisterMod(info);
                    var modContext = _context.WithConfiguration(configuration);
                    patches = PatchDiscovery.Discover(assembly);
                    instance.Load(modContext);
                    modLoaded = true;
                    patchRuntime = PatchRuntime.Attach(modContext, patches, info);
                    _loaded[sourcePath] = new LoadedMod(
                        loadContext, instance, info, patches, patchRuntime, configuration);
                    var installed = GetOrAddInstalled(sourcePath);
                    installed.Info = info;
                    installed.LastError = null;
                    _context.Info($"Loaded {info.Name} {info.Version} [{info.Id}]");
                    if (patches.Count > 0)
                        _context.Info(
                            $"Attached {patches.UnrealCount} Unreal and " +
                            $"{patches.NativeCount} native attributed patch(es).");
                }
                catch
                {
                    patchRuntime?.Dispose();
                    patches?.Dispose();
                    if (modLoaded)
                    {
                        try { instance?.Unload(); }
                        catch (Exception unloadException)
                        {
                            _context.Error($"Rollback unload failed: {unloadException.Message}");
                        }
                    }
                    configuration?.Dispose();
                    loadContext.Unload();
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
                GetOrAddInstalled(sourcePath).LastError = exception.Message;
            _context.Error($"Could not hot-load {Path.GetFileName(sourcePath)}: {exception}");
        }
    }

    private string CreateShadowCopy(string sourcePath)
    {
        var generation = Path.Combine(
            _cacheDirectory,
            Path.GetFileNameWithoutExtension(sourcePath),
            $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(generation);

        // Copying adjacent managed files lets a mod bring ordinary managed
        // dependencies. Framework SDK assemblies are still shared explicitly.
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(sourcePath)!))
        {
            var extension = Path.GetExtension(file);
            if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            CopyWithRetry(file, Path.Combine(generation, Path.GetFileName(file)));
        }
        return Path.Combine(generation, Path.GetFileName(sourcePath));
    }

    private static void CopyWithRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(100);
            }
        }
    }

    ManagedModStatus[] IModManagementBackend.SnapshotInstalledMods()
    {
        (string Path, string FileName, ModInfo? Info, bool Loaded, string? Error,
            string[] ActiveDependents)[] snapshot;
        lock (_gate)
        {
            snapshot = _installed.Values
                .Select(installed => (
                    Path: installed.SourcePath,
                    FileName: Path.GetFileName(installed.SourcePath),
                    Info: installed.Info,
                    Loaded: _loaded.ContainsKey(installed.SourcePath),
                    Error: installed.LastError,
                    ActiveDependents: installed.Info is null
                        ? []
                        : GetActiveDependentsLocked(installed.Info.Id)))
                .OrderBy(item => item.Info?.Name ?? item.FileName,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return snapshot.Select(item => new ManagedModStatus(
                item.FileName,
                item.Info?.Name ?? Path.GetFileNameWithoutExtension(item.FileName),
                item.Info?.Version ?? "",
                item.Info?.Description ?? "",
                _configuration.IsModEnabled(item.FileName),
                item.Loaded,
                item.Error)
            {
                Id = item.Info?.Id ?? "",
                Dependencies = item.Info?.Dependencies.ToArray() ?? [],
                ActiveDependents = item.ActiveDependents
            })
            .ToArray();
    }

    void IModManagementBackend.SetEnabled(string fileName, bool enabled)
    {
        var sourcePath = ResolveFileName(fileName);
        if (enabled)
        {
            EnableWithDependencies(sourcePath, []);
            LoadEnabledInDependencyOrder();
            ThrowIfNotLoaded(sourcePath);
        }
        else
        {
            lock (_gate) EnsureCanStopLocked(sourcePath, "disable");
            _configuration.SetModEnabled(Path.GetFileName(sourcePath), false);
            Remove(sourcePath);
        }
    }

    void IModManagementBackend.Load(string fileName)
    {
        var sourcePath = ResolveFileName(fileName);
        EnableWithDependencies(sourcePath, []);
        LoadEnabledInDependencyOrder();
        ThrowIfNotLoaded(sourcePath);
    }

    void IModManagementBackend.Reload(string fileName)
    {
        var sourcePath = ResolveFileName(fileName);
        lock (_gate) EnsureCanStopLocked(sourcePath, "reload");
        Reload(sourcePath, force: true);
        ThrowIfNotLoaded(sourcePath);
    }

    void IModManagementBackend.Unload(string fileName)
    {
        var sourcePath = ResolveFileName(fileName);
        lock (_gate) EnsureCanStopLocked(sourcePath, "unload");
        Remove(sourcePath);
    }

    void IModManagementBackend.Refresh()
    {
        Directory.CreateDirectory(_modsDirectory);
        var paths = Directory.EnumerateFiles(_modsDirectory, "*.dll")
            .Select(NormalizePath)
            .ToArray();
        var present = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

        string[] removed;
        lock (_gate)
            removed = _installed.Keys.Where(path => !present.Contains(path)).ToArray();
        foreach (var path in removed)
        {
            Remove(path);
            lock (_gate) _installed.Remove(path);
        }

        foreach (var path in paths)
        {
            RegisterInstalled(path);
            ProbeInstalled(path);
        }
        LoadEnabledInDependencyOrder();
    }

    // Kahn-style loading: every pass loads only nodes whose dependencies are
    // already active. A pass with no ready node is a cycle, and unrelated mods
    // have already loaded by that point.
    private void LoadEnabledInDependencyOrder()
    {
        InstalledMod[] installed;
        lock (_gate) installed = _installed.Values.ToArray();

        var byId = installed
            .Where(item => item.Info is not null)
            .GroupBy(item => item.Info!.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in byId.Values.Where(group => group.Length > 1))
        {
            var files = string.Join(", ", duplicate.Select(item => Path.GetFileName(item.SourcePath)));
            foreach (var item in duplicate)
                item.LastError = $"Duplicate mod ID '{item.Info!.Id}' is declared by: {files}.";
        }

        var pending = installed
            .Where(item => item.Info is not null &&
                           byId[item.Info.Id].Length == 1 &&
                           _configuration.IsModEnabled(Path.GetFileName(item.SourcePath)))
            .Where(item =>
            {
                lock (_gate) return !_loaded.ContainsKey(item.SourcePath);
            })
            .ToList();

        while (pending.Count > 0)
        {
            var changed = false;
            foreach (var item in pending.ToArray())
            {
                var issue = FindUnavailableDependency(item, byId, pending);
                if (issue is null) continue;
                item.LastError = issue;
                pending.Remove(item);
                changed = true;
                _context.Error($"Could not load {Path.GetFileName(item.SourcePath)}: {issue}");
            }

            var ready = pending
                .Where(item => item.Info!.Dependencies.All(dependencyId =>
                {
                    var dependency = byId[dependencyId][0];
                    lock (_gate) return _loaded.ContainsKey(dependency.SourcePath);
                }))
                .OrderBy(item => item.Info!.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var item in ready)
            {
                Reload(item.SourcePath, force: true);
                pending.Remove(item);
                changed = true;
            }

            if (changed) continue;
            var cycle = string.Join(" -> ", pending.Select(item => item.Info!.Id));
            foreach (var item in pending)
                item.LastError = $"Dependency cycle detected among: {cycle}.";
            _context.Error($"Managed mod dependency cycle detected: {cycle}.");
            break;
        }
    }

    private string? FindUnavailableDependency(
        InstalledMod item,
        IReadOnlyDictionary<string, InstalledMod[]> byId,
        IReadOnlyCollection<InstalledMod> pending)
    {
        foreach (var dependencyId in item.Info!.Dependencies)
        {
            if (!byId.TryGetValue(dependencyId, out var matches))
                return $"Required mod '{dependencyId}' is not installed.";
            if (matches.Length != 1)
                return $"Required mod ID '{dependencyId}' is ambiguous.";
            var dependency = matches[0];
            if (!_configuration.IsModEnabled(Path.GetFileName(dependency.SourcePath)))
                return $"Required mod '{dependency.Info!.Name}' is disabled.";
            bool loaded;
            lock (_gate) loaded = _loaded.ContainsKey(dependency.SourcePath);
            if (!loaded && !pending.Contains(dependency))
                return $"Required mod '{dependency.Info!.Name}' failed to load.";
        }
        return null;
    }

    private void EnableWithDependencies(string sourcePath, HashSet<string> visiting)
    {
        sourcePath = NormalizePath(sourcePath);
        RegisterInstalled(sourcePath);
        InstalledMod installed;
        lock (_gate) installed = GetOrAddInstalled(sourcePath);
        if (installed.Info is null)
        {
            ProbeInstalled(sourcePath);
            lock (_gate) installed = GetOrAddInstalled(sourcePath);
        }
        var info = installed.Info ?? throw new InvalidOperationException(
            installed.LastError ?? $"Could not inspect {Path.GetFileName(sourcePath)}.");
        if (!visiting.Add(info.Id))
            throw new InvalidOperationException(
                $"Dependency cycle detected while enabling '{info.Id}'.");

        foreach (var dependencyId in info.Dependencies)
        {
            InstalledMod dependency;
            lock (_gate)
            {
                var matches = _installed.Values.Where(item =>
                        string.Equals(item.Info?.Id, dependencyId,
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0)
                    throw new InvalidOperationException(
                        $"Required mod '{dependencyId}' is not installed.");
                if (matches.Length > 1)
                    throw new InvalidOperationException(
                        $"Required mod ID '{dependencyId}' is ambiguous.");
                dependency = matches[0];
            }
            EnableWithDependencies(dependency.SourcePath, visiting);
        }

        visiting.Remove(info.Id);
        _configuration.SetModEnabled(Path.GetFileName(sourcePath), true);
    }

    private void ThrowIfNotLoaded(string sourcePath)
    {
        lock (_gate)
        {
            if (_loaded.ContainsKey(sourcePath)) return;
            var installed = GetOrAddInstalled(sourcePath);
            throw new InvalidOperationException(
                installed.LastError ?? $"{Path.GetFileName(sourcePath)} did not load.");
        }
    }

    private void EnsureUniqueIdLocked(string sourcePath, string id)
    {
        var duplicate = _installed.Values.FirstOrDefault(item =>
            item.SourcePath != sourcePath &&
            string.Equals(item.Info?.Id, id, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Mod ID '{id}' is already declared by {Path.GetFileName(duplicate.SourcePath)}.");
    }

    private void EnsureDependenciesLoadedLocked(string sourcePath, ModInfo? overrideInfo = null)
    {
        var info = overrideInfo ?? GetOrAddInstalled(sourcePath).Info ??
            throw new InvalidOperationException(
                $"Metadata for {Path.GetFileName(sourcePath)} is unavailable.");
        foreach (var dependencyId in info.Dependencies)
        {
            var matches = _installed.Values.Where(item =>
                    string.Equals(item.Info?.Id, dependencyId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length == 0)
                throw new InvalidOperationException(
                    $"Required mod '{dependencyId}' is not installed.");
            if (matches.Length > 1)
                throw new InvalidOperationException(
                    $"Required mod ID '{dependencyId}' is ambiguous.");
            if (!_configuration.IsModEnabled(Path.GetFileName(matches[0].SourcePath)))
                throw new InvalidOperationException(
                    $"Required mod '{matches[0].Info!.Name}' is disabled.");
            if (!_loaded.ContainsKey(matches[0].SourcePath))
                throw new InvalidOperationException(
                    $"Required mod '{matches[0].Info!.Name}' is not loaded.");
        }
    }

    private void EnsureCanStopLocked(string sourcePath, string operation)
    {
        if (!_installed.TryGetValue(sourcePath, out var installed) || installed.Info is null)
            return;
        var dependents = GetActiveDependentsLocked(installed.Info.Id);
        if (dependents.Length == 0) return;
        throw new InvalidOperationException(
            $"Cannot {operation} {installed.Info.Name}. Disable these dependent mods first: " +
            $"{string.Join(", ", dependents)}.");
    }

    private string[] GetActiveDependentsLocked(string dependencyId) =>
        _installed.Values
            .Where(item => item.Info is not null &&
                           item.Info.Dependencies.Contains(
                               dependencyId, StringComparer.OrdinalIgnoreCase) &&
                           (_loaded.ContainsKey(item.SourcePath) ||
                            _configuration.IsModEnabled(Path.GetFileName(item.SourcePath))))
            .Select(item => item.Info!.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void RegisterInstalled(string sourcePath)
    {
        sourcePath = NormalizePath(sourcePath);
        lock (_gate) GetOrAddInstalled(sourcePath);
    }

    private InstalledMod GetOrAddInstalled(string sourcePath)
    {
        if (_installed.TryGetValue(sourcePath, out var installed)) return installed;
        installed = new InstalledMod(sourcePath);
        _installed.Add(sourcePath, installed);
        return installed;
    }

    private string EnsureModsPath(string sourcePath)
    {
        var normalized = NormalizePath(sourcePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_modsDirectory)) +
                   Path.DirectorySeparatorChar;
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(normalized), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The requested file is not a DLL in Briefcase/Mods.");
        return normalized;
    }

    private string ResolveFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.Equals(Path.GetFileName(fileName), StringComparison.Ordinal) ||
            !Path.GetExtension(fileName).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A managed mod DLL file name is required.");
        return EnsureModsPath(Path.Combine(_modsDirectory, fileName));
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private void Remove(string sourcePath)
    {
        sourcePath = NormalizePath(sourcePath);
        UnloadTicket? unloadTicket;
        lock (_gate)
            unloadTicket = BeginUnload(sourcePath);
        if (unloadTicket is not null)
            CompleteUnload(unloadTicket);
    }

    // This method owns the last strong references to mod-defined types. Its
    // stack frame has returned before CompleteUnload starts collecting.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private UnloadTicket? BeginUnload(string sourcePath)
    {
        if (!_loaded.Remove(sourcePath, out var loaded))
            return null;
        try
        {
            // Remove every MethodInfo reference before asking CoreCLR to unload
            // the collectible assembly that declared those patch methods.
            loaded.PatchRuntime.Dispose();
            loaded.Patches.Dispose();
            loaded.Instance.Unload();
        }
        catch (Exception exception)
        {
            _context.Error($"Unload failed for {loaded.Info.Name}: {exception.Message}");
        }
        finally
        {
            // Entries and panels may hold delegates declared by the mod. Always
            // remove them before unloading its collectible AssemblyLoadContext.
            loaded.Configuration.Dispose();
        }
        var ticket = new UnloadTicket(
            new WeakReference(loaded.LoadContext, trackResurrection: true),
            loaded.Info);
        loaded.LoadContext.Unload();
        return ticket;
    }

    private void CompleteUnload(UnloadTicket ticket)
    {
        for (var attempt = 0; ticket.Reference.IsAlive && attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            // A native patch callback returns through a reverse P/Invoke frame.
            // Give that thread a scheduling point before the next collection;
            // otherwise eight back-to-back collections can all observe the same
            // transient frame and report a leak that disappears immediately after.
            if (ticket.Reference.IsAlive) Thread.Sleep(10);
        }
        if (ticket.Reference.IsAlive)
            _context.Warning($"{ticket.Info.Name} requested reload, but its old AssemblyLoadContext is still referenced.");
        else
            _context.Info($"Unloaded {ticket.Info.Name} [{ticket.Info.Id}]");
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        List<UnloadTicket> unloadTickets;
        lock (_gate)
        {
            foreach (var timer in _pending.Values)
                timer.Dispose();
            _pending.Clear();
            unloadTickets = [];
            // Stop dependents before their dependencies. This mirrors the
            // topological load order and keeps shared services alive until the
            // last consumer has finished its Unload method.
            while (_loaded.Count > 0)
            {
                var leaf = _loaded.First(pair => !_loaded.Values.Any(candidate =>
                    candidate.Info.Dependencies.Contains(
                        pair.Value.Info.Id, StringComparer.OrdinalIgnoreCase)));
                var ticket = BeginUnload(leaf.Key);
                if (ticket is not null) unloadTickets.Add(ticket);
            }
        }
        foreach (var ticket in unloadTickets)
            CompleteUnload(ticket);
    }

    private sealed record LoadedMod(
        ManagedModLoadContext LoadContext,
        BriefcaseMod Instance,
        ModInfo Info,
        PatchSet Patches,
        PatchRuntime PatchRuntime,
        ConfigurationRegistry.ModScope Configuration);
    private sealed record UnloadTicket(WeakReference Reference, ModInfo Info);

    private sealed class InstalledMod(string sourcePath)
    {
        public string SourcePath { get; } = sourcePath;
        public ModInfo? Info { get; set; }
        public string? LastError { get; set; }
    }
}

internal sealed class ManagedModLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly Assembly? _generatedSdk;
    private static readonly Assembly ModSdk = typeof(BriefcaseMod).Assembly;
#if !BRIEFCASE_HEADLESS
    private static readonly Assembly ImGuiNet = typeof(ImGui).Assembly;
#endif

    public ManagedModLoadContext(string mainAssemblyPath, Assembly? generatedSdk)
        : base($"BriefcaseMod:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}",
            isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _generatedSdk = generatedSdk;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Type identity must be shared: a private copy of Briefcase.ModApi.BriefcaseMod would
        // not be assignable to the host's BriefcaseMod type.
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, ModSdk.GetName()))
            return ModSdk;
#if !BRIEFCASE_HEADLESS
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, ImGuiNet.GetName()))
            return ImGuiNet;
#endif
        if (_generatedSdk is not null &&
            AssemblyName.ReferenceMatchesDefinition(assemblyName, _generatedSdk.GetName()))
            return _generatedSdk;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
