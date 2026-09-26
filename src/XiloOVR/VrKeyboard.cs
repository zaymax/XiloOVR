#nullable enable
using System.Text;
using Valve.VR;

namespace XiloOVR;

/// <summary>
/// The SteamVR keyboard boilerplate shared by the wrist panel and the settings tab.
/// The text-length limits live here so the open call and the read-back cannot drift
/// apart: GetKeyboardText counts UTF-8 bytes, not characters, so the read buffer
/// leaves room for 200 characters of Cyrillic/CJK.
/// </summary>
public static class VrKeyboard
{
    private const int MaxInputChars = 200;
    private const int ReadBufferBytes = 1024;

    /// <summary>Opens the keyboard attached to the overlay; returns false (and logs) on failure.</summary>
    public static bool Open(ulong overlayHandle, string description, string existing)
    {
        var error = OpenVR.Overlay.ShowKeyboardForOverlay(
            overlayHandle,
            (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
            (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
            0, description, MaxInputChars, existing, 0);
        if (error != EVROverlayError.None)
            Console.Error.WriteLine($"warning: could not open the VR keyboard: {error}");
        return error == EVROverlayError.None;
    }

    /// <summary>Reads the text after VREvent_KeyboardDone, trimmed.</summary>
    public static string ReadText()
    {
        var buffer = new StringBuilder(ReadBufferBytes);
        OpenVR.Overlay.GetKeyboardText(buffer, ReadBufferBytes);
        return buffer.ToString().Trim();
    }
}
