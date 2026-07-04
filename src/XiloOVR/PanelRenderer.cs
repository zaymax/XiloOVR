#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace XiloOVR;

/// <summary>One grid cell: either a special glyph ("+", "←"), an item icon, or a text label.</summary>
public sealed class PanelCell
{
    public string? Glyph;
    public string? IconPath;
    public string? Label;
    public string? CountText;
    public bool Complete;
}

/// <summary>Everything the wrist panel shows in one frame; built by ChecklistUI.</summary>
public sealed class PanelView
{
    public string HeaderRight = "";
    public IReadOnlyList<PanelCell> Cells = Array.Empty<PanelCell>();
    public int HoverId = -1;
    public bool CanScrollUp;
    public bool CanScrollDown;
    public string FooterText = "";
    public IReadOnlyList<ChatMessage> Chat = Array.Empty<ChatMessage>();
}

/// <summary>
/// Renders the wrist panel — an icon grid plus chat feed — into an RGBA8 buffer for
/// IVROverlay.SetOverlayRaw, and owns the pixel layout used for laser hit-testing:
/// HitTest must mirror the rectangles drawn here. GDI+ is Windows-only, which matches
/// the app's target.
/// </summary>
public static class PanelRenderer
{
    public const int Columns = 4;
    public const int HitUp = 9000;
    public const int HitDown = 9001;

    private const int Margin = 16;
    private const int HeaderHeight = 60;
    private const int FooterHeight = 34;
    private const int CellGap = 8;
    private const int ChatLineHeight = 22;
    private const int ChatPadding = 12;
    private const int ArrowWidth = 42;

    private static readonly Color Background = Color.FromArgb(235, 16, 20, 28);
    private static readonly Color CellFill = Color.FromArgb(255, 26, 32, 44);
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

    private static Rectangle UpArrowRect(AppConfig config) =>
        new(config.PanelPixelWidth - Margin - ArrowWidth * 2 - 8, config.PanelPixelHeight - FooterHeight + 3, ArrowWidth, FooterHeight - 6);

    private static Rectangle DownArrowRect(AppConfig config) =>
        new(config.PanelPixelWidth - Margin - ArrowWidth, config.PanelPixelHeight - FooterHeight + 3, ArrowWidth, FooterHeight - 6);

    /// <summary>Maps a panel pixel to a cell index, the scroll arrows, or -1.</summary>
    public static int HitTest(AppConfig config, int cellCount, int x, int y)
    {
        if (UpArrowRect(config).Contains(x, y))
            return HitUp;
        if (DownArrowRect(config).Contains(x, y))
            return HitDown;

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
        return index < Math.Min(cellCount, VisibleCellCapacity(config)) ? index : -1;
    }

    public static byte[] RenderPanel(AppConfig config, PanelView view, out int width, out int height)
    {
        width = config.PanelPixelWidth;
        height = config.PanelPixelHeight;

        var accent = Theme.Accent(config);
        var hoverFill = Theme.WithAlpha(accent, 70);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            g.Clear(Background);
            using var borderPen = new Pen(accent, 3);
            g.DrawRectangle(borderPen, 1, 1, width - 3, height - 3);

            using var titleFont = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
            using var glyphFont = new Font("Segoe UI", 48, FontStyle.Bold, GraphicsUnit.Pixel);
            using var countFont = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            using var accentBrush = new SolidBrush(accent);
            using var cellBrush = new SolidBrush(CellFill);
            using var hoverBrush = new SolidBrush(hoverFill);
            using var doneBrush = new SolidBrush(DoneGreen);
            using var dimBrush = new SolidBrush(Color.FromArgb(150, 10, 12, 18));
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far };
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var dividerPen = new Pen(Theme.WithAlpha(accent, 120), 1);

            // Header
            g.DrawString("XiloOVR", titleFont, Brushes.White, Margin, 16);
            g.DrawString(view.HeaderRight, titleFont, accentBrush, new RectangleF(0, 16, width - Margin, 34), rightAlign);
            g.DrawLine(dividerPen, Margin, HeaderHeight - 4, width - Margin, HeaderHeight - 4);

            // Cells
            var cell = CellSize(config);
            var stride = cell + CellGap;
            var capacity = VisibleCellCapacity(config);
            for (var i = 0; i < view.Cells.Count && i < capacity; i++)
            {
                var item = view.Cells[i];
                var x = Margin + (i % Columns) * stride;
                var y = HeaderHeight + (i / Columns) * stride;
                var cellRect = new Rectangle(x, y, cell, cell);

                g.FillRectangle(cellBrush, cellRect);
                if (i == view.HoverId)
                {
                    g.FillRectangle(hoverBrush, cellRect);
                    using var hoverPen = new Pen(accent, 2);
                    g.DrawRectangle(hoverPen, x, y, cell - 1, cell - 1);
                }

                if (item.Glyph != null)
                {
                    g.DrawString(item.Glyph, glyphFont, accentBrush, new RectangleF(x, y, cell, cell), center);
                }
                else
                {
                    var iconSide = cell - 34;
                    var iconRect = new Rectangle(x + (cell - iconSide) / 2, y + 5, iconSide, iconSide);
                    var icon = IconCache.Get(item.IconPath);
                    if (icon != null)
                        g.DrawImage(icon, iconRect);
                    else if (item.Label != null)
                        g.DrawString(item.Label, smallFont, Brushes.LightGray,
                            new RectangleF(x + 4, y + 4, cell - 8, cell - 30), center);

                    if (item.Complete)
                    {
                        g.FillRectangle(dimBrush, iconRect);
                        using var checkPen = new Pen(DoneGreen, 5);
                        var cx = iconRect.X + iconRect.Width / 2;
                        var cy = iconRect.Y + iconRect.Height / 2;
                        g.DrawLines(checkPen, new[]
                        {
                            new Point(cx - 18, cy + 2),
                            new Point(cx - 6, cy + 14),
                            new Point(cx + 18, cy - 12),
                        });
                    }
                }

                if (item.CountText != null)
                {
                    g.DrawString(item.CountText, countFont, item.Complete ? doneBrush : Brushes.White,
                        new RectangleF(x, y + cell - 26, cell - 6, 22), rightAlign);
                }
            }

            if (config.IsChatEnabled)
                DrawChatSection(g, config, view.Chat, width, height, smallFont, dividerPen, accent);

            // Footer: text left, scroll arrows right.
            g.DrawString(view.FooterText, smallFont, Brushes.Gray, Margin, height - FooterHeight + 8);
            if (view.CanScrollUp || view.CanScrollDown)
            {
                DrawArrow(UpArrowRect(config), "▲", view.CanScrollUp, view.HoverId == HitUp);
                DrawArrow(DownArrowRect(config), "▼", view.CanScrollDown, view.HoverId == HitDown);
            }

            void DrawArrow(Rectangle rect, string glyph, bool enabled, bool hovered)
            {
                using var bg = new SolidBrush(hovered && enabled ? hoverFill : CellFill);
                g.FillRectangle(bg, rect);
                using var pen = new Pen(enabled ? accent : Color.FromArgb(70, 160, 160, 160), hovered && enabled ? 2 : 1);
                g.DrawRectangle(pen, rect);
                g.DrawString(glyph, smallFont, enabled ? accentBrush : Brushes.DimGray,
                    new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), center);
            }
        }

        return ToRgba(bitmap);
    }

    private static void DrawChatSection(Graphics g, AppConfig config, IReadOnlyList<ChatMessage> chat,
        int width, int height, Font smallFont, Pen dividerPen, Color accent)
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
    internal static byte[] ToRgba(Bitmap bitmap)
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
