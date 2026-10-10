#nullable enable
using System.Drawing;

namespace XiloOVR;

/// <summary>Accent-color theming, driven by AccentColorHex in config (hot-reloadable).</summary>
internal static class Theme
{
    private static readonly Color Fallback = ColorTranslator.FromHtml(AppConfig.DefaultAccentColorHex);

    private static string? _cachedHex;
    private static Color _cachedColor = Fallback;

    public static Color Accent(AppConfig config)
    {
        var hex = config.AccentColorHex;
        if (hex == _cachedHex)
            return _cachedColor;
        _cachedColor = Parse(hex) ?? Fallback;
        _cachedHex = hex;
        return _cachedColor;
    }

    /// <summary>
    /// Parses an HTML color ("#34D399", "#3D9", "red"); null when it is not a usable
    /// accent. ColorTranslator returns a fully transparent Color.Empty for an empty
    /// string instead of throwing, and an invisible accent would make the panel
    /// border, glyphs and laser vanish - so that counts as invalid too.
    /// </summary>
    public static Color? Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;
        try
        {
            var color = ColorTranslator.FromHtml(hex.Trim());
            return color.IsEmpty || color.A == 0 ? null : color;
        }
        catch
        {
            return null;
        }
    }

    public static Color WithAlpha(Color color, int alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
