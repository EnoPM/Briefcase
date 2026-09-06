using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
#if !BRIEFCASE_HEADLESS
using Briefcase.Rendering;
#endif

namespace Briefcase.ManagedHost;

public static unsafe class EntryPoint
{
    private static ManagedModManager? _manager;
    private static BuiltInServiceLoader? _builtIns;
#if !BRIEFCASE_HEADLESS
    private static ManagedRenderingHost? _rendering;
#endif
    private static ConfigurationRegistry? _configuration;

    // version.dll obtains this function pointer through hostfxr. The method uses
    // the small C ABI shared with the native bootstrap. Managed mods never see
    // that pointer directly; they receive the typed ModContext facade.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(NativeHostApi* host)
    {
        try
        {
            if (_manager is not null)
                return 0;

            var modManagement = new DeferredModManagementBackend();
            var context = new ModContext(host).WithModManagement(modManagement);
            if (!context.IsValid)
                return 1;

            var gameDirectory = Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrWhiteSpace(gameDirectory))
                return 2;

            var frameworkDirectory = Path.Combine(gameDirectory, "Briefcase");
            var coreDirectory = Path.Combine(frameworkDirectory, "Core");
            var generatedSdk = GeneratedSdkLoader.Load(context, coreDirectory);
            var isServer = generatedSdk?.GetName().Name?.EndsWith(
                ".Server.Sdk", StringComparison.Ordinal) == true;
#if BRIEFCASE_HEADLESS
            if (!isServer)
            {
                context.Error("The headless Briefcase host can only run with a generated server SDK.");
                return 4;
            }
#endif
            _configuration = new ConfigurationRegistry(
                Path.Combine(frameworkDirectory, "settings.json"),
                context.Info,
                context.Warning,
                context.Error);
#if !BRIEFCASE_HEADLESS
            // A full package may be copied to a dedicated server while it is
            // being migrated. Target detection still keeps all window, input,
            // and rendering work disabled in a server process.
            if (!isServer)
            {
            _rendering = new ManagedRenderingHost(
                context.Info,
                context.Error,
                _configuration.Draw);
            _rendering.Start();
            }
#endif
            _manager = new ManagedModManager(
                context,
                Path.Combine(frameworkDirectory, "Mods"),
                Path.Combine(coreDirectory, "Cache"),
                generatedSdk,
                _configuration);
            modManagement.Attach(_manager);
            _configuration.AttachModControl(_manager);
            _manager.Start();
            _builtIns = new BuiltInServiceLoader(
                context,
                Path.Combine(coreDirectory, "BuiltIns"),
                generatedSdk,
                _configuration);
            _builtIns.Start();
            return 0;
        }
        catch
        {
            return 3;
        }
    }
}
