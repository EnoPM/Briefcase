using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Briefcase.ServerApp;

public sealed class BalanceNode(string name, string displayName, Bitmap? icon, BalanceNode? parent)
{
    public string Name { get; } = name;
    public string DisplayName { get; } = displayName;
    public Bitmap? Icon { get; } = icon;
    public bool HasIcon => Icon is not null;
    public BalanceNode? Parent { get; } = parent;
    public string Path => Parent is null ? DisplayName : Parent.Path + " / " + DisplayName;
    public string KeyPath => Parent is null ? Name : Parent.KeyPath + "\u001f" + Name;
    public bool IsExpanded { get; set; } = parent is null;
    public ObservableCollection<BalanceNode> Children { get; } = [];
    public List<BalanceField> Fields { get; } = [];
    public int SettingCount => Fields.Count + Children.Sum(child => child.SettingCount);
}

public sealed class BalanceTreeRow
{
    private static readonly Dictionary<string, Geometry> Glyphs = new(StringComparer.Ordinal)
    {
        ["Characters"] = Geometry.Parse("M 7,10 A 3,3 0 1 0 7,4 A 3,3 0 1 0 7,10 M 14,11 A 2.5,2.5 0 1 0 14,6 A 2.5,2.5 0 1 0 14,11 M 2,18 L 2,16 C 2,13 4,12 7,12 C 10,12 12,13 12,16 L 12,18 M 13,13 C 16,12 18,14 18,17 L 18,18"),
        ["Gadgets"] = Geometry.Parse("M 3,3 L 9,3 L 9,9 L 3,9 Z M 11,3 L 17,3 L 17,9 L 11,9 Z M 3,11 L 9,11 L 9,17 L 3,17 Z M 11,11 L 17,11 L 17,17 L 11,17 Z"),
        ["Game"] = Geometry.Parse("M 3,5 L 17,5 M 3,10 L 17,10 M 3,15 L 17,15 M 7,3 L 7,7 M 13,8 L 13,12 M 8,13 L 8,17"),
        ["Weapons"] = Geometry.Parse("M 2,10 L 18,10 M 7,6 L 13,6 L 15,10 L 13,14 L 7,14 Z M 4,10 L 4,17"),
        ["Passives"] = Geometry.Parse("M 10,18 C 5,14 2,11 2,7 C 2,3 7,2 10,6 C 13,2 18,3 18,7 C 18,11 15,14 10,18 Z"),
        ["Expertises"] = Geometry.Parse("M 11,2 L 4,11 L 9,11 L 7,18 L 16,8 L 11,8 Z")
    };

    private static readonly Geometry OpenChevron = Geometry.Parse("M 4,7 L 10,13 L 16,7");
    private static readonly Geometry ClosedChevron = Geometry.Parse("M 7,4 L 13,10 L 7,16");
    private static readonly Geometry FolderGlyph = Geometry.Parse("M 2,5 L 8,5 L 10,7 L 18,7 L 18,16 L 2,16 Z");

    public BalanceTreeRow(BalanceNode node, int depth, bool forceExpanded = false)
    { Node = node; Depth = depth; ForceExpanded = forceExpanded; }
    public BalanceTreeRow(BalanceField field, int depth)
    { Field = field; Depth = depth; }

    public BalanceNode? Node { get; }
    public BalanceField? Field { get; }
    public int Depth { get; }
    public bool ForceExpanded { get; }
    public bool IsFolder => Node is not null;
    public bool IsField => Field is not null;
    public Avalonia.Thickness Indent => new(Depth * 20, 0, 0, 0);
    public string Title => Node?.DisplayName ?? Field?.Label ?? "";
    public string Count => Node is null ? "" : AppTranslations.Format("balance.settings_count", "{0:N0} settings", Node.SettingCount);
    public Bitmap? Icon => Node?.Icon;
    public bool HasIcon => Icon is not null;
    public Geometry? Glyph => Node is { Icon: null } node ? Glyphs.GetValueOrDefault(node.Name) ?? FolderGlyph : null;
    public bool HasGlyph => Glyph is not null;
    public Geometry Chevron => Node is { IsExpanded: true } || ForceExpanded ? OpenChevron : ClosedChevron;
    public string Hint => Field is null ? "" : string.Join("\n", new[]
        { Field.Description, Field.SourceKey, Field.RangeHint }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public bool IsNumber => Field?.IsNumber == true;
    public bool IsBoolean => Field?.IsBoolean == true;
    public string Value { get => Field?.Value ?? ""; set { if (Field is not null) Field.Value = value; } }
    public bool BoolValue { get => Field?.BoolValue == true; set { if (Field is not null) Field.BoolValue = value; } }
}

public static class BalanceTreeProjection
{
    public static IReadOnlyList<BalanceTreeRow> Build(IEnumerable<BalanceNode> roots,
        string? search, out int matchingSettings)
    {
        var query = search?.Trim() ?? "";
        var rows = new List<BalanceTreeRow>();
        matchingSettings = 0;
        foreach (var root in roots.OrderBy(node => node.Name switch
            { "Characters" => 0, "Gadgets" => 1, "Game" => 2, _ => 3 }))
            Append(root, 0, query, false, rows, ref matchingSettings);
        return rows;
    }

    private static void Append(BalanceNode node, int depth, string query, bool ancestorMatches,
        List<BalanceTreeRow> rows, ref int matches)
    {
        var pathMatches = ancestorMatches || NameMatches(node, query);
        if (query.Length > 0 && !SubtreeMatches(node, query, ancestorMatches)) return;
        rows.Add(new BalanceTreeRow(node, depth, query.Length > 0));
        if (query.Length == 0 && !node.IsExpanded) return;
        foreach (var child in node.Children.OrderBy(child => child.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Append(child, depth + 1, query, pathMatches, rows, ref matches);
        foreach (var field in node.Fields.OrderBy(field => field.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            if (query.Length > 0 && !pathMatches && !FieldMatches(field, query)) continue;
            rows.Add(new BalanceTreeRow(field, depth + 1));
            if (query.Length > 0) matches++;
        }
    }

    private static bool SubtreeMatches(BalanceNode node, string query, bool ancestorMatches)
    {
        if (query.Length == 0) return true;
        var pathMatches = ancestorMatches || NameMatches(node, query);
        return (pathMatches && node.SettingCount > 0) ||
            node.Fields.Any(field => FieldMatches(field, query)) ||
            node.Children.Any(child => SubtreeMatches(child, query, pathMatches));
    }

    private static bool NameMatches(BalanceNode node, string query) =>
        node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        node.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static bool FieldMatches(BalanceField field, string query) =>
        new[] { field.Label, field.Table, field.Row, field.Field, field.Value,
                field.Description, field.AllowedRange }
            .Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class BalancingPanel : UserControl, INotifyPropertyChanged
{
    private ServerRow? _server;
    private bool _demo;
    private Action<string>? _report;
    private BalanceProfileEditor? _editor;
    private IReadOnlyList<BalanceField> _allFields = [];
    private string _loadMessage = AppTranslations.Get("balance.select_server", "Select a server to view its balance profile.");
    private string _message = AppTranslations.Get("balance.select_server", "Select a server to view its balance profile.");
    private string _resultCount = "";
    private bool _canSave;
    private IReadOnlyList<BalanceTreeRow> _visibleRows = [];

    public new event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Saved;
    public ObservableCollection<BalanceNode> Roots { get; } = [];
    public IReadOnlyList<BalanceTreeRow> VisibleRows { get => _visibleRows; private set => Set(ref _visibleRows, value); }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public string ResultCount { get => _resultCount; private set => Set(ref _resultCount, value); }
    public bool CanSave { get => _canSave; private set => Set(ref _canSave, value); }

    public BalancingPanel()
    {
        InitializeComponent();
        DataContext = this;
    }

    public void Load(ServerRow? server, bool demo, Action<string> report)
    {
        _server = server;
        _demo = demo;
        _report = report;
        _editor = null;
        _allFields = [];
        CanSave = false;
        if (server is null)
            Message = AppTranslations.Get("balance.select_server", "Select a server to view its balance profile.");
        else if (demo)
        {
            _allFields = DemoFields();
            Message = AppTranslations.Get("balance.demo", "Screenshot preview: changes are not saved.");
        }
        else if (!ServerLocation.TryCreate(server, out var location, out var error))
            Message = error;
        else
        {
            try
            {
                _editor = BalanceProfileEditor.Load(location!);
                _allFields = _editor.Fields;
                CanSave = _allFields.Count > 0;
                Message = _allFields.Count == 0
                    ? AppTranslations.Get("balance.no_profile", "No community balance profile or template was found. Install the dedicated server's balance template first.")
                    : "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                Message = ex.Message;
                report(ex.Message);
            }
        }
        _loadMessage = Message;
        SearchBox.Text = "";
        RebuildSections();
    }

    private void RebuildSections()
    {
        Roots.Clear();
        foreach (var field in _allFields)
        {
            var placement = BalanceGrouping.Classify(field);
            var siblings = Roots;
            BalanceNode? parent = null;
            for (var level = 0; level < placement.Path.Length; level++)
            {
                var name = placement.Path[level];
                var node = siblings.FirstOrDefault(child => child.Name == name);
                if (node is null)
                {
                    var display = placement.Character is { } character
                        ? level == 1 ? BalanceGameNames.Character(character)
                        : level == 3 ? BalanceGameNames.Variant(character, placement.Path[2],
                            placement.IconVariant ?? "") ?? name
                        : AppTranslations.Get("balance.category." + name, name)
                        : AppTranslations.Get("balance.category." + name, name);
                    node = new BalanceNode(name, display, BalanceIcons.For(placement, level), parent);
                    siblings.Add(node);
                }
                parent = node;
                siblings = node.Children;
            }
            parent!.Fields.Add(field);
        }
        UpdateView();
    }

    private void SearchChanged(object? sender, TextChangedEventArgs e)
        => UpdateView();

    private void ToggleNodeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BalanceTreeRow { Node: { } node } }) return;
        // Search shows every matching branch. Keep the user's regular expansion state intact.
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        node.IsExpanded = !node.IsExpanded;
        UpdateView();
    }

    private void UpdateView()
    {
        var query = SearchBox?.Text?.Trim() ?? "";
        VisibleRows = BalanceTreeProjection.Build(Roots, query, out var matches);
        Message = query.Length > 0 && matches == 0
            ? AppTranslations.Get("balance.no_search_results", "No balance settings match this search.")
            : _loadMessage;
        ResultCount = query.Length == 0
            ? AppTranslations.Format("balance.settings_count", "{0:N0} settings", _allFields.Count)
            : AppTranslations.Format("balance.matching_count", "{0:N0} matching settings", matches);
    }

    private void ReloadClicked(object? sender, RoutedEventArgs e) => Load(_server, _demo, _report ?? (_ => { }));

    private void SaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_demo || _editor is null || !CanSave) return;
        try
        {
            var expanded = DescendantNodes(Roots).Where(node => node.IsExpanded)
                .Select(node => node.KeyPath).ToHashSet(StringComparer.Ordinal);
            var query = SearchBox.Text;
            _editor.Save();
            _report?.Invoke(AppTranslations.Get("balance.saved", "Balance profile saved. Restart the server if it is running."));
            Saved?.Invoke(this, EventArgs.Empty);
            Load(_server, false, _report ?? (_ => { }));
            foreach (var node in DescendantNodes(Roots))
                node.IsExpanded = expanded.Contains(node.KeyPath);
            SearchBox.Text = query;
            UpdateView();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { _report?.Invoke(ex.Message); }
    }

    private static IEnumerable<BalanceNode> DescendantNodes(IEnumerable<BalanceNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in DescendantNodes(node.Children)) yield return child;
        }
    }

    private bool Set<T>(ref T backing, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(backing, value)) return false;
        backing = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }

    private static IReadOnlyList<BalanceField> DemoFields()
    {
        static BalanceField Number(string table, string row, string field, string value, string range) => new()
        {
            Table = table, Row = row, Field = field, Label = BalanceProfileEditor.Humanize(field),
            Description = "Example from the packaged balance profile.", AllowedRange = range,
            Kind = JsonValueKind.Number, Value = value, OriginalValue = value
        };
        return
        [
            Number("DT_Balancing_HitscanWeapons", "Ace_Weapon_Base", "FireRate", "0.75", "0 to 120"),
            Number("DT_Balancing_HitscanWeapons", "Ace_Weapon_Base", "MagSize", "5", "0 to 3000"),
            Number("DT_Ace_PassivesBalancing", "Ace_Passive_Mod1", "Duration", "12", "0 to 120"),
            Number("DT_Ace_ActivesBalancing", "Ace_Active_Base", "CancelDelay", "1.5", "0 to 15"),
            Number("DT_Balancing_HitscanWeapons", "Squire_Weapon_Mod1", "FireRate", "0.6", "0 to 120"),
            Number("DT_Gadgets_Balancing", "BouncingMat", "Cooldown", "60", "0 to 600"),
            Number("DT_Balancing_HitscanWeapons", "DEye_Weapon", "MagSize", "10", "0 to 3000"),
            Number("DT_SpyShared_HealthPool", "HealthPool_High", "HealthPool", "120", "0 to 1200")
        ];
    }
}

internal static class BalanceIcons
{
    private sealed record Entry(string Category, string? Character, string Name, string Png);
    private static readonly Lazy<Entry[]> Catalog = new(ReadCatalog);
    private static readonly Dictionary<(string Png, int Width), Bitmap> Cache = new();
    private const int BalancingPortraitWidth = 50;
    private const int PlayerPortraitWidth = 108;

    private static string LoadoutCharacter(string character) => character switch
    {
        "Red" => "Socialite", "Xiu" => "MadameXiu", _ => character
    };

    public static Bitmap? CharacterPortrait(string character, bool dead = false)
    {
        var entry = Catalog.Value.FirstOrDefault(item => item.Category == "Character" &&
            item.Character == (character == "Red" ? "Socialite" : character));
        if (entry is null) return null;
        var png = dead ? entry.Png.Replace("_U.png", "_D.png", StringComparison.Ordinal) : entry.Png;
        return LoadBitmap(png, PlayerPortraitWidth);
    }

    public static Bitmap? Gadget(string id)
    {
        var suffix = id switch
        {
            "DEye" => "Eye", "AutoTurret" => "Turret", "GooPod" => "Goo",
            "Scrambler" => "Jammer", "UmbrellaShield" => "Shield",
            "Tripwire" => "Wire", "BounceMat" => "BouncingMat",
            "SpyGlass" => "Glasses", _ => id
        };
        return Find("Gadget", null, suffix);
    }

    public static Bitmap? Upgrade(string id) => Find("Lootable", null, id);

    public static Bitmap? Loadout(string character, string category, string variant) =>
        Find(category switch
        {
            "Weapons" => "Weapon", "Expertises" => "Expertise", "Passives" => "Passive",
            _ => ""
        }, LoadoutCharacter(character), variant, 56);

    public static Bitmap? For(BalancePlacement placement, int level)
    {
        var path = placement.Path;
        if (placement.Character is { } character)
        {
            if (level == 1) return Find("Character", character, null,
                BalancingPortraitWidth);
            if (placement.IconCategory is { } category && level >= 2)
                return Find(category, character, level == 3 ? placement.IconVariant : "Base");
        }
        else if (placement.Gadget is { } gadget && level == path.Length - 1)
        {
            var suffix = gadget switch
            {
                "DEye" => "Eye", "AutoTurret" => "Turret", "GooPod" => "Goo",
                "Scrambler" => "Jammer", "UmbrellaShield" => "Shield",
                "BouncingMat" => "BouncingMat", "HackTrap" => "HackTrap",
                "Tripwire" => "Wire", _ => gadget
            };
            return Find("Gadget", null, suffix);
        }
        return null;
    }

    private static Bitmap? Find(string category, string? character, string? variant,
        int targetWidth = 0)
    {
        var entry = Catalog.Value.FirstOrDefault(item => item.Category == category &&
            (character is null || item.Character == character) &&
            (variant is null || (variant == "Base"
                ? !item.Name.EndsWith("_Mod1", StringComparison.Ordinal) &&
                  !item.Name.EndsWith("_Mod2", StringComparison.Ordinal)
                : item.Name.EndsWith("_" + variant, StringComparison.Ordinal) ||
                  item.Name.EndsWith("_" + character + "_" + variant, StringComparison.Ordinal))) &&
            (category != "Gadget" || item.Name.EndsWith("_" + variant, StringComparison.Ordinal)));
        if (entry is null) return null;
        return LoadBitmap(entry.Png, targetWidth);
    }

    private static Bitmap? LoadBitmap(string png, int targetWidth)
    {
        var key = (png, targetWidth);
        if (Cache.TryGetValue(key, out var bitmap)) return bitmap;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Briefcase.ServerApp/Assets/Game/UI/" + png));
            bitmap = targetWidth > 0
                ? Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
            Cache[key] = bitmap;
            return bitmap;
        }
        catch (FileNotFoundException) { return null; }
    }

    private static Entry[] ReadCatalog()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Briefcase.ServerApp/Assets/Game/IconCatalog.json"));
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.EnumerateArray().Select(item => new Entry(
                item.GetProperty("category").GetString() ?? "",
                item.GetProperty("character").ValueKind == JsonValueKind.String
                    ? item.GetProperty("character").GetString() : null,
                item.GetProperty("name").GetString() ?? "",
                item.GetProperty("png").GetString() ?? "")).ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        { return []; }
    }
}
