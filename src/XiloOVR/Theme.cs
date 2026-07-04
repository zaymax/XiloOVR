#nullable enable
using System.Drawing;

namespace XiloOVR;

/// <summary>Accent-color theming, driven by AccentColorHex in config (hot-reloadable).</summary>
internal static class Theme
{
    private static readonly Color Fallback = Color.FromArgb(255, 52, 211, 153);

    private static string? _cachedHex;
    private static Color _cachedColor = Fallback;

    public static Color Accent(AppConfig config)
    {
        var hex = config.AccentColorHex;
        if (hex == _cachedHex)
            return _cachedColor;
        try
        {
            _cachedColor = ColorTranslator.FromHtml(hex);
        }
        catch
        {
            _cachedColor = Fallback;
        }
        _cachedHex = hex;
        return _cachedColor;
    }

    public static Color WithAlpha(Color color, int alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
