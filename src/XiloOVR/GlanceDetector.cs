#nullable enable
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// Decides whether the wrist panel should be visible in glance mode, like checking a
/// watch: the watch face (controller +Y, "up out of the button face") must point at the
/// headset AND the headset must roughly look toward the wrist. Both angles use
/// show/hide hysteresis so the panel does not flicker at the threshold.
/// </summary>
public sealed class GlanceDetector
{
    private const float GazeShowDegrees = 45f;
    private const float GazeHideDegrees = 65f;

    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private bool _visible;

    public bool Update(AppConfig config, uint wristDeviceIndex)
    {
        if (wristDeviceIndex == OpenVR.k_unTrackedDeviceIndexInvalid)
            return _visible = false;

        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);
        if (wristDeviceIndex >= _poses.Length)
            return _visible = false;
        var wrist = _poses[wristDeviceIndex];
        var hmd = _poses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (!wrist.bPoseIsValid || !hmd.bPoseIsValid)
            return _visible = false;

        var w = wrist.mDeviceToAbsoluteTracking;
        var h = hmd.mDeviceToAbsoluteTracking;

        // Row-major [R|t]: rotation columns are the device's basis vectors in world space.
        var wristUp = Normalize(w.m1, w.m5, w.m9); // +Y, out of the button face
        var hmdForward = Normalize(-h.m2, -h.m6, -h.m10); // view direction is -Z
        var toHmd = Normalize(h.m3 - w.m3, h.m7 - w.m7, h.m11 - w.m11);

        var wristAngle = AngleDegrees(Dot(wristUp, toHmd));
        var gazeAngle = AngleDegrees(Dot(hmdForward, (-toHmd.X, -toHmd.Y, -toHmd.Z)));

        // Hysteresis: harder to turn on than to keep on.
        if (_visible)
            _visible = wristAngle < config.GlanceHideDegrees && gazeAngle < GazeHideDegrees;
        else if (wristAngle < config.GlanceShowDegrees && gazeAngle < GazeShowDegrees)
            _visible = true;
        return _visible;
    }

    private static (float X, float Y, float Z) Normalize(float x, float y, float z)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z);
        return length < 1e-6f ? (0f, 0f, 0f) : (x / length, y / length, z / length);
    }

    private static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static float AngleDegrees(float cosine) =>
        MathF.Acos(Math.Clamp(cosine, -1f, 1f)) * 180f / MathF.PI;
}
