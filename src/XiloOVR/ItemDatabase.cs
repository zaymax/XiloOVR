#nullable enable
using System.Text.Json;

namespace XiloOVR;

public sealed class GameItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Russian name from the game's localization table; null when the source has none.</summary>
    public string? NameRu { get; set; }

    public string Category { get; set; } = "misc";
    public string? Icon { get; set; }
    public string? Note { get; set; }

    /// <summary>Ids this item had in earlier databases; checklists written with them migrate on load.</summary>
    public List<string>? LegacyIds { get; set; }

    public string DisplayName(bool russian) => russian && !string.IsNullOrWhiteSpace(NameRu) ? NameRu : Name;
}

/// <summary>One database category as shown in the in-VR browser.</summary>
public sealed record ItemCategory(string Key, string Name, string NameRu, int Count)
{
    public string DisplayName(bool russian) => russian ? NameRu : Name;
}

/// <summary>
/// Static reference of game items shipped as data/items_database.json. Read-only at
/// runtime; users edit the file with a text editor after game patches, no rebuild needed.
/// </summary>
public sealed class ItemDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Dictionary<string, GameItem> _byId;
    private readonly Dictionary<string, GameItem> _byLegacyId;

    private ItemDatabase(Dictionary<string, GameItem> byId, Dictionary<string, GameItem> byLegacyId)
    {
        _byId = byId;
        _byLegacyId = byLegacyId;
    }

    public int Count => _byId.Count;

    /// <summary>Looks an id up, accepting ids from earlier databases as well.</summary>
    public GameItem? Find(string id) =>
        _byId.TryGetValue(id, out var item) ? item : _byLegacyId.TryGetValue(id, out item) ? item : null;

    /// <summary>The current id for a legacy one, or null when the id is current or unknown.</summary>
    public string? CurrentIdFor(string legacyId) =>
        !_byId.ContainsKey(legacyId) && _byLegacyId.TryGetValue(legacyId, out var item) ? item.Id : null;

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
            .Select(pair => new ItemCategory(pair.Key, DisplayCategory(pair.Key, false), DisplayCategory(pair.Key, true), pair.Value))
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
    {
        "task-items", "keys", "valuables", "resources", "medicine", "provisions", "weapons", "attachments",
        "magazines", "gun-parts", "ammo", "armor", "helmets", "face-shields", "backpacks", "holsters",
        "containers", "grenades", "misc", "gear",
    };

    private static readonly Dictionary<string, string> CategoryNamesRu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["task-items"] = "Квестовые", ["keys"] = "Ключи", ["valuables"] = "Ценности", ["resources"] = "Ресурсы",
        ["medicine"] = "Медицина", ["provisions"] = "Провизия", ["weapons"] = "Оружие", ["attachments"] = "Обвесы",
        ["magazines"] = "Магазины", ["gun-parts"] = "Детали оружия", ["ammo"] = "Патроны", ["armor"] = "Броня",
        ["helmets"] = "Шлемы", ["face-shields"] = "Забрала", ["backpacks"] = "Рюкзаки", ["holsters"] = "Подсумки",
        ["containers"] = "Контейнеры", ["grenades"] = "Гранаты", ["misc"] = "Разное", ["gear"] = "Снаряжение",
    };

    private static int PreferredOrder(string key)
    {
        var index = Array.FindIndex(CategoryPriority, c => string.Equals(c, key, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? CategoryPriority.Length : index;
    }

    private static string NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? "misc" : category.Trim().ToLowerInvariant();

    /// <summary>"task-items" → "Task items" (or the Russian label); one display rule for every category id.</summary>
    public static string DisplayCategory(string key, bool russian)
    {
        if (russian && CategoryNamesRu.TryGetValue(key, out var ru))
            return ru;
        var text = key.Replace('-', ' ').Replace('_', ' ').Trim();
        return text.Length == 0 ? "Misc" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Name/category search for the in-VR picker (English and Russian names): prefix matches rank first.</summary>
    public IReadOnlyList<GameItem> Search(string query, int max)
    {
        query = query.Trim();
        if (query.Length == 0)
            return _byId.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();

        var starts = new List<GameItem>();
        var contains = new List<GameItem>();
        foreach (var item in _byId.Values)
        {
            var ru = item.NameRu ?? "";
            if (item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                ru.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                starts.Add(item);
            else if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     ru.Contains(query, StringComparison.OrdinalIgnoreCase) ||
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
        var byLegacyId = new Dictionary<string, GameItem>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"warning: item database not found at {path}; checklist will show raw ids");
            return new ItemDatabase(byId, byLegacyId);
        }

        var file = JsonSerializer.Deserialize<DatabaseFile>(File.ReadAllText(path), JsonOptions);
        foreach (var item in file?.Items ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                continue;
            if (!byId.TryAdd(item.Id, item))
                Console.Error.WriteLine($"warning: duplicate item id '{item.Id}' in database, keeping the first entry");
        }
        foreach (var item in byId.Values)
        {
            foreach (var legacy in item.LegacyIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(legacy) && !byId.ContainsKey(legacy))
                    byLegacyId.TryAdd(legacy, item);
            }
        }
        return new ItemDatabase(byId, byLegacyId);
    }

    private sealed class DatabaseFile
    {
        public List<GameItem> Items { get; set; } = new();
    }
}
