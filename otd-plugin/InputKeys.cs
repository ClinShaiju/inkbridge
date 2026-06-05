using System;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>Minimal SendInput keyboard helper for firing modifier+key combos (e.g. Ctrl+Z).</summary>
    internal static class InputKeys
    {
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_Y = 0x59;
        public const ushort VK_Z = 0x5A;

        /// <summary>Press modifier+key then release both (e.g. CtrlCombo(VK_Z) = Ctrl+Z).</summary>
        public static void CtrlCombo(ushort vk)
        {
            Key(VK_CONTROL, false);
            Key(vk, false);
            Key(vk, true);
            Key(VK_CONTROL, true);
        }

        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static void Key(ushort vk, bool up)
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
            };
            var arr = new[] { inp };
            if (SendInput(1, arr, Marshal.SizeOf<INPUT>()) != 1)
                Log.Write("Inkbridge", $"SendInput failed (err {Marshal.GetLastWin32Error()})", LogLevel.Debug);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

        // The Win32 INPUT union is sized by its LARGEST member (MOUSEINPUT). If we declare only
        // KEYBDINPUT, sizeof(INPUT) comes out 8 bytes short on x64 (32 vs 40), and SendInput then
        // rejects every call with ERROR_INVALID_PARAMETER (returns 0) — the keystroke silently
        // never lands. So MOUSEINPUT is included purely to make the union the correct size.
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
    }
}
