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
