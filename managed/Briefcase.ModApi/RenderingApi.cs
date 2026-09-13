
namespace Briefcase.ModApi;

public readonly record struct RenderFrame(
    uint Width,
    uint Height,
    float DeltaSeconds,
    ulong FrameNumber,
    bool MenuVisible,
    OverlayDrawingApi Overlay);

public readonly struct RenderingApi
{
    public bool IsAvailable => ManagedRenderingBridge.Current?.IsAvailable == true;

    public bool MenuVisible
    {
        get => ManagedRenderingBridge.Current?.MenuVisible == true;
        set
        {
            if (ManagedRenderingBridge.Current is { IsAvailable: true } managed)
                managed.MenuVisible = value;
        }
    }

    public IRenderRegistration Register(Action<RenderFrame> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var managed = ManagedRenderingBridge.Current;
        if (managed?.IsAvailable != true)
            throw new InvalidOperationException("The host rendering API is unavailable.");
        return managed.Register(draw);
    }
}

internal interface IManagedRenderingService
{
    bool IsAvailable { get; }
    bool MenuVisible { get; set; }
    IRenderRegistration Register(Action<RenderFrame> draw);
}

internal static class ManagedRenderingBridge
{
    public static IManagedRenderingService? Current { get; set; }
}

public interface IRenderRegistration : IDisposable
{
    bool RenderWhenMenuHidden { get; set; }
}

/// <summary>
/// Toolkit-neutral immediate drawing surface. Calls made during a render
/// callback are batched and rendered in the game's D3D11 swap chain by
/// Briefcase's client-only native renderer.
/// </summary>
public readonly struct OverlayDrawingApi
{
    private readonly OverlayCommandBuffer? _commands;

    internal OverlayDrawingApi(OverlayCommandBuffer commands) => _commands = commands;

    public float Framerate => _commands?.Framerate ?? 0;

    public void DrawCircle(
        float x,
        float y,
        float radius,
        uint rgba,
        int segments = 0,
        float thickness = 1,
        bool filled = false)
    {
        _commands?.Add(new OverlayCommand(
            OverlayCommandKind.Circle, x, y, 0, 0, radius, thickness, 0,
            rgba, null, filled, segments));
    }

    public void DrawLine(
        float x1,
        float y1,
        float x2,
        float y2,
        uint rgba,
        float thickness = 1) =>
        _commands?.Add(new OverlayCommand(
            OverlayCommandKind.Line, x1, y1, x2, y2, 0, thickness, 0,
            rgba, null, false, 0));

    public void DrawRectFilled(
        float minimumX,
        float minimumY,
        float maximumX,
        float maximumY,
        uint rgba,
        float rounding = 0) =>
        _commands?.Add(new OverlayCommand(
            OverlayCommandKind.FilledRectangle,
            minimumX, minimumY, maximumX, maximumY, 0, 0, rounding,
            rgba, null, true, 0));

    public void DrawText(float x, float y, uint rgba, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _commands?.Add(new OverlayCommand(
            OverlayCommandKind.Text, x, y, 0, 0, 0, 0, 0,
            rgba, text, false, 0));
    }
}

public static class OverlayColor
{
    /// <summary>Creates an RGBA color in Briefcase's stable packed format.</summary>
    public static uint Rgba(byte red, byte green, byte blue, byte alpha = 255) =>
        red | ((uint)green << 8) | ((uint)blue << 16) | ((uint)alpha << 24);
}

internal enum OverlayCommandKind
{
    Circle,
    Line,
    FilledRectangle,
    Text
}

internal readonly record struct OverlayCommand(
    OverlayCommandKind Kind,
    float X1,
    float Y1,
    float X2,
    float Y2,
    float Radius,
    float Thickness,
    float Rounding,
    uint Color,
    string? Text,
    bool Filled,
    int Segments);

internal sealed class OverlayCommandBuffer(float framerate)
{
    private const int MaximumCommands = 4096;
    private readonly List<OverlayCommand> _commands = [];

    public float Framerate { get; } = framerate;

    public void Add(OverlayCommand command)
    {
        if (_commands.Count < MaximumCommands) _commands.Add(command);
    }

    public OverlayFrameSnapshot Snapshot(uint width, uint height) =>
        new(width, height, [.. _commands]);
}

internal sealed record OverlayFrameSnapshot(
    uint Width,
    uint Height,
    OverlayCommand[] Commands)
{
    public static OverlayFrameSnapshot Empty { get; } = new(1, 1, []);
}
