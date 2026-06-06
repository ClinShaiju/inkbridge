using System;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Direct-touch consumer: turns inkbridge touch frames into genuine Windows multitouch via
    /// the Win32 pointer-injection API (InitializeTouchInjection / InjectTouchInput). The contacts
    /// arrive as real WM_POINTER touch input, so the DWM synthesizes pinch-zoom / two-finger pan /
    /// rotate for touch-aware apps — i.e. the rMPP behaves like an external touchscreen.
    ///
    /// OTD's own output pipeline can't do this (it is single-pointer); this consumer bypasses it and
    /// calls user32 directly. In the default "Follow OTD area" configuration it reuses OTD's exact
    /// tablet-area → display affine transform, so touch is cropped to the configured Tablet area and
    /// rotated identically to the pen. Explicit monitor/rotation overrides instead stretch the whole
    /// touch grid (2064×2832, portrait native) onto the chosen monitor, rotated by the selected angle.
    /// </summary>
    internal sealed class TouchInjector : ITouchConsumer
    {
        private const int MaxContacts = TouchPacket.Slots;

        private static bool _initialized;
        private static readonly object _initGate = new();

        // Rotation source: -2 = follow OTD's configured area rotation, -1 = follow the device's
        // reported orientation, else 0/1/2/3 = 0/90/180/270° CW fixed override.
        private readonly int _rotation;
        // Target monitor: -1 = follow OTD's configured Display area, -2 = primary, else 0-based
        // left-to-right monitor index.
        private readonly int _monitor;

        public TouchInjector(int rotation, int monitor) { _rotation = rotation; _monitor = monitor; }

        // Per-slot: was this contact down on the previous frame? Drives DOWN/UPDATE/UP.
        private readonly bool[] _wasDown = new bool[MaxContacts];
        // Last injected pixel position per slot, so Reset can issue UP at a valid location.
        private readonly int[] _lastX = new int[MaxContacts];
        private readonly int[] _lastY = new int[MaxContacts];

        // Reused so we don't allocate per frame on the hot path.
        private readonly POINTER_TOUCH_INFO[] _scratch = new POINTER_TOUCH_INFO[MaxContacts];

        public void OnFrame(in TouchPacket frame)
        {
            if (!EnsureInitialized())
                return;

            // Default ("Follow OTD area" for both rotation and monitor): map through OTD's exact
            // tablet-area → display transform, so touch is cropped to the configured Tablet area and
            // rotated like the pen. Any explicit monitor/rotation override drops to the whole-surface
            // path below (those choices are defined as mapping the full touch grid onto a monitor).
            Matrix3x2 xform = default;
            int oLeft = 0, oTop = 0, oW = 0, oH = 0;
            bool useOtd = _monitor == -1 && _rotation == -2
                && TouchTarget.TryGetTransform(out xform, out oLeft, out oTop, out oW, out oH)
                && oW > 0 && oH > 0;

            // Whole-surface path: resolve the target rect and rotation up front.
            int tLeft = 0, tTop = 0, tW = 0, tH = 0;
            byte orientation = 0;
            if (!useOtd)
            {
                // Resolve the target rect (virtual-desktop pixels, same coordinate space as injection):
                //  _monitor == -1 → OTD's configured Display area (matches the pen's screen)
                //  else           → a specific monitor by selector (primary or 0-based index)
                bool haveRect = _monitor == -1
                    ? TouchTarget.TryGet(out tLeft, out tTop, out tW, out tH)
                    : MonitorList.TryGet(_monitor, out tLeft, out tTop, out tW, out tH);
                if (!haveRect)
                {
                    // Fall back to the primary monitor's bounds.
                    tLeft = 0; tTop = 0;
                    tW = GetSystemMetrics(SM_CXSCREEN);
                    tH = GetSystemMetrics(SM_CYSCREEN);
                }
                if (tW <= 0 || tH <= 0)
                    return;

                // Resolve rotation: OTD area rotation (-2), device orientation (-1), or fixed (0..3).
                int rot = _rotation;
                if (rot == -2 && !TouchTarget.TryGetRotation(out rot))
                    rot = -1; // OTD rotation unreadable → fall back to device orientation
                orientation = (byte)(rot >= 0 ? rot : frame.Orientation);
            }

            int n = 0;
            bool anything = false;
            for (int i = 0; i < MaxContacts; i++)
            {
                var c = frame.Contacts[i];
                bool down = c.Active;
                if (!down && !_wasDown[i])
                    continue; // empty slot, nothing to report

                anything = true;
                int px, py;
                if (useOtd)
                    MapVia(xform, c.X, c.Y, oLeft, oTop, oW, oH, out px, out py);
                else
                    MapToScreen(c.X, c.Y, orientation, tLeft, tTop, tW, tH, out px, out py);

                uint flags;
                if (down && !_wasDown[i])
                    flags = POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                else if (down)
                    flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
                else
                    flags = POINTER_FLAG_UP; // was down, now lifted

                uint pressure = c.Pressure > 0 ? (uint)(c.Pressure * 4) : 512u; // 0..255 → 0..1024
                int half = Math.Max(2, c.Major / 2);

                _scratch[n] = new POINTER_TOUCH_INFO
                {
                    pointerInfo = new POINTER_INFO
                    {
                        pointerType = PT_TOUCH,
                        pointerId = (uint)i, // slot = stable contact id for its lifetime
                        pointerFlags = flags,
                        ptPixelLocation = new POINT { X = px, Y = py },
                    },
                    touchFlags = TOUCH_FLAG_NONE,
                    touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_PRESSURE,
                    rcContact = new RECT
                    {
                        left = px - half, top = py - half, right = px + half, bottom = py + half,
                    },
                    orientation = 90,
                    pressure = pressure,
                };
                n++;
                _wasDown[i] = down;
                _lastX[i] = px;
                _lastY[i] = py;
            }

            if (!anything || n == 0)
                return;

            if (!InjectTouchInput((uint)n, _scratch))
            {
                int err = Marshal.GetLastWin32Error();
                // Common, recoverable: a bad transition (e.g. UP for a contact Windows lost).
                // Reset our view so the next touch-down re-establishes contacts cleanly.
                Log.Write("Inkbridge", $"InjectTouchInput failed (err {err}); resetting contacts", LogLevel.Debug);
                Reset();
            }
        }

        /// <summary>
        /// Cancel every contact Windows currently has down (POINTER_FLAG_UP | CANCELED) so the
        /// interaction is discarded rather than completed as a click/gesture. Used when a withheld
        /// multi-finger touch turns out to be a command tap — Windows then produces no click or
        /// right-click from the contacts we'd injected. Best-effort; clears local state.
        /// </summary>
        public void CancelAll()
        {
            if (_initialized)
            {
                int n = 0;
                for (int i = 0; i < MaxContacts; i++)
                {
                    if (!_wasDown[i]) continue;
                    _scratch[n++] = new POINTER_TOUCH_INFO
                    {
                        pointerInfo = new POINTER_INFO
                        {
                            pointerType = PT_TOUCH,
                            pointerId = (uint)i,
                            pointerFlags = POINTER_FLAG_UP | POINTER_FLAG_CANCELED,
                            ptPixelLocation = new POINT { X = _lastX[i], Y = _lastY[i] },
                        },
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = TOUCH_FLAG_NONE,
                    };
                }
                if (n > 0)
                {
                    try { InjectTouchInput((uint)n, _scratch); } catch { }
                }
            }
            for (int i = 0; i < MaxContacts; i++)
                _wasDown[i] = false;
        }

        /// <summary>
        /// Release every contact Windows still thinks is down (mode switch / disconnect), so a
        /// touch point can't stick down. Injects POINTER_FLAG_UP for each held slot at its last
        /// position, then clears local state.
        /// </summary>
        public void Reset()
        {
            if (_initialized)
            {
                int n = 0;
                for (int i = 0; i < MaxContacts; i++)
                {
                    if (!_wasDown[i]) continue;
                    _scratch[n++] = new POINTER_TOUCH_INFO
                    {
                        pointerInfo = new POINTER_INFO
                        {
                            pointerType = PT_TOUCH,
                            pointerId = (uint)i,
                            pointerFlags = POINTER_FLAG_UP,
                            ptPixelLocation = new POINT { X = _lastX[i], Y = _lastY[i] },
                        },
                        touchFlags = TOUCH_FLAG_NONE,
                        touchMask = TOUCH_FLAG_NONE,
                    };
                }
                if (n > 0)
                {
                    try { InjectTouchInput((uint)n, _scratch); } catch { }
                }
            }
            for (int i = 0; i < MaxContacts; i++)
                _wasDown[i] = false;
        }

        private static bool EnsureInitialized()
        {
            if (_initialized) return true;
            lock (_initGate)
            {
                if (_initialized) return true;
                // TOUCH_FEEDBACK_NONE: no OS touch-visualization circles (clean passthrough).
                if (InitializeTouchInjection(MaxContacts, TOUCH_FEEDBACK_NONE))
                {
                    _initialized = true;
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    // ERROR_ALREADY_INITIALIZED would still let injection work; treat as ready.
                    Log.Write("Inkbridge", $"InitializeTouchInjection failed (err {err})", LogLevel.Warning);
                    _initialized = err == 0;
                }
                return _initialized;
            }
        }

        /// <summary>
        /// Map raw touch coords (portrait native: X 0..2064 short edge, Y 0..2832 long edge) into
        /// the target monitor rect (virtual-desktop pixels), rotating by orientation
        /// (0=portrait, 1/2/3 = 90/180/270° CW). Fills the rect (aspect stretched — fine for
        /// absolute touch). NOTE: 90/270 mapping should be confirmed on-device.
        /// </summary>
        private static void MapToScreen(ushort x, ushort y, byte orientation,
            int left, int top, int width, int height, out int px, out int py)
        {
            double nx = Math.Clamp(x / (double)TouchPacket.MaxX, 0.0, 1.0);
            double ny = Math.Clamp(y / (double)TouchPacket.MaxY, 0.0, 1.0);

            double u, v;
            switch (orientation & 0x03)
            {
                case 1: u = ny;       v = 1.0 - nx; break; // 90° CW
                case 2: u = 1.0 - nx; v = 1.0 - ny; break; // 180°
                case 3: u = 1.0 - ny; v = nx;       break; // 270° CW
                default: u = nx;      v = ny;       break; // 0° portrait native
            }

            px = left + (int)Math.Round(u * (width - 1));
            py = top + (int)Math.Round(v * (height - 1));
        }

        /// <summary>
        /// Map a raw touch-grid point through OTD's tablet-area → display transform (crop + rotation
        /// baked in), then clamp to the display rect so a touch outside the configured Tablet area
        /// sticks to the nearest edge instead of running off-screen — matching how the pen behaves
        /// outside its area.
        /// </summary>
        private static void MapVia(in Matrix3x2 m, ushort x, ushort y,
            int left, int top, int width, int height, out int px, out int py)
        {
            var p = Vector2.Transform(new Vector2(x, y), m);
            px = Math.Clamp((int)Math.Round(p.X), left, left + width - 1);
            py = Math.Clamp((int)Math.Round(p.Y), top, top + height - 1);
        }

        // ── Win32 pointer-injection interop ──

        private const uint PT_TOUCH = 0x00000002;

        private const uint TOUCH_FEEDBACK_NONE = 0x00000003;

        private const uint POINTER_FLAG_INRANGE = 0x00000002;
        private const uint POINTER_FLAG_INCONTACT = 0x00000004;
        private const uint POINTER_FLAG_CANCELED = 0x00008000;
        private const uint POINTER_FLAG_DOWN = 0x00010000;
        private const uint POINTER_FLAG_UPDATE = 0x00020000;
        private const uint POINTER_FLAG_UP = 0x00040000;

        private const uint TOUCH_FLAG_NONE = 0x00000000;
        private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
        private const uint TOUCH_MASK_PRESSURE = 0x00000004;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_INFO
        {
            public uint pointerType;
            public uint pointerId;
            public uint frameId;
            public uint pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation;
            public POINT ptHimetricLocation;
            public POINT ptPixelLocationRaw;
            public POINT ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int inputData;
            public uint dwKeyStates;
            public ulong PerformanceCount;
            public int ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_TOUCH_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint touchFlags;
            public uint touchMask;
            public RECT rcContact;
            public RECT rcContactRaw;
            public uint orientation;
            public uint pressure;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
