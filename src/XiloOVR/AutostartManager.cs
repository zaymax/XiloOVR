#nullable enable
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// Registers XiloOVR with SteamVR's own autostart list. Enabling writes the app
/// manifest permanently into SteamVR's app database and flags the app for auto-launch,
/// so SteamVR starts the overlay whenever the runtime comes up; disabling only clears
/// the auto-launch flag (the manifest entry is harmless and keeps bindings stable).
/// </summary>
public static class AutostartManager
{
    private static bool? _lastApplied;

    public static void Apply(bool enabled)
    {
        // Called on every settings click / config reload; re-registering the manifest
        // with SteamVR each time would be pointless cross-process work.
        if (_lastApplied == enabled)
            return;

        var applications = OpenVR.Applications;
        if (applications == null)
        {
            Console.Error.WriteLine("warning: IVRApplications unavailable; autostart setting not applied");
            return;
        }

        var manifestPath = Path.Combine(AppContext.BaseDirectory, "app.vrmanifest");
        if (enabled && File.Exists(manifestPath))
        {
            // Permanent registration survives this process; the temporary one from
            // InputManager only lives for the session.
            var addError = applications.AddApplicationManifest(manifestPath, false);
            if (addError != EVRApplicationError.None)
            {
                Console.Error.WriteLine($"warning: could not register the app manifest for autostart ({addError})");
                return;
            }
        }

        var current = applications.GetApplicationAutoLaunch(InputManager.AppKey);
        if (current == enabled)
        {
            _lastApplied = enabled;
            return;
        }

        var error = applications.SetApplicationAutoLaunch(InputManager.AppKey, enabled);
        if (error != EVRApplicationError.None)
        {
            Console.Error.WriteLine($"warning: SetApplicationAutoLaunch failed ({error})");
            return;
        }
        _lastApplied = enabled;
        Console.WriteLine(enabled
            ? "Autostart enabled: SteamVR will launch XiloOVR on startup."
            : "Autostart disabled.");
    }
}
