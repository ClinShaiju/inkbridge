using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>Touch passthrough mode, chosen from the OTD "Touch mode" dropdown.</summary>
    public enum TouchMode
    {
        /// <summary>No touch. The plugin never subscribes to the touch channel, so the daemon never reads event3.</summary>
        Disabled,
        /// <summary>Genuine Windows multitouch via InjectTouchInput (pinch-zoom / pan / rotate).</summary>
        DirectTouch,
        /// <summary>Multi-finger gestures recognized PC-side → keystrokes / wheel via SendInput.</summary>
        Gesture,
    }

    /// <summary>Consumes decoded touch frames. One implementation per non-Disabled mode.</summary>
    internal interface ITouchConsumer
    {
        void OnFrame(in TouchPacket frame);
        /// <summary>Release all contacts / reset gesture state (mode switch or disconnect).</summary>
        void Reset();
    }

    /// <summary>User-tunable touch options, sent/applied per session.</summary>
    internal readonly record struct TouchOptions(
        bool AlwaysOn,    // stream even when the AppLoad app is closed (options byte bit0)
        bool PalmReject,  // suppress touch while the pen is in range (options byte bit1 = !this)
        int Rotation,     // -2 follow OTD area, -1 follow device, else 0/1/2/3 fixed
        int Monitor,      // -1 follow OTD area, -2 primary, else 0-based monitor index
        bool TapGestures  // in Direct mode, also fire 2/3-finger tap → undo/redo on top
    );

    /// <summary>
    /// Selects the touch consumer for the active <see cref="TouchMode"/> and registers it (plus the
    /// touch subscription + options) with the shared <see cref="ConnectionManager"/>, which owns the
    /// muxed connection and routes channel-2 frames to the consumer. A static singleton so OTD's
    /// per-settings-apply reconstruction of <see cref="InkbridgeTool"/> just re-asserts the mode
    /// (idempotent) — same pattern the device hub uses.
    ///
    /// Enabling a mode sends <c>sub touch</c> (the daemon then reads event3 and streams while the
    /// AppLoad app is open / always-on); Disabled sends <c>unsub touch</c> and the tablet's
    /// touchscreen returns to driving the stock UI.
    /// </summary>
    internal sealed class TouchService
    {
        public static readonly TouchService Instance = new();

        private readonly object _gate = new();
        private TouchMode _mode = TouchMode.Disabled;
        private TouchOptions _opts;
        private ITouchConsumer? _consumer;

        private TouchService() { }

        /// <summary>
        /// Apply the selected <paramref name="mode"/> + <paramref name="opts"/>. No-op if nothing
        /// changed; otherwise build the consumer for the mode and (un)subscribe touch via the shared
        /// connection. See <see cref="TouchOptions"/> for the rotation / monitor / palm / always-on
        /// fields.
        /// </summary>
        public void SetMode(TouchMode mode, TouchOptions opts)
        {
            lock (_gate)
            {
                bool unchanged = mode == _mode && opts == _opts
                    && (mode == TouchMode.Disabled || _consumer != null);
                if (unchanged)
                    return;

                _mode = mode;
                _opts = opts;

                if (mode == TouchMode.Disabled)
                {
                    _consumer = null;
                    ConnectionManager.Instance.SetTouch(false, opts, null);
                    Log.Write("Inkbridge", "Touch mode: Disabled");
                    return;
                }

                // With tap gestures, use the coordinator that withholds ambiguous multi-touch so a
                // 2/3-finger tap fires Undo/Redo without Windows' competing right-click — while
                // pinch/pan stay native and single-finger touch keeps zero latency. Otherwise plain.
                ITouchConsumer consumer = mode == TouchMode.DirectTouch
                    ? (opts.TapGestures
                        ? new DirectTouchWithTaps(opts.Rotation, opts.Monitor)
                        : new TouchInjector(opts.Rotation, opts.Monitor))
                    : new TouchGestures();

                _consumer = consumer;
                ConnectionManager.Instance.SetTouch(true, opts, consumer);
                Log.Write("Inkbridge", $"Touch mode: {mode} ({opts})");
            }
        }
    }
}
