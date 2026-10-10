using System.Globalization;
using System.Text.Json;
using Avalonia.Markup.Xaml;

namespace Briefcase.ServerApp;

/// <summary>Loads bundled and shared translations; English is also embedded in the AOT binary.</summary>
public static class AppTranslations
{
    private const int MaximumBytes = 512 * 1024;
    private static readonly Lazy<IReadOnlyDictionary<string, string>> English = new(LoadEnglish);
    private static IReadOnlyDictionary<string, string> _selected = new Dictionary<string, string>();
    private static IReadOnlyDictionary<string, string> _mapAliases = new Dictionary<string, string>();
    public static string Language { get; private set; } = "en";

    public sealed record LanguageOption(string Code, string Name);

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 32 ||
            !IsAsciiLetter(language[0]) || !char.IsAsciiLetterOrDigit(language[^1]) ||
            language.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            return "en";
        return language;
    }

    public static string DefaultLanguage(string? executableDirectory = null)
    {
        try { return MatchAvailableLanguage(CultureInfo.CurrentUICulture.Name, executableDirectory); }
        catch (CultureNotFoundException) { return "en"; }
    }

    public static string MatchAvailableLanguage(string? systemLanguage, string? executableDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(systemLanguage)) return "en";
        var available = AvailableLanguages(executableDirectory);
        var code = systemLanguage.Trim();
        while (code.Length > 0)
        {
            var match = available.FirstOrDefault(option =>
                option.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Code;
            var separator = code.LastIndexOf('-');
            if (separator < 0) break;
            code = code[..separator];
        }
        return "en";
    }

    public static IReadOnlyList<LanguageOption> AvailableLanguages(string? executableDirectory = null)
    {
        var languages = new List<LanguageOption>();
        foreach (var (code, values) in TranslationFiles(executableDirectory))
            if (values.TryGetValue("language.name", out var name))
                languages.Add(new LanguageOption(code, name));

        if (!languages.Any(option => option.Code.Equals("en", StringComparison.OrdinalIgnoreCase)))
            languages.Add(new LanguageOption("en", English.Value.TryGetValue("language.name", out var name) ? name : "English"));
        return languages.OrderBy(option => option.Code.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static void Select(string language, string? executableDirectory = null)
    {
        Language = NormalizeLanguage(language);
        var files = TranslationFiles(executableDirectory).ToArray();
        _selected = files.FirstOrDefault(file => file.Code.Equals(Language, StringComparison.OrdinalIgnoreCase)).Values ??
            new Dictionary<string, string>();
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddMapAliases(aliases, English.Value);
        foreach (var (_, values) in files) AddMapAliases(aliases, values);
        _mapAliases = aliases;
    }

    public static bool TryResolveMapAlias(string name, out string id) => _mapAliases.TryGetValue(name, out id!);

    public static string Get(string key, string? englishFallback = null)
    {
        if (_selected.TryGetValue(key, out var translated)) return translated;
        if (English.Value.TryGetValue(key, out var english)) return english;
        return englishFallback ?? key;
    }

    public static string Format(string key, string englishFallback, params object[] args)
    {
        try { return string.Format(Get(key, englishFallback), args); }
        catch (FormatException) { return string.Format(englishFallback, args); }
    }

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(AppTranslations).Assembly.GetManifestResourceStream(
            "Briefcase.ServerApp.Fallback.en.json");
        return stream is null ? new Dictionary<string, string>() : Parse(stream);
    }

    private static IEnumerable<(string Code, IReadOnlyDictionary<string, string> Values)> TranslationFiles(string? executableDirectory)
    {
        var root = new DirectoryInfo(executableDirectory ?? AppContext.BaseDirectory);
        var directories = new List<string> { Path.Combine(root.FullName, "Translations") };
        if (root.Parent?.Name.Equals("versions", StringComparison.OrdinalIgnoreCase) == true &&
            root.Parent.Parent?.Name.Equals("App", StringComparison.OrdinalIgnoreCase) == true &&
            root.Parent.Parent.Parent is { } installRoot)
            directories.Add(Path.Combine(installRoot.FullName, "Translations"));
        var files = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            string[] paths;
            try { paths = Directory.GetFiles(directory, "*.json"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { continue; }
            foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var code = Path.GetFileNameWithoutExtension(path);
                if (!code.Equals(NormalizeLanguage(code), StringComparison.Ordinal)) continue;
                if (Read(path) is not { } values) continue;
                if (!files.TryGetValue(code, out var bundled))
                    files[code] = values;
                else
                {
                    var merged = new Dictionary<string, string>(bundled, StringComparer.Ordinal);
                    foreach (var (key, value) in values) merged[key] = value;
                    files[code] = merged;
                }
            }
        }
        return files.Select(file => (file.Key, file.Value));
    }

    private static void AddMapAliases(Dictionary<string, string> aliases, IReadOnlyDictionary<string, string> values)
    {
        const string mapPrefix = "game.maps.";
        const string aliasPrefix = "game.map_alias.";
        foreach (var (key, value) in values)
        {
            if (key.StartsWith(mapPrefix, StringComparison.Ordinal) &&
                English.Value.ContainsKey(key))
                aliases.TryAdd(value, key[mapPrefix.Length..]);
            else if (key.StartsWith(aliasPrefix, StringComparison.Ordinal) &&
                     English.Value.ContainsKey(mapPrefix + value))
                aliases.TryAdd(key[aliasPrefix.Length..], value);
        }
    }

    private static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static IReadOnlyDictionary<string, string>? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
            using var stream = File.OpenRead(path);
            return Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return null; }
    }

    private static IReadOnlyDictionary<string, string> Parse(Stream stream)
    {
        using var json = JsonDocument.Parse(stream);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A translation file must contain a JSON object.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in json.RootElement.EnumerateObject())
        {
            if (values.Count >= 2048 || item.Name.Length > 160 ||
                item.Value.ValueKind != JsonValueKind.String) continue;
            var value = item.Value.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 1000)
                values[item.Name] = value;
        }
        return values;
    }
}

public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider? serviceProvider) => AppTranslations.Get(Key);
}
