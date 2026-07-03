#nullable enable
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// The in-VR checklist panel: visibility with a short fade, laser-pointer hover via
/// ComputeOverlayIntersection (pointing with the free hand), click-to-check, and
/// re-rendering the overlay texture whenever anything changes.
/// </summary>
public sealed class ChecklistUI
{
    private const float FadeDurationMs = 200f;
    private const int WatchdogIntervalMs = 5000;

    private readonly OverlayManager _overlay;
    private readonly AppConfig _config;
    private readonly ChecklistData _checklist;
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private readonly System.Diagnostics.Stopwatch _watchdog = System.Diagnostics.Stopwatch.StartNew();

    private bool _userVisible;
    private bool _shown;
    private float _alpha;
    private int _hoverIndex = -1;
    private bool _dirty = true;

    public ChecklistUI(OverlayManager overlay, AppConfig config, ChecklistData checklist)
    {
        _overlay = overlay;
        _config = config;
        _checklist = checklist;
        _userVisible = config.StartVisible;
    }

    public bool PanelShown => _shown;

    public void ToggleVisibility()
    {
        _userVisible = !_userVisible;
        Console.WriteLine(_userVisible ? "Panel shown." : "Panel hidden (long-press again to bring it back).");
    }

    public void MarkDirty() => _dirty = true;

    public void Update(double deltaMs, bool wristControllerPresent, uint pointerDeviceIndex, bool incrementClicked, bool decrementClicked)
    {
        if (_checklist.ConsumeFileChanges())
        {
            IconCache.Clear(); // icons may have changed together with the database
            _dirty = true;
        }

        UpdateAlpha(deltaMs, wristControllerPresent);

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
        if (_shown && pointerDeviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid)
            hover = ComputeHoveredRow(pointerDeviceIndex);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            _dirty = true;
        }

        if (_shown && _hoverIndex >= 0)
        {
            if (incrementClicked)
            {
                _checklist.Increment(_hoverIndex);
                _dirty = true;
            }
            if (decrementClicked)
            {
                _checklist.Decrement(_hoverIndex);
                _dirty = true;
            }
        }

        if (_dirty)
        {
            var pixels = PanelRenderer.RenderChecklist(_config, _checklist, _hoverIndex, out var width, out var height);
            _overlay.UploadTexture(pixels, width, height);
            _dirty = false;
        }
    }

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
                _hoverIndex = -1;
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

    /// <summary>Casts a ray from the pointer controller and maps the overlay UV hit to a row.</summary>
    private int ComputeHoveredRow(uint pointerDeviceIndex)
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

        // Overlay UVs are bottom-left origin; raw texture rows start at the top.
        var x = (int)(results.vUVs.v0 * _config.PanelPixelWidth);
        var y = (int)((1f - results.vUVs.v1) * _config.PanelPixelHeight);
        return PanelRenderer.CellAtPixel(_config, _checklist.Entries.Count, x, y);
    }
}
