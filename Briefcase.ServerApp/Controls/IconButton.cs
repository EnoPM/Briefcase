using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Briefcase.ServerApp;

/// <summary>A compact icon-only button whose glyph is centered by its drawn bounds.</summary>
public sealed class IconButton : Button
{
    public static readonly StyledProperty<Geometry?> IconDataProperty =
        AvaloniaProperty.Register<IconButton, Geometry?>(nameof(IconData));
    public static readonly StyledProperty<IBrush> IconStrokeProperty =
        AvaloniaProperty.Register<IconButton, IBrush>(nameof(IconStroke), Brushes.White);
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(IconSize), 17);
    public static readonly StyledProperty<double> IconStrokeThicknessProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(IconStrokeThickness), 1.7);

    private readonly IconGlyph _glyph = new();

    public Geometry? IconData { get => GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }
    public IBrush IconStroke { get => GetValue(IconStrokeProperty); set => SetValue(IconStrokeProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }
    public double IconStrokeThickness
    {
        get => GetValue(IconStrokeThicknessProperty);
        set => SetValue(IconStrokeThicknessProperty, value);
    }

    public IconButton()
    {
        Width = 30;
        Height = 30;
        MinHeight = 30;
        Padding = new Thickness(0);
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Content = _glyph;
        RefreshGlyph();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_glyph is not null && (change.Property == IconDataProperty || change.Property == IconStrokeProperty ||
                                  change.Property == IconSizeProperty || change.Property == IconStrokeThicknessProperty ||
                                  change.Property == WidthProperty || change.Property == HeightProperty))
            RefreshGlyph();
    }

    private void RefreshGlyph()
    {
        _glyph.IconData = IconData;
        _glyph.IconStroke = IconStroke;
        _glyph.IconStrokeThickness = IconStrokeThickness;
        _glyph.Width = Width;
        _glyph.Height = Height;
        _glyph.MaxIconSize = IconSize;
        _glyph.InvalidateVisual();
    }

}

/// <summary>The same centered glyph can also be used in buttons with text.</summary>
public sealed class IconGlyph : Control
{
    private Geometry? _iconData;
    private IBrush _iconStroke = Brushes.White;
    private double _iconStrokeThickness = 1.7;
    private double _maxIconSize = double.NaN;

    public IconGlyph()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
    }

    public Geometry? IconData { get => _iconData; set { _iconData = value; InvalidateVisual(); } }
    public IBrush IconStroke { get => _iconStroke; set { _iconStroke = value; InvalidateVisual(); } }
    public double MaxIconSize { get => _maxIconSize; set { _maxIconSize = value; InvalidateVisual(); } }
    public double IconStrokeThickness
    {
        get => _iconStrokeThickness;
        set { _iconStrokeThickness = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (IconData is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var shape = IconData.Bounds;
        if (shape.Width <= 0 || shape.Height <= 0) return;
        const double inset = 1.5;
        var iconWidth = double.IsNaN(MaxIconSize) ? Bounds.Width : Math.Min(Bounds.Width, MaxIconSize);
        var iconHeight = double.IsNaN(MaxIconSize) ? Bounds.Height : Math.Min(Bounds.Height, MaxIconSize);
        var scale = Math.Min((iconWidth - inset * 2) / shape.Width,
            (iconHeight - inset * 2) / shape.Height);
        if (scale <= 0) return;
        var offsetX = (Bounds.Width - shape.Width * scale) / 2 - shape.X * scale;
        var offsetY = (Bounds.Height - shape.Height * scale) / 2 - shape.Y * scale;
        using (context.PushTransform(new Matrix(scale, 0, 0, scale, offsetX, offsetY)))
            context.DrawGeometry(null, new Pen(IconStroke, IconStrokeThickness / scale), IconData);
    }
}
