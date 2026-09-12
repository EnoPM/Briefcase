using Avalonia;
using Avalonia.Controls;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Arranges cards in one or two equal columns. It deliberately avoids a
/// virtualized or animated layout so resizing the game window remains cheap.
/// </summary>
internal sealed class ResponsiveCardPanel : Panel
{
    public double MinimumColumnWidth { get; set; } = BriefcaseTheme.MinimumCardWidth;
    public double ColumnSpacing { get; set; } = BriefcaseTheme.CardSpacing;
    public double RowSpacing { get; set; } = BriefcaseTheme.CardSpacing;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : Math.Max(MinimumColumnWidth, DesiredSize.Width);
        var columns = ColumnCount(width, MinimumColumnWidth, ColumnSpacing);
        var columnWidth = Math.Max(0, (width - ((columns - 1) * ColumnSpacing)) / columns);
        var height = 0d;

        for (var index = 0; index < Children.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < Children.Count; column++)
            {
                var child = Children[index + column];
                child.Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight;
            if (index + columns < Children.Count) height += RowSpacing;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount(finalSize.Width, MinimumColumnWidth, ColumnSpacing);
        var columnWidth = Math.Max(
            0, (finalSize.Width - ((columns - 1) * ColumnSpacing)) / columns);
        var y = 0d;

        for (var index = 0; index < Children.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < Children.Count; column++)
                rowHeight = Math.Max(rowHeight, Children[index + column].DesiredSize.Height);

            for (var column = 0; column < columns && index + column < Children.Count; column++)
            {
                var x = column * (columnWidth + ColumnSpacing);
                Children[index + column].Arrange(new Rect(x, y, columnWidth, rowHeight));
            }
            y += rowHeight + RowSpacing;
        }
        return finalSize;
    }

    internal static int ColumnCount(
        double width,
        double minimumColumnWidth = BriefcaseTheme.MinimumCardWidth,
        double spacing = BriefcaseTheme.CardSpacing)
    {
        if (!double.IsFinite(width) || width < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(minimumColumnWidth) || minimumColumnWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumColumnWidth));
        if (!double.IsFinite(spacing) || spacing < 0)
            throw new ArgumentOutOfRangeException(nameof(spacing));
        return width >= (minimumColumnWidth * 2) + spacing ? 2 : 1;
    }
}
