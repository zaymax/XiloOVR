#nullable enable
using System.Runtime.InteropServices;
using Valve.VR;

namespace XiloOVR;

/// <summary>Thrown when the SteamVR connection cannot be established; the message is user-facing.</summary>
public sealed class VRInitException : Exception
{
    public VRInitException(string message) : base(message) { }
}

/// <summary>Owns the OpenVR connection and the overlay handle.</summary>
public sealed class OverlayManager : IDisposable
{
    public const string OverlayKey = "xiloovr.wrist";

    private bool _vrInitialized;
    private CVRSystem? _system;

    public ulong Handle { get; private set; } = OpenVR.k_ulOverlayHandleInvalid;

    public CVRSystem System => _system ?? throw new InvalidOperationException("OpenVR is not initialized.");

    public void Initialize(AppConfig config)
    {
        // VRApplication_Overlay: draw on top of whatever scene app is running, never own the scene.
        // Note: with this application type SteamVR may auto-start if it is installed but not running.
        var initError = EVRInitError.None;
        _system = OpenVR.Init(ref initError, EVRApplicationType.VRApplication_Overlay);
        if (initError != EVRInitError.None || _system == null)
            throw new VRInitException(DescribeInitError(initError));
        _vrInitialized = true;

        var vrOverlay = OpenVR.Overlay
            ?? throw new VRInitException("SteamVR is running, but the overlay interface (IVROverlay) is unavailable.");

        var handle = OpenVR.k_ulOverlayHandleInvalid;
        var error = vrOverlay.CreateOverlay(OverlayKey, "XiloOVR", ref handle);
        if (error != EVROverlayError.None)
            throw new VRInitException($"CreateOverlay failed: {vrOverlay.GetOverlayErrorNameFromEnum(error)}");
        Handle = handle;

        vrOverlay.SetOverlayWidthInMeters(Handle, config.WidthMeters);
        Console.WriteLine($"Connected to SteamVR, overlay '{OverlayKey}' created.");
    }

    /// <summary>Uploads an RGBA8 buffer as the overlay texture (SetOverlayRaw copies it synchronously).</summary>
    public void UploadTexture(byte[] rgba, int width, int height)
        => UploadTextureTo(Handle, rgba, width, height);

    /// <summary>Uploads an RGBA8 buffer to any overlay handle (SetOverlayRaw copies it synchronously).</summary>
    public static void UploadTextureTo(ulong handle, byte[] rgba, int width, int height)
    {
        var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var error = OpenVR.Overlay.SetOverlayRaw(handle, pinned.AddrOfPinnedObject(), (uint)width, (uint)height, 4);
            if (error != EVROverlayError.None)
                Console.Error.WriteLine($"warning: SetOverlayRaw failed: {error}");
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>Drains the SteamVR event queue. Returns false when SteamVR asks us to exit.</summary>
    public bool PumpEvents(out bool devicesChanged, out bool sceneAppChanged)
    {
        devicesChanged = false;
        sceneAppChanged = false;
        if (_system == null)
            return false;

        var vrEvent = new VREvent_t();
        var size = (uint)Marshal.SizeOf<VREvent_t>();
        while (_system.PollNextEvent(ref vrEvent, size))
        {
            switch ((EVREventType)vrEvent.eventType)
            {
                case EVREventType.VREvent_Quit:
                    Console.WriteLine("SteamVR is shutting down, exiting.");
                    // Acknowledge, or SteamVR will consider us unresponsive and force-kill the process.
                    _system.AcknowledgeQuit_Exiting();
                    return false;

                case EVREventType.VREvent_TrackedDeviceActivated:
                case EVREventType.VREvent_TrackedDeviceDeactivated:
                case EVREventType.VREvent_TrackedDeviceRoleChanged:
                    devicesChanged = true;
                    break;

                case EVREventType.VREvent_SceneApplicationChanged:
                    // A game started or stopped; some setups drop overlay state here.
                    Console.WriteLine("Scene application changed, re-asserting the panel.");
                    sceneAppChanged = true;
                    break;

                case EVREventType.VREvent_Input_BindingLoadSuccessful:
                    Console.WriteLine("SteamVR loaded controller bindings for the tracker.");
                    break;

                case EVREventType.VREvent_Input_BindingLoadFailed:
                    Console.Error.WriteLine(
                        "warning: SteamVR failed to load the default controller bindings; " +
                        "bind the two actions manually in SteamVR > Settings > Controllers > Manage Controller Bindings.");
                    break;
            }
        }
        return true;
    }

    private static string DescribeInitError(EVRInitError error) => error switch
    {
        EVRInitError.Init_InstallationNotFound or EVRInitError.Init_PathRegistryNotFound =>
            "SteamVR does not appear to be installed. Install SteamVR from Steam and try again.",
        EVRInitError.Init_NoServerForBackgroundApp or EVRInitError.Init_VRClientDLLNotFound =>
            "SteamVR is not running. Start SteamVR, then launch the tracker again.",
        EVRInitError.Init_HmdNotFound or EVRInitError.Init_HmdNotFoundPresenceFailed =>
            "SteamVR responded, but no headset was detected. Connect the headset and try again.",
        _ => $"Could not connect to SteamVR: {OpenVR.GetStringForHmdError(error)}",
    };

    public void Dispose()
    {
        if (Handle != OpenVR.k_ulOverlayHandleInvalid)
        {
            OpenVR.Overlay?.DestroyOverlay(Handle);
            Handle = OpenVR.k_ulOverlayHandleInvalid;
        }
        if (_vrInitialized)
        {
            OpenVR.Shutdown();
            _vrInitialized = false;
        }
    }
}
