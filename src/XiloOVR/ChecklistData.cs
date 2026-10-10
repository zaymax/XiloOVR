#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XiloOVR;

public sealed class ChecklistEntry
{
    public string ItemId { get; set; } = "";

    /// <summary>How many of this item the user wants to find.</summary>
    public int Needed { get; set; } = 1;

    /// <summary>How many are already found.</summary>
    public int Collected { get; set; }

    /// <summary>Legacy v0.1 field ({"itemId", "found"}); migrated to counts on load.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Found { get; set; }

    [JsonIgnore]
    public bool IsComplete => Collected >= Needed;
}

/// <summary>Which watched files changed since the last check (see ChecklistData.ConsumeFileChanges).</summary>
[Flags]
public enum DataChanges
{
    None = 0,
    Checklist = 1,
    Database = 2,
}

/// <summary>
/// The user's active checklist: which items they are hunting and how many are found.
/// Persisted to checklist.json next to the executable; entries reference the item
/// database by id. Items are added/removed from the in-VR picker or by editing that
/// file; both it and the item database are watched and hot-reloaded, so edits show up
/// on the wrist panel without restarting the app.
/// </summary>
public sealed class ChecklistData : IDisposable
{
    private const int MaxNeeded = 99;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _checklistPath;
    private readonly string _databasePath;
    private readonly string _dataDirectory;
    private readonly List<FileSystemWatcher> _watchers = new();

    private ItemDatabase _database;
    private List<ChecklistEntry> _entries = new();
    private volatile bool _checklistChanged;
    private volatile bool _databaseChanged;
    private long _suppressChecklistReloadUntilTicks;

    private ChecklistData(string checklistPath, string databasePath, ItemDatabase database)
    {
        _checklistPath = checklistPath;
        _databasePath = databasePath;
        _dataDirectory = Path.GetDirectoryName(databasePath) ?? "";
        _database = database;
    }

    public IReadOnlyList<ChecklistEntry> Entries => _entries;

    public (int Done, int Total) Progress => (_entries.Count(e => e.IsComplete), _entries.Count);

    public static ChecklistData LoadOrCreate(string checklistPath, string databasePath)
    {
        var data = new ChecklistData(checklistPath, databasePath, ItemDatabase.Load(databasePath));
        data.LoadEntries();
        data.MigrateLegacyIds();
        data.StartWatching();
        return data;
    }

    public GameItem? ItemFor(ChecklistEntry entry) => _database.Find(entry.ItemId);

    public string DisplayName(ChecklistEntry entry, bool russian) =>
        _database.Find(entry.ItemId)?.DisplayName(russian) ?? $"{entry.ItemId}?";

    /// <summary>Absolute path of the entry's icon, or null when unknown.</summary>
    public string? IconPathFor(ChecklistEntry entry)
    {
        var item = ItemFor(entry);
        return item == null ? null : IconPathFor(item);
    }

    public string? IconPathFor(GameItem item) =>
        string.IsNullOrEmpty(item.Icon) ? null : Path.Combine(_dataDirectory, item.Icon);

    public IReadOnlyList<GameItem> SearchDatabase(string query, int max) => _database.Search(query, max);

    public IReadOnlyList<ItemCategory> DatabaseCategories() => _database.Categories();

    public IReadOnlyList<GameItem> DatabaseCategory(string key) => _database.InCategory(key);

    public int DatabaseCount => _database.Count;

    /// <summary>How many of this item the checklist currently wants (0 = not listed).</summary>
    public int NeededOf(string itemId) => FindEntry(itemId)?.Needed ?? 0;

    /// <summary>Picker action: add the item, or bump how many of it are needed.</summary>
    public void AddOrIncrementNeeded(string itemId)
    {
        var entry = FindEntry(itemId);
        if (entry == null)
            _entries.Add(new ChecklistEntry { ItemId = itemId, Needed = 1 });
        else
            entry.Needed = Math.Min(entry.Needed + 1, MaxNeeded);
        Save();
    }

    /// <summary>Picker action: lower the needed count, removing the item at zero.</summary>
    public void DecrementNeededOrRemove(string itemId)
    {
        var entry = FindEntry(itemId);
        if (entry == null)
            return;
        entry.Needed--;
        if (entry.Needed < 1)
        {
            _entries.Remove(entry);
        }
        else if (entry.Collected > entry.Needed)
        {
            entry.Collected = entry.Needed;
        }
        Save();
    }

    public void Increment(int index) => Adjust(index, +1);

    public void Decrement(int index) => Adjust(index, -1);

    private void Adjust(int index, int delta)
    {
        if (index < 0 || index >= _entries.Count)
            return;
        var entry = _entries[index];
        var next = Math.Clamp(entry.Collected + delta, 0, entry.Needed);
        if (next == entry.Collected)
            return;
        entry.Collected = next;
        Save();
    }

    private ChecklistEntry? FindEntry(string itemId) =>
        _entries.FirstOrDefault(e => string.Equals(e.ItemId, itemId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reports which watched files changed since the last call and reloads them as a
    /// side effect. The two files are tracked separately: a checklist edit must not
    /// re-parse the 1400-item database (and drop every cached icon with it).
    /// </summary>
    public DataChanges ConsumeFileChanges()
    {
        var changes = DataChanges.None;
        if (_databaseChanged)
        {
            _databaseChanged = false;
            changes |= DataChanges.Database;
        }
        if (_checklistChanged)
        {
            _checklistChanged = false;
            changes |= DataChanges.Checklist;
        }
        if (changes == DataChanges.None)
            return changes;

        try
        {
            if (changes.HasFlag(DataChanges.Database))
                _database = ItemDatabase.Load(_databasePath);
            if (changes.HasFlag(DataChanges.Checklist))
                LoadEntries();
            MigrateLegacyIds(); // a new database may know more legacy ids
            Console.WriteLine(changes.HasFlag(DataChanges.Database)
                ? "Item database reloaded from disk."
                : "Checklist reloaded from disk.");
        }
        catch (Exception ex)
        {
            // Likely caught the editor mid-write; keep current state, the next event retries.
            Console.Error.WriteLine($"warning: reload failed, keeping previous state: {ex.Message}");
        }
        return changes;
    }

    private void LoadEntries()
    {
        if (!File.Exists(_checklistPath))
        {
            _entries = SampleEntries();
            Save();
            Console.WriteLine($"Created sample checklist: {_checklistPath}");
            return;
        }

        var file = JsonSerializer.Deserialize<ChecklistFile>(File.ReadAllText(_checklistPath), JsonOptions);
        _entries = file?.Entries?.Where(e => !string.IsNullOrWhiteSpace(e.ItemId)).ToList() ?? new List<ChecklistEntry>();

        foreach (var entry in _entries)
        {
            entry.Needed = Math.Clamp(entry.Needed, 1, MaxNeeded);
            if (entry.Found.HasValue) // migrate the v0.1 boolean format
            {
                entry.Collected = entry.Found.Value ? entry.Needed : 0;
                entry.Found = null;
            }
            entry.Collected = Math.Clamp(entry.Collected, 0, entry.Needed);
        }
    }

    /// <summary>
    /// Rewrites checklist entries that still use ids from an earlier database (the
    /// database carries each item's legacy ids), merging duplicates, and saves if anything changed.
    /// </summary>
    private void MigrateLegacyIds()
    {
        var changed = false;
        var merged = new List<ChecklistEntry>();
        foreach (var entry in _entries)
        {
            var current = _database.CurrentIdFor(entry.ItemId);
            if (current != null)
            {
                Console.WriteLine($"checklist: '{entry.ItemId}' is now '{current}'.");
                entry.ItemId = current;
                changed = true;
            }
            var existing = merged.FirstOrDefault(e => string.Equals(e.ItemId, entry.ItemId, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                merged.Add(entry);
                continue;
            }
            existing.Needed = Math.Min(MaxNeeded, existing.Needed + entry.Needed);
            existing.Collected = Math.Clamp(existing.Collected + entry.Collected, 0, existing.Needed);
            changed = true;
        }
        if (!changed)
            return;
        _entries = merged;
        Save();
    }

    private void Save()
    {
        // Our own write trips the checklist watcher too; reloading what we just wrote is
        // pointless work (and a chance to catch the file half-written), so ignore events
        // for a moment. Hand edits made in that window are picked up with the next one.
        Volatile.Write(ref _suppressChecklistReloadUntilTicks, DateTime.UtcNow.AddSeconds(1.5).Ticks);
        try
        {
            File.WriteAllText(_checklistPath, JsonSerializer.Serialize(new ChecklistFile { Entries = _entries }, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not save checklist: {ex.Message}");
        }
    }

    private List<ChecklistEntry> SampleEntries()
    {
        // Seed from known database ids so a fresh install shows something meaningful.
        var sample = new List<ChecklistEntry>();
        foreach (var (id, needed) in new[] { ("valuable.task.ark_floppydisk", 1), ("valuable.task.tommy_baseball", 1), ("valuable.task.johnny_drill", 3) })
        {
            if (_database.Find(id) != null)
                sample.Add(new ChecklistEntry { ItemId = id, Needed = needed });
        }
        return sample;
    }

    private void StartWatching()
    {
        Watch(_checklistPath, () =>
        {
            if (DateTime.UtcNow.Ticks < Volatile.Read(ref _suppressChecklistReloadUntilTicks))
                return; // our own Save()
            _checklistChanged = true;
        });
        Watch(_databasePath, () => _databaseChanged = true);
    }

    private void Watch(string filePath, Action onChanged)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        var watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => onChanged();
        watcher.Created += (_, _) => onChanged();
        watcher.Renamed += (_, _) => onChanged();
        _watchers.Add(watcher);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    private sealed class ChecklistFile
    {
        public List<ChecklistEntry>? Entries { get; set; }
    }
}
