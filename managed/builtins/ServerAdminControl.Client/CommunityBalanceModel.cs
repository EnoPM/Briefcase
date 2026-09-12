using System.Text.Json;
using System.Text.Json.Nodes;

namespace ServerAdminControl.Client;

internal sealed class CommunityBalanceSnapshot
{
    private static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    public CommunityBalanceSnapshot(
        string hash,
        string contentSha256,
        string contentSha1,
        string contentMd5,
        string? compressedSha256,
        string? compressedSha1,
        string? compressedMd5,
        int compressedBytes,
        int uncompressedBytes,
        DateTime receivedAtLocal,
        JsonNode root,
        IReadOnlyList<BalanceTable> tables)
    {
        Hash = hash;
        ContentSha256 = contentSha256;
        ContentSha1 = contentSha1;
        ContentMd5 = contentMd5;
        CompressedSha256 = compressedSha256;
        CompressedSha1 = compressedSha1;
        CompressedMd5 = compressedMd5;
        CompressedBytes = compressedBytes;
        UncompressedBytes = uncompressedBytes;
        ReceivedAtLocal = receivedAtLocal;
        Root = root;
        Tables = tables;
        Editor = BalanceEditorModel.Create(tables);
    }

    public string Hash { get; }
    public string ContentSha256 { get; }
    public string ContentSha1 { get; }
    public string ContentMd5 { get; }
    public string? CompressedSha256 { get; }
    public string? CompressedSha1 { get; }
    public string? CompressedMd5 { get; }
    public int CompressedBytes { get; }
    public int UncompressedBytes { get; }
    public DateTime ReceivedAtLocal { get; }
    public JsonNode Root { get; }
    public IReadOnlyList<BalanceTable> Tables { get; }
    public BalanceEditorModel Editor { get; }
    public int OptionCount => Editor.Settings.Count;

    public string HashAlgorithm => Hash.Equals(ContentSha256, StringComparison.OrdinalIgnoreCase)
        ? "SHA-256"
        : Hash.Equals(ContentSha1, StringComparison.OrdinalIgnoreCase)
            ? "SHA-1"
            : Hash.Equals(ContentMd5, StringComparison.OrdinalIgnoreCase)
                ? "MD5"
                : Hash.Equals(CompressedSha256, StringComparison.OrdinalIgnoreCase)
                    ? "SHA-256 of compressed payload"
                    : Hash.Equals(CompressedSha1, StringComparison.OrdinalIgnoreCase)
                        ? "SHA-1 of compressed payload"
                        : Hash.Equals(CompressedMd5, StringComparison.OrdinalIgnoreCase)
                            ? "MD5 of compressed payload"
                            : "game-specific or not content-derived";

    /// <summary>
    /// Updates only represented CSVData nodes. All unrelated JSON properties
    /// and table metadata remain semantically unchanged after serialization.
    /// </summary>
    public string SerializeWithEdits()
    {
        if (!Editor.IsValid)
            throw new InvalidDataException(
                $"{Editor.InvalidCount} setting(s) contain an invalid value.");
        foreach (var table in Tables) table.WriteBack();
        return Root.ToJsonString(CompactJson);
    }
}

internal sealed class BalanceTable
{
    private readonly JsonObject? _source;
    private readonly string? _csvPropertyName;
    private readonly bool? _editableOverride;

    public BalanceTable(
        string name,
        IReadOnlyList<string> headers,
        IReadOnlyList<BalanceRow> rows,
        JsonObject? source = null,
        string? csvPropertyName = null,
        bool? editableOverride = null)
    {
        Name = name;
        Headers = headers;
        Rows = rows;
        _source = source;
        _csvPropertyName = csvPropertyName;
        _editableOverride = editableOverride;
    }

    public string Name { get; }
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<BalanceRow> Rows { get; }
    public bool IsEditable => _editableOverride ??
                              (_source is not null && _csvPropertyName is not null);

    public void WriteBack()
    {
        if (_source is null || _csvPropertyName is null) return;
        _source[_csvPropertyName] = CsvWriter.Write(Headers, Rows);
    }
}

internal sealed class BalanceRow(IReadOnlyList<BalanceValue> cells)
{
    public IReadOnlyList<BalanceValue> Cells { get; } = cells;
    public string Name => Cells.Count == 0 ? "Unnamed row" : Cells[0].Value;
    public IReadOnlyList<BalanceValue> Values => Cells.Count <= 1
        ? []
        : Cells.Skip(1).ToArray();
}

internal sealed class BalanceValue(
    string name,
    string value,
    Action<string>? writeThrough = null,
    bool canEdit = true)
{
    private string _value = value;

    public string Name { get; } = name;
    public bool CanEdit { get; } = canEdit;
    public string Value
    {
        get => _value;
        set
        {
            _value = value;
            writeThrough?.Invoke(value);
        }
    }
}

internal static class CommunityBalanceState
{
    private static CommunityBalanceSnapshot? _snapshot;
    private static string _status = "Waiting for a profile from the Briefcase server...";

    public static CommunityBalanceSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public static string Status => Volatile.Read(ref _status);

    public static void Publish(CommunityBalanceSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        Volatile.Write(ref _status, "The server profile was received and decoded successfully.");
    }

    public static void WaitingForServer() => Volatile.Write(
        ref _status, "A profile was requested from the connected game server...");

    public static void Fail(string error) => Volatile.Write(
        ref _status, $"The received profile could not be decoded: {error}");

    public static void Reset()
    {
        Volatile.Write(ref _snapshot, null);
        Volatile.Write(ref _status, "Waiting for a profile from the Briefcase server...");
    }
}

internal static class CsvWriter
{
    public static string Write(IReadOnlyList<string> headers, IReadOnlyList<BalanceRow> rows)
    {
        var output = new System.Text.StringBuilder();
        WriteRecord(headers);
        foreach (var row in rows)
        {
            var cells = row.Cells.Select(cell => cell.Value).ToArray();
            WriteRecord(cells);
        }
        return output.ToString();

        void WriteRecord(IReadOnlyList<string> cells)
        {
            for (var index = 0; index < cells.Count; index++)
            {
                if (index != 0) output.Append(',');
                var value = cells[index] ?? "";
                var quoted = value.IndexOfAny([',', '"', '\r', '\n']) >= 0;
                if (quoted) output.Append('"');
                foreach (var character in value)
                {
                    if (character == '"') output.Append("\"\"");
                    else output.Append(character);
                }
                if (quoted) output.Append('"');
            }
            output.Append("\r\n");
        }
    }
}
