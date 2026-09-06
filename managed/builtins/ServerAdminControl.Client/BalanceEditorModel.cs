using System.Globalization;
using System.Text.RegularExpressions;

namespace ServerAdminControl.Client;

/// <summary>
/// User-facing view of the game's transport data. The transport can add tables,
/// rows or fields without requiring a new parser: unknown values are wrapped as
/// text and their original source cells remain available for serialization.
/// </summary>
internal sealed class BalanceEditorModel
{
    private BalanceEditorModel(
        IReadOnlyList<BalanceCategory> categories,
        IReadOnlyList<BalanceSetting> settings)
    {
        Categories = categories;
        Settings = settings;
    }

    public IReadOnlyList<BalanceCategory> Categories { get; }
    public IReadOnlyList<BalanceSetting> Settings { get; }
    public int ModifiedCount => Settings.Count(setting => setting.IsModified);
    public int InvalidCount => Settings.Count(setting => !setting.IsValid);
    public bool IsValid => InvalidCount == 0;

    public void ResetAll()
    {
        foreach (var setting in Settings) setting.Reset();
    }

    public static BalanceEditorModel Create(IReadOnlyList<BalanceTable> tables)
    {
        var settings = new List<BalanceSetting>();
        var nextId = 0;
        foreach (var table in tables)
        {
            if (LooksLikeSettingList(table))
                AddSettingList(table, settings, ref nextId);
            else
                AddWideTable(table, settings, ref nextId);
        }

        var categories = settings
            .GroupBy(setting => setting.Category, StringComparer.OrdinalIgnoreCase)
            .Select(category => new BalanceCategory(
                category.Key,
                category
                    .GroupBy(setting => setting.Section, StringComparer.OrdinalIgnoreCase)
                    .Select(section => new BalanceSection(section.Key, section.ToArray()))
                    .OrderBy(section => section.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new BalanceEditorModel(categories, settings);
    }

    private static bool LooksLikeSettingList(BalanceTable table)
    {
        var columns = table.Headers.Select(BalanceNames.Normalize).ToHashSet();
        return columns.Overlaps(BalanceNames.RowColumns) &&
               columns.Overlaps(BalanceNames.FieldColumns) &&
               columns.Overlaps(BalanceNames.ValueColumns);
    }

    private static void AddSettingList(
        BalanceTable table,
        ICollection<BalanceSetting> destination,
        ref int nextId)
    {
        foreach (var row in table.Rows)
        {
            var rowCell = FindCell(row, BalanceNames.RowColumns);
            var fieldCell = FindCell(row, BalanceNames.FieldColumns);
            var valueCell = FindCell(row, BalanceNames.ValueColumns);
            if (rowCell is null || fieldCell is null || valueCell is null) continue;

            var tableCell = FindCell(row, BalanceNames.TableColumns);
            var sourceTable = tableCell?.Value;
            if (string.IsNullOrWhiteSpace(sourceTable)) sourceTable = table.Name;
            var info = FindCell(row, BalanceNames.DescriptionColumns)?.Value;
            var allowedRange = FindCell(row, BalanceNames.RangeColumns)?.Value;

            destination.Add(new BalanceSetting(
                $"setting-{nextId++}",
                BalanceNames.HumanizeTable(sourceTable),
                BalanceNames.HumanizeRow(rowCell.Value, sourceTable),
                BalanceNames.Humanize(fieldCell.Value),
                info,
                allowedRange,
                $"{sourceTable} / {rowCell.Value} / {fieldCell.Value}",
                valueCell,
                table.IsEditable && valueCell.CanEdit));
        }
    }

    private static void AddWideTable(
        BalanceTable table,
        ICollection<BalanceSetting> destination,
        ref int nextId)
    {
        var category = BalanceNames.HumanizeTable(table.Name);
        foreach (var row in table.Rows)
        {
            var section = BalanceNames.HumanizeRow(row.Name, table.Name);
            foreach (var value in row.Values)
            {
                destination.Add(new BalanceSetting(
                    $"setting-{nextId++}",
                    category,
                    section,
                    BalanceNames.Humanize(value.Name),
                    description: null,
                    allowedRange: null,
                    $"{table.Name} / {row.Name} / {value.Name}",
                    value,
                    table.IsEditable && value.CanEdit));
            }
        }
    }

    private static BalanceValue? FindCell(
        BalanceRow row, IReadOnlySet<string> acceptedNames) =>
        row.Cells.FirstOrDefault(cell =>
            acceptedNames.Contains(BalanceNames.Normalize(cell.Name)));
}

internal sealed record BalanceCategory(
    string Name,
    IReadOnlyList<BalanceSection> Sections)
{
    public int SettingCount => Sections.Sum(section => section.Settings.Count);
}

internal sealed record BalanceSection(
    string Name,
    IReadOnlyList<BalanceSetting> Settings);

internal enum BalanceSettingKind
{
    Boolean,
    Integer,
    Number,
    Text
}

internal sealed class BalanceSetting
{
    private readonly BalanceValue _source;
    private readonly string _originalValue;
    private readonly NumericRange? _numericRange;

    public BalanceSetting(
        string id,
        string category,
        string section,
        string name,
        string? description,
        string? allowedRange,
        string sourcePath,
        BalanceValue source,
        bool isEditable)
    {
        Id = id;
        Category = category;
        Section = section;
        Name = name;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        AllowedRange = string.IsNullOrWhiteSpace(allowedRange) ? null : allowedRange.Trim();
        SourcePath = sourcePath;
        _source = source;
        _originalValue = source.Value;
        Draft = source.Value;
        IsEditable = isEditable;
        Kind = DetectKind(source.Value);
        _numericRange = NumericRange.TryParse(AllowedRange);
        Validate();
    }

    public string Id { get; }
    public string Category { get; }
    public string Section { get; }
    public string Name { get; }
    public string? Description { get; }
    public string? AllowedRange { get; }
    public string SourcePath { get; }
    public BalanceSettingKind Kind { get; }
    public bool IsEditable { get; }
    public string Draft { get; private set; }
    public bool IsValid { get; private set; }
    public string? ValidationError { get; private set; }
    public bool IsModified => !Draft.Equals(_originalValue, StringComparison.Ordinal);

    public bool BooleanValue => bool.TryParse(Draft, out var value) && value;

    public void SetBoolean(bool value)
    {
        Draft = value ? "true" : "false";
        Validate();
    }

    public void SetDraft(string value)
    {
        Draft = value;
        Validate();
    }

    public void Reset()
    {
        Draft = _originalValue;
        Validate();
    }

    private void Validate()
    {
        ValidationError = null;
        double? numericValue = null;
        switch (Kind)
        {
            case BalanceSettingKind.Boolean when !bool.TryParse(Draft, out _):
                ValidationError = "Enter true or false.";
                break;
            case BalanceSettingKind.Integer:
                if (!long.TryParse(Draft, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var integer))
                    ValidationError = "Enter a whole number.";
                else numericValue = integer;
                break;
            case BalanceSettingKind.Number:
                if (!double.TryParse(Draft, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    ValidationError = "Enter a finite number using a decimal point.";
                else numericValue = number;
                break;
        }

        if (ValidationError is null && numericValue is not null &&
            _numericRange is not null && !_numericRange.Contains(numericValue.Value))
            ValidationError = $"The value must be between {_numericRange.Minimum:G} and " +
                              $"{_numericRange.Maximum:G}.";

        IsValid = ValidationError is null;
        if (IsValid) _source.Value = Draft;
    }

    private static BalanceSettingKind DetectKind(string value)
    {
        if (bool.TryParse(value, out _)) return BalanceSettingKind.Boolean;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return BalanceSettingKind.Integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return BalanceSettingKind.Number;
        return BalanceSettingKind.Text;
    }
}

internal sealed record NumericRange(double Minimum, double Maximum)
{
    private static readonly Regex RangePattern = new(
        @"^\s*(?<minimum>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s+to\s+" +
        @"(?<maximum>[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public bool Contains(double value) => value >= Minimum && value <= Maximum;

    public static NumericRange? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = RangePattern.Match(text);
        if (!match.Success ||
            !double.TryParse(match.Groups["minimum"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var minimum) ||
            !double.TryParse(match.Groups["maximum"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var maximum) || minimum > maximum)
            return null;
        return new NumericRange(minimum, maximum);
    }
}

internal static class BalanceNames
{
    public static readonly IReadOnlySet<string> TableColumns = Aliases(
        "table", "tableName", "dataTable", "dataTableName");
    public static readonly IReadOnlySet<string> RowColumns = Aliases("row", "rowName");
    public static readonly IReadOnlySet<string> FieldColumns = Aliases(
        "field", "fieldName", "property", "propertyName");
    public static readonly IReadOnlySet<string> ValueColumns = Aliases("value");
    public static readonly IReadOnlySet<string> DescriptionColumns = Aliases(
        "info", "description", "effect");
    public static readonly IReadOnlySet<string> RangeColumns = Aliases(
        "allowedRange", "range");

    public static string Normalize(string value) => new(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static string HumanizeTable(string value)
    {
        var result = value.StartsWith("DT_", StringComparison.OrdinalIgnoreCase)
            ? value[3..]
            : value;
        result = Humanize(result)
            .Replace(" Balancing", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Balancing ", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" Actives", " — Expertise", StringComparison.OrdinalIgnoreCase)
            .Replace(" Active", " — Expertise", StringComparison.OrdinalIgnoreCase)
            .Replace(" Passives", " — Passives", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(result) ? "Other settings" : result.Trim();
    }

    public static string HumanizeRow(string value, string table)
    {
        var tableName = table.StartsWith("DT_", StringComparison.OrdinalIgnoreCase)
            ? table[3..]
            : table;
        var prefix = tableName.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var result = value;
        if (!string.IsNullOrWhiteSpace(prefix) &&
            result.StartsWith(prefix + "_", StringComparison.OrdinalIgnoreCase))
            result = result[(prefix.Length + 1)..];
        return Humanize(result);
    }

    public static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed";
        var spaced = Regex.Replace(value.Replace('_', ' '),
            @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    private static IReadOnlySet<string> Aliases(params string[] values) =>
        values.Select(Normalize).ToHashSet(StringComparer.Ordinal);
}
