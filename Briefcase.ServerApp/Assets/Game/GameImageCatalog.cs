using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Briefcase.ServerApp;

/// <summary>Named game images bundled with the app, available to JSON form schemas.</summary>
public static class GameImageCatalog
{
    private const string ResourceName = "Briefcase.ServerApp.GameImages.json";
    private const string UriRoot = "avares://Briefcase.ServerApp/Assets/Game/UI/";
    private static readonly Lazy<Catalog> Data = new(ReadCatalog);
    private static readonly Dictionary<string, Bitmap> Bitmaps = new(StringComparer.Ordinal);

    private sealed record Catalog(Dictionary<string, string> Images, Dictionary<string, string> LootItems);

    public static bool Contains(string id) => Data.Value.Images.ContainsKey(id);

    public static string ForLootItem(string objectType)
    {
        var items = Data.Value.LootItems;
        if (items.TryGetValue(objectType, out var image)) return image;
        // LootControl's LAF variants represent the same item, spawned in a different container.
        if (objectType.EndsWith("LAF", StringComparison.Ordinal) &&
            items.TryGetValue(objectType[..^3], out image)) return image;
        return "loot/container";
    }

    public static Bitmap? Load(string id)
    {
        if (!Data.Value.Images.TryGetValue(id, out var path)) return null;
        if (Bitmaps.TryGetValue(id, out var bitmap)) return bitmap;
        try
        {
            using var stream = AssetLoader.Open(new Uri(UriRoot + path));
            bitmap = Bitmap.DecodeToWidth(stream, 48, BitmapInterpolationMode.HighQuality);
            Bitmaps.Add(id, bitmap);
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Catalog ReadCatalog()
    {
        using var stream = typeof(GameImageCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Bundled game image catalog is missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported game image catalog version.");
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("images").EnumerateObject())
        {
            var path = item.Value.GetString() ?? "";
            if (!Regex.IsMatch(item.Name, "^[a-z][a-z0-9/-]{0,79}$") ||
                !Regex.IsMatch(path, "^[A-Za-z0-9_./-]{1,200}\\.png$") ||
                path.Contains("..", StringComparison.Ordinal) || !images.TryAdd(item.Name, path))
                throw new InvalidDataException("Invalid bundled game image entry.");
        }
        var lootItems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("lootItems").EnumerateObject())
        {
            var id = item.Value.GetString() ?? "";
            if (!images.ContainsKey(id) || !lootItems.TryAdd(item.Name, id))
                throw new InvalidDataException("Invalid bundled loot image entry.");
        }
        if (!images.ContainsKey("loot/container"))
            throw new InvalidDataException("Bundled loot placeholder image is missing.");
        return new Catalog(images, lootItems);
    }
}
