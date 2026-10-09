using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Briefcase.ServerApp;

public sealed record MapChoice(string Id, string Label);
public sealed record MapRotationEntry(Guid EntryId, string Id, string Label);

public sealed partial class MapRotationEditor : UserControl
{
    private const double RowHeight = 38;
    private const double SlotHeight = 44;
    private readonly List<RowView> _rows = [];
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private DragState? _drag;
    private double _pointerViewportY;

    public IReadOnlyList<MapChoice> Available { get; } = ServerIniEditor.Maps
        .Select(map => new MapChoice(map.Id, GameMapNames.Display(map.Id))).ToArray();
    public ObservableCollection<MapRotationEntry> Entries { get; } = [];
    public IReadOnlyList<string> Rotation => Entries.Select(entry => entry.Id).ToArray();
    public event Action<IReadOnlyList<string>>? RotationChanged;

    public MapRotationEditor()
    {
        InitializeComponent();
        DataContext = this;
        var menu = new MenuFlyout();
        menu.FlyoutPresenterClasses.Add("map-rotation-menu");
        foreach (var map in Available)
        {
            var item = new MenuItem { Header = map.Label };
            item.Click += (_, _) => AppendMap(map);
            menu.Items.Add(item);
        }
        AddMapButton.Flyout = menu;
        RotationScroll.SizeChanged += (_, _) => UpdateRowWidths();
        _autoScroll.Tick += (_, _) => AutoScrollTick();
    }

    public void Load(IEnumerable<string> ids)
    {
        _autoScroll.Stop();
        _drag = null;
        Entries.Clear();
        foreach (var id in ids)
            if (Available.FirstOrDefault(map => map.Id == id) is { } map)
                Entries.Add(new MapRotationEntry(Guid.NewGuid(), map.Id, map.Label));
        RebuildRows();
    }

    private void AppendMap(MapChoice map)
    {
        if (_drag is not null) return;
        Entries.Add(new MapRotationEntry(Guid.NewGuid(), map.Id, map.Label));
        RebuildRows();
        RotationChanged?.Invoke(Rotation);
        Dispatcher.UIThread.Post(() => RotationScroll.Offset =
            new Vector(0, Math.Max(0, RotationScroll.Extent.Height - RotationScroll.Viewport.Height)));
    }

    private void RebuildRows()
    {
        RowsCanvas.Children.Clear();
        _rows.Clear();
        for (var index = 0; index < Entries.Count; index++)
        {
            var row = CreateRow(Entries[index], index);
            _rows.Add(row);
            RowsCanvas.Children.Add(row.Surface);
        }
        RowsCanvas.Height = Math.Max(294, Entries.Count * SlotHeight - (SlotHeight - RowHeight));
        EmptyHint.IsVisible = Entries.Count == 0;
        UpdateRowWidths();
    }

    private RowView CreateRow(MapRotationEntry entry, int index)
    {
        var handle = new TextBlock
        {
            Text = "⋮⋮", FontSize = 16, Foreground = Brush("#C9ADEB"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var order = new TextBlock
        {
            Text = (index + 1).ToString("00", CultureInfo.InvariantCulture),
            FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brush("#AA8CCB"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = entry.Label, FontSize = 13, FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var remove = new IconButton
        {
            IconData = Geometry.Parse("M 3,3 L 15,15 M 15,3 L 3,15"),
            IconStroke = Brush("#EDAFBA"), IconStrokeThickness = 1.8,
            Width = 28, Height = 28, MinHeight = 28, IconSize = 15,
            VerticalAlignment = VerticalAlignment.Center, IsEnabled = Entries.Count > 1
        };
        ToolTip.SetTip(remove, AppTranslations.Get("ui.remove_this_occurrence"));
        remove.Classes.Add("subtle-danger");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("22,27,*,Auto"), ColumnSpacing = 9 };
        grid.Children.Add(handle);
        Grid.SetColumn(order, 1);
        grid.Children.Add(order);
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);
        var surface = new Border
        {
            Height = RowHeight, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3),
            Background = Brush("#2B2533"), BorderBrush = Brush("#473A55"),
            BorderThickness = new Thickness(1), Child = grid,
            RenderTransform = At(index * SlotHeight),
            Cursor = new Cursor(StandardCursorType.Hand), Focusable = true
        };
        ToolTip.SetTip(surface, AppTranslations.Get("ui.drag_to_reorder"));
        surface.Transitions = MovementTransitions();
        var row = new RowView(entry, surface, remove, order);
        surface.PointerPressed += (_, e) => StartDrag(row, e);
        surface.PointerMoved += (_, e) => DragMoved(row, e);
        surface.PointerReleased += (_, e) => EndDrag(row, e);
        surface.PointerCaptureLost += (_, _) => CancelDrag(row);
        surface.KeyDown += (_, e) => MoveWithKeyboard(row, e);
        remove.Click += (_, _) =>
        {
            if (_drag is not null || Entries.Count <= 1) return;
            Entries.Remove(entry);
            RebuildRows();
            RotationChanged?.Invoke(Rotation);
        };
        return row;
    }

    private void UpdateRowWidths()
    {
        var width = Math.Max(300, RotationScroll.Bounds.Width - 18);
        RowsCanvas.Width = width;
        foreach (var row in _rows) row.Surface.Width = width;
    }

    private void StartDrag(RowView row, PointerPressedEventArgs e)
    {
        if (_drag is not null || !e.GetCurrentPoint(row.Surface).Properties.IsLeftButtonPressed) return;
        var positionOnRemove = e.GetPosition(row.Remove);
        if (positionOnRemove.X >= 0 && positionOnRemove.Y >= 0 &&
            positionOnRemove.X < row.Remove.Bounds.Width && positionOnRemove.Y < row.Remove.Bounds.Height) return;
        var index = _rows.IndexOf(row);
        if (index < 0) return;
        _pointerViewportY = e.GetPosition(RotationScroll).Y;
        var pointerCanvasY = e.GetPosition(RowsCanvas).Y;
        _drag = new DragState(row, index, index, pointerCanvasY - index * SlotHeight, e.Pointer);
        row.Surface.Focus();
        e.Pointer.Capture(row.Surface);
        row.Surface.Transitions = null;
        row.Surface.Opacity = 0.96;
        row.Surface.BorderBrush = Brush("#A875E7");
        row.Surface.ZIndex = 10;
        e.Handled = true;
    }

    private void DragMoved(RowView row, PointerEventArgs e)
    {
        if (_drag is not { } drag || drag.Row != row || drag.Pointer != e.Pointer) return;
        _pointerViewportY = e.GetPosition(RotationScroll).Y;
        UpdateDragPosition(e.GetPosition(RowsCanvas).Y);
        if (_pointerViewportY < 30 || _pointerViewportY > RotationScroll.Viewport.Height - 30)
            _autoScroll.Start();
        else
            _autoScroll.Stop();
        e.Handled = true;
    }

    private void UpdateDragPosition(double pointerCanvasY)
    {
        if (_drag is not { } drag) return;
        var maximum = Math.Max(0, (Entries.Count - 1) * SlotHeight);
        var y = Math.Clamp(pointerCanvasY - drag.GrabOffset, 0, maximum);
        drag.Row.Surface.RenderTransform = At(y);
        var target = Math.Clamp((int)Math.Round(y / SlotHeight), 0, Entries.Count - 1);
        if (target == drag.TargetIndex) return;
        drag.TargetIndex = target;
        var nextSlot = 0;
        foreach (var row in _rows)
        {
            if (row == drag.Row) continue;
            if (nextSlot == target) nextSlot++;
            row.Order.Text = (nextSlot + 1).ToString("00", CultureInfo.InvariantCulture);
            row.Surface.RenderTransform = At(nextSlot * SlotHeight);
            nextSlot++;
        }
        drag.Row.Order.Text = (target + 1).ToString("00", CultureInfo.InvariantCulture);
    }

    private void AutoScrollTick()
    {
        if (_drag is null) { _autoScroll.Stop(); return; }
        var speed = _pointerViewportY < 30 ? -7 :
            _pointerViewportY > RotationScroll.Viewport.Height - 30 ? 7 : 0;
        if (speed == 0) { _autoScroll.Stop(); return; }
        var limit = Math.Max(0, RotationScroll.Extent.Height - RotationScroll.Viewport.Height);
        var next = Math.Clamp(RotationScroll.Offset.Y + speed, 0, limit);
        RotationScroll.Offset = new Vector(0, next);
        UpdateDragPosition(_pointerViewportY + next);
    }

    private void EndDrag(RowView row, PointerReleasedEventArgs e)
    {
        if (_drag is not { } drag || drag.Row != row || drag.Pointer != e.Pointer) return;
        _drag = null;
        _autoScroll.Stop();
        e.Pointer.Capture(null);
        if (drag.OriginIndex != drag.TargetIndex)
        {
            Entries.Move(drag.OriginIndex, drag.TargetIndex);
            _rows.RemoveAt(drag.OriginIndex);
            _rows.Insert(drag.TargetIndex, row);
            RowsCanvas.Children.Remove(row.Surface);
            RowsCanvas.Children.Insert(drag.TargetIndex, row.Surface);
        }
        row.Surface.Transitions = MovementTransitions();
        row.Surface.Opacity = 1;
        row.Surface.BorderBrush = Brush("#473A55");
        row.Surface.ZIndex = 0;
        for (var index = 0; index < _rows.Count; index++)
        {
            _rows[index].Order.Text = (index + 1).ToString("00", CultureInfo.InvariantCulture);
            _rows[index].Surface.RenderTransform = At(index * SlotHeight);
        }
        if (drag.OriginIndex != drag.TargetIndex) RotationChanged?.Invoke(Rotation);
        e.Handled = true;
    }

    private void CancelDrag(RowView row)
    {
        if (_drag is not { } drag || drag.Row != row) return;
        _drag = null;
        _autoScroll.Stop();
        row.Surface.Transitions = MovementTransitions();
        row.Surface.Opacity = 1;
        row.Surface.BorderBrush = Brush("#473A55");
        row.Surface.ZIndex = 0;
        for (var index = 0; index < _rows.Count; index++)
        {
            _rows[index].Order.Text = (index + 1).ToString("00", CultureInfo.InvariantCulture);
            _rows[index].Surface.RenderTransform = At(index * SlotHeight);
        }
    }

    private void MoveWithKeyboard(RowView row, KeyEventArgs e)
    {
        var direction = e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 };
        if (direction == 0 || _drag is not null) return;
        var from = _rows.IndexOf(row);
        var to = from + direction;
        if (from < 0 || to < 0 || to >= Entries.Count) return;
        Entries.Move(from, to);
        RebuildRows();
        RotationChanged?.Invoke(Rotation);
        Dispatcher.UIThread.Post(() => _rows[to].Surface.Focus());
        e.Handled = true;
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static TransformOperations At(double y) => TransformOperations.Parse(
        "translate(0px, " + y.ToString(CultureInfo.InvariantCulture) + "px)");
    private static Transitions MovementTransitions() =>
    [
        new TransformOperationsTransition
        {
            Property = Border.RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new CubicEaseOut()
        }
    ];

    private sealed record RowView(MapRotationEntry Entry, Border Surface, Button Remove, TextBlock Order);
    private sealed class DragState(RowView row, int originIndex, int targetIndex, double grabOffset, IPointer pointer)
    {
        public RowView Row { get; } = row;
        public int OriginIndex { get; } = originIndex;
        public int TargetIndex { get; set; } = targetIndex;
        public double GrabOffset { get; } = grabOffset;
        public IPointer Pointer { get; } = pointer;
    }
}
