using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Briefcase.ServerApp;

/// <summary>A compact catalog-driven form for the UE4SS LootControl mod.</summary>
public sealed class LootControlEditorControl : UserControl
{
    private sealed record PlaceChoice(LootPlaceKey Key)
    {
        public override string ToString() => Key.Label;
    }

    private sealed record MapChoice(string Id)
    {
        public override string ToString() => Id == "*"
            ? AppTranslations.Get("loot.all_maps", "All maps") : GameMapNames.Display(Id);
    }

    private sealed record ItemChoice(string Id)
    {
        public override string ToString() => LootControlConfigEditor.DisplayItem(Id);
    }

    private sealed class PlaceSection
    {
        public required ComboBox Place { get; init; }
        public required ComboBox AddItem { get; init; }
        public required TextBox Search { get; init; }
        public required StackPanel Rows { get; init; }
    }

    private readonly LootControlConfigEditor _editor;
    private readonly Dictionary<(LootPlaceKey Place, string ObjectType), string> _weightDraft = new();
    private readonly Dictionary<(LootPlaceKey Place, string ObjectType), string> _addedDraft = new();
    private readonly Dictionary<(string Map, string ObjectType), string> _countDraft = new();
    private readonly Dictionary<string, string> _occurrenceDraft = new(StringComparer.Ordinal);
    private readonly StackPanel _locationSections = new() { Spacing = 7 };
    private readonly StackPanel _mapRows = new() { Spacing = 3 };
    private readonly StackPanel _globalRows = new() { Spacing = 3 };
    private readonly TextBox _mapSearch = new();
    private readonly TextBox _globalSearch = new();
    private readonly ComboBox _maps = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _diagnostics = new();

    public LootControlEditorControl(LootControlConfigEditor editor)
    {
        _editor = editor;
        _diagnostics.Content = AppTranslations.Get("loot.diagnostics", "Detailed diagnostics");
        _diagnostics.IsChecked = editor.Diagnostics;
        _mapSearch.PlaceholderText = _globalSearch.PlaceholderText =
            AppTranslations.Get("loot.search", "Search items");
        _maps.ItemsSource = new[] { new MapChoice("*") }
            .Concat(editor.Maps.Select(map => new MapChoice(map))).ToArray();
        _maps.SelectedIndex = editor.Maps.Count > 0 ? 1 : 0;
        _maps.SelectionChanged += (_, _) => BuildMapRows();
        _mapSearch.TextChanged += (_, _) => BuildMapRows();
        _globalSearch.TextChanged += (_, _) => BuildGlobalRows();

        BuildLocationSections();

        var tabs = new TabControl { HorizontalAlignment = HorizontalAlignment.Stretch };
        tabs.Items.Add(new TabItem
        {
            Header = AppTranslations.Get("loot.locations", "Locations"),
            Content = _locationSections
        });
        tabs.Items.Add(new TabItem
        {
            Header = AppTranslations.Get("loot.map_limits", "Map limits"),
            Content = Section(_maps, _mapSearch, _mapRows,
                AppTranslations.Get("loot.vanilla", "Vanilla"),
                AppTranslations.Get("loot.map_limits_hint",
                    "Set the maximum number of each item on this map. The vanilla values were captured in Solo mode and can vary by game version. Leave blank to inherit the game's value or an all-maps override."),
                editor.Maps.Count == 0
                    ? AppTranslations.Get("loot.no_maps", "Start a map once to collect its item counts.") : null)
        });
        tabs.Items.Add(new TabItem
        {
            Header = AppTranslations.Get("loot.global", "Global weights"),
            Content = Section(null, _globalSearch, _globalRows,
                AppTranslations.Get("loot.vanilla", "Vanilla"),
                AppTranslations.Get("loot.global_hint",
                    "This factor applies to the item in every location. Location weights above can refine it."), null)
        });
        tabs.Items.Add(new TabItem
        {
            Header = AppTranslations.Get("loot.options", "Options"),
            Content = new StackPanel
            {
                Spacing = 8, Margin = new Thickness(2, 12, 2, 2),
                Children =
                {
                    _diagnostics,
                    new TextBlock
                    {
                        Text = AppTranslations.Get("loot.diagnostics_hint",
                            "Detailed diagnostics can produce many log entries while maps load. Use it only when troubleshooting."),
                        TextWrapping = TextWrapping.Wrap, FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#AFA5BB"))
                    }
                }
            }
        });
        Content = tabs;
        BuildMapRows();
        BuildGlobalRows();
    }

    public string Render() => _editor.Render(_weightDraft, _countDraft, _occurrenceDraft, _addedDraft,
        _diagnostics.IsChecked == true);

    private static string AreaId(LootPlaceKey place) => place.Vault ? "vault" : place.Security switch
    {
        3 => "purple", 2 => "blue", 1 => "green", 0 => "public", _ => "security" + place.Security
    };

    private void BuildLocationSections()
    {
        _locationSections.Children.Clear();
        _locationSections.Children.Add(new TextBlock
        {
            Text = AppTranslations.Get("loot.locations_hint",
                "Vanilla items and their relative weights appear after a map loads. Changes to one item keep the other items in its pool."),
            TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#AFA5BB")),
            FontSize = 12, Margin = new Thickness(2, 9, 2, 4)
        });
        if (_editor.Places.Count == 0)
        {
            _locationSections.Children.Add(Label(AppTranslations.Get("loot.no_places",
                "Start a map once to let LootControl collect its spawn locations.")));
            return;
        }
        var order = new[] { "vault", "purple", "blue", "green", "public" };
        var groups = _editor.Places.GroupBy(AreaId)
            .OrderBy(group => Array.IndexOf(order, group.Key) is var index && index >= 0
                ? index : order.Length).ThenBy(group => group.Key);
        foreach (var group in groups)
        {
            var choices = group.OrderByDescending(key => _editor.LocationItems(key).Count)
                .ThenBy(key => key.PointType == "Default" ? 0 : 1)
                .ThenBy(key => key.Security).ThenBy(key => key.PointType)
                .Select(key => new PlaceChoice(key)).ToArray();
            var place = new ComboBox { ItemsSource = choices,
                SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var search = new TextBox { PlaceholderText = AppTranslations.Get("loot.search", "Search items") };
            var rows = new StackPanel { Spacing = 3 };
            var add = new ComboBox { PlaceholderText = AppTranslations.Get("loot.add_item", "Add an item"),
                HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 220,
                ItemTemplate = new FuncDataTemplate<ItemChoice>((choice, _) =>
                    choice is null ? new TextBlock() : ItemLabel(choice.Id)) };
            var section = new PlaceSection { Place = place, AddItem = add, Search = search, Rows = rows };
            place.SelectionChanged += (_, _) => BuildPlaceRows(section);
            search.TextChanged += (_, _) => BuildPlaceRows(section);
            add.SelectionChanged += (_, _) =>
            {
                if (add.SelectedItem is not ItemChoice selected || place.SelectedItem is not PlaceChoice location)
                    return;
                _addedDraft[(location.Key, selected.Id)] = "1";
                add.SelectedItem = null;
                BuildPlaceRows(section);
            };
            var content = new StackPanel { Spacing = 8, Margin = new Thickness(4, 8, 4, 10) };
            content.Children.Add(place);
            content.Children.Add(search);
            content.Children.Add(Header(AppTranslations.Get("loot.vanilla", "Vanilla")));
            content.Children.Add(rows);
            content.Children.Add(add);
            _locationSections.Children.Add(new Expander
            {
                Header = AppTranslations.Get("loot.area." + group.Key, group.Key),
                IsExpanded = group.Key == "vault", Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
            BuildPlaceRows(section);
        }
    }

    private static StackPanel Section(Control? selector, TextBox search, StackPanel rows,
        string baselineLabel, string hint, string? emptyMessage)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(2, 9, 2, 2) };
        panel.Children.Add(new TextBlock
        {
            Text = hint, Foreground = new SolidColorBrush(Color.Parse("#AFA5BB")),
            TextWrapping = TextWrapping.Wrap, FontSize = 12
        });
        if (selector is not null) panel.Children.Add(selector);
        if (emptyMessage is not null)
            panel.Children.Add(new TextBlock
            {
                Text = emptyMessage, Foreground = new SolidColorBrush(Color.Parse("#D2AC77")),
                TextWrapping = TextWrapping.Wrap, FontSize = 12
            });
        panel.Children.Add(search);
        panel.Children.Add(Header(baselineLabel));
        panel.Children.Add(rows);
        return panel;
    }

    private static Grid Header(string baselineLabel)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,112,100"), ColumnSpacing = 10,
            Margin = new Thickness(6, 2) };
        grid.Children.Add(Label(AppTranslations.Get("loot.item", "Item"), "#AFA5BB"));
        var baseline = Label(baselineLabel, "#AFA5BB");
        Grid.SetColumn(baseline, 1);
        grid.Children.Add(baseline);
        var edited = Label(AppTranslations.Get("loot.override", "Override"), "#AFA5BB");
        Grid.SetColumn(edited, 2);
        grid.Children.Add(edited);
        return grid;
    }

    private static TextBlock Label(string text, string color = "#E9E2EF") => new()
    {
        Text = text, Foreground = new SolidColorBrush(Color.Parse(color)), FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
    };

    private StackPanel ItemLabel(string type)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center };
        var icon = GameImageCatalog.Load(_editor.ImageFor(type));
        if (icon is not null)
            panel.Children.Add(new Image { Source = icon, Width = 22, Height = 22,
                Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
        var label = Label(LootControlConfigEditor.DisplayItem(type));
        ToolTip.SetTip(panel, type);
        panel.Children.Add(label);
        return panel;
    }

    private void BuildPlaceRows(PlaceSection section)
    {
        section.Rows.Children.Clear();
        if (section.Place.SelectedItem is not PlaceChoice selected) return;
        var items = _editor.LocationItems(selected.Key).ToDictionary(item => item.ObjectType,
            StringComparer.Ordinal);
        foreach (var (key, value) in _weightDraft)
            if (key.Place == selected.Key)
            {
                if (string.IsNullOrWhiteSpace(value)) items.Remove(key.ObjectType);
                else items[key.ObjectType] = items.TryGetValue(key.ObjectType, out var existing)
                    ? existing with { Weight = value }
                    : new LootLocationItem(key.ObjectType, value, false,
                        _editor.VanillaWeight(selected.Key, key.ObjectType));
            }
        foreach (var (key, value) in _addedDraft)
            if (key.Place == selected.Key)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (_editor.VanillaWeight(selected.Key, key.ObjectType) is not null)
                        items[key.ObjectType] = new LootLocationItem(key.ObjectType, "", false,
                            _editor.VanillaWeight(selected.Key, key.ObjectType), true);
                    else items.Remove(key.ObjectType);
                }
                else items[key.ObjectType] = items.TryGetValue(key.ObjectType, out var existing)
                    ? existing with { Weight = value, UseAddition = true }
                    : new LootLocationItem(key.ObjectType, value, true, null, true);
            }
        var query = section.Search.Text?.Trim();
        foreach (var item in items.Values.OrderBy(item => LootControlConfigEditor.DisplayItem(item.ObjectType),
                     StringComparer.CurrentCultureIgnoreCase).Where(item =>
                     string.IsNullOrWhiteSpace(query) || item.ObjectType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     LootControlConfigEditor.DisplayItem(item.ObjectType).Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var key = (selected.Key, item.ObjectType);
            var box = Input(item.Weight, AppTranslations.Get("loot.weight_tip",
                "Relative weight. Blank keeps the game's value."));
            box.TextChanged += (_, _) =>
            {
                if (item.UseAddition) _addedDraft[key] = box.Text ?? "";
                else _weightDraft[key] = box.Text ?? "";
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,112,100,30"), ColumnSpacing = 10 };
            var name = ItemLabel(item.ObjectType);
            row.Children.Add(name);
            var original = Label(item.VanillaWeight ?? "—", "#AFA5BB");
            Grid.SetColumn(original, 1);
            row.Children.Add(original);
            Grid.SetColumn(box, 2);
            row.Children.Add(box);
            if (item.Added)
            {
                var remove = new Button { Content = "×", Width = 28, Height = 28,
                    Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(remove, AppTranslations.Get("loot.remove_item", "Remove added item"));
                remove.Click += (_, _) => { _addedDraft[key] = ""; BuildPlaceRows(section); };
                Grid.SetColumn(remove, 3);
                row.Children.Add(remove);
            }
            section.Rows.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#2B2632")),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3), Child = row });
        }
        if (items.Count == 0)
            section.Rows.Children.Add(Label(AppTranslations.Get("loot.no_configured_items",
                "No vanilla items were exported for this location. You can add one below."), "#AFA5BB"));
        section.AddItem.ItemsSource = _editor.ObjectTypes.Where(type => !items.ContainsKey(type))
            .Select(type => new ItemChoice(type)).ToArray();
    }

    private void BuildMapRows()
    {
        _mapRows.Children.Clear();
        if (_maps.SelectedItem is not MapChoice selected) return;
        foreach (var type in Matching(_mapSearch.Text))
        {
            var key = (selected.Id, type);
            var value = _countDraft.GetValueOrDefault(key) ?? _editor.Count(selected.Id, type) ?? "";
            var box = Input(value, AppTranslations.Get("loot.count_tip",
                "Maximum count from 0 to 10000. Blank inherits the game's limit."));
            box.TextChanged += (_, _) => _countDraft[key] = box.Text ?? "";
            _mapRows.Children.Add(Row(type, _editor.VanillaCount(selected.Id, type), box,
                _editor.VanillaCountDetails(selected.Id, type)));
        }
    }

    private void BuildGlobalRows()
    {
        _globalRows.Children.Clear();
        foreach (var type in Matching(_globalSearch.Text))
        {
            var value = _occurrenceDraft.GetValueOrDefault(type) ?? _editor.Occurrence(type) ?? "";
            var box = Input(value, AppTranslations.Get("loot.global_tip",
                "Global occurrence factor. Blank keeps the game's value."));
            box.TextChanged += (_, _) => _occurrenceDraft[type] = box.Text ?? "";
            _globalRows.Children.Add(Row(type, _editor.VanillaOccurrence(type), box));
        }
    }

    private IEnumerable<string> Matching(string? query) => _editor.ObjectTypes.Where(type =>
        string.IsNullOrWhiteSpace(query) || type.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        LootControlConfigEditor.DisplayItem(type).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));

    private static TextBox Input(string text, string tip)
    {
        var input = new TextBox { Text = text, PlaceholderText = "—", HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 28, Padding = new Thickness(7, 3) };
        ToolTip.SetTip(input, tip);
        return input;
    }

    private Border Row(string type, string baseline, TextBox input, string? baselineDetail = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,112,100"), ColumnSpacing = 10 };
        var item = ItemLabel(type);
        grid.Children.Add(item);
        var original = Label(baseline, "#AFA5BB");
        if (baselineDetail is not null) ToolTip.SetTip(original, baselineDetail);
        Grid.SetColumn(original, 1);
        grid.Children.Add(original);
        Grid.SetColumn(input, 2);
        grid.Children.Add(input);
        return new Border { Background = new SolidColorBrush(Color.Parse("#2B2632")),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 3), Child = grid };
    }
}
