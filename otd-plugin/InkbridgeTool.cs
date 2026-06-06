using System.Collections.Generic;
using System.Reflection;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.DependencyInjection;

namespace Inkbridge
{
    /// <summary>
    /// Resident plugin that registers the inkbridge device hub with OTD so our
    /// network pen source is enumerated like any USB tablet.
    ///
    /// OTD only exposes <see cref="IDriver"/> and IDriverDaemon to plugin
    /// (<c>[Resolved]</c>) injection — NOT ICompositeDeviceHub directly. The concrete
    /// Driver does expose a public ICompositeDeviceHub CompositeDeviceHub property, so
    /// we reach it by reflection (avoiding a compile-time dependency on OTD core).
    /// See docs/feasibility.md.
    /// </summary>
    [PluginName("inkbridge")]
    public class InkbridgeTool : ITool
    {
        [Resolved] public IDriver? Driver { set; get; }

        // Touch-mode dropdown values. OTD 0.6.7's GeneratedControls cannot render a raw enum
        // property (throws NotSupportedException), so the choice is a *string* validated against
        // this member — that's how OTD builds a dropdown. Mapped to the TouchMode enum below.
        private const string ModeDisabled = "Disabled";
        private const string ModeDirect = "Direct touch";
        private const string ModeGesture = "Gesture";

        // Must be STATIC — OTD requires the PropertyValidated source member to be static
        // ("Validation method must be static"), else the dropdown values fail to populate.
        public static IEnumerable<string> TouchModeChoices => new[] { ModeDisabled, ModeDirect, ModeGesture };

        /// <summary>
        /// Touch passthrough mode (dropdown). Disabled = the plugin never connects to the daemon's
        /// touch port (:9294), so the rMPP touchscreen keeps driving its own UI. Direct touch =
        /// genuine Windows multitouch (pinch-zoom). Gesture = multi-finger gestures → keystrokes.
        /// See docs/touch-modes.md. The daemon grabs the touchscreen only while a client is
        /// connected, so switching away from Disabled is what actually routes fingers to Windows.
        /// </summary>
        [Property("Touch mode"), PropertyValidated(nameof(TouchModeChoices)), DefaultPropertyValue(ModeDisabled)]
        public string TouchModeName { get; set; } = ModeDisabled;

        /// <summary>
        /// When unchecked (default), touch only flows while the on-device inkbridge AppLoad app is
        /// open — leaving the app stops touch so the rMPP works normally. Check to keep touch active
        /// regardless (the rMPP becomes a permanent Windows touch surface).
        /// </summary>
        [BooleanProperty("Touch without app open",
            "Stream touch even when the on-device inkbridge app is closed (rMPP touch is then always sent to Windows).")]
        public bool TouchWithoutApp { get; set; }

        /// <summary>
        /// Pen-priority palm rejection: while the pen (or eraser) is in range, touch is suppressed so
        /// a palm resting on the screen while drawing doesn't register. On by default.
        /// </summary>
        [BooleanProperty("Palm rejection",
            "Ignore touch while the pen is in range, so a resting palm doesn't register while drawing."),
            DefaultPropertyValue(true)]
        public bool PalmRejection { get; set; } = true;

        /// <summary>
        /// In Direct touch mode, also recognize discrete multi-finger taps on top of real
        /// multitouch: 2-finger tap → Undo, 3-finger tap → Redo. Pinch-zoom / pan stay native
        /// (Windows handles them). No effect in Gesture mode (which already does taps).
        /// </summary>
        [BooleanProperty("Tap gestures in Direct touch",
            "While Direct touch is active, 2-finger tap = Undo and 3-finger tap = Redo (pinch/pan stay native)."),
            DefaultPropertyValue(true)]
        public bool TapGesturesInDirect { get; set; } = true;

        // Touch rotation choices. "Follow OTD area" reuses the same rotation you set on the pen's
        // area (recommended — touch then aligns like the pen). "Follow device" uses the tablet's
        // auto-detected orientation; the fixed values lock the mapping to your monitor.
        private const string RotOtd = "Follow OTD area";
        private const string RotFollow = "Follow device";
        private const string Rot0 = "0°";
        private const string Rot90 = "90°";
        private const string Rot180 = "180°";
        private const string Rot270 = "270°";

        public static IEnumerable<string> TouchRotationChoices =>
            new[] { RotOtd, RotFollow, Rot0, Rot90, Rot180, Rot270 };

        /// <summary>
        /// Touch coordinate rotation. "Follow OTD area" (default) uses the same rotation configured
        /// on the pen's tablet area, so touch lines up like the pen. "Follow device" rotates with
        /// the tablet's detected orientation; pick a fixed angle to lock the mapping to your monitor.
        /// </summary>
        [Property("Touch rotation"), PropertyValidated(nameof(TouchRotationChoices)), DefaultPropertyValue(RotOtd)]
        public string TouchRotationName { get; set; } = RotOtd;

        // Which monitor touch maps onto. "Follow OTD area" uses OTD's configured Display area (same
        // screen + region as the pen). The explicit choices pin touch to a whole monitor using its
        // real bounds — handy on multi-monitor / mixed-DPI setups where the OTD area lands on the
        // wrong screen. Monitors are numbered left-to-right.
        private const string MonOtd = "Follow OTD area";
        private const string MonPrimary = "Primary";
        private const string Mon1 = "Monitor 1";
        private const string Mon2 = "Monitor 2";
        private const string Mon3 = "Monitor 3";
        private const string Mon4 = "Monitor 4";

        public static IEnumerable<string> TouchMonitorChoices =>
            new[] { MonOtd, MonPrimary, Mon1, Mon2, Mon3, Mon4 };

        /// <summary>
        /// Target monitor for Direct touch. "Follow OTD area" matches the pen's screen/region; the
        /// explicit choices map the full touch surface onto one monitor (left-to-right numbering).
        /// </summary>
        [Property("Touch monitor"), PropertyValidated(nameof(TouchMonitorChoices)), DefaultPropertyValue(MonOtd)]
        public string TouchMonitorName { get; set; } = MonOtd;

        // The hub is registered exactly ONCE per daemon process. OTD constructs/initializes
        // (and disposes) the tool on every settings-apply; if each Initialize connected a
        // fresh hub we'd accumulate multiple identical endpoints -> OTD logs "More than 1
        // matching device" and opens competing TcpSources that abort each other in a
        // reconnect storm, so the bound stream never stabilises and the cursor never moves.
        // A static singleton makes registration idempotent regardless of how many times
        // (or how many instances) Initialize runs.
        private static readonly object _gate = new();
        private static InkbridgeHub? _hub;

        public bool Initialize()
        {
            if (Driver == null)
            {
                Log.Write("Inkbridge", "IDriver was not injected — cannot register hub", LogLevel.Error);
                return false;
            }

            // Start PC-side telemetry once: pushes connection/latency/rate + the active-area config
            // to the on-device visualizer (replaces the standalone companion app). Idempotent.
            InkbridgeTelemetry.Start();

            // Apply the selected touch mode. OTD reconstructs this tool on every settings-apply, so
            // this re-asserts the dropdown value; TouchService is a static singleton and no-ops when
            // the mode is unchanged (mirrors the static _hub idempotency below).
            TouchMode mode = TouchModeName switch
            {
                ModeDirect => TouchMode.DirectTouch,
                ModeGesture => TouchMode.Gesture,
                _ => TouchMode.Disabled,
            };
            int rotation = TouchRotationName switch
            {
                Rot0 => 0,
                Rot90 => 1,
                Rot180 => 2,
                Rot270 => 3,
                RotFollow => -1,
                _ => -2, // Follow OTD area (default)
            };
            int monitor = TouchMonitorName switch
            {
                MonPrimary => -2, // MonitorList.PrimarySelector
                Mon1 => 0,
                Mon2 => 1,
                Mon3 => 2,
                Mon4 => 3,
                _ => -1, // Follow OTD area (default)
            };
            TouchService.Instance.SetMode(mode, new TouchOptions(
                AlwaysOn: TouchWithoutApp,
                PalmReject: PalmRejection,
                Rotation: rotation,
                Monitor: monitor,
                TapGestures: TapGesturesInDirect));

            lock (_gate)
            {
                if (_hub != null)
                {
                    // CRITICAL: do NOT call Driver.Detect() here. OTD's DriverDaemon.SetSettings
                    // binds the OutputMode to the current InputDeviceTree FIRST, then initializes
                    // tools. A Detect() at this point rebuilds the tree with new InputDevice
                    // instances *after* the output mode was bound to the old tree, so the new
                    // (reading) device has OutputMode == null and every report is dropped
                    // (OutputMode?.Read no-ops) — the cursor never moves. On re-apply we must
                    // leave the already-registered endpoint untouched so the daemon's freshly
                    // assigned OutputMode stays attached to the device that is actually reading.
                    Log.Write("Inkbridge", "Device hub already registered; leaving existing endpoint (no re-detect)");
                    return true;
                }

                var hub = Driver.GetType()
                    .GetProperty("CompositeDeviceHub", BindingFlags.Public | BindingFlags.Instance)?
                    .GetValue(Driver) as ICompositeDeviceHub;

                if (hub == null)
                {
                    Log.Write("Inkbridge", "Could not reach Driver.CompositeDeviceHub — cannot register hub", LogLevel.Error);
                    return false;
                }

                _hub = new InkbridgeHub();
                hub.ConnectDeviceHub(_hub);
                Log.Write("Inkbridge", "Registered inkbridge device hub with OTD");
            }

            // First registration only: populate Driver.InputDevices with our endpoint so a
            // subsequent settings-apply can bind the OutputMode to it. (We deliberately accept
            // that this first apply leaves the endpoint without an output mode — the NEXT apply
            // binds it, because SetSettings assigns OutputMode to already-detected devices before
            // re-initializing this tool, which now no-ops. Hence: apply settings twice.)
            Driver.Detect();
            return true;
        }

        public void Dispose()
        {
            // Intentionally do NOT disconnect the hub here. OTD disposes and re-creates the
            // tool on every settings-apply; disconnecting/reconnecting per apply is exactly
            // the churn that produced duplicate, competing endpoints. The single static hub
            // stays connected for the daemon's lifetime (cleared when the process exits).
        }
    }
}
