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
