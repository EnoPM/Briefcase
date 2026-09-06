using System.Runtime.InteropServices;

namespace Briefcase.ModApi;

// Unreal's FKey is 0x18 bytes in the supported Deceive Inc. build. The public
// value deliberately stays opaque: mods can pass a zero-initialized key to a
// Blueprint event that only uses the event as a signal, without exposing or
// constructing Unreal's private FName/FKeyDetails pointers.
[StructLayout(LayoutKind.Sequential, Size = 0x18)]
public readonly struct FKey
{
}

/// <summary>
/// Blittable representation of Unreal's eight-byte FName value. Briefcase only
/// exposes the canonical None value for now; constructing arbitrary names needs
/// access to Unreal's name pool and belongs in a separate bounded API.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct UnrealName(int ComparisonIndex, int Number)
{
    public static UnrealName None => default;
}

/// <summary>
/// Managed representation of Unreal's localized FText value. The runtime
/// converts this UTF-16 value to a real engine-owned FText for the duration of
/// an invocation, so mods never depend on FText's private 24-byte layout.
/// </summary>
public readonly record struct UnrealText
{
    public string Value { get; }

    public UnrealText(string value) =>
        Value = value ?? throw new ArgumentNullException(nameof(value));

    public static implicit operator UnrealText(string value) => new(value);
    public static implicit operator string(UnrealText value) => value.Value ?? string.Empty;
    public override string ToString() => Value ?? string.Empty;
}
