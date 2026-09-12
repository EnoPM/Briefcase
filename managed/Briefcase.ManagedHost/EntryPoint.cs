using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
#if !BRIEFCASE_HEADLESS
using Briefcase.Rendering;
#endif

namespace Briefcase.ManagedHost;

[SupportedOSPlatform("windows")]
public static unsafe class EntryPoint
{
    private static ManagedModManager? _manager;
    private static BuiltInServiceLoader? _builtIns;
    private static Thread? _initializationThread;
    private static GameThreadService? _gameThread;
#if !BRIEFCASE_HEADLESS
    private static ManagedRenderingHost? _rendering;
    private static FrameworkStartupProgress? _startupProgress;
#endif
    private static ConfigurationRegistry? _configuration;
    private static int _initializationStarted;

    // version.dll obtains this function pointer through hostfxr. Only the small
    // native-facing setup remains synchronous. SDK generation, assembly probing
    // and mod loading continue on a lower-priority worker so the game can keep
    // rendering while Briefcase initializes.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(NativeHostApi* host)
    {
        try
        {
            if (Volatile.Read(ref _initializationStarted) != 0)
                return 0;

            var modManagement = new DeferredModManagementBackend();
            var nativeContext = new ModContext(host);
            if (!nativeContext.IsValid)
                return 1;
            _gameThread = new GameThreadService(nativeContext);
            var context = nativeContext
                .WithGameThread(_gameThread.CreateScope("briefcase.core"))
                .WithModManagement(modManagement);

            var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(gameDirectory))
                return 2;

            var isServer = IsDedicatedServerProcess();
#if BRIEFCASE_HEADLESS
            if (!isServer)
            {
                context.Error("The headless Briefcase host can only run in the dedicated server process.");
                return 4;
            }
#endif
            if (Interlocked.CompareExchange(ref _initializationStarted, 1, 0) != 0)
                return 0;

            var frameworkDirectory = Path.Combine(gameDirectory, "Briefcase");
            var coreDirectory = Path.Combine(frameworkDirectory, "Core");
            FrameworkDependencyResolver.Attach(coreDirectory);
            var loaderConfigurationPath = Path.Combine(frameworkDirectory, "loader.json");
#if !BRIEFCASE_HEADLESS
            var enableGameWindowChrome = FrameworkUiSettings.ReadGameWindowChrome(
                loaderConfigurationPath, context.Warning);
            var avaloniaMenuLifetime = FrameworkUiSettings.ReadAvaloniaMenuLifetime(
                loaderConfigurationPath, context.Warning);
            var avaloniaMenuMargins = FrameworkUiSettings.ReadAvaloniaMenuMargins(
                loaderConfigurationPath, context.Warning);
#endif
            _configuration = new ConfigurationRegistry(
                Path.Combine(frameworkDirectory, "settings.json"),
                context.Info,
                context.Warning,
                context.Error);

#if !BRIEFCASE_HEADLESS
            if (!isServer)
            {
                _startupProgress = new FrameworkStartupProgress();
                _rendering = new ManagedRenderingHost(
                    context.Info,
                    context.Error,
                    Path.Combine(coreDirectory, "Ui", "Avalonia", "Briefcase.AvaloniaUi.dll"),
                    new AvaloniaUiState(
                        _configuration,
                        _startupProgress,
                        avaloniaMenuLifetime,
                        avaloniaMenuMargins),
                    _configuration.GetAvaloniaPlacement,
                    _configuration.RememberAvaloniaPlacement,
                    enableGameWindowChrome,
                    _startupProgress);
                context.Info("Framework UI: Avalonia");
                _rendering.Start();
            }
#endif

            _initializationThread = new Thread(() => InitializeComponents(
                context,
                modManagement,
                frameworkDirectory,
                coreDirectory,
                isServer))
            {
                IsBackground = true,
                Name = "Briefcase startup",
                Priority = ThreadPriority.BelowNormal
            };
            _initializationThread.Start();
            return 0;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _initializationStarted, 0);
            try { new ModContext(host).Error($"Managed host initialization failed: {exception}"); }
            catch { }
            return 3;
        }
    }

    private static void InitializeComponents(
        ModContext context,
        DeferredModManagementBackend modManagement,
        string frameworkDirectory,
        string coreDirectory,
        bool isServer)
    {
        try
        {
            Report(0.08, "Loading the generated SDK", "Identifying the SDK for this game build...");
            var generatedSdk = GeneratedSdkLoader.Load(
                context,
                coreDirectory,
                (progress, detail) =>
                    Report(0.08 + (progress * 0.24), "Loading the generated SDK", detail));
            if (generatedSdk is not null)
            {
                var sdkIsServer = generatedSdk.GetName().Name?.EndsWith(
                    ".Server.Sdk", StringComparison.Ordinal) == true;
                if (sdkIsServer != isServer)
                    throw new InvalidOperationException(
                        "The generated SDK target does not match the running executable.");
            }

            Report(0.34, "Discovering mods", "Reading installed mod metadata...");
            _manager = new ManagedModManager(
                context,
                Path.Combine(frameworkDirectory, "Mods"),
                Path.Combine(coreDirectory, "Cache"),
                generatedSdk,
                _configuration!,
                _gameThread!);
            modManagement.Attach(_manager);
            _configuration!.AttachModControl(_manager);
            _manager.Start((progress, detail) =>
                Report(0.34 + (progress * 0.46), "Loading mods", detail));

            Report(0.82, "Loading Core services", "Starting built-in Briefcase services...");
            _builtIns = new BuiltInServiceLoader(
                context,
                Path.Combine(coreDirectory, "BuiltIns"),
                generatedSdk,
                _configuration,
                _gameThread!);
            _builtIns.Start((progress, detail) =>
                Report(0.82 + (progress * 0.17), "Loading Core services", detail));

            Report(0.99, "Finalizing", "Preparing the configuration interface...");
#if !BRIEFCASE_HEADLESS
            _startupProgress?.Complete();
#endif
            context.Info("Managed host initialization completed in the background.");
        }
        catch (Exception exception)
        {
            context.Error($"Managed host background initialization failed: {exception}");
#if !BRIEFCASE_HEADLESS
            _startupProgress?.Fail(exception.Message);
#endif
        }
    }

    private static bool IsDedicatedServerProcess() =>
        string.Equals(
            Path.GetFileName(Environment.ProcessPath),
            "DeceiveIncServer-Win64-Shipping.exe",
            StringComparison.OrdinalIgnoreCase);

    private static void Report(double progress, string stage, string detail)
    {
#if !BRIEFCASE_HEADLESS
        _startupProgress?.Report(progress, stage, detail);
#endif
        Thread.Yield();
    }
}
