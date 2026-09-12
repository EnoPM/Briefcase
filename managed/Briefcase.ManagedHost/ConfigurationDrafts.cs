#if !BRIEFCASE_HEADLESS
using Briefcase.ModApi;

namespace Briefcase.ManagedHost;

internal sealed partial class ConfigurationRegistry
{
    private readonly ConfigurationDraftStore _configurationDrafts = new();

    internal ConfigurationPageDraft GetConfigurationDraft(ModScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _configurationDrafts.GetOrCreate(
            scope.Info.Id,
            scope.SnapshotEntries());
    }

    internal bool HasConfigurationDraft(string modId) =>
        _configurationDrafts.IsDirty(modId);

    internal void RemoveConfigurationDraft(string modId) =>
        _configurationDrafts.Remove(modId);
}

/// <summary>
/// Session-owned drafts let the user move between pages or close the F1 menu
/// without changing the values observed by a mod. A draft is discarded when
/// its mod unloads and every remaining draft naturally disappears with the
/// managed host at process exit.
/// </summary>
internal sealed class ConfigurationDraftStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ConfigurationPageDraft> _pages =
        new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationPageDraft GetOrCreate(
        string pageId,
        IReadOnlyList<IConfigurationEntry> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            if (!_pages.TryGetValue(pageId, out var page))
            {
                page = new ConfigurationPageDraft(entries);
                _pages.Add(pageId, page);
            }
            else
            {
                page.Synchronize(entries);
            }
            return page;
        }
    }

    public bool IsDirty(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return false;
        lock (_gate)
            return _pages.TryGetValue(pageId, out var page) && page.IsDirty;
    }

    public void Remove(string pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId)) return;
        lock (_gate) _pages.Remove(pageId);
    }
}

internal sealed class ConfigurationPageDraft
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DraftEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationPageDraft(IReadOnlyList<IConfigurationEntry> entries) =>
        Synchronize(entries);

    public bool IsDirty
    {
        get
        {
            lock (_gate) return _entries.Values.Any(entry => entry.IsDirty);
        }
    }

    public object GetValue(IConfigurationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate) return Get(entry).DraftValue;
    }

    public void SetValue(IConfigurationEntry entry, object value)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(value);
        if (!entry.ValueType.IsInstanceOfType(value))
            throw new ArgumentException(
                $"Draft value for {entry.Section}/{entry.Key} must be {entry.ValueType.Name}.",
                nameof(value));
        lock (_gate) Get(entry).DraftValue = value;
    }

    public void Cancel()
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
                entry.DraftValue = entry.OriginalValue;
        }
    }

    public void Apply()
    {
        DraftEntry[] changed;
        lock (_gate) changed = _entries.Values.Where(entry => entry.IsDirty).ToArray();
        foreach (var draft in changed)
        {
            draft.Entry.BoxedValue = draft.DraftValue;
            var applied = draft.Entry.BoxedValue;
            lock (_gate)
            {
                draft.OriginalValue = applied;
                draft.DraftValue = applied;
            }
        }
    }

    public void Synchronize(IReadOnlyList<IConfigurationEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var id = Id(entry);
                retained.Add(id);
                if (!_entries.TryGetValue(id, out var draft) ||
                    !ReferenceEquals(draft.Entry, entry))
                {
                    _entries[id] = new DraftEntry(entry);
                    continue;
                }
                if (draft.IsDirty) continue;
                var current = entry.BoxedValue;
                draft.OriginalValue = current;
                draft.DraftValue = current;
            }
            foreach (var removed in _entries.Keys.Where(id => !retained.Contains(id)).ToArray())
                _entries.Remove(removed);
        }
    }

    private DraftEntry Get(IConfigurationEntry entry)
    {
        var id = Id(entry);
        if (!_entries.TryGetValue(id, out var draft) || !ReferenceEquals(draft.Entry, entry))
            throw new InvalidOperationException($"Configuration draft entry '{id}' is unavailable.");
        return draft;
    }

    private static string Id(IConfigurationEntry entry) => $"{entry.Section}/{entry.Key}";

    private sealed class DraftEntry(IConfigurationEntry entry)
    {
        public IConfigurationEntry Entry { get; } = entry;
        public object OriginalValue { get; set; } = entry.BoxedValue;
        public object DraftValue { get; set; } = entry.BoxedValue;
        public bool IsDirty => !Equals(OriginalValue, DraftValue);
    }
}
#endif
