using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Resolves the monitor rectangle that touch should map onto — OTD's configured Display area
    /// for the active profile, so touch lands on the same screen as the pen. OTD stores the
    /// Display area as a centre point (X,Y) plus Width/Height in virtual-desktop pixels; we convert
    /// to a top-left rect. Cached and refreshed at most every couple of seconds (cheap; touch is
    /// hot). Falls back to "unavailable" (caller uses the primary monitor) if settings.json can't
    /// be read — e.g. before the user has saved a profile.
    /// </summary>
    internal static class TouchTarget
    {
        private static readonly object _gate = new();
        private static DateTime _nextRead = DateTime.MinValue;
        private static bool _have;
        private static int _left, _top, _width, _height;
        private static bool _haveRot;
        private static int _rotationIdx; // 0/1/2/3 = 0/90/180/270°, from OTD's Tablet.Rotation
        private static bool _haveXform;
        private static Matrix3x2 _xform; // touch-raw grid → Win32 virtual-desktop px (OTD area→display)

        // Physical digitizer surface in millimetres — the area the touch grid (TouchPacket.MaxX/MaxY)
        // and the pen digitizer both cover. MUST match tablet-spec.json Specifications.Digitizer
        // Width/Height; OTD stores the Tablet area in these same mm units.
        private const double SurfaceWidthMm = 179.0;
        private const double SurfaceHeightMm = 239.0;

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenTabletDriver", "settings.json");

        public static bool TryGet(out int left, out int top, out int width, out int height)
        {
            lock (_gate)
            {
                if (DateTime.UtcNow >= _nextRead)
                {
                    _nextRead = DateTime.UtcNow.AddSeconds(2);
                    Refresh();
                }
                left = _left; top = _top; width = _width; height = _height;
                return _have;
            }
        }

        /// <summary>
        /// OTD's configured tablet-area rotation as a 0/1/2/3 index (0/90/180/270°), so touch can
        /// follow the same rotation the pen uses. False if unreadable.
        /// </summary>
        public static bool TryGetRotation(out int index)
        {
            lock (_gate)
            {
                if (DateTime.UtcNow >= _nextRead)
                {
                    _nextRead = DateTime.UtcNow.AddSeconds(2);
                    Refresh();
                }
                index = _rotationIdx;
                return _haveRot;
            }
        }

        /// <summary>
        /// The full OTD tablet-area → display transform, mapping a raw touch-grid point
        /// (0..TouchPacket.MaxX, 0..MaxY) straight to Win32 virtual-desktop pixels — cropping to the
        /// configured Tablet area and applying its rotation exactly as OTD does for the pen. Also
        /// returns the OTD Display rect (Win32 px) so callers can clamp to its edges. False if
        /// settings.json (Display + Tablet areas) can't be read.
        /// </summary>
        public static bool TryGetTransform(out Matrix3x2 transform, out int left, out int top, out int width, out int height)
        {
            lock (_gate)
            {
                if (DateTime.UtcNow >= _nextRead)
                {
                    _nextRead = DateTime.UtcNow.AddSeconds(2);
                    Refresh();
                }
                transform = _xform;
                left = _left; top = _top; width = _width; height = _height;
                return _haveXform;
            }
        }

        private static void Refresh()
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                var abs = doc.RootElement.GetProperty("Profiles")[0].GetProperty("AbsoluteModeSettings");
                var d = abs.GetProperty("Display");
                double w = d.GetProperty("Width").GetDouble();
                double h = d.GetProperty("Height").GetDouble();
                double cx = d.GetProperty("X").GetDouble();
                double cy = d.GetProperty("Y").GetDouble();
                // OTD stores Display coordinates with the origin at the TOP-LEFT of the virtual
                // desktop's bounding box (so the left/top-most monitor sits at 0,0), NOT at the
                // Win32 primary origin. InjectTouchInput wants Win32 virtual-screen pixels, where
                // the desktop origin is SM_X/YVIRTUALSCREEN (negative for monitors left/above the
                // primary). Convert by adding that origin — this is what makes "Follow OTD area"
                // land on the same monitor as the pen (e.g. OTD X=2880 → physical 0 = primary).
                int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                if (w > 0 && h > 0)
                {
                    _left = (int)Math.Round(cx - w / 2.0) + vx;
                    _top = (int)Math.Round(cy - h / 2.0) + vy;
                    _width = (int)Math.Round(w);
                    _height = (int)Math.Round(h);
                    _have = true;
                }
                else { _have = false; }

                // OTD stores the tablet area in millimetres: centre (X,Y), size (Width,Height) and
                // Rotation in degrees (0/90/180/270). Width/Height are in the area's OWN (rotated)
                // frame, so the crop and rotation are coupled — only the full affine transform maps
                // it correctly.
                var t = abs.GetProperty("Tablet");
                double rot = t.TryGetProperty("Rotation", out var r) ? r.GetDouble() : 0;
                _rotationIdx = ((int)Math.Round(rot / 90.0) % 4 + 4) % 4;
                _haveRot = true;

                double tw = t.GetProperty("Width").GetDouble();
                double th = t.GetProperty("Height").GetDouble();
                double tcx = t.GetProperty("X").GetDouble();
                double tcy = t.GetProperty("Y").GetDouble();
                if (_have && tw > 0 && th > 0)
                {
                    // Mirror OTD's AbsoluteOutputMode transform (raw → mm → tablet-area-relative →
                    // rotate → scale to display → display centre), so touch is cropped and rotated
                    // exactly like the pen. The only difference from the pen is the first scale: we
                    // start from the touch grid (MaxX/MaxY) rather than the pen digitizer, both of
                    // which cover the same physical surface (SurfaceWidthMm × SurfaceHeightMm).
                    var m = Matrix3x2.CreateScale(
                        (float)(SurfaceWidthMm / TouchPacket.MaxX),
                        (float)(SurfaceHeightMm / TouchPacket.MaxY));
                    m *= Matrix3x2.CreateTranslation((float)-tcx, (float)-tcy);
                    m *= Matrix3x2.CreateRotation((float)(-rot * Math.PI / 180.0));
                    m *= Matrix3x2.CreateScale((float)(w / tw), (float)(h / th));
                    m *= Matrix3x2.CreateTranslation((float)(cx + vx), (float)(cy + vy));
                    _xform = m;
                    _haveXform = true;
                }
                else { _haveXform = false; }
            }
            catch (Exception e)
            {
                _have = false;
                _haveRot = false;
                _haveXform = false;
                Log.Write("Inkbridge", $"touch target read failed: {e.Message}", LogLevel.Debug);
            }
        }

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
