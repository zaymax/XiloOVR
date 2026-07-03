#nullable enable
using System.Diagnostics;
using System.Runtime.InteropServices;
using Valve.VR;

namespace ExfilZoneTracker;

/// <summary>
/// SteamVR Input integration: registers the app and its action manifest so the tracker
/// shows up in SteamVR's controller-binding UI with default bindings, then reads two
/// boolean actions: toggle_panel (any hand, long-press detected here) and interact
/// (click, restricted to the pointer hand). If any step fails the tracker still runs,
/// just without button input.
/// </summary>
public sealed class InputManager
{
    public const string AppKey = "zaymax.exfilzone-wrist-tracker";

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ulong _actionSet = OpenVR.k_ulInvalidActionSetHandle;
    private ulong _toggleAction;
    private ulong _interactAction;
    private ulong _decrementAction;
    private ulong _leftHandSource;
    private ulong _rightHandSource;
    private VRActiveActionSet_t[]? _activeSets;

    private double _togglePressStartMs = -1;
    private bool _toggleFired;

    public bool Available { get; private set; }

    public void Initialize()
    {
        var input = OpenVR.Input;
        if (input == null)
        {
            Console.Error.WriteLine("warning: IVRInput unavailable; button input disabled");
            return;
        }

        RegisterApplication();

        var actionsPath = Path.Combine(AppContext.BaseDirectory, "input", "actions.json");
        if (!File.Exists(actionsPath))
        {
            Console.Error.WriteLine($"warning: {actionsPath} not found; button input disabled");
            return;
        }

        var error = input.SetActionManifestPath(actionsPath);
        if (error != EVRInputError.None)
        {
            Console.Error.WriteLine($"warning: SetActionManifestPath failed ({error}); button input disabled");
            return;
        }

        if (!TryGetHandle(input.GetActionSetHandle("/actions/main", ref _actionSet), "action set") ||
            !TryGetHandle(input.GetActionHandle("/actions/main/in/toggle_panel", ref _toggleAction), "toggle action") ||
            !TryGetHandle(input.GetActionHandle("/actions/main/in/interact", ref _interactAction), "interact action") ||
            !TryGetHandle(input.GetActionHandle("/actions/main/in/decrement", ref _decrementAction), "decrement action") ||
            !TryGetHandle(input.GetInputSourceHandle("/user/hand/left", ref _leftHandSource), "left hand source") ||
            !TryGetHandle(input.GetInputSourceHandle("/user/hand/right", ref _rightHandSource), "right hand source"))
        {
            return;
        }

        _activeSets = new[]
        {
            new VRActiveActionSet_t
            {
                ulActionSet = _actionSet,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
            },
        };
        Available = true;
        Console.WriteLine("SteamVR Input ready. Rebind under SteamVR > Settings > Controllers > Manage Controller Bindings.");
    }

    public void Update()
    {
        if (!Available || _activeSets == null)
            return;
        var error = OpenVR.Input.UpdateActionState(_activeSets, (uint)Marshal.SizeOf<VRActiveActionSet_t>());
        if (error != EVRInputError.None)
            Console.Error.WriteLine($"warning: UpdateActionState failed ({error})");
    }

    /// <summary>Fires exactly once when toggle_panel has been held for holdMs (either hand).</summary>
    public bool PollToggleLongPress(int holdMs)
    {
        if (!Available)
            return false;

        ReadDigital(_toggleAction, OpenVR.k_ulInvalidInputValueHandle, out var pressed, out _);
        var now = _clock.Elapsed.TotalMilliseconds;

        if (!pressed)
        {
            _togglePressStartMs = -1;
            _toggleFired = false;
            return false;
        }
        if (_togglePressStartMs < 0)
            _togglePressStartMs = now;
        if (_toggleFired || now - _togglePressStartMs < holdMs)
            return false;

        _toggleFired = true;
        return true;
    }

    /// <summary>True on the rising edge of interact on the given hand.</summary>
    public bool PollInteractClick(bool leftHand)
    {
        if (!Available)
            return false;
        ReadDigital(_interactAction, leftHand ? _leftHandSource : _rightHandSource, out var pressed, out var changed);
        return pressed && changed;
    }

    /// <summary>True on the rising edge of decrement on the given hand.</summary>
    public bool PollDecrementClick(bool leftHand)
    {
        if (!Available)
            return false;
        ReadDigital(_decrementAction, leftHand ? _leftHandSource : _rightHandSource, out var pressed, out var changed);
        return pressed && changed;
    }

    /// <summary>
    /// Registers a temporary app manifest for this session so SteamVR associates the
    /// process with our app key; that is what makes bindings persist between runs.
    /// </summary>
    private static void RegisterApplication()
    {
        var applications = OpenVR.Applications;
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "app.vrmanifest");
        if (applications == null || !File.Exists(manifestPath))
            return;

        var error = applications.AddApplicationManifest(manifestPath, true);
        if (error != EVRApplicationError.None)
        {
            Console.Error.WriteLine($"warning: AddApplicationManifest failed ({error})");
            return;
        }
        error = applications.IdentifyApplication((uint)Environment.ProcessId, AppKey);
        if (error != EVRApplicationError.None)
            Console.Error.WriteLine($"warning: IdentifyApplication failed ({error})");
    }

    private static bool TryGetHandle(EVRInputError error, string what)
    {
        if (error == EVRInputError.None)
            return true;
        Console.Error.WriteLine($"warning: could not get {what} ({error}); button input disabled");
        return false;
    }

    private static void ReadDigital(ulong action, ulong restrictToDevice, out bool state, out bool changed)
    {
        var data = new InputDigitalActionData_t();
        var error = OpenVR.Input.GetDigitalActionData(
            action, ref data, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), restrictToDevice);
        if (error != EVRInputError.None || !data.bActive)
        {
            state = false;
            changed = false;
            return;
        }
        state = data.bState;
        changed = data.bChanged;
    }
}
