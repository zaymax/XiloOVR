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
    private const int ChatLineHeight = 22;
    private const int ChatPadding = 12;

    private static readonly Color Background = Color.FromArgb(235, 16, 20, 28);
    private static readonly Color CellFill = Color.FromArgb(255, 26, 32, 44);
    private static readonly Color Accent = Color.FromArgb(255, 90, 200, 250);
    private static readonly Color HoverFill = Color.FromArgb(70, 90, 200, 250);
    private static readonly Color DoneGreen = Color.FromArgb(255, 110, 220, 130);
    private static readonly Color TwitchPurple = Color.FromArgb(255, 145, 70, 255);

    // Fallback username colors for chatters who never set one (deterministic by name).
    private static readonly Color[] NamePalette =
    {
        Color.FromArgb(255, 255, 105, 97),
        Color.FromArgb(255, 255, 180, 80),
        Color.FromArgb(255, 120, 220, 120),
        Color.FromArgb(255, 100, 200, 250),
        Color.FromArgb(255, 200, 140, 255),
        Color.FromArgb(255, 250, 150, 200),
    };

    public static int CellSize(AppConfig config) =>
        (config.PanelPixelWidth - 2 * Margin - (Columns - 1) * CellGap) / Columns;

    public static int ChatSectionHeight(AppConfig config) =>
        config.IsChatEnabled ? Math.Clamp(config.ChatMessagesShown, 1, 20) * ChatLineHeight + ChatPadding : 0;

    public static int VisibleCellCapacity(AppConfig config)
    {
        var stride = CellSize(config) + CellGap;
        var rows = (config.PanelPixelHeight - HeaderHeight - FooterHeight - ChatSectionHeight(config) + CellGap) / stride;
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

    public static byte[] RenderChecklist(AppConfig config, ChecklistData checklist, IReadOnlyList<ChatMessage> chat, int hoverIndex, out int width, out int height)
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

            if (config.IsChatEnabled)
                DrawChatSection(g, config, chat, width, height, smallFont, dividerPen);

            var footerText = entries.Count > capacity
                ? $"+{entries.Count - capacity} more, edit checklist.json"
                : "free hand: trigger +1, grip/A -1";
            g.DrawString(footerText, smallFont, Brushes.Gray, Margin, height - FooterHeight + 8);
        }

        return ToRgba(bitmap);
    }

    private static void DrawChatSection(Graphics g, AppConfig config, IReadOnlyList<ChatMessage> chat,
        int width, int height, Font smallFont, Pen dividerPen)
    {
        var sectionTop = height - FooterHeight - ChatSectionHeight(config);
        g.DrawLine(dividerPen, Margin, sectionTop, width - Margin, sectionTop);

        using var chatFont = new Font("Segoe UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var authorFont = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
        using var badgeFont = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);
        using var singleLine = new StringFormat
        {
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        if (chat.Count == 0)
        {
            g.DrawString($"connecting to twitch.tv/{config.TwitchChannel.Trim().TrimStart('#').ToLowerInvariant()} ...",
                chatFont, Brushes.Gray, Margin, sectionTop + ChatPadding / 2f);
            return;
        }

        var shown = Math.Min(chat.Count, Math.Clamp(config.ChatMessagesShown, 1, 20));
        // Newest at the bottom, like a regular chat; partially filled feed stays bottom-aligned.
        var y = (float)(height - FooterHeight - shown * ChatLineHeight - ChatPadding / 2);
        for (var i = chat.Count - shown; i < chat.Count; i++)
        {
            var message = chat[i];
            var x = (float)Margin;

            var badgeColor = message.Source switch
            {
                "tw" => TwitchPurple,
                "yt" => Color.FromArgb(255, 230, 60, 60),
                _ => Color.DimGray,
            };
            using (var badgeBrush = new SolidBrush(badgeColor))
                g.FillRectangle(badgeBrush, x, y + 3, 16, 16);
            g.DrawString(message.Source == "tw" ? "T" : message.Source == "yt" ? "Y" : "•",
                badgeFont, Brushes.White, x + 3.5f, y + 4);
            x += 22;

            var authorText = message.Author + ":";
            using (var authorBrush = new SolidBrush(AuthorColor(message)))
                g.DrawString(authorText, authorFont, authorBrush, x, y);
            x += g.MeasureString(authorText, authorFont).Width + 2;

            g.DrawString(message.Text, chatFont, Brushes.LightGray,
                new RectangleF(x, y, width - Margin - x, ChatLineHeight), singleLine);
            y += ChatLineHeight;
        }
    }

    private static Color AuthorColor(ChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.ColorHex))
        {
            try
            {
                return ColorTranslator.FromHtml(message.ColorHex);
            }
            catch
            {
                // fall through to the palette
            }
        }
        var hash = 0;
        foreach (var c in message.Author)
            hash = hash * 31 + c;
        return NamePalette[Math.Abs(hash % NamePalette.Length)];
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
