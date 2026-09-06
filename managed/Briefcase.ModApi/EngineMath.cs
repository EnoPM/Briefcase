using System.Runtime.InteropServices;

namespace Briefcase.ModApi;

// These structs deliberately mirror Unreal Engine's UE 4.27 float layouts.
// They contain values copied by ProcessEvent; they never expose game pointers.
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FVector(float X, float Y, float Z)
{
    public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
    public static FVector operator +(FVector left, FVector right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
    public static FVector operator -(FVector left, FVector right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    public static FVector operator *(FVector value, float scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);
    public static float Distance(FVector left, FVector right) => (left - right).Length;
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct FVector2D(float X, float Y);

[StructLayout(LayoutKind.Sequential)]
public readonly record struct FRotator(float Pitch, float Yaw, float Roll);

public readonly unsafe partial struct UnrealApi
{
    private static class EngineFunctions
    {
        private static UnrealParameter Return<T>(int offset, int size) =>
            new("ReturnValue", typeof(T), offset, size, true);

        public static readonly UnrealFunction GetActorLocation = new(
            "/Script/Engine.Actor", "K2_GetActorLocation", 12, [],
            typeof(FVector)) { ReturnParameter = Return<FVector>(0, 12) };

        public static readonly UnrealFunction GetVelocity = new(
            "/Script/Engine.Actor", "GetVelocity", 12, [],
            typeof(FVector)) { ReturnParameter = Return<FVector>(0, 12) };

        public static readonly UnrealFunction IsLocalController = new(
            "/Script/Engine.Controller", "IsLocalController", 1, [],
            typeof(bool)) { ReturnParameter = Return<bool>(0, 1) };

        public static readonly UnrealFunction GetControlRotation = new(
            "/Script/Engine.Controller", "GetControlRotation", 12, [],
            typeof(FRotator)) { ReturnParameter = Return<FRotator>(0, 12) };

        public static readonly UnrealFunction SetControlRotation = new(
            "/Script/Engine.Controller", "SetControlRotation", 12,
            [new UnrealParameter("NewRotation", typeof(FRotator), 0, 12)]);

        public static readonly UnrealFunction GetActorEyesViewPoint = new(
            "/Script/Engine.Actor", "GetActorEyesViewPoint", 24,
            [
                new UnrealParameter("OutLocation", typeof(FVector), 0, 12, true),
                new UnrealParameter("OutRotation", typeof(FRotator), 12, 12, true)
            ]);

        public static readonly UnrealFunction LineOfSightTo = new(
            // UE 4.27 packs this parameter buffer to 22 bytes:
            // UObject* (8), FVector (12), input bool (1), return bool (1).
            // Using an aligned C++ size of 24 causes the native bridge's
            // reflected ParmsSize validation to reject every call.
            "/Script/Engine.Controller", "LineOfSightTo", 22,
            [
                new UnrealParameter("Other", typeof(UnrealObjectReference), 0, 8),
                new UnrealParameter("ViewPoint", typeof(FVector), 8, 12),
                new UnrealParameter("bAlternateChecks", typeof(bool), 20, 1)
            ],
            typeof(bool)) { ReturnParameter = Return<bool>(21, 1) };

        public static readonly UnrealFunction ProjectWorldLocationToScreen = new(
            "/Script/Engine.PlayerController", "ProjectWorldLocationToScreen", 22,
            [
                new UnrealParameter("WorldLocation", typeof(FVector), 0, 12),
                new UnrealParameter("ScreenLocation", typeof(FVector2D), 12, 8, true),
                new UnrealParameter("bPlayerViewportRelative", typeof(bool), 20, 1)
            ],
            typeof(bool)) { ReturnParameter = Return<bool>(21, 1) };
    }

    public FVector GetActorLocation(UnrealObject actor) =>
        Invoke<FVector>(actor.Handle, EngineFunctions.GetActorLocation, []);

    public FVector GetVelocity(UnrealObject actor) =>
        Invoke<FVector>(actor.Handle, EngineFunctions.GetVelocity, []);

    public bool IsLocalController(UnrealObject controller) =>
        Invoke<bool>(controller.Handle, EngineFunctions.IsLocalController, []);

    public FRotator GetControlRotation(UnrealObject controller) =>
        Invoke<FRotator>(controller.Handle, EngineFunctions.GetControlRotation, []);

    public void SetControlRotation(UnrealObject controller, FRotator rotation) =>
        InvokeVoid(controller.Handle, EngineFunctions.SetControlRotation, [rotation]);

    public void GetActorEyesViewPoint(
        UnrealObject actor,
        out FVector location,
        out FRotator rotation)
    {
        var buffer = InvokeBuffer(
            actor.Handle, EngineFunctions.GetActorEyesViewPoint, []);
        location = ReadValue<FVector>(
            buffer.Buffer, EngineFunctions.GetActorEyesViewPoint.Parameters[0]);
        rotation = ReadValue<FRotator>(
            buffer.Buffer, EngineFunctions.GetActorEyesViewPoint.Parameters[1]);
    }

    public bool HasLineOfSight(
        UnrealObject controller,
        UnrealObject other,
        FVector viewPoint,
        bool alternateChecks = false) =>
        Invoke<bool>(
            controller.Handle,
            EngineFunctions.LineOfSightTo,
            [new UnrealObjectReference(other.Handle), viewPoint, alternateChecks]);

    public bool ProjectWorldLocationToScreen(
        UnrealObject playerController,
        FVector worldLocation,
        out FVector2D screenLocation,
        bool playerViewportRelative = false)
    {
        var buffer = InvokeBuffer(
            playerController.Handle,
            EngineFunctions.ProjectWorldLocationToScreen,
            [worldLocation, playerViewportRelative]);
        screenLocation = ReadValue<FVector2D>(
            buffer.Buffer, EngineFunctions.ProjectWorldLocationToScreen.Parameters[1]);
        return ReadValue<bool>(
            buffer.Buffer, EngineFunctions.ProjectWorldLocationToScreen.ReturnParameter!.Value);
    }
}
