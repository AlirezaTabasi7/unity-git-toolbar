using System.Runtime.InteropServices;

namespace GitToolbar
{
    /// <summary>
    /// Reads whether Left Alt (Left Option on macOS) is held, straight from the OS.
    /// Unity has no public way to query modifier keys outside of GUI events.
    /// </summary>
    static class LeftAltKey
    {
#if UNITY_EDITOR_WIN
        const int k_VirtualKeyLeftAlt = 0xA4; // VK_LMENU

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int virtualKey);

        public static bool IsSupported => true;
        public static bool IsHeld => (GetAsyncKeyState(k_VirtualKeyLeftAlt) & 0x8000) != 0;
#elif UNITY_EDITOR_OSX
        const int k_CombinedSessionState = 0;     // kCGEventSourceStateCombinedSessionState
        const ulong k_LeftAlternateMask = 0x20;   // NX_DEVICELALTKEYMASK

        [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
        static extern ulong CGEventSourceFlagsState(int stateId);

        public static bool IsSupported => true;
        public static bool IsHeld => (CGEventSourceFlagsState(k_CombinedSessionState) & k_LeftAlternateMask) != 0;
#else
        public static bool IsSupported => false;
        public static bool IsHeld => false;
#endif
    }
}
