#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExfilZoneTracker;

/// <summary>
/// User settings, stored as config.json next to the executable.
/// Position is in meters and rotation in degrees, both in the controller's local
/// coordinate space: +X right, +Y up out of the button face, -Z along the pointing direction.
/// </summary>
public sealed class AppConfig
{
    public string Hand { get; set; } = "right";

    /// <summary>Physical width of the panel in meters; height follows the pixel aspect ratio.</summary>
    public float WidthMeters { get; set; } = 0.22f;

    public Vec3 PositionMeters { get; set; } = new() { X = 0f, Y = 0.02f, Z = 0.13f };

    /// <summary>X = pitch, Y = yaw, Z = roll. Applied as yaw, then pitch, then roll.</summary>
    public Vec3 RotationDegrees { get; set; } = new() { X = -90f, Y = 0f, Z = 0f };

    public int PanelPixelWidth { get; set; } = 600;
    public int PanelPixelHeight { get; set; } = 400;

    [JsonIgnore]
    public string HandNormalized => Hand.Trim().ToLowerInvariant() == "left" ? "left" : "right";

    [JsonIgnore]
    public bool IsLeftHand => HandNormalized == "left";
}

public sealed class Vec3
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads config.json, or writes one with defaults so the user has a file to edit.</summary>
    public static AppConfig LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            Console.WriteLine($"Created default config: {path}");
            return defaults;
        }

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        var hand = config.Hand.Trim().ToLowerInvariant();
        if (hand is not ("left" or "right"))
            Console.Error.WriteLine($"warning: unknown hand '{config.Hand}' in config, using 'right'");
        return config;
    }
}
