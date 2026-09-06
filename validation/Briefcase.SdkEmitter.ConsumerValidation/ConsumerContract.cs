using Briefcase.DeceiveInc;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using Briefcase.Unreal.Engine;
using Briefcase.Unreal.SlateCore;

namespace Briefcase.SdkEmitter.ConsumerValidation;

internal static class ConsumerContract
{
    [UnrealPrefixPatch(
        typeof(PlayerController),
        nameof(PlayerController.ClientReturnToMainMenuWithTextReason))]
    private static void ObserveReturnReason(UnrealText ReturnReason)
    {
        _ = ReturnReason.Value;
    }

    [UnrealPostfixPatch(typeof(Spy), nameof(Spy.BP_OnCoverRatioUpdate))]
    private static void ObserveCoverRatio(Spy __instance, float NewRatio)
    {
        _ = __instance.CoverRatio;
        __instance.BP_OnCoverRatioUpdate(NewRatio);
    }

    [NativePrefixPatch(typeof(DIMenuSubsystem), nameof(DIMenuSubsystem.DisplayMainMenu))]
    private static void ObserveMainMenu(DIMenuSubsystem __instance)
    {
        __instance.DisplayMainMenu();
    }

    public static T Wrap<T>(UnrealApi api, UnrealObjectHandle handle)
        where T : UnrealObject, IUnrealObject<T> => T.FromObject(api, handle);

    public static FMargin CreateMargin(float value) => new()
    {
        Left = value,
        Top = value,
        Right = value,
        Bottom = value
    };

    // These calls are deliberately compiled against the generated assembly.
    // They verify that TextProperty parameters and return values stay exposed
    // as UnrealText instead of silently disappearing from a future SDK.
    public static UnrealText ValidateTextSurface(
        PlayerController controller,
        KismetTextLibrary textLibrary)
    {
        controller.ClientWasKicked("Briefcase FText validation");
        controller.ClientReturnToMainMenuWithTextReason(
            new UnrealText("Briefcase FText validation"));
        return textLibrary.Conv_StringToText("Briefcase FText validation");
    }
}
