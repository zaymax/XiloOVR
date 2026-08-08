#nullable enable
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// The settings tab in the SteamVR dashboard: a separate dashboard overlay with
/// laser-driven buttons that edit AppConfig live and persist it to config.json.
/// SteamVR provides mouse emulation for dashboard overlays (no custom ray-casting)
/// and its built-in VR keyboard handles text input for the Twitch channel name.
/// </summary>
public sealed class SettingsUI : IDisposable
{
    private const int PanelWidth = 1024;
    private const int PanelHeight = 720;
    private const int Margin = 24;
    private const int RowHeight = 46;
    private const int ButtonSize = 36;
    private const int RefreshIntervalMs = 1000; // keeps the chat status line fresh while the dashboard is open

    private static readonly Color Background = Color.FromArgb(250, 16, 20, 28);
    private static readonly Color CellFill = Color.FromArgb(255, 26, 32, 44);
    private static readonly Color Accent = Color.FromArgb(255, 90, 200, 250);
    private static readonly Color AccentDim = Color.FromArgb(90, 90, 200, 250);
    private static readonly Color HoverFill = Color.FromArgb(70, 90, 200, 250);

    private readonly AppConfig _config;
    private readonly TwitchChatClient _chat;
    private readonly YouTubeChatClient _youtube;
    private readonly TwitchFollowPoller _follows;
    private readonly Action _applyAndSave;
    private readonly List<(Rectangle Bounds, Action OnClick)> _widgets = new();
    private readonly System.Diagnostics.Stopwatch _refresh = System.Diagnostics.Stopwatch.StartNew();

    private enum KeyboardTarget
    {
        Channel,
        AccentColor,
        Username,
        Token,
        ClientId,
        YouTubeChannel,
    }

    private ulong _handle = OpenVR.k_ulOverlayHandleInvalid;
    private ulong _thumbnail = OpenVR.k_ulOverlayHandleInvalid;
    private int _hoverWidget = -1;
    private bool _dirty = true;
    private bool _keyboardOpen;
    private KeyboardTarget _keyboardTarget;

    public SettingsUI(AppConfig config, TwitchChatClient chat, YouTubeChatClient youtube,
        TwitchFollowPoller follows, Action applyAndSave)
    {
        _config = config;
        _chat = chat;
        _youtube = youtube;
        _follows = follows;
        _applyAndSave = applyAndSave;

        var vrOverlay = OpenVR.Overlay;
        var error = vrOverlay.CreateDashboardOverlay("xiloovr.settings", "XiloOVR", ref _handle, ref _thumbnail);
        if (error != EVROverlayError.None)
        {
            Console.Error.WriteLine($"warning: could not create the dashboard settings tab: {error}");
            _handle = OpenVR.k_ulOverlayHandleInvalid;
            return;
        }

        vrOverlay.SetOverlayWidthInMeters(_handle, 2.5f);
        vrOverlay.SetOverlayInputMethod(_handle, VROverlayInputMethod.Mouse);
        var mouseScale = new HmdVector2_t { v0 = PanelWidth, v1 = PanelHeight };
        vrOverlay.SetOverlayMouseScale(_handle, ref mouseScale);
        UploadThumbnail();
        Console.WriteLine("Settings tab registered in the SteamVR dashboard.");
    }

    public void MarkDirty() => _dirty = true;

    public void Update()
    {
        if (_handle == OpenVR.k_ulOverlayHandleInvalid)
            return;

        PollEvents();

        // The chat status line changes without user input; refresh it while visible.
        if (_refresh.ElapsedMilliseconds >= RefreshIntervalMs && OpenVR.Overlay.IsOverlayVisible(_handle))
        {
            _refresh.Restart();
            _dirty = true;
        }

        if (_dirty)
        {
            Render();
            _dirty = false;
        }
    }

    private void PollEvents()
    {
        var vrOverlay = OpenVR.Overlay;
        var vrEvent = new VREvent_t();
        var size = (uint)Marshal.SizeOf<VREvent_t>();
        while (vrOverlay.PollNextOverlayEvent(_handle, ref vrEvent, size))
        {
            switch ((EVREventType)vrEvent.eventType)
            {
                case EVREventType.VREvent_MouseMove:
                    OnPointerMove((int)vrEvent.data.mouse.x, FlipY(vrEvent.data.mouse.y));
                    break;

                case EVREventType.VREvent_MouseButtonDown:
                    if (vrEvent.data.mouse.button == (uint)EVRMouseButton.Left)
                        OnClick((int)vrEvent.data.mouse.x, FlipY(vrEvent.data.mouse.y));
                    break;

                case EVREventType.VREvent_OverlayShown:
                    _dirty = true;
                    break;

                case EVREventType.VREvent_KeyboardDone:
                    OnKeyboardDone();
                    break;

                case EVREventType.VREvent_KeyboardClosed:
                    _keyboardOpen = false;
                    break;
            }
        }
    }

    /// <summary>Dashboard mouse coordinates have a bottom-left origin; our pixels are top-down.</summary>
    private static int FlipY(float y) => PanelHeight - 1 - (int)y;

    private void OnPointerMove(int x, int y)
    {
        var hover = _widgets.FindIndex(w => w.Bounds.Contains(x, y));
        if (hover != _hoverWidget)
        {
            _hoverWidget = hover;
            _dirty = true;
        }
    }

    private void OnClick(int x, int y)
    {
        var index = _widgets.FindIndex(w => w.Bounds.Contains(x, y));
        if (index < 0)
            return;
        _widgets[index].OnClick();
        _applyAndSave();
        _dirty = true;
    }

    private void OpenKeyboard(KeyboardTarget target, string description, string existing)
    {
        if (_keyboardOpen)
            return;
        _keyboardTarget = target;
        var error = OpenVR.Overlay.ShowKeyboardForOverlay(
            _handle,
            (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
            (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
            0, description, 200, existing, 0);
        _keyboardOpen = error == EVROverlayError.None;
        if (!_keyboardOpen)
            Console.Error.WriteLine($"warning: could not open the VR keyboard: {error}");
    }

    private void OnKeyboardDone()
    {
        _keyboardOpen = false;
        var buffer = new StringBuilder(256);
        OpenVR.Overlay.GetKeyboardText(buffer, 256);
        var text = buffer.ToString().Trim();
        switch (_keyboardTarget)
        {
            case KeyboardTarget.Channel:
                _config.TwitchChannel = text.TrimStart('#').ToLowerInvariant();
                break;
            case KeyboardTarget.AccentColor:
                _config.AccentColorHex = text.StartsWith('#') || text.Length == 0 ? text : "#" + text;
                break;
            case KeyboardTarget.Username:
                _config.TwitchUsername = text;
                break;
            case KeyboardTarget.Token:
                _config.TwitchOAuthToken = text;
                break;
            case KeyboardTarget.ClientId:
                _config.TwitchClientId = text;
                break;
            case KeyboardTarget.YouTubeChannel:
                _config.YouTubeChannel = text;
                break;
        }
        _applyAndSave();
        _dirty = true;
    }

    private void Render()
    {
        _widgets.Clear();

        using var bitmap = new Bitmap(PanelWidth, PanelHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var titleFont = new Font("Segoe UI", 30, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sectionFont = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
            using var labelFont = new Font("Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
            using var buttonFont = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
            using var smallFont = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
            var accent = Theme.Accent(_config);
            var accentDim = Theme.WithAlpha(accent, 90);
            var hoverFill = Theme.WithAlpha(accent, 70);
            using var accentBrush = new SolidBrush(accent);
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far };
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

            g.Clear(Background);
            using var borderPen = new Pen(accent, 3);
            g.DrawRectangle(borderPen, 1, 1, PanelWidth - 3, PanelHeight - 3);

            // Header
            g.DrawString("XiloOVR", titleFont, accentBrush, Margin, 18);
            g.DrawString("Settings", titleFont, Brushes.White, Margin + 150, 18);
            var version = typeof(SettingsUI).Assembly.GetName().Version;
            g.DrawString($"v{version?.Major}.{version?.Minor}.{version?.Build}", labelFont, Brushes.Gray,
                new RectangleF(0, 26, PanelWidth - Margin, 24), rightAlign);
            using var dividerPen = new Pen(Theme.WithAlpha(accent, 120), 1);
            g.DrawLine(dividerPen, Margin, 62, PanelWidth - Margin, 62);

            // Local helpers keep the row plumbing in one place.
            void Button(Rectangle rect, string label, Action onClick, bool active = false)
            {
                var index = _widgets.Count;
                var hovered = index == _hoverWidget;
                using var bg = new SolidBrush(active ? accentDim : hovered ? hoverFill : CellFill);
                g.FillRectangle(bg, rect);
                using var pen = new Pen(hovered || active ? accent : Color.FromArgb(90, 200, 200, 200), hovered ? 2 : 1);
                g.DrawRectangle(pen, rect);
                g.DrawString(label, buttonFont, active ? Brushes.White : Brushes.LightGray,
                    new RectangleF(rect.X, rect.Y, rect.Width, rect.Height), centered);
                _widgets.Add((rect, onClick));
            }

            void StepperRow(int x, int y, int width, string caption, string value, Action minus, Action plus)
            {
                g.DrawString(caption, labelFont, Brushes.LightGray, x, y + 10);
                var plusRect = new Rectangle(x + width - ButtonSize, y + 4, ButtonSize, ButtonSize);
                var minusRect = new Rectangle(x + width - ButtonSize * 2 - 10, y + 4, ButtonSize, ButtonSize);
                g.DrawString(value, labelFont, Brushes.White,
                    new RectangleF(x, y + 10, width - ButtonSize * 2 - 24, 26), rightAlign);
                Button(minusRect, "−", minus);
                Button(plusRect, "+", plus);
            }

            void TwoStateRow(int x, int y, int width, string caption, string first, string second, bool firstActive, Action pickFirst, Action pickSecond)
            {
                g.DrawString(caption, labelFont, Brushes.LightGray, x, y + 10);
                const int buttonWidth = 92;
                Button(new Rectangle(x + width - buttonWidth * 2 - 10, y + 4, buttonWidth, ButtonSize), first, pickFirst, firstActive);
                Button(new Rectangle(x + width - buttonWidth, y + 4, buttonWidth, ButtonSize), second, pickSecond, !firstActive);
            }

            // Left column: wrist panel placement.
            const int colWidth = 460;
            var leftX = Margin;
            var y1 = 76;
            g.DrawString("Wrist panel", sectionFont, accentBrush, leftX, y1);
            y1 += 34;
            TwoStateRow(leftX, y1, colWidth, "Hand", "Left", "Right", _config.IsLeftHand,
                () => _config.Hand = "left", () => _config.Hand = "right");
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Width", $"{_config.WidthMeters:0.00} m",
                () => _config.WidthMeters = Step(_config.WidthMeters, -0.02f, 0.10f, 0.60f),
                () => _config.WidthMeters = Step(_config.WidthMeters, +0.02f, 0.10f, 0.60f));
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Position X (right)", $"{_config.PositionMeters.X:0.00} m",
                () => _config.PositionMeters.X = Step(_config.PositionMeters.X, -0.01f, -0.5f, 0.5f),
                () => _config.PositionMeters.X = Step(_config.PositionMeters.X, +0.01f, -0.5f, 0.5f));
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Position Y (up)", $"{_config.PositionMeters.Y:0.00} m",
                () => _config.PositionMeters.Y = Step(_config.PositionMeters.Y, -0.01f, -0.5f, 0.5f),
                () => _config.PositionMeters.Y = Step(_config.PositionMeters.Y, +0.01f, -0.5f, 0.5f));
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Position Z (to wrist)", $"{_config.PositionMeters.Z:0.00} m",
                () => _config.PositionMeters.Z = Step(_config.PositionMeters.Z, -0.01f, -0.5f, 0.5f),
                () => _config.PositionMeters.Z = Step(_config.PositionMeters.Z, +0.01f, -0.5f, 0.5f));
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Pitch", $"{_config.RotationDegrees.X:0}°",
                () => _config.RotationDegrees.X = Step(_config.RotationDegrees.X, -5f, -180f, 180f),
                () => _config.RotationDegrees.X = Step(_config.RotationDegrees.X, +5f, -180f, 180f));
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Yaw", $"{_config.RotationDegrees.Y:0}°",
                () => _config.RotationDegrees.Y = Step(_config.RotationDegrees.Y, -5f, -180f, 180f),
                () => _config.RotationDegrees.Y = Step(_config.RotationDegrees.Y, +5f, -180f, 180f));
            y1 += RowHeight;
            StepperRow(leftX, y1, colWidth, "Roll", $"{_config.RotationDegrees.Z:0}°",
                () => _config.RotationDegrees.Z = Step(_config.RotationDegrees.Z, -5f, -180f, 180f),
                () => _config.RotationDegrees.Z = Step(_config.RotationDegrees.Z, +5f, -180f, 180f));
            y1 += RowHeight;
            TwoStateRow(leftX, y1, colWidth, "Show on start", "On", "Off", _config.StartVisible,
                () => _config.StartVisible = true, () => _config.StartVisible = false);
            y1 += RowHeight;
            TwoStateRow(leftX, y1, colWidth, "Autostart with SteamVR", "On", "Off", _config.AutostartWithSteamVR,
                () => _config.AutostartWithSteamVR = true, () => _config.AutostartWithSteamVR = false);
            y1 += RowHeight;
            g.DrawString("Accent color", labelFont, Brushes.LightGray, leftX, y1 + 10);
            g.DrawString(_config.AccentColorHex, labelFont, accentBrush,
                new RectangleF(leftX, y1 + 10, colWidth - 110, 26), rightAlign);
            Button(new Rectangle(leftX + colWidth - 92, y1 + 4, 92, ButtonSize), "Edit",
                () => OpenKeyboard(KeyboardTarget.AccentColor, "Accent color, HTML hex (e.g. #34D399)", _config.AccentColorHex));

            // A value row: caption left, (possibly masked) value right, Edit button.
            void EditRow(int x, ref int y, string caption, string value, KeyboardTarget target, string prompt, string existing)
            {
                g.DrawString(caption, labelFont, Brushes.LightGray, x, y + 10);
                g.DrawString(value, labelFont, Brushes.White,
                    new RectangleF(x, y + 10, colWidth - 110, 26), rightAlign);
                Button(new Rectangle(x + colWidth - 92, y + 4, 92, ButtonSize), "Edit",
                    () => OpenKeyboard(target, prompt, existing));
                y += RowHeight;
            }

            // Right column: Twitch + YouTube.
            var rightX = 540;
            var y2 = 76;
            g.DrawString("Twitch", sectionFont, accentBrush, rightX, y2);
            y2 += 34;
            EditRow(rightX, ref y2, "Channel",
                string.IsNullOrWhiteSpace(_config.TwitchChannel) ? "(off)" : "#" + _config.TwitchChannel,
                KeyboardTarget.Channel, "Twitch channel name (empty = chat off)", _config.TwitchChannel);
            StepperRow(rightX, y2, colWidth, "Chat lines", _config.ChatMessagesShown.ToString(),
                () => _config.ChatMessagesShown = (int)Step(_config.ChatMessagesShown, -1, 1, 20),
                () => _config.ChatMessagesShown = (int)Step(_config.ChatMessagesShown, +1, 1, 20));
            y2 += RowHeight;
            EditRow(rightX, ref y2, "Account (to send)",
                string.IsNullOrWhiteSpace(_config.TwitchUsername) ? "(anonymous)" : _config.TwitchUsername,
                KeyboardTarget.Username, "Twitch account name (empty = read-only)", _config.TwitchUsername);
            EditRow(rightX, ref y2, "OAuth token", Mask(_config.TwitchOAuthToken),
                KeyboardTarget.Token, "OAuth token with chat:read + chat:edit (see README)", "");
            EditRow(rightX, ref y2, "Client id (follows)", Mask(_config.TwitchClientId),
                KeyboardTarget.ClientId, "Twitch app client id, only for follow alerts", _config.TwitchClientId);
            TwoStateRow(rightX, y2, colWidth, "Alerts (follow/sub/raid)", "On", "Off", _config.AlertsEnabled,
                () => _config.AlertsEnabled = true, () => _config.AlertsEnabled = false);
            y2 += RowHeight;
            g.DrawString($"Status: {_chat.StatusLine}", smallFont, Brushes.Gray, rightX, y2 + 2);
            g.DrawString(_follows.StatusLine == "off" ? "" : $"Follows: {_follows.StatusLine}",
                smallFont, Brushes.Gray, rightX, y2 + 22);
            y2 += 50;

            g.DrawString("YouTube chat", sectionFont, accentBrush, rightX, y2);
            y2 += 34;
            EditRow(rightX, ref y2, "Channel / video",
                string.IsNullOrWhiteSpace(_config.YouTubeChannel) ? "(off)" : _config.YouTubeChannel,
                KeyboardTarget.YouTubeChannel, "YouTube @handle, URL, or video id (empty = off)", _config.YouTubeChannel);
            g.DrawString($"Status: {_youtube.StatusLine}", smallFont, Brushes.Gray, rightX, y2 + 2);

            g.DrawString("Changes apply instantly and save to config.json next to the exe; editing the file by hand still works.",
                smallFont, Brushes.Gray, Margin, PanelHeight - 30);
        }

        OverlayManager.UploadTextureTo(_handle, PanelRenderer.ToRgba(bitmap), PanelWidth, PanelHeight);
    }

    private static float Step(float value, float delta, float min, float max) =>
        Math.Clamp((float)Math.Round(value + delta, 3), min, max);

    /// <summary>Secrets stay recognizable in the UI but never fully readable.</summary>
    private static string Mask(string secret)
    {
        secret = secret.Trim();
        if (secret.Length == 0)
            return "(not set)";
        return secret.Length <= 4 ? "••••" : "•••" + secret[^4..];
    }

    private void UploadThumbnail()
    {
        if (_thumbnail == OpenVR.k_ulOverlayHandleInvalid)
            return;
        using var bitmap = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.FromArgb(255, 16, 20, 28));
            using var pen = new Pen(Theme.Accent(_config), 6);
            g.DrawRectangle(pen, 3, 3, 121, 121);
            using var font = new Font("Segoe UI", 46, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Theme.Accent(_config));
            using var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("XO", font, brush, new RectangleF(0, 0, 128, 128), centered);
        }
        OverlayManager.UploadTextureTo(_thumbnail, PanelRenderer.ToRgba(bitmap), 128, 128);
    }

    public void Dispose()
    {
        if (_handle != OpenVR.k_ulOverlayHandleInvalid)
        {
            OpenVR.Overlay?.DestroyOverlay(_handle);
            _handle = OpenVR.k_ulOverlayHandleInvalid;
        }
    }
}
