#nullable enable
using System.Text.Json;

namespace XiloOVR;

public sealed class GameItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "misc";
    public string? Icon { get; set; }
    public string? Note { get; set; }
}

/// <summary>One database category as shown in the in-VR browser.</summary>
public sealed record ItemCategory(string Key, string Name, int Count);

/// <summary>
/// Static reference of game items shipped as data/items_database.json. Read-only at
/// runtime; users edit the file with a text editor after game patches, no rebuild needed.
/// </summary>
public sealed class ItemDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, GameItem> _byId;

    private ItemDatabase(Dictionary<string, GameItem> byId) => _byId = byId;

    public int Count => _byId.Count;

    public GameItem? Find(string id) => _byId.TryGetValue(id, out var item) ? item : null;

    /// <summary>Categories for the in-VR browser: hunt-relevant ones first, the rest alphabetically.</summary>
    public IReadOnlyList<ItemCategory> Categories()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _byId.Values)
        {
            var key = NormalizeCategory(item.Category);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts
            .Select(pair => new ItemCategory(pair.Key, DisplayCategory(pair.Key), pair.Value))
            .OrderBy(c => PreferredOrder(c.Key))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>All items of one category, sorted by name.</summary>
    public IReadOnlyList<GameItem> InCategory(string category) =>
        _byId.Values
            .Where(i => string.Equals(NormalizeCategory(i.Category), category, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly string[] CategoryPriority =
        { "task-items", "keys", "misc", "medicine", "provisions", "gear", "weapons", "attachments", "ammo", "grenades" };

    private static int PreferredOrder(string key)
    {
        var index = Array.FindIndex(CategoryPriority, c => string.Equals(c, key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? CategoryPriority.Length : index;
    }

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "misc" : category.Trim().ToLowerInvariant();

    /// <summary>"task-items" → "Task items"; one display rule for every category id.</summary>
    public static string DisplayCategory(string key)
    {
        var text = key.Replace('-', ' ').Replace('_', ' ').Trim();
        return text.Length == 0 ? "Misc" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Name/category search for the in-VR picker: prefix matches rank first.</summary>
    public IReadOnlyList<GameItem> Search(string query, int max)
    {
        query = query.Trim();
        if (query.Length == 0)
            return _byId.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();

        var starts = new List<GameItem>();
        var contains = new List<GameItem>();
        foreach (var item in _byId.Values)
        {
            if (item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                starts.Add(item);
            else if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
                contains.Add(item);
        }
        starts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        contains.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        starts.AddRange(contains);
        return starts.Count > max ? starts.GetRange(0, max) : starts;
    }

    public static ItemDatabase Load(string path)
    {
        var byId = new Dictionary<string, GameItem>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"warning: item database not found at {path}; checklist will show raw ids");
            return new ItemDatabase(byId);
        }

        var file = JsonSerializer.Deserialize<DatabaseFile>(File.ReadAllText(path), JsonOptions);
        foreach (var item in file?.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                continue;
            if (!byId.TryAdd(item.Id, item))
                Console.Error.WriteLine($"warning: duplicate item id '{item.Id}' in database, keeping the first entry");
        }
        return new ItemDatabase(byId);
    }

    private sealed class DatabaseFile
    {
        public List<GameItem> Items { get; set; } = new();
    }
}
