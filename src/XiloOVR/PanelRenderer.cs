#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace XiloOVR;

/// <summary>
/// One grid cell: a special glyph ("+", "←"), the search cell (magnifier + label), an
/// item icon, or a text tile (category name, or an item whose icon is missing).
/// </summary>
public sealed class PanelCell
{
    public string? Glyph;
    public bool SearchIcon;
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

    /// <summary>Active follow/sub/raid alert; drawn as a banner over the header while set.</summary>
    public ChatMessage? Alert;
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
    public const int HitChat = 9002;

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
    private static readonly Color YouTubeRed = Color.FromArgb(255, 230, 60, 60);

    // Fonts never change between frames; GDI+ font creation is not free, and a hover
    // change re-renders the whole panel.
    private static readonly Font TitleFont = new("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font GlyphFont = new("Segoe UI", 48, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font CountFont = new("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font TileFont = new("Segoe UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font SmallFont = new("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font AlertFont = new("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font ChatFont = new("Segoe UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font AuthorFont = new("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font BadgeFont = new("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel);

    private static readonly StringFormat RightAlign = new() { Alignment = StringAlignment.Far };
    private static readonly StringFormat Center = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private static readonly StringFormat SingleLine = new()
    {
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter,
    };
    private static readonly StringFormat CenteredSingleLine = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter,
    };

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

    // The panel is re-rendered on every hover change; reusing the bitmap and the pixel
    // buffer (1.2 MB each at the default size) keeps that off the large-object heap.
    private static Bitmap? _bitmap;
    private static byte[]? _pixels;

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

    /// <summary>Maps a panel pixel to a cell index, the scroll arrows, the chat feed, or -1.</summary>
    public static int HitTest(AppConfig config, int cellCount, int x, int y)
    {
        if (UpArrowRect(config).Contains(x, y))
            return HitUp;
        if (DownArrowRect(config).Contains(x, y))
            return HitDown;

        // Clicking the chat feed opens the reply keyboard (when logged in to Twitch).
        var chatHeight = ChatSectionHeight(config);
        if (chatHeight > 0)
        {
            var chatTop = config.PanelPixelHeight - FooterHeight - chatHeight;
            if (y >= chatTop && y < config.PanelPixelHeight - FooterHeight && x >= Margin && x <= config.PanelPixelWidth - Margin)
                return HitChat;
        }

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

    /// <summary>
    /// Draws the panel and returns its RGBA pixels. The returned buffer is reused by the
    /// next call; SetOverlayRaw copies it synchronously, so that is safe for the caller.
    /// </summary>
    public static byte[] RenderPanel(AppConfig config, PanelView view, out int width, out int height)
    {
        width = config.PanelPixelWidth;
        height = config.PanelPixelHeight;

        if (_bitmap == null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }
        var bitmap = _bitmap;

        var accent = Theme.Accent(config);
        var hoverFill = Theme.WithAlpha(accent, 70);

        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            g.Clear(Background);
            using var borderPen = new Pen(accent, 3);
            g.DrawRectangle(borderPen, 1, 1, width - 3, height - 3);

            using var accentBrush = new SolidBrush(accent);
            using var cellBrush = new SolidBrush(CellFill);
            using var hoverBrush = new SolidBrush(hoverFill);
            using var doneBrush = new SolidBrush(DoneGreen);
            using var dimBrush = new SolidBrush(Color.FromArgb(150, 10, 12, 18));
            using var dividerPen = new Pen(Theme.WithAlpha(accent, 120), 1);

            // Header; an active alert takes the whole strip over for a few seconds.
            if (view.Alert != null)
            {
                var banner = new Rectangle(Margin / 2, 6, width - Margin, HeaderHeight - 14);
                g.FillRectangle(accentBrush, banner);
                using var darkBrush = new SolidBrush(Color.FromArgb(255, 12, 16, 22));
                g.DrawString($"★ {view.Alert.Author} {view.Alert.Text}", AlertFont, darkBrush,
                    new RectangleF(banner.X + 6, banner.Y, banner.Width - 12, banner.Height), CenteredSingleLine);
            }
            else
            {
                g.DrawString("XiloOVR", TitleFont, Brushes.White, Margin, 16);
                g.DrawString(view.HeaderRight, TitleFont, accentBrush, new RectangleF(0, 16, width - Margin, 34), RightAlign);
            }
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

                if (item.SearchIcon)
                {
                    // Magnifier drawn by hand: Segoe UI has no reliable glyph for it.
                    var r = cell / 5;
                    var cx = x + cell / 2 - r / 3;
                    var cy = y + cell / 2 - r / 2 - 6;
                    using var lensPen = new Pen(accent, 5);
                    g.DrawEllipse(lensPen, cx - r, cy - r, 2 * r, 2 * r);
                    g.DrawLine(lensPen, cx + r * 0.7f, cy + r * 0.7f, cx + r * 1.6f, cy + r * 1.6f);
                    if (item.Label != null)
                        g.DrawString(item.Label, SmallFont, Brushes.LightGray,
                            new RectangleF(x, y + cell - 28, cell, 24), Center);
                }
                else if (item.Glyph != null)
                {
                    g.DrawString(item.Glyph, GlyphFont, accentBrush, new RectangleF(x, y, cell, cell), Center);
                }
                else
                {
                    var iconSide = cell - 34;
                    var iconRect = new Rectangle(x + (cell - iconSide) / 2, y + 5, iconSide, iconSide);
                    var icon = IconCache.Get(item.IconPath);
                    if (icon != null)
                        g.DrawImage(icon, iconRect);
                    else if (item.Label != null)
                        g.DrawString(item.Label, TileFont, Brushes.LightGray,
                            new RectangleF(x + 4, y + 4, cell - 8, cell - 30), Center);

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
                    g.DrawString(item.CountText, CountFont, item.Complete ? doneBrush : Brushes.White,
                        new RectangleF(x, y + cell - 26, cell - 6, 22), RightAlign);
                }
            }

            if (config.IsChatEnabled)
                DrawChatSection(g, config, view.Chat, width, height, dividerPen, accent);

            // Footer: text left, scroll arrows right. The text is clipped before the
            // arrows so a long "Category 1-7 of 573 — ..." line cannot run under them.
            var showArrows = view.CanScrollUp || view.CanScrollDown;
            var footerRight = showArrows ? UpArrowRect(config).X - 8 : width - Margin;
            g.DrawString(view.FooterText, SmallFont, Brushes.Gray,
                new RectangleF(Margin, height - FooterHeight + 8, footerRight - Margin, FooterHeight - 8), SingleLine);
            if (showArrows)
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
                g.DrawString(glyph, SmallFont, enabled ? accentBrush : Brushes.DimGray,
                    new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), Center);
            }
        }

        return ToRgba(bitmap, ref _pixels);
    }

    private static void DrawChatSection(Graphics g, AppConfig config, IReadOnlyList<ChatMessage> chat,
        int width, int height, Pen dividerPen, Color accent)
    {
        var sectionTop = height - FooterHeight - ChatSectionHeight(config);
        g.DrawLine(dividerPen, Margin, sectionTop, width - Margin, sectionTop);

        if (chat.Count == 0)
        {
            var target = !string.IsNullOrWhiteSpace(config.TwitchChannel)
                ? $"twitch.tv/{config.TwitchChannelNormalized}"
                : "youtube live chat";
            g.DrawString($"connecting to {target} ...",
                ChatFont, Brushes.Gray, Margin, sectionTop + ChatPadding / 2f);
            return;
        }

        var shown = Math.Min(chat.Count, Math.Clamp(config.ChatMessagesShown, 1, 20));
        // Newest at the bottom, like a regular chat; partially filled feed stays bottom-aligned.
        var y = (float)(height - FooterHeight - shown * ChatLineHeight - ChatPadding / 2);
        for (var i = chat.Count - shown; i < chat.Count; i++)
        {
            var message = chat[i];
            var x = (float)Margin;

            if (message.IsAlert)
            {
                using var alertFill = new SolidBrush(Theme.WithAlpha(accent, 45));
                g.FillRectangle(alertFill, Margin - 4, y, width - 2 * Margin + 8, ChatLineHeight);
            }

            var badgeColor = message.IsAlert ? accent : message.Source switch
            {
                "tw" => TwitchPurple,
                "yt" => YouTubeRed,
                _ => Color.DimGray,
            };
            using (var badgeBrush = new SolidBrush(badgeColor))
                g.FillRectangle(badgeBrush, x, y + 3, 16, 16);
            g.DrawString(message.IsAlert ? "★" : message.Source == "tw" ? "T" : message.Source == "yt" ? "Y" : "•",
                BadgeFont, Brushes.White, x + (message.IsAlert ? 2f : 3.5f), y + 4);
            x += 22;

            var authorText = message.Author + ":";
            using (var authorBrush = new SolidBrush(AuthorColor(message)))
                g.DrawString(authorText, AuthorFont, authorBrush, x, y);
            x += g.MeasureString(authorText, AuthorFont).Width + 2;

            g.DrawString(message.Text, ChatFont, message.IsAlert ? Brushes.White : Brushes.LightGray,
                new RectangleF(x, y, width - Margin - x, ChatLineHeight), SingleLine);
            y += ChatLineHeight;
        }
    }

    private static Color AuthorColor(ChatMessage message)
    {
        var color = Theme.Parse(message.ColorHex);
        if (color != null)
            return color.Value;
        var hash = 0;
        foreach (var c in message.Author)
            hash = hash * 31 + c;
        return NamePalette[Math.Abs(hash % NamePalette.Length)];
    }

    /// <summary>
    /// Copies a 32bpp bitmap into an RGBA8 buffer for SetOverlayRaw. GDI+ stores
    /// Format32bppArgb as BGRA in memory, so the red and blue bytes are swapped in place
    /// after one bulk copy. <paramref name="buffer"/> is (re)allocated only when its
    /// size does not match, so a caller that renders repeatedly keeps one buffer.
    /// </summary>
    internal static byte[] ToRgba(Bitmap bitmap, ref byte[]? buffer)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rowBytes = width * 4;
        var length = rowBytes * height;
        if (buffer == null || buffer.Length != length)
            buffer = new byte[length];

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, buffer, 0, length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        SwapRedBlue(buffer);
        return buffer;
    }

    private static void SwapRedBlue(byte[] pixels)
    {
        if (BitConverter.IsLittleEndian)
        {
            // One 32-bit pixel at a time: BGRA little-endian is 0xAARRGGBB; swap the two
            // outer bytes and the alpha/green stay where they are.
            var words = MemoryMarshal.Cast<byte, uint>(pixels);
            for (var i = 0; i < words.Length; i++)
            {
                var v = words[i];
                words[i] = (v & 0xFF00FF00u) | ((v & 0x00FF0000u) >> 16) | ((v & 0x000000FFu) << 16);
            }
            return;
        }
        for (var i = 0; i + 3 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}
