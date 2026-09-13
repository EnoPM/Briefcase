using Briefcase.DeceiveInc;
using Briefcase.ModApi;

namespace Briefcase.HotReloadSample;

public sealed class HotReloadMod : BriefcaseMod
{
    // Change this string while the game is running, rebuild the project and copy
    // the DLL in its Briefcase/Mods package. The log should show Unloaded then Loaded.
    private const string BuildMarker = "hot reload validated";

    public override ModInfo Info { get; } = new(
        Id: "briefcase.hot-reload-sample",
        Name: "Briefcase Hot Reload Sample",
        Author: "EnoPM",
        Version: "1.0.0-dev",
        Description: "Collectible C# development mod loaded through CoreCLR.",
        RequiredCapabilities: Briefcase.ModApi.Interop.BriefcaseAbi.CoreCapability)
    {
        // Dependencies use stable ModInfo IDs. Briefcase loads Hello first and
        // prevents it from stopping while this sample remains active.
        Dependencies = ["briefcase.hello-managed"]
    };

    public override void Load(ModContext context)
    {
        // Keep the reload proof deterministic. Live UObject handles can become
        // stale while the game changes maps or menus; the generated descriptor
        // is address-free and is sufficient to prove that the shared SDK and a
        // fresh collectible mod generation were loaded correctly.
        context.Info(
            $"{BuildMarker}; shared SDK descriptor " +
            $"{Spy.UnrealPath}.CoverRatio offset=0x{Spy.Properties.CoverRatio.Offset:X}.");
    }

    public override void Unload()
    {
        // Future mods must detach event handlers, cancel timers and stop worker
        // threads here. Any surviving reference prevents collectible ALC unload.
    }
}
