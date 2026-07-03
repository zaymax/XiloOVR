#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace XiloOVR;

/// <summary>
/// Renders the checklist panel — a grid of item icons with collected/needed counters —
/// into an RGBA8 pixel buffer for IVROverlay.SetOverlayRaw. Also owns the pixel layout
/// used for laser hit-testing: CellAtPixel must mirror the rectangles drawn here.
/// GDI+ is Windows-only, which matches the app's target.
/// </summary>
public static class PanelRenderer
{
    private const int Margin = 16;
    private const int HeaderHeight = 60;
    private const int FooterHeight = 34;
    private const int Columns = 4;
    private const int CellGap = 8;

    private static readonly Color Background = Color.FromArgb(235, 16, 20, 28);
    private static readonly Color CellFill = Color.FromArgb(255, 26, 32, 44);
    private static readonly Color Accent = Color.FromArgb(255, 90, 200, 250);
    private static readonly Color HoverFill = Color.FromArgb(70, 90, 200, 250);
    private static readonly Color DoneGreen = Color.FromArgb(255, 110, 220, 130);

    public static int CellSize(AppConfig config) =>
        (config.PanelPixelWidth - 2 * Margin - (Columns - 1) * CellGap) / Columns;

    public static int VisibleCellCapacity(AppConfig config)
    {
        var stride = CellSize(config) + CellGap;
        var rows = (config.PanelPixelHeight - HeaderHeight - FooterHeight + CellGap) / stride;
        return Math.Max(0, rows) * Columns;
    }

    /// <summary>Maps a panel pixel to a checklist cell index, or -1 outside any cell.</summary>
    public static int CellAtPixel(AppConfig config, int entryCount, int x, int y)
    {
        var cell = CellSize(config);
        var stride = cell + CellGap;
        var localX = x - Margin;
        var localY = y - HeaderHeight;
        if (localX < 0 || localY < 0)
            return -1;

        var column = localX / stride;
        var row = localY / stride;
        if (column >= Columns || localX % stride > cell || localY % stride > cell)
            return -1;

        var index = row * Columns + column;
        return index < Math.Min(entryCount, VisibleCellCapacity(config)) ? index : -1;
    }

    public static byte[] RenderChecklist(AppConfig config, ChecklistData checklist, int hoverIndex, out int width, out int height)
    {
        width = config.PanelPixelWidth;
        height = config.PanelPixelHeight;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            g.Clear(Background);
            using var borderPen = new Pen(Accent, 3);
            g.DrawRectangle(borderPen, 1, 1, width - 3, height - 3);

            using var titleFont = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
            using var countFont = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var accentBrush = new SolidBrush(Accent);
            using var cellBrush = new SolidBrush(CellFill);
            using var hoverBrush = new SolidBrush(HoverFill);
            using var doneBrush = new SolidBrush(DoneGreen);
            using var dimBrush = new SolidBrush(Color.FromArgb(150, 10, 12, 18));
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far };
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            // Header: title left, done/total progress right.
            var (done, total) = checklist.Progress;
            g.DrawString("XiloOVR", titleFont, Brushes.White, Margin, 16);
            g.DrawString($"{done}/{total}", titleFont, accentBrush, new RectangleF(0, 16, width - Margin, 34), rightAlign);
            using var dividerPen = new Pen(Color.FromArgb(120, 90, 200, 250), 1);
            g.DrawLine(dividerPen, Margin, HeaderHeight - 4, width - Margin, HeaderHeight - 4);

            var entries = checklist.Entries;
            var capacity = VisibleCellCapacity(config);
            var cell = CellSize(config);
            var stride = cell + CellGap;

            if (entries.Count == 0)
            {
                g.DrawString("Checklist is empty.", countFont, Brushes.LightGray, Margin, HeaderHeight + 20);
                g.DrawString("Add items to checklist.json next to the exe.", smallFont, Brushes.Gray, Margin, HeaderHeight + 52);
            }

            for (var i = 0; i < entries.Count && i < capacity; i++)
            {
                var entry = entries[i];
                var x = Margin + (i % Columns) * stride;
                var y = HeaderHeight + (i / Columns) * stride;
                var cellRect = new Rectangle(x, y, cell, cell);

                g.FillRectangle(cellBrush, cellRect);
                if (i == hoverIndex)
                {
                    g.FillRectangle(hoverBrush, cellRect);
                    using var hoverPen = new Pen(Accent, 2);
                    g.DrawRectangle(hoverPen, x, y, cell - 1, cell - 1);
                }

                // Icon area: square, leaving a strip at the bottom for the counter.
                var iconSide = cell - 34;
                var iconRect = new Rectangle(x + (cell - iconSide) / 2, y + 5, iconSide, iconSide);
                var icon = IconCache.Get(checklist.IconPathFor(entry));
                if (icon != null)
                {
                    g.DrawImage(icon, iconRect);
                }
                else
                {
                    // No icon: show the item name instead so the cell is still usable.
                    g.DrawString(checklist.DisplayName(entry), smallFont, Brushes.LightGray,
                        new RectangleF(x + 4, y + 4, cell - 8, cell - 30), center);
                }

                if (entry.IsComplete)
                {
                    g.FillRectangle(dimBrush, iconRect); // dim the icon
                    using var checkPen = new Pen(DoneGreen, 5);
                    var cxm = iconRect.X + iconRect.Width / 2;
                    var cym = iconRect.Y + iconRect.Height / 2;
                    g.DrawLines(checkPen, new[]
                    {
                        new Point(cxm - 18, cym + 2),
                        new Point(cxm - 6, cym + 14),
                        new Point(cxm + 18, cym - 12),
                    });
                }

                g.DrawString($"{entry.Collected}/{entry.Needed}", countFont,
                    entry.IsComplete ? doneBrush : Brushes.White,
                    new RectangleF(x, y + cell - 26, cell - 6, 22), rightAlign);
            }

            var footerText = entries.Count > capacity
                ? $"+{entries.Count - capacity} more, edit checklist.json"
                : "free hand: trigger +1, grip/A -1";
            g.DrawString(footerText, smallFont, Brushes.Gray, Margin, height - FooterHeight + 8);
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
