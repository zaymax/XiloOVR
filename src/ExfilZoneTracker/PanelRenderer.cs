#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ExfilZoneTracker;

/// <summary>
/// Renders the panel content into an RGBA8 pixel buffer for IVROverlay.SetOverlayRaw.
/// GDI+ is Windows-only, which is fine: the whole app targets the Windows SteamVR runtime.
/// </summary>
public static class PanelRenderer
{
    public static byte[] RenderPlaceholder(AppConfig config, out int width, out int height)
    {
        width = config.PanelPixelWidth;
        height = config.PanelPixelHeight;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.Clear(Color.FromArgb(235, 16, 20, 28));
            using var border = new Pen(Color.FromArgb(255, 90, 200, 250), 4);
            g.DrawRectangle(border, 2, 2, width - 5, height - 5);

            using var titleFont = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel);
            using var bodyFont = new Font("Segoe UI", 20, FontStyle.Regular, GraphicsUnit.Pixel);
            using var centered = new StringFormat { Alignment = StringAlignment.Center };

            // The arrow marks the panel's +Y edge, which makes tuning RotationDegrees much easier.
            g.DrawString("▲ TOP", bodyFont, Brushes.DimGray, new RectangleF(0, 12, width, 30), centered);
            g.DrawString("Hello wrist overlay", titleFont, Brushes.White,
                new RectangleF(0, height / 2f - 64, width, 48), centered);
            g.DrawString($"ExfilZone Tracker prototype, {config.HandNormalized} hand", bodyFont, Brushes.LightGray,
                new RectangleF(0, height / 2f + 4, width, 30), centered);
            g.DrawString("edit config.json to adjust position and size", bodyFont, Brushes.DimGray,
                new RectangleF(0, height - 48, width, 30), centered);
        }

        return ToRgba(bitmap);
    }

    /// <summary>GDI+ stores Format32bppArgb as BGRA in memory; OpenVR raw overlays expect RGBA.</summary>
    private static byte[] ToRgba(Bitmap bitmap)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var bgra = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);

            var rgba = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var src = y * stride + x * 4;
                    var dst = (y * bitmap.Width + x) * 4;
                    rgba[dst] = bgra[src + 2];     // R
                    rgba[dst + 1] = bgra[src + 1]; // G
                    rgba[dst + 2] = bgra[src];     // B
                    rgba[dst + 3] = bgra[src + 3]; // A
                }
            }
            return rgba;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
