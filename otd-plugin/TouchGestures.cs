using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Gesture consumer: recognizes a small vocabulary of multi-finger gestures from inkbridge
    /// touch frames and emits ordinary keystrokes / mouse-wheel via SendInput. Because the output
    /// is plain input events it works in every app, at the cost of being discrete rather than analog.
    ///
    /// Vocabulary (defaults; tune the thresholds on-device):
    ///   • 2-finger tap            → Ctrl+Z  (undo)
    ///   • 3-finger tap            → Ctrl+Y  (redo)
    ///   • 2-finger pinch in/out   → Ctrl + wheel  (zoom)        [skipped in tap-only mode]
    ///   • 2-finger drag (vert)    → wheel scroll                [skipped in tap-only mode]
    ///
    /// An "episode" runs from the first finger down to the last finger up. A tap is an episode that
    /// is short and in which no finger moved far FROM ITS OWN landing point — measuring per-contact
    /// (not centroid) is essential, because fingers land a few ms apart and the centroid would jump.
    /// </summary>
    internal sealed class TouchGestures : ITouchConsumer
    {
        private const int Slots = TouchPacket.Slots;

        // Thresholds in raw touch units (grid 2064 × 2832 over ~179 × 239 mm → ~11.5 units/mm).
        private const double PinchStep = 70;    // spread change per emitted zoom tick
        private const double ScrollStep = 90;   // centroid vertical move per emitted wheel tick
        private const double TapMoveTol = 130;  // max per-finger travel still counted as a tap (~11mm)
        private const long TapMaxMs = 450;      // max episode duration still counted as a tap

        // When true, only fire discrete multi-finger taps and skip continuous pinch/scroll (Direct
        // touch already gives native pinch/pan; re-emitting would double up).
        private readonly bool _tapOnly;

        public TouchGestures(bool tapOnly) => _tapOnly = tapOnly;

        // Per-slot contact tracking.
        private readonly bool[] _down = new bool[Slots];
        private readonly double[] _sx = new double[Slots];
        private readonly double[] _sy = new double[Slots];

        // Episode state.
        private bool _inEpisode;
        private int _maxCount;
        private long _startMs;
        private double _maxMove;        // max displacement of any single finger from its landing point
        private bool _continuousFired;  // a pinch/scroll tick fired → not a tap

        // Continuous (2-finger) baselines, reset each time a tick is emitted.
        private double _baseSpread;
        private double _baseCy;
        private bool _haveBaseline;

        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public void OnFrame(in TouchPacket frame)
        {
            int count = 0;
            double ax = 0, ay = 0, bx = 0, by = 0; // up to two points, for pinch/scroll
            int taken = 0;
            double cySum = 0;

            for (int i = 0; i < Slots; i++)
            {
                var c = frame.Contacts[i];
                if (c.Active)
                {
                    count++;
                    if (!_down[i])
                    {
                        _down[i] = true; _sx[i] = c.X; _sy[i] = c.Y; // finger just landed
                    }
                    else
                    {
                        double d = Dist(c.X, c.Y, _sx[i], _sy[i]);
                        if (d > _maxMove) _maxMove = d;
                    }
                    cySum += c.Y;
                    if (taken == 0) { ax = c.X; ay = c.Y; taken = 1; }
                    else if (taken == 1) { bx = c.X; by = c.Y; taken = 2; }
                }
                else
                {
                    _down[i] = false;
                }
            }

            if (count == 0) { EndEpisode(); return; }

            if (!_inEpisode)
            {
                _inEpisode = true;
                _maxCount = count;
                _startMs = _clock.ElapsedMilliseconds;
                _maxMove = 0;
                _continuousFired = false;
                _haveBaseline = false;
            }
            _maxCount = Math.Max(_maxCount, count);

            if (!_tapOnly && count == 2)
            {
                double cy = cySum / count;
                double spread = Dist(ax, ay, bx, by);
                if (!_haveBaseline)
                {
                    _baseSpread = spread; _baseCy = cy; _haveBaseline = true;
                }
                else
                {
                    double dSpread = spread - _baseSpread;
                    if (Math.Abs(dSpread) >= PinchStep)
                    {
                        ZoomTicks((int)(dSpread / PinchStep)); // +ve = fingers apart = zoom in
                        _baseSpread = spread; _baseCy = cy; _continuousFired = true;
                    }
                    else
                    {
                        double dCy = cy - _baseCy;
                        if (Math.Abs(dCy) >= ScrollStep)
                        {
                            ScrollTicks(-(int)(dCy / ScrollStep)); // finger down = scroll down
                            _baseCy = cy; _baseSpread = spread; _continuousFired = true;
                        }
                    }
                }
            }
            else
            {
                _haveBaseline = false;
            }
        }

        public void Reset()
        {
            for (int i = 0; i < Slots; i++) _down[i] = false;
            _inEpisode = false;
            _haveBaseline = false;
        }

        private void EndEpisode()
        {
            if (!_inEpisode) return;
            _inEpisode = false;
            _haveBaseline = false;

            long dur = _clock.ElapsedMilliseconds - _startMs;
            bool wasTap = !_continuousFired && dur <= TapMaxMs && _maxMove <= TapMoveTol;

            if (_maxCount >= 2)
                Log.Write("Inkbridge",
                    $"touch episode: fingers={_maxCount} dur={dur}ms move={_maxMove:F0} tap={wasTap}",
                    LogLevel.Debug);

            if (!wasTap) return;
            if (_maxCount == 2) { KeyCombo(VK_CONTROL, VK_Z); Log.Write("Inkbridge", "gesture: 2-finger tap → Undo"); }
            else if (_maxCount == 3) { KeyCombo(VK_CONTROL, VK_Y); Log.Write("Inkbridge", "gesture: 3-finger tap → Redo"); }
        }

        private static double Dist(double x0, double y0, double x1, double y1)
        {
            double dx = x0 - x1, dy = y0 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ── emit helpers ──

        private void ZoomTicks(int ticks)
        {
            if (ticks == 0) return;
            ticks = Math.Clamp(ticks, -5, 5);
            SendKey(VK_CONTROL, false);
            Wheel(ticks * WHEEL_DELTA);
            SendKey(VK_CONTROL, true);
        }

        private void ScrollTicks(int ticks)
        {
            if (ticks == 0) return;
            ticks = Math.Clamp(ticks, -5, 5);
            Wheel(ticks * WHEEL_DELTA);
        }

        private static void KeyCombo(ushort modifier, ushort key)
        {
            SendKey(modifier, false);
            SendKey(key, false);
            SendKey(key, true);
            SendKey(modifier, true);
        }

        // ── Win32 SendInput interop ──

        private const int INPUT_MOUSE = 0;
        private const int INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const int WHEEL_DELTA = 120;

        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_Y = 0x59;
        private const ushort VK_Z = 0x5A;

        private static void SendKey(ushort vk, bool up)
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
                },
            };
            Send(inp);
        }

        private static void Wheel(int delta)
        {
            var inp = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL },
                },
            };
            Send(inp);
        }

        private static void Send(INPUT inp)
        {
            var arr = new[] { inp };
            uint sent = SendInput(1, arr, Marshal.SizeOf<INPUT>());
            if (sent != 1)
                Log.Write("Inkbridge", $"SendInput failed (err {Marshal.GetLastWin32Error()})", LogLevel.Debug);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public InputUnion U; }

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
