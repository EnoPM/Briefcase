using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Briefcase.ModApi;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Renders the immutable primitive batch produced by client mods. This control
/// is separate from the modal menu and remains pointer-transparent.
/// </summary>
internal sealed class AvaloniaOverlayDrawingView : Control
{
    private OverlayFrameSnapshot _frame = OverlayFrameSnapshot.Empty;

    public AvaloniaOverlayDrawingView()
    {
        IsHitTestVisible = false;
    }

    public bool HasContent => _frame.Commands.Length != 0;

    public void Update(OverlayFrameSnapshot frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var frame = _frame;
        if (frame.Commands.Length == 0 || frame.Width == 0 || frame.Height == 0)
            return;

        var scaleX = Bounds.Width / frame.Width;
        var scaleY = Bounds.Height / frame.Height;
        using var scaled = context.PushTransform(Matrix.CreateScale(scaleX, scaleY));
        foreach (var command in frame.Commands)
        {
            var brush = Brush(command.Color);
            switch (command.Kind)
            {
                case OverlayCommandKind.Circle:
                {
                    var pen = command.Filled
                        ? null
                        : new Pen(brush, Math.Max(0.5, command.Thickness));
                    context.DrawEllipse(
                        command.Filled ? brush : null,
                        pen,
                        new Point(command.X1, command.Y1),
                        Math.Max(0, command.Radius),
                        Math.Max(0, command.Radius));
                    break;
                }
                case OverlayCommandKind.Line:
                    context.DrawLine(
                        new Pen(brush, Math.Max(0.5, command.Thickness)),
                        new Point(command.X1, command.Y1),
                        new Point(command.X2, command.Y2));
                    break;
                case OverlayCommandKind.FilledRectangle:
                    context.DrawRectangle(
                        brush,
                        null,
                        new Rect(
                            command.X1,
                            command.Y1,
                            Math.Max(0, command.X2 - command.X1),
                            Math.Max(0, command.Y2 - command.Y1)),
                        Math.Max(0, command.Rounding),
                        Math.Max(0, command.Rounding));
                    break;
                case OverlayCommandKind.Text when command.Text is { } text:
                    context.DrawText(
                        new FormattedText(
                            text,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            Typeface.Default,
                            14,
                            brush),
                        new Point(command.X1, command.Y1));
                    break;
            }
        }
    }

    private static IBrush Brush(uint rgba) => new SolidColorBrush(Color.FromArgb(
        (byte)(rgba >> 24),
        (byte)rgba,
        (byte)(rgba >> 8),
        (byte)(rgba >> 16)));
}
