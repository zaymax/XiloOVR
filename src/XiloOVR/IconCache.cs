#nullable enable
using System.Drawing;

namespace XiloOVR;

/// <summary>
/// Caches item icon bitmaps loaded from data/icons. Bitmaps are copied out of their
/// source stream so files are not locked and can be replaced while the app runs.
/// </summary>
public static class IconCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return null;
        if (Cache.TryGetValue(absolutePath, out var cached))
            return cached;

        Bitmap? bitmap = null;
        try
        {
            if (File.Exists(absolutePath))
            {
                using var stream = new MemoryStream(File.ReadAllBytes(absolutePath));
                using var decoded = new Bitmap(stream);
                bitmap = new Bitmap(decoded); // detach from the stream: GDI+ keeps streams alive otherwise
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not load icon '{absolutePath}': {ex.Message}");
        }
        Cache[absolutePath] = bitmap; // negative results cached too, avoids retry spam
        return bitmap;
    }

    public static void Clear()
    {
        foreach (var bitmap in Cache.Values)
            bitmap?.Dispose();
        Cache.Clear();
    }
}
