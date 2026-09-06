using Briefcase.DeceiveInc;
using Briefcase.ModApi;

namespace Briefcase.HotReloadSample;

internal static class CoverRatioPatch
{
    // Both the type and the member are compiler checked. Patch options stay on
    // the method they configure instead of being spread over a container type.
    [UnrealPostfixPatch(
        typeof(Spy),
        nameof(Spy.BP_OnCoverRatioUpdate),
        Priority = 100,
        Reentrancy = PatchReentrancy.SuppressCurrentPatch,
        OnException = PatchExceptionPolicy.LogAndContinue)]
    private static void OnCoverRatioUpdatePostfix(Spy __instance)
    {
        // Spy is a real generated class. Properties and generated UFunctions
        // are called exactly like ordinary C# members.
        _ = __instance.CoverRatio;

        // This generated method uses the validated ProcessEvent invocation ABI.
        // Once patch dispatch is enabled, SuppressCurrentPatch prevents this
        // postfix from recursively invoking itself for the nested call.
        __instance.BP_OnCoverRatioUpdate(__instance.CoverRatio);
    }
}
