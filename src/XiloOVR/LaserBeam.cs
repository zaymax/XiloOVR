#nullable enable
using System.Drawing;
using System.Drawing.Imaging;
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// A visible beam from the pointer controller: two thin crossed overlay ribbons
/// attached to the device and stretched to the current hit distance, so the beam is
/// visible from any angle. Purely visual — hit-testing still happens in ChecklistUI
/// via ComputeOverlayIntersection.
/// </summary>
public sealed class LaserBeam : IDisposable
{
    private const int TexWidth = 1024;
    private const int TexHeight = 4;

    // Constant rotations: quad X axis (its width) runs along the controller's -Z
    // (pointing direction); the second ribbon is rolled 90° around the beam axis.
    private static readonly float[][] Rotations =
    {
        new float[] { 0, 0, 1, 0, 1, 0, -1, 0, 0 },  // Ry(90)
        new float[] { 0, 1, 0, 0, 0, -1, -1, 0, 0 }, // Ry(90) * Rx(90)
    };

    private readonly ulong[] _handles = { OpenVR.k_ulOverlayHandleInvalid, OpenVR.k_ulOverlayHandleInvalid };

    private string? _texturedHex;
    private bool _shown;
    private uint _device = OpenVR.k_unTrackedDeviceIndexInvalid;
    private float _length = -1f;

    public LaserBeam()
    {
        var vrOverlay = OpenVR.Overlay;
        for (var i = 0; i < 2; i++)
        {
            ulong handle = 0;
            var error = vrOverlay.CreateOverlay($"xiloovr.laser.{i}", "XiloOVR Laser", ref handle);
            if (error != EVROverlayError.None)
            {
                Console.Error.WriteLine($"warning: could not create the laser overlay: {error}");
                return;
            }
            _handles[i] = handle;
            vrOverlay.SetOverlaySortOrder(handle, 1); // draw over the wrist panel
        }
    }

    public void Update(uint pointerDevice, bool panelShown, float length, AppConfig config)
    {
        if (_handles[1] == OpenVR.k_ulOverlayHandleInvalid)
            return;

        var vrOverlay = OpenVR.Overlay;
        if (!panelShown || pointerDevice == OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            if (_shown)
            {
                foreach (var handle in _handles)
                    vrOverlay.HideOverlay(handle);
                _shown = false;
            }
            return;
        }

        EnsureTexture(config);

        var deviceChanged = pointerDevice != _device;
        _device = pointerDevice;
        if (deviceChanged || Math.Abs(length - _length) > 0.01f)
        {
            _length = length;
            for (var i = 0; i < 2; i++)
            {
                var r = Rotations[i];
                var transform = new HmdMatrix34_t
                {
                    m0 = r[0], m1 = r[1], m2 = r[2], m3 = 0f,
                    m4 = r[3], m5 = r[4], m6 = r[5], m7 = 0f,
                    m8 = r[6], m9 = r[7], m10 = r[8], m11 = -length / 2f, // centered along the beam
                };
                vrOverlay.SetOverlayWidthInMeters(_handles[i], length); // ribbon height follows the texture aspect (~length/256)
                vrOverlay.SetOverlayTransformTrackedDeviceRelative(_handles[i], pointerDevice, ref transform);
            }
        }

        if (!_shown)
        {
            foreach (var handle in _handles)
                vrOverlay.ShowOverlay(handle);
            _shown = true;
        }
    }

    /// <summary>Re-renders the beam texture when the accent color changes (hot-reload friendly).</summary>
    private void EnsureTexture(AppConfig config)
    {
        if (config.AccentColorHex == _texturedHex)
            return;
        _texturedHex = config.AccentColorHex;

        var accent = Theme.Accent(config);
        var rgba = new byte[TexWidth * TexHeight * 4];
        for (var x = 0; x < TexWidth; x++)
        {
            // Bright at the controller, fading toward the far end.
            var alpha = (byte)(230 - 190 * x / (TexWidth - 1));
            for (var y = 0; y < TexHeight; y++)
            {
                var offset = (y * TexWidth + x) * 4;
                rgba[offset] = accent.R;
                rgba[offset + 1] = accent.G;
                rgba[offset + 2] = accent.B;
                rgba[offset + 3] = alpha;
            }
        }
        foreach (var handle in _handles)
            OverlayManager.UploadTextureTo(handle, rgba, TexWidth, TexHeight);
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            if (handle != OpenVR.k_ulOverlayHandleInvalid)
                OpenVR.Overlay?.DestroyOverlay(handle);
        }
    }
}
