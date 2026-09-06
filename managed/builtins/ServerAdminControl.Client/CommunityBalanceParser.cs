using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ServerAdminControl.Client;

internal static class CommunityBalanceParser
{
    private const int MaximumProfileBytes = 64 * 1024 * 1024;

    public static CommunityBalanceSnapshot Parse(
        byte[] compressed, int expectedBytes, string hash)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        if (expectedBytes is <= 0 or > MaximumProfileBytes)
            throw new InvalidDataException(
                $"The announced uncompressed size ({FormatCount(expectedBytes)}) is invalid.");
        if (compressed.Length == 0)
            throw new InvalidDataException("The compressed profile is empty.");

        return ParseJson(
            DecompressUtf8(compressed, expectedBytes), hash, compressed.Length,
            Convert.ToHexString(SHA256.HashData(compressed)),
            Convert.ToHexString(SHA1.HashData(compressed)),
            Convert.ToHexString(MD5.HashData(compressed)));
    }

    public static CommunityBalanceSnapshot ParseJson(
        string json,
        string hash,
        int compressedBytes,
        string? compressedSha256 = null,
        string? compressedSha1 = null,
        string? compressedMd5 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var utf8 = new UTF8Encoding(false, true).GetBytes(json);
        if (utf8.Length > MaximumProfileBytes)
            throw new InvalidDataException("The profile exceeds 64 MiB.");
        var root = JsonNode.Parse(utf8) ??
            throw new InvalidDataException("The profile JSON has no root value.");
        var tables = ReadOverrides(root);
        if (tables.Count == 0) tables = ReadTables(root);
        if (tables.Count == 0) tables = [FlattenJson(root)];

        return new CommunityBalanceSnapshot(
            string.IsNullOrWhiteSpace(hash) ? "(not supplied)" : hash,
            Convert.ToHexString(SHA256.HashData(utf8)),
            Convert.ToHexString(SHA1.HashData(utf8)),
            Convert.ToHexString(MD5.HashData(utf8)),
            compressedSha256,
            compressedSha1,
            compressedMd5,
            compressedBytes,
            utf8.Length,
            DateTime.Now,
            root,
            tables.OrderBy(table => table.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string DecompressUtf8(byte[] compressed, int expectedBytes)
    {
        // Unreal's profile uses Zlib. The announced output length bounds memory
        // before any data is accepted by the JSON parser.
        using var input = new MemoryStream(compressed, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedBytes);
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = zlib.Read(buffer);
            if (read == 0) break;
            if (output.Length + read > MaximumProfileBytes)
                throw new InvalidDataException("The decompressed profile exceeds 64 MiB.");
            output.Write(buffer, 0, read);
        }
        if (output.Length != expectedBytes)
            throw new InvalidDataException(
                $"Expected {FormatCount(expectedBytes)} decompressed bytes; " +
                $"got {FormatCount(output.Length)}.");
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
    }

    private static List<BalanceTable> ReadTables(JsonNode root)
    {
        if (root is not JsonObject rootObject ||
            FindProperty(rootObject, "Tables")?.Value is not JsonArray tablesNode)
            return [];

        var result = new List<BalanceTable>();
        var unnamed = 1;
        foreach (var node in tablesNode)
        {
            if (node is not JsonObject entry) continue;
            var name = ReadString(entry, "TableName") ?? $"Table {unnamed++}";
            var csvProperty = FindProperty(entry, "CSVData");
            var csv = csvProperty?.Value?.GetValue<string>();
            if (csv is null || csvProperty is null) continue;
            result.Add(ParseCsv(name, csv, entry, csvProperty.Value.Key));
        }
        return result;
    }

    private static List<BalanceTable> ReadOverrides(JsonNode root)
    {
        if (root is not JsonObject rootObject ||
            FindProperty(rootObject, "overrides")?.Value is not JsonArray overrides)
            return [];

        var entries = overrides.OfType<JsonObject>().ToArray();
        if (entries.Length == 0) return [];

        // Known columns are ordered for a stable editor. Any property added by
        // a future game build is appended and preserved in its original object.
        var headers = new List<string>
        {
            "table", "row", "field", "value", "info", "allowedRange"
        };
        foreach (var entry in entries)
        foreach (var property in entry)
            if (!headers.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                headers.Add(property.Key);

        var rows = new List<BalanceRow>(entries.Length);
        foreach (var entry in entries)
        {
            var cells = new List<BalanceValue>(headers.Count);
            foreach (var header in headers)
            {
                var property = FindProperty(entry, header);
                cells.Add(property is null
                    ? new BalanceValue(header, "", canEdit: false)
                    : BindJsonValue(entry, property.Value.Key, property.Value.Value));
            }
            rows.Add(new BalanceRow(cells));
        }

        return [new BalanceTable(
            "Community Balance", headers, rows, editableOverride: true)];
    }

    private static BalanceValue BindJsonValue(
        JsonObject owner, string propertyName, JsonNode? node)
    {
        if (node is not JsonValue value)
            return new BalanceValue(
                propertyName, node?.ToJsonString() ?? "", canEdit: false);

        if (value.TryGetValue<bool>(out var boolean))
            return new BalanceValue(
                propertyName,
                boolean ? "true" : "false",
                text => owner[propertyName] = bool.Parse(text));
        if (value.TryGetValue<long>(out var integer))
            return new BalanceValue(
                propertyName,
                integer.ToString(CultureInfo.InvariantCulture),
                text => owner[propertyName] = long.Parse(
                    text, NumberStyles.Integer, CultureInfo.InvariantCulture));
        if (value.TryGetValue<double>(out var number))
            return new BalanceValue(
                propertyName,
                number.ToString("R", CultureInfo.InvariantCulture),
                text => owner[propertyName] = double.Parse(
                    text, NumberStyles.Float, CultureInfo.InvariantCulture));
        if (value.TryGetValue<string>(out var textValue))
            return new BalanceValue(
                propertyName, textValue, text => owner[propertyName] = text);

        return new BalanceValue(propertyName, value.ToJsonString(), canEdit: false);
    }

    private static BalanceTable ParseCsv(
        string name,
        string csv,
        JsonObject source,
        string csvPropertyName)
    {
        var records = CsvReader.Read(csv);
        if (records.Count == 0)
            return new BalanceTable(name, [], [], source, csvPropertyName);
        var headers = records[0];
        var rows = new List<BalanceRow>(Math.Max(0, records.Count - 1));
        for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
        {
            var record = records[rowIndex];
            if (record.All(string.IsNullOrEmpty)) continue;
            var count = Math.Max(headers.Count, record.Count);
            var values = new List<BalanceValue>(count);
            for (var column = 0; column < count; column++)
            {
                var columnName = column < headers.Count &&
                                 !string.IsNullOrWhiteSpace(headers[column])
                    ? headers[column]
                    : $"Column {column + 1}";
                values.Add(new BalanceValue(
                    columnName, column < record.Count ? record[column] : ""));
            }
            rows.Add(new BalanceRow(values));
        }
        return new BalanceTable(name, headers, rows, source, csvPropertyName);
    }

    private static BalanceTable FlattenJson(JsonNode root)
    {
        var values = new List<BalanceValue>();
        Flatten(root, "$", values);
        return new BalanceTable(
            "Raw profile", ["Path", "Value"],
            [new BalanceRow(
                [new BalanceValue("Group", "Profile values"), .. values])]);
    }

    private static void Flatten(JsonNode? value, string path, List<BalanceValue> output)
    {
        switch (value)
        {
            case JsonObject item:
                foreach (var property in item)
                    Flatten(property.Value, path + "." + property.Key, output);
                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    Flatten(array[index], $"{path}[{index}]", output);
                break;
            default:
                output.Add(new BalanceValue(path, value?.ToJsonString() ?? "null"));
                break;
        }
    }

    private static KeyValuePair<string, JsonNode?>? FindProperty(
        JsonObject element, string name)
    {
        foreach (var property in element)
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property;
        return null;
    }

    private static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string? ReadString(JsonObject element, string name) =>
        FindProperty(element, name)?.Value?.GetValue<string>();
}

internal static class CsvReader
{
    public static List<List<string>> Read(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else quoted = false;
                }
                else field.Append(character);
                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                    FinishRow();
                    break;
                case '\n':
                    FinishRow();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted) throw new InvalidDataException("A CSV field has no closing quote.");
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;

        void FinishRow()
        {
            row.Add(field.ToString());
            field.Clear();
            rows.Add(row);
            row = [];
        }
    }
}
