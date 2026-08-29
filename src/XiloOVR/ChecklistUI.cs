#nullable enable
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// The in-VR wrist panel: the checklist grid with a "+" cell that opens an item picker
/// (VR-keyboard search over the database), laser-pointer hover and clicks with a
/// visible beam, row scrolling, fade in/out, and texture re-rendering on any change.
/// </summary>
public sealed class ChecklistUI
{
    private const float FadeDurationMs = 200f;
    private const int WatchdogIntervalMs = 5000;
    private const int ChatBufferLimit = 60;
    private const int PickerResultLimit = 60;
    private const float DefaultLaserLength = 0.8f;
    private const int AlertBannerMs = 8000;
    private const int FooterFlashMs = 4000;

    private readonly OverlayManager _overlay;
    private readonly AppConfig _config;
    private readonly ChecklistData _checklist;
    private readonly TwitchChatClient _twitch;
    private readonly IReadOnlyList<IChatSource> _chatSources;
    private readonly LaserBeam _laser;
    private readonly List<ChatMessage> _chat = new();
    private readonly List<ChatMessage> _drainBuffer = new();
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private readonly System.Diagnostics.Stopwatch _watchdog = System.Diagnostics.Stopwatch.StartNew();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private bool _userVisible;
    private bool _shown;
    private float _alpha;
    private bool _dirty = true;

    private int _hoverId = -1;
    private int _lastCellCount;

    // Follow/sub/raid banner shown over the header until this deadline passes.
    private ChatMessage? _alertBanner;
    private double _alertBannerUntilMs;

    // Short-lived footer hint (e.g. "log in to reply").
    private string? _footerFlash;
    private double _footerFlashUntilMs;

    // Picker state: "+" opens the VR keyboard, results replace the grid until "←".
    private bool _pickerMode;
    private bool _keyboardOpen;
    private KeyboardPurpose _keyboardPurpose;
    private string _query = "";
    private List<GameItem> _results = new();
    private int _scrollChecklist;
    private int _scrollPicker;

    private enum KeyboardPurpose
    {
        Search,
        Reply,
    }

    public ChecklistUI(OverlayManager overlay, AppConfig config, ChecklistData checklist,
        TwitchChatClient twitch, IReadOnlyList<IChatSource> chatSources, LaserBeam laser)
    {
        _overlay = overlay;
        _config = config;
        _checklist = checklist;
        _twitch = twitch;
        _chatSources = chatSources;
        _laser = laser;
        _userVisible = config.StartVisible;
    }

    public bool PanelShown => _shown;

    public void ToggleVisibility()
    {
        _userVisible = !_userVisible;
        Console.WriteLine(_userVisible ? "Panel shown." : "Panel hidden (press the toggle again to bring it back).");
    }

    public void MarkDirty() => _dirty = true;

    public void Update(double deltaMs, bool wristControllerPresent, uint pointerDeviceIndex, bool incrementClicked, bool decrementClicked)
    {
        if (_checklist.ConsumeFileChanges())
        {
            IconCache.Clear(); // icons may have changed together with the database
            _dirty = true;
        }

        DrainChatSources();

        PollOverlayEvents();
        UpdateAlpha(deltaMs, wristControllerPresent);

        // Timed UI elements fade on their own, without any new input.
        var nowMs = _clock.Elapsed.TotalMilliseconds;
        if (_alertBanner != null && nowMs > _alertBannerUntilMs)
        {
            _alertBanner = null;
            _dirty = true;
        }
        if (_footerFlash != null && nowMs > _footerFlashUntilMs)
        {
            _footerFlash = null;
            _dirty = true;
        }

        // Self-heal: some setups drop overlay visibility/texture on scene-app switches,
        // so periodically re-assert both while the panel is supposed to be on screen.
        if (_shown && _watchdog.ElapsedMilliseconds >= WatchdogIntervalMs)
        {
            _watchdog.Restart();
            var vrOverlay = OpenVR.Overlay;
            if (!vrOverlay.IsOverlayVisible(_overlay.Handle))
                Console.WriteLine("note: compositor reports the panel hidden while it should be visible, re-showing.");
            vrOverlay.ShowOverlay(_overlay.Handle);
            _dirty = true; // re-upload the texture as well
        }

        var hover = -1;
        var laserLength = DefaultLaserLength;
        if (_shown && pointerDeviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid)
            hover = ComputePointerTarget(pointerDeviceIndex, ref laserLength);
        if (hover != _hoverId)
        {
            _hoverId = hover;
            _dirty = true;
        }

        if (_shown && incrementClicked)
            OnTrigger(_hoverId);
        if (_shown && decrementClicked)
            OnGrip(_hoverId);

        _laser.Update(pointerDeviceIndex, _shown, laserLength, _config);

        if (_dirty)
        {
            var view = BuildView();
            _lastCellCount = view.Cells.Count;
            var pixels = PanelRenderer.RenderPanel(_config, view, out var width, out var height);
            _overlay.UploadTexture(pixels, width, height);
            _dirty = false;
        }
    }

    /// <summary>Pulls new messages from every chat source (Twitch, YouTube, follows).</summary>
    private void DrainChatSources()
    {
        _drainBuffer.Clear();
        var any = false;
        foreach (var source in _chatSources)
            any |= source.TryDrain(_drainBuffer);
        if (!any)
            return;

        foreach (var message in _drainBuffer)
        {
            // AlertsEnabled only controls the banner takeover; the event line always
            // stays in the feed (a Super Chat is still a message).
            if (message.IsAlert && _config.AlertsEnabled)
            {
                _alertBanner = message;
                _alertBannerUntilMs = _clock.Elapsed.TotalMilliseconds + AlertBannerMs;
            }
            _chat.Add(message);
        }
        if (_chat.Count > ChatBufferLimit)
            _chat.RemoveRange(0, _chat.Count - ChatBufferLimit);
        _dirty = true;
    }

    // ---- interaction -----------------------------------------------------------------

    private void OnTrigger(int id)
    {
        switch (id)
        {
            case PanelRenderer.HitUp:
                Scroll(-1);
                return;
            case PanelRenderer.HitDown:
                Scroll(+1);
                return;
            case PanelRenderer.HitChat:
                OnChatClicked();
                return;
            case < 0:
                return;
        }

        if (!_pickerMode)
        {
            if (id == 0) // the "+" cell
            {
                OpenSearchKeyboard();
                return;
            }
            var index = _scrollChecklist * PanelRenderer.Columns + id - 1;
            _checklist.Increment(index);
            _dirty = true;
        }
        else
        {
            if (id == 0) // "←" back to the checklist
            {
                _pickerMode = false;
                _dirty = true;
                return;
            }
            if (id == 1) // re-open the search keyboard
            {
                OpenSearchKeyboard();
                return;
            }
            var index = _scrollPicker * PanelRenderer.Columns + id - 2;
            if (index >= 0 && index < _results.Count)
            {
                _checklist.AddOrIncrementNeeded(_results[index].Id);
                _dirty = true;
            }
        }
    }

    private void OnGrip(int id)
    {
        if (id < 0 || id >= PanelRenderer.HitUp)
            return;

        if (!_pickerMode)
        {
            if (id == 0)
                return;
            _checklist.Decrement(_scrollChecklist * PanelRenderer.Columns + id - 1);
            _dirty = true;
        }
        else
        {
            var index = _scrollPicker * PanelRenderer.Columns + id - 2;
            if (index >= 0 && index < _results.Count)
            {
                _checklist.DecrementNeededOrRemove(_results[index].Id);
                _dirty = true;
            }
        }
    }

    private void Scroll(int deltaRows)
    {
        if (_pickerMode)
            _scrollPicker = Math.Max(0, _scrollPicker + deltaRows);
        else
            _scrollChecklist = Math.Max(0, _scrollChecklist + deltaRows);
        _dirty = true; // BuildView clamps against the data size
    }

    /// <summary>Clicking the chat feed opens the reply keyboard (needs a Twitch login).</summary>
    private void OnChatClicked()
    {
        if (_twitch.CanSend)
        {
            OpenKeyboard(KeyboardPurpose.Reply, $"Send to #{_config.TwitchChannelNormalized}", "");
            return;
        }
        // Say what is actually missing instead of a generic "try again".
        _footerFlash = string.IsNullOrWhiteSpace(_config.TwitchChannel)
            ? "to reply, set a Twitch channel in the dashboard settings"
            : _twitch.LoginFailed
                ? "Twitch login failed - check the account and token in settings"
                : _config.HasTwitchLogin
                    ? "connecting to Twitch, try again in a moment"
                    : "to reply, set Twitch account + token in the dashboard settings";
        _footerFlashUntilMs = _clock.Elapsed.TotalMilliseconds + FooterFlashMs;
        _dirty = true;
    }

    private void OpenSearchKeyboard() => OpenKeyboard(KeyboardPurpose.Search, "Search items (empty = all)", _query);

    private void OpenKeyboard(KeyboardPurpose purpose, string description, string existing)
    {
        if (_keyboardOpen)
            return;
        _keyboardPurpose = purpose;
        var error = OpenVR.Overlay.ShowKeyboardForOverlay(
            _overlay.Handle,
            (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
            (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
            0, description, 200, existing, 0);
        _keyboardOpen = error == EVROverlayError.None;
        if (!_keyboardOpen)
            Console.Error.WriteLine($"warning: could not open the VR keyboard: {error}");
    }

    private void PollOverlayEvents()
    {
        var vrOverlay = OpenVR.Overlay;
        var vrEvent = new VREvent_t();
        var size = (uint)Marshal.SizeOf<VREvent_t>();
        while (vrOverlay.PollNextOverlayEvent(_overlay.Handle, ref vrEvent, size))
        {
            switch ((EVREventType)vrEvent.eventType)
            {
                case EVREventType.VREvent_KeyboardDone:
                    _keyboardOpen = false;
                    // The buffer size is in UTF-8 bytes, not chars: 200 chars of
                    // Cyrillic/CJK need up to ~800 bytes.
                    var buffer = new StringBuilder(1024);
                    vrOverlay.GetKeyboardText(buffer, 1024);
                    var text = buffer.ToString().Trim();
                    if (_keyboardPurpose == KeyboardPurpose.Reply)
                    {
                        if (text.Length > 0 && !_twitch.SendMessage(text))
                        {
                            _footerFlash = "message not sent - Twitch connection dropped";
                            _footerFlashUntilMs = _clock.Elapsed.TotalMilliseconds + FooterFlashMs;
                        }
                        _dirty = true;
                        break;
                    }
                    _query = text;
                    _results = new List<GameItem>(_checklist.SearchDatabase(_query, PickerResultLimit));
                    _pickerMode = true;
                    _scrollPicker = 0;
                    _dirty = true;
                    break;

                case EVREventType.VREvent_KeyboardClosed:
                    _keyboardOpen = false;
                    break;
            }
        }
    }

    // ---- view building ---------------------------------------------------------------

    private PanelView BuildView()
    {
        var capacity = PanelRenderer.VisibleCellCapacity(_config);
        var cells = new List<PanelCell>();
        string headerRight;
        string footer;
        bool canUp, canDown;

        if (!_pickerMode)
        {
            var entries = _checklist.Entries;
            var slots = Math.Max(0, capacity - 1);
            ClampScroll(ref _scrollChecklist, entries.Count, slots);
            var offset = _scrollChecklist * PanelRenderer.Columns;

            cells.Add(new PanelCell { Glyph = "+" });
            for (var i = offset; i < entries.Count && cells.Count < capacity; i++)
            {
                var entry = entries[i];
                cells.Add(new PanelCell
                {
                    IconPath = _checklist.IconPathFor(entry),
                    Label = _checklist.DisplayName(entry),
                    CountText = $"{entry.Collected}/{entry.Needed}",
                    Complete = entry.IsComplete,
                });
            }

            var (done, total) = _checklist.Progress;
            headerRight = $"{done}/{total}";
            canUp = _scrollChecklist > 0;
            canDown = offset + slots < entries.Count;
            footer = entries.Count == 0
                ? "click + to add items from the database"
                : canUp || canDown
                    ? $"items {offset + 1}-{Math.Min(entries.Count, offset + slots)} of {entries.Count}"
                    : _twitch.CanSend
                        ? "trigger +1, grip -1, click chat to reply"
                        : "trigger +1, grip -1, + adds items";
        }
        else
        {
            var slots = Math.Max(0, capacity - 2);
            ClampScroll(ref _scrollPicker, _results.Count, slots);
            var offset = _scrollPicker * PanelRenderer.Columns;

            cells.Add(new PanelCell { Glyph = "←" });
            cells.Add(new PanelCell { Label = "search" });
            for (var i = offset; i < _results.Count && cells.Count < capacity; i++)
            {
                var item = _results[i];
                var needed = _checklist.NeededOf(item.Id);
                cells.Add(new PanelCell
                {
                    IconPath = _checklist.IconPathFor(item),
                    Label = item.Name,
                    CountText = needed > 0 ? $"×{needed}" : null,
                });
            }

            headerRight = $"{_results.Count} found";
            canUp = _scrollPicker > 0;
            canDown = offset + slots < _results.Count;
            footer = _query.Length == 0
                ? "all items — trigger adds, grip removes"
                : $"'{_query}' — trigger adds, grip removes";
        }

        return new PanelView
        {
            HeaderRight = headerRight,
            Cells = cells,
            HoverId = _hoverId,
            CanScrollUp = canUp,
            CanScrollDown = canDown,
            FooterText = _footerFlash ?? footer,
            Chat = _chat,
            Alert = _alertBanner,
        };
    }

    private static void ClampScroll(ref int scrollRows, int itemCount, int slots)
    {
        var maxOffset = Math.Max(0, itemCount - slots);
        var maxRows = (maxOffset + PanelRenderer.Columns - 1) / PanelRenderer.Columns;
        scrollRows = Math.Clamp(scrollRows, 0, maxRows);
    }

    // ---- visibility / pointer ----------------------------------------------------------

    private void UpdateAlpha(double deltaMs, bool wristControllerPresent)
    {
        if (!wristControllerPresent)
        {
            // No controller to hang on: hide instantly, a fading panel would float in space.
            _alpha = 0f;
        }
        else
        {
            var target = _userVisible ? 1f : 0f;
            var step = (float)(deltaMs / FadeDurationMs);
            _alpha = target > _alpha ? Math.Min(target, _alpha + step) : Math.Max(target, _alpha - step);
        }

        var vrOverlay = OpenVR.Overlay;
        if (_alpha <= 0f)
        {
            if (_shown)
            {
                vrOverlay.HideOverlay(_overlay.Handle);
                _shown = false;
                _hoverId = -1;
            }
            return;
        }

        if (!_shown)
        {
            vrOverlay.ShowOverlay(_overlay.Handle);
            _shown = true;
        }
        vrOverlay.SetOverlayAlpha(_overlay.Handle, _alpha);
    }

    /// <summary>Casts a ray from the pointer controller; returns the hit control and beam length.</summary>
    private int ComputePointerTarget(uint pointerDeviceIndex, ref float laserLength)
    {
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);
        if (pointerDeviceIndex >= _poses.Length)
            return -1;
        var pose = _poses[pointerDeviceIndex];
        if (!pose.bPoseIsValid)
            return -1;

        var m = pose.mDeviceToAbsoluteTracking;
        var parameters = new VROverlayIntersectionParams_t
        {
            eOrigin = ETrackingUniverseOrigin.TrackingUniverseStanding,
            vSource = new HmdVector3_t { v0 = m.m3, v1 = m.m7, v2 = m.m11 },
            // Controller forward is -Z in its local space: third rotation column, negated.
            vDirection = new HmdVector3_t { v0 = -m.m2, v1 = -m.m6, v2 = -m.m10 },
        };

        var results = new VROverlayIntersectionResults_t();
        if (!OpenVR.Overlay.ComputeOverlayIntersection(_overlay.Handle, ref parameters, ref results))
            return -1;
        if (results.fDistance > _config.MaxLaserDistanceMeters)
            return -1;

        laserLength = results.fDistance;

        // Overlay UVs are bottom-left origin; raw texture rows start at the top.
        var x = (int)(results.vUVs.v0 * _config.PanelPixelWidth);
        var y = (int)((1f - results.vUVs.v1) * _config.PanelPixelHeight);
        return PanelRenderer.HitTest(_config, _lastCellCount, x, y);
    }
}
