using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Inkbridge
{
    /// <summary>
    /// Enumerates the monitors as the OTD daemon process sees them (EnumDisplayMonitors), so the
    /// rects are in exactly the same coordinate space InjectTouchInput consumes — no DPI guesswork.
    /// Used when the user pins touch to a specific monitor instead of "Follow OTD area".
    /// Monitors are numbered left-to-right (by physical left edge); index 0 = leftmost.
    /// Cached and refreshed at most every couple of seconds.
    /// </summary>
    internal static class MonitorList
    {
        private readonly struct Mon
        {
            public readonly int L, T, R, B;
            public readonly bool Primary;
            public Mon(int l, int t, int r, int b, bool primary) { L = l; T = t; R = r; B = b; Primary = primary; }
        }

        private static readonly object _gate = new();
        private static DateTime _nextRead = DateTime.MinValue;
        private static List<Mon> _mons = new();

        /// <summary>Target rect for a monitor selector. -2 = primary; else 0-based left-to-right index.</summary>
        public static bool TryGet(int selector, out int left, out int top, out int width, out int height)
        {
            left = top = width = height = 0;
            lock (_gate)
            {
                if (DateTime.UtcNow >= _nextRead)
                {
                    _nextRead = DateTime.UtcNow.AddSeconds(2);
                    Refresh();
                }
                if (_mons.Count == 0) return false;

                Mon m;
                if (selector == PrimarySelector)
                {
                    int idx = _mons.FindIndex(x => x.Primary);
                    m = idx >= 0 ? _mons[idx] : _mons[0];
                }
                else
                {
                    if (selector < 0 || selector >= _mons.Count) return false;
                    m = _mons[selector];
                }
                left = m.L; top = m.T; width = m.R - m.L; height = m.B - m.T;
                return width > 0 && height > 0;
            }
        }

        public const int PrimarySelector = -2;

        private static void Refresh()
        {
            var found = new List<Mon>();
            MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi))
                {
                    bool primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    found.Add(new Mon(mi.rcMonitor.left, mi.rcMonitor.top,
                        mi.rcMonitor.right, mi.rcMonitor.bottom, primary));
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            found.Sort((a, b) => a.L != b.L ? a.L.CompareTo(b.L) : a.T.CompareTo(b.T));
            _mons = found;
        }

        private const uint MONITORINFOF_PRIMARY = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    }
}
