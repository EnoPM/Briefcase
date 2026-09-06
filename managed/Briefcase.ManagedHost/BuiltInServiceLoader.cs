using System.Reflection;
using Briefcase.ModApi;

namespace Briefcase.ManagedHost;

/// <summary>
/// Loads game-specific services shipped as part of Briefcase Core. Built-ins
/// use the normal strongly typed mod surface, including generated SDK types,
/// but deliberately stay outside Briefcase/Mods and its lifecycle controls.
/// </summary>
internal sealed class BuiltInServiceLoader : IDisposable
{
    private readonly ModContext _context;
    private readonly string _directory;
    private readonly Assembly? _generatedSdk;
    private readonly ConfigurationRegistry _configuration;
    private readonly List<LoadedService> _loaded = [];

    public BuiltInServiceLoader(
        ModContext context,
        string directory,
        Assembly? generatedSdk,
        ConfigurationRegistry configuration)
    {
        _context = context;
        _directory = directory;
        _generatedSdk = generatedSdk;
        _configuration = configuration;
    }

    public void Start()
    {
        Directory.CreateDirectory(_directory);
        foreach (var path in Directory.EnumerateFiles(_directory, "*.dll")
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            Load(path);
        _context.Info($"Core built-in directory: {_directory}");
    }

    private void Load(string path)
    {
        ManagedModLoadContext? loadContext = null;
        PatchSet? patches = null;
        PatchRuntime? patchRuntime = null;
        ConfigurationRegistry.ModScope? configuration = null;
        BriefcaseMod? instance = null;
        var modLoaded = false;
        try
        {
            path = Path.GetFullPath(path);
            loadContext = new ManagedModLoadContext(path, _generatedSdk);
            var assembly = loadContext.LoadFromAssemblyPath(path);
            var candidates = assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(BriefcaseMod).IsAssignableFrom(type))
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidOperationException(
                    $"Expected exactly one BriefcaseMod implementation; found {candidates.Length}.");

            instance = (BriefcaseMod?)Activator.CreateInstance(candidates[0]) ??
                       throw new InvalidOperationException(
                           "The built-in BriefcaseMod class could not be constructed.");
            var info = instance.Info;
            if ((info.RequiredCapabilities & ~_context.Capabilities) != 0)
                throw new InvalidOperationException(
                    "The framework does not expose every required capability.");

            configuration = _configuration.RegisterMod(info);
            var modContext = _context.WithConfiguration(configuration);
            patches = PatchDiscovery.Discover(assembly);
            instance.Load(modContext);
            modLoaded = true;
            patchRuntime = PatchRuntime.Attach(modContext, patches, info);
            _loaded.Add(new LoadedService(
                loadContext, instance, info, patches, patchRuntime, configuration));
            _context.Info($"Loaded Core built-in {info.Name} {info.Version} [{info.Id}]");
        }
        catch (Exception exception)
        {
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
        ConfigurationRegistry.ModScope Configuration);
}
