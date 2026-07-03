#nullable enable
using System.Diagnostics;
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// Keeps the overlay glued to the configured controller. Once
/// SetOverlayTransformTrackedDeviceRelative is applied, the compositor tracks the device
/// for us; this class re-applies the transform when the controller (re)appears, its
/// device index changes, or the config is hot-reloaded. Visibility is owned by ChecklistUI.
/// </summary>
public sealed class WristAttachment
{
    private const int RecheckIntervalMs = 500;

    private readonly OverlayManager _overlay;
    private readonly AppConfig _config;
    private readonly Stopwatch _sinceLastCheck = Stopwatch.StartNew();

    private ETrackedControllerRole _role;
    private HmdMatrix34_t _offset;
    private uint _deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
    private bool _reportedWaiting;

    public WristAttachment(OverlayManager overlay, AppConfig config)
    {
        _overlay = overlay;
        _config = config;
        Reconfigure();
    }

    public bool Present => _deviceIndex != OpenVR.k_unTrackedDeviceIndexInvalid;

    /// <summary>Re-reads hand/offset from config and forces a re-attach on the next update.</summary>
    public void Reconfigure()
    {
        _role = _config.IsLeftHand ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand;
        _offset = BuildOffset(_config);
        _deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
        _reportedWaiting = false;
    }

    /// <summary>Re-applies the transform to the current device, e.g. after a scene-app switch.</summary>
    public void ReassertTransform()
    {
        if (!Present)
            return;
        var offset = _offset;
        var error = OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_overlay.Handle, _deviceIndex, ref offset);
        if (error != EVROverlayError.None)
            Console.Error.WriteLine($"warning: transform re-assert failed: {error}");
    }

    public void Update(bool devicesChanged)
    {
        if (!devicesChanged && Present && _sinceLastCheck.ElapsedMilliseconds < RecheckIntervalMs)
            return;
        _sinceLastCheck.Restart();

        var index = _overlay.System.GetTrackedDeviceIndexForControllerRole(_role);
        if (index == _deviceIndex)
            return;
        _deviceIndex = index;

        if (index == OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            if (!_reportedWaiting)
            {
                Console.WriteLine($"Waiting for the {_config.HandNormalized} controller (panel hidden).");
                _reportedWaiting = true;
            }
            return;
        }

        var offset = _offset;
        var error = OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(_overlay.Handle, index, ref offset);
        if (error != EVROverlayError.None)
        {
            Console.Error.WriteLine($"warning: could not attach overlay to device {index}: {error}");
            _deviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
            return;
        }

        _reportedWaiting = false;
        Console.WriteLine($"Panel attached to the {_config.HandNormalized} controller (device #{index}).");
    }

    /// <summary>
    /// Builds the controller-local offset matrix (row-major 3x4, [R | t]).
    /// Rotation is applied as yaw (Y), then pitch (X), then roll (Z).
    /// </summary>
    private static HmdMatrix34_t BuildOffset(AppConfig config)
    {
        var rotation = Multiply(
            Multiply(RotationY(config.RotationDegrees.Y), RotationX(config.RotationDegrees.X)),
            RotationZ(config.RotationDegrees.Z));

        return new HmdMatrix34_t
        {
            m0 = (float)rotation[0, 0], m1 = (float)rotation[0, 1], m2 = (float)rotation[0, 2], m3 = config.PositionMeters.X,
            m4 = (float)rotation[1, 0], m5 = (float)rotation[1, 1], m6 = (float)rotation[1, 2], m7 = config.PositionMeters.Y,
            m8 = (float)rotation[2, 0], m9 = (float)rotation[2, 1], m10 = (float)rotation[2, 2], m11 = config.PositionMeters.Z,
        };
    }

    private static double[,] RotationX(double degrees)
    {
        var (sin, cos) = SinCos(degrees);
        return new[,] { { 1.0, 0, 0 }, { 0, cos, -sin }, { 0, sin, cos } };
    }

    private static double[,] RotationY(double degrees)
    {
        var (sin, cos) = SinCos(degrees);
        return new[,] { { cos, 0, sin }, { 0, 1.0, 0 }, { -sin, 0, cos } };
    }

    private static double[,] RotationZ(double degrees)
    {
        var (sin, cos) = SinCos(degrees);
        return new[,] { { cos, -sin, 0 }, { sin, cos, 0 }, { 0, 0, 1.0 } };
    }

    private static (double Sin, double Cos) SinCos(double degrees)
    {
        var radians = degrees * Math.PI / 180.0;
        return (Math.Sin(radians), Math.Cos(radians));
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[3, 3];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
                result[row, col] = a[row, 0] * b[0, col] + a[row, 1] * b[1, col] + a[row, 2] * b[2, col];
        return result;
    }
}
