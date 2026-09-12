using System.Reflection;
using Briefcase.ModApi;

namespace Briefcase.ManagedHost;

/// <summary>
/// Loads game-specific services shipped as part of Briefcase Core. Built-ins
/// use the normal strongly typed mod surface, including generated SDK types,
/// but deliberately stay outside Briefcase/Mods and its lifecycle controls.
/// </summary>
internal sealed unsafe class BuiltInServiceLoader : IDisposable
{
    private readonly ModContext _context;
    private readonly string _directory;
    private readonly Assembly? _generatedSdk;
    private readonly ConfigurationRegistry _configuration;
    private readonly GameThreadService _gameThread;
    private readonly List<LoadedService> _loaded = [];

    public BuiltInServiceLoader(
        ModContext context,
        string directory,
        Assembly? generatedSdk,
        ConfigurationRegistry configuration,
        GameThreadService gameThread)
    {
        _context = context;
        _directory = directory;
        _generatedSdk = generatedSdk;
        _configuration = configuration;
        _gameThread = gameThread;
    }

    public void Start(Action<double, string>? progress = null)
    {
        Directory.CreateDirectory(_directory);
        var paths = Directory.EnumerateFiles(_directory, "*.dll")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            var path = paths[index];
            progress?.Invoke(
                (double)(index + 1) / Math.Max(1, paths.Length),
                $"Loading {Path.GetFileName(path)}...");
            _context.Info($"Loading Core built-in {Path.GetFileName(path)}.");
            Load(path);
            if (progress is not null) Thread.Yield();
        }
        progress?.Invoke(1, paths.Length == 0
            ? "No target-specific Core services are installed."
            : $"Loaded {paths.Length} Core service(s).");
        _context.Info($"Core built-in directory: {_directory}");
    }

    private void Load(string path)
    {
        ManagedModLoadContext? loadContext = null;
        PatchSet? patches = null;
        PatchRuntime? patchRuntime = null;
        ConfigurationRegistry.ModScope? configuration = null;
        BriefcaseMod? instance = null;
        IGameThreadScope? gameThread = null;
        IUnrealAssetScope? assets = null;
        IUnrealEventScope? events = null;
        var modLoaded = false;
        try
        {
            path = Path.GetFullPath(path);
            var fileName = Path.GetFileName(path);
            loadContext = new ManagedModLoadContext(path, _generatedSdk);
            _context.Info($"Core built-in {fileName}: loading assembly.");
            var assembly = loadContext.LoadFromAssemblyPath(path);
            _context.Info($"Core built-in {fileName}: discovering entry point.");
            var candidates = assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(BriefcaseMod).IsAssignableFrom(type))
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one BriefcaseMod implementation; found {candidates.Length}.");

            _context.Info($"Core built-in {fileName}: constructing entry point.");
            instance = (BriefcaseMod?)Activator.CreateInstance(candidates[0]) ??
                       throw new InvalidOperationException(
                           "The built-in BriefcaseMod class could not be constructed.");
            var info = instance.Info;
            _context.Info($"Core built-in {fileName}: entry point {info.Id} constructed.");
            if ((info.RequiredCapabilities & ~_context.Capabilities) != 0)
                throw new InvalidOperationException(
                    "The framework does not expose every required capability.");

            configuration = _configuration.RegisterMod(info);
            gameThread = _gameThread.CreateScope(info.Id);
            assets = new UnrealAssetScope(_context.Unreal, info.Id, _context.Warning);
            events = new UnrealEventScope(
                _context.Unreal, _context.UnrealNative, _context.PatchingNative,
                new GameThreadApi(gameThread), info.Id, _context.Error);
            var modContext = configuration.AttachTo(_context)
                .WithGameThread(gameThread)
                .WithAssets(assets)
                .WithEvents(events);
            _context.Info($"Core built-in {fileName}: discovering attributed patches.");
            patches = PatchDiscovery.Discover(assembly);
            _context.Info($"Core built-in {fileName}: invoking Load().");
            instance.Load(modContext);
            modLoaded = true;
            _context.Info($"Core built-in {fileName}: attaching patches.");
            patchRuntime = PatchRuntime.Attach(modContext, patches, info);
            _loaded.Add(new LoadedService(
                loadContext, instance, info, patches, patchRuntime,
                configuration, gameThread, assets, events));
            _context.Info($"Loaded Core built-in {info.Name} {info.Version} [{info.Id}]");
        }
        catch (Exception exception)
        {
            events?.Dispose();
            patchRuntime?.Dispose();
            patches?.Dispose();
            if (modLoaded)
            {
                try { instance?.Unload(); }
                catch (Exception unloadException)
                {
                    _context.Error($"Built-in rollback failed: {unloadException.Message}");
                }
            }
            assets?.Dispose();
            gameThread?.Dispose();
            configuration?.Dispose();
            loadContext?.Unload();
            _context.Error($"Could not load Core built-in {Path.GetFileName(path)}: {exception}");
        }
    }

    public void Dispose()
    {
        for (var index = _loaded.Count - 1; index >= 0; index--)
        {
            var loaded = _loaded[index];
            try
            {
                loaded.Events.Dispose();
                loaded.PatchRuntime.Dispose();
                loaded.Patches.Dispose();
                loaded.Instance.Unload();
            }
            catch (Exception exception)
            {
                _context.Error($"Could not unload Core built-in {loaded.Info.Name}: {exception}");
            }
            finally
            {
                loaded.Assets.Dispose();
                loaded.GameThread.Dispose();
                loaded.Configuration.Dispose();
                loaded.LoadContext.Unload();
            }
        }
        _loaded.Clear();
    }

    private sealed record LoadedService(
        ManagedModLoadContext LoadContext,
        BriefcaseMod Instance,
        ModInfo Info,
        PatchSet Patches,
        PatchRuntime PatchRuntime,
        ConfigurationRegistry.ModScope Configuration,
        IGameThreadScope GameThread,
        IUnrealAssetScope Assets,
        IUnrealEventScope Events);
}
