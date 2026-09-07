using Briefcase.DeceiveInc;
using Briefcase.ModApi;

namespace Briefcase.HelloSample;

public sealed class MyMod : BriefcaseMod
{
    public override ModInfo Info { get; } = new(
        Id: "briefcase.hello-managed",
        Name: "Briefcase Hello Sample",
        Author: "EnoPM",
        Version: "1.0.0",
        Description: "Managed C# example using generated Deceive Inc. types.",
        RequiredCapabilities: Briefcase.ModApi.Interop.BriefcaseAbi.CoreCapability |
                              Briefcase.ModApi.Interop.BriefcaseAbi.GameThreadCapability);

    public override void Load(ModContext context)
    {
        context.Info($"C# mod loaded. Framework {context.FrameworkVersion}; " +
                     $"known type {Spy.UnrealPath}, CoverRatio offset=0x{Spy.Properties.CoverRatio.Offset:X}.");

        context.GameThread.OnEngineReady(() =>
            context.Info("Hello Sample entered Unreal's game thread."));
        context.GameThread.OnMapLoaded(world =>
            context.Info($"Map world observed: {world.Path}"));
        context.GameThread.RunAfter(TimeSpan.FromSeconds(1), () =>
            context.Info("One-second game-thread timer completed."));
    }
}
