using System;
using System.IO;
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
                if (w > 0 && h > 0)
                {
                    // OTD stores Display coordinates with the origin at the TOP-LEFT of the virtual
                    // desktop's bounding box (so the left/top-most monitor sits at 0,0), NOT at the
                    // Win32 primary origin. InjectTouchInput wants Win32 virtual-screen pixels, where
                    // the desktop origin is SM_X/YVIRTUALSCREEN (negative for monitors left/above the
                    // primary). Convert by adding that origin — this is what makes "Follow OTD area"
                    // land on the same monitor as the pen (e.g. OTD X=2880 → physical 0 = primary).
                    int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                    int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                    _left = (int)Math.Round(cx - w / 2.0) + vx;
                    _top = (int)Math.Round(cy - h / 2.0) + vy;
                    _width = (int)Math.Round(w);
                    _height = (int)Math.Round(h);
                    _have = true;
                }
                else { _have = false; }

                // OTD stores the tablet-area rotation in degrees (0/90/180/270).
                var t = abs.GetProperty("Tablet");
                double rot = t.TryGetProperty("Rotation", out var r) ? r.GetDouble() : 0;
                _rotationIdx = ((int)Math.Round(rot / 90.0) % 4 + 4) % 4;
                _haveRot = true;
            }
            catch (Exception e)
            {
                _have = false;
                _haveRot = false;
                Log.Write("Inkbridge", $"touch target read failed: {e.Message}", LogLevel.Debug);
            }
        }

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
