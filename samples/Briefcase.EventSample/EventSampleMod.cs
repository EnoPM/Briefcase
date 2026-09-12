using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.EventSample;

/// <summary>
/// Demonstrates an exact-instance, typed Unreal multicast-delegate subscription.
/// The sample is built with Briefcase but is not included in release packages.
/// </summary>
public sealed class EventSampleMod : BriefcaseMod
{
    private IDisposable? _serverListSubscription;

    public override ModInfo Info { get; } = new(
        Id: "briefcase.event-sample",
        Name: "Briefcase Event Sample",
        Author: "EnoPM",
        Version: "1.0.0",
        Description: "Typed Unreal multicast-delegate subscription example.",
        RequiredCapabilities: BriefcaseAbi.CoreCapability |
                              BriefcaseAbi.UnrealReflectionCapability |
                              BriefcaseAbi.UnrealInvocationCapability |
                              BriefcaseAbi.PatchingCapability |
                              BriefcaseAbi.GameThreadCapability);

    public override void Load(ModContext context)
    {
        context.GameThread.OnEngineReady(() => Subscribe(context));
    }

    private void Subscribe(ModContext context)
    {
        if (!context.Events.IsAvailable)
        {
            context.Warning("The Unreal event service is unavailable.");
            return;
        }

        var browser = context.Unreal
            .FindObjects(EOSServerBrowserSubsystem.StaticClass)
            .FirstOrDefault(candidate =>
                !candidate.Name.Contains("Default__", StringComparison.Ordinal));
        if (browser is null)
        {
            context.Warning("No live EOSServerBrowserSubsystem was found.");
            return;
        }

        _serverListSubscription = context.Events.Subscribe(
            browser,
            EOSServerBrowserSubsystem.Properties.OnServerListUpdated,
            (_, arguments) =>
            {
                var servers = arguments.Get<UnrealArray<FDIServerInfo>>("Servers");
                context.Info($"OnServerListUpdated received {servers.Count} server(s).");

                // Self-disposal is safe. Briefcase disables this callback now and
                // removes its native binding on a later game-thread dispatch.
                _serverListSubscription?.Dispose();
                _serverListSubscription = null;
            });

        // Subscribe queues its native work for the next game-thread pulse. Queue
        // the trigger immediately after it so registration always runs first,
        // even in an unattended client that becomes idle after startup.
        context.GameThread.Post(() =>
        {
            context.Info("Requesting a vanilla server-list refresh.");
            browser.RefreshServerList();
            context.Info("Server-list refresh requested.");
        });
    }

    public override void Unload()
    {
        _serverListSubscription?.Dispose();
        _serverListSubscription = null;
    }
}
