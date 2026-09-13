using Briefcase.ModApi;

namespace Briefcase.Rendering;


internal readonly record struct AvaloniaMenuSize(double Width, double Height);

internal static class AvaloniaMenuLayout
{
    public static AvaloniaMenuSize Calculate(
        double clientWidth,
        double clientHeight,
        double horizontalMarginPercent,
        double verticalMarginPercent)
    {
        clientWidth = Math.Max(1, clientWidth);
        clientHeight = Math.Max(1, clientHeight);
        horizontalMarginPercent = Math.Clamp(horizontalMarginPercent, 0, 45);
        verticalMarginPercent = Math.Clamp(verticalMarginPercent, 0, 45);
        return new AvaloniaMenuSize(
            Math.Max(1, clientWidth * (1 - (horizontalMarginPercent / 50))),
            Math.Max(1, clientHeight * (1 - (verticalMarginPercent / 50))));
    }
}

public readonly record struct AvaloniaWindowPlacement(
    double X,
    double Y,
    double Width,
    double Height);

internal interface IRetainedMenuHost : IDisposable
{
    bool HasFailed { get; }
    bool IsForeground { get; }
    void Start();
    void SetVisible(bool visible);
    void SetSuspended(bool suspended);
    void SubmitOverlay(OverlayFrameSnapshot frame);
}

/// <summary>
/// Keeps the optional retained UI behind a BCL-only boundary. Constructing this
/// wrapper never resolves Avalonia types; the real host is created only when the
/// user first asks to show it.
/// </summary>
internal sealed class LazyRetainedMenuHost(
    Func<IRetainedMenuHost>? factory,
    Action<string> error) : IDisposable
{
    private IRetainedMenuHost? _host;
    private bool _factoryFailed;
    private bool _suspended;

    public bool UseRetained => factory is not null &&
        !_factoryFailed && _host?.HasFailed != true;
    public bool IsForeground => _host?.IsForeground == true;

    /// <summary>
    /// Resolves and starts the configured retained backend without showing its
    /// window. Briefcase calls this only after Unreal's client startup gate, so
    /// Avalonia can initialize before the first F1 without competing with the
    /// game's shader compilation.
    /// </summary>
    public void Prepare()
    {
        if (_host is not null || _factoryFailed || factory is null) return;
        try
        {
            _host = factory();
            _host.Start();
            _host.SetSuspended(_suspended);
        }
        catch (Exception exception)
        {
            _factoryFailed = true;
            _host?.Dispose();
            _host = null;
            error($"Retained menu creation failed: {exception}");
        }
    }

    public void SetVisible(bool visible)
    {
        if (visible) Prepare();
        _host?.SetVisible(visible);
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        _host?.SetSuspended(suspended);
    }

    public void SubmitOverlay(OverlayFrameSnapshot frame) =>
        _host?.SubmitOverlay(frame);

    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
    }
}

internal static class AvaloniaPlacementConstraints
{
    private const double GameEdgeMargin = 40;

    public static AvaloniaWindowPlacement Fit(
        AvaloniaWindowPlacement? saved,
        double clientWidth,
        double clientHeight,
        double defaultWidth,
        double defaultHeight,
        double minimumWidth,
        double minimumHeight,
        double scale)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        clientWidth = Math.Max(1, clientWidth);
        clientHeight = Math.Max(1, clientHeight);
        var minWidth = Math.Max(1, minimumWidth * scale);
        var minHeight = Math.Max(1, minimumHeight * scale);
        var maxWidth = Math.Max(minWidth, clientWidth - GameEdgeMargin);
        var maxHeight = Math.Max(minHeight, clientHeight - GameEdgeMargin);
        var desiredWidth = saved?.Width is > 0
            ? saved.Value.Width
            : defaultWidth * scale;
        var desiredHeight = saved?.Height is > 0
            ? saved.Value.Height
            : defaultHeight * scale;
        var width = Math.Clamp(desiredWidth, minWidth, maxWidth);
        var height = Math.Clamp(desiredHeight, minHeight, maxHeight);
        var x = saved?.X ?? Math.Max(0, (clientWidth - width) / 2);
        var y = saved?.Y ?? Math.Max(0, (clientHeight - height) / 2);
        return ConstrainPosition(
            new AvaloniaWindowPlacement(x, y, width, height),
            clientWidth,
            clientHeight);
    }

    public static AvaloniaWindowPlacement ConstrainPosition(
        AvaloniaWindowPlacement placement,
        double clientWidth,
        double clientHeight) => placement with
        {
            X = Math.Clamp(placement.X, 0, Math.Max(0, clientWidth - placement.Width)),
            Y = Math.Clamp(placement.Y, 0, Math.Max(0, clientHeight - placement.Height))
        };

    public static AvaloniaWindowPlacement Center(
        AvaloniaWindowPlacement placement,
        double clientWidth,
        double clientHeight) => placement with
        {
            X = Math.Max(0, (clientWidth - placement.Width) / 2),
            Y = Math.Max(0, (clientHeight - placement.Height) / 2)
        };
}
