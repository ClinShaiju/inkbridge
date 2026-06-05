using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>Touch passthrough mode, chosen from the OTD "Touch mode" dropdown.</summary>
    public enum TouchMode
    {
        /// <summary>No touch. The plugin never connects to :9294, so the daemon never reads event3.</summary>
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
    /// Owns the touch link to the rMPP daemon (TCP &lt;host&gt;:9294) and dispatches decoded
    /// 88-byte frames to the consumer for the active <see cref="TouchMode"/>. A static singleton
    /// so OTD's per-settings-apply reconstruction of <see cref="InkbridgeTool"/> just re-asserts
    /// the mode (idempotent) instead of spawning duplicate readers — same pattern the device hub
    /// uses (see InkbridgeTool's static _hub).
    ///
    /// The daemon treats a connected client as the enable signal (it grabs event3 and streams
    /// only while we're connected), so switching to Disabled simply drops the connection and the
    /// tablet's touchscreen returns to driving the stock UI.
    /// </summary>
    internal sealed class TouchService
    {
        public static readonly TouchService Instance = new();

        private const int Port = 9294;

        private readonly object _gate = new();
        private TouchMode _mode = TouchMode.Disabled;
        private TouchOptions _opts;
        private Thread? _worker;
        private CancellationTokenSource? _cts;
        // The worker's current connection, tracked so a mode switch can force-close it and
        // unblock the worker's blocking socket read (Thread.Interrupt does not abort a Read).
        private TcpClient? _client;

        private TouchService() { }

        private static string Host =>
            Environment.GetEnvironmentVariable("INKBRIDGE_HOST") ?? "10.11.99.1";

        private static int TouchPort =>
            int.TryParse(Environment.GetEnvironmentVariable("INKBRIDGE_TOUCH_PORT"), out var p) ? p : Port;

        /// <summary>
        /// Apply the selected <paramref name="mode"/> + <paramref name="opts"/>. No-op if nothing
        /// changed; otherwise (re)start the reader worker. See <see cref="TouchOptions"/> for the
        /// rotation / monitor / palm-rejection / always-on fields.
        /// </summary>
        public void SetMode(TouchMode mode, TouchOptions opts)
        {
            lock (_gate)
            {
                bool running = _worker is { IsAlive: true };
                bool unchanged = mode == _mode && opts == _opts;
                if (unchanged && (mode == TouchMode.Disabled || running))
                    return;

                StopLocked();
                _mode = mode;
                _opts = opts;
                if (mode == TouchMode.Disabled)
                {
                    Log.Write("Inkbridge", "Touch mode: Disabled");
                    return;
                }

                var cts = new CancellationTokenSource();
                _cts = cts;
                _worker = new Thread(() => Run(mode, opts, cts.Token))
                {
                    IsBackground = true,
                    Name = "InkbridgeTouch",
                };
                _worker.Start();
                Log.Write("Inkbridge", $"Touch mode: {mode} ({opts})");
            }
        }

        private void StopLocked()
        {
            // Cancel the worker and force-close its socket so a blocking Read aborts at once.
            // We don't Join (could stall OTD's settings-apply); the cancelled worker self-exits.
            try { _cts?.Cancel(); } catch { }
            try { _client?.Close(); } catch { }
            _cts = null;
            _client = null;
            _worker = null;
        }

        private void Run(TouchMode mode, TouchOptions opts, CancellationToken token)
        {
            ITouchConsumer consumer;
            if (mode == TouchMode.DirectTouch)
            {
                // With tap gestures, use the coordinator that withholds ambiguous multi-touch so a
                // 2/3-finger tap fires Undo/Redo without Windows' competing right-click — while
                // pinch/pan stay native and single-finger touch keeps zero latency. Otherwise plain.
                consumer = opts.TapGestures
                    ? new DirectTouchWithTaps(opts.Rotation, opts.Monitor)
                    : new TouchInjector(opts.Rotation, opts.Monitor);
            }
            else
            {
                consumer = new TouchGestures();
            }

            var buf = new byte[TouchPacket.Size];
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = new TcpClient { NoDelay = true };
                    client.Connect(Host, TouchPort);
                    lock (_gate)
                    {
                        if (token.IsCancellationRequested) { client.Dispose(); break; }
                        _client = client; // let StopLocked close it to unblock our Read
                    }
                    using var stream = client.GetStream();

                    var hello = new byte[4];
                    ReadExact(stream, hello, 4);
                    if (hello[0] != (byte)'I' || hello[1] != (byte)'B' ||
                        hello[2] != (byte)'T' || hello[3] != (byte)'1')
                        throw new InvalidOperationException("bad inkbridge touch hello");

                    // Reply with one options byte: bit0 = "always on" (daemon gates touch on the
                    // AppLoad app being open unless this is set); bit1 = "disable palm rejection".
                    byte optByte = 0;
                    if (opts.AlwaysOn) optByte |= 0x01;
                    if (!opts.PalmReject) optByte |= 0x02;
                    stream.WriteByte(optByte);

                    Log.Write("Inkbridge", $"Touch connected to {Host}:{TouchPort} ({mode})");

                    while (!token.IsCancellationRequested)
                    {
                        ReadExact(stream, buf, TouchPacket.Size);
                        var frame = TouchPacket.Parse(buf);
                        consumer.OnFrame(frame);
                    }
                }
                catch (Exception e)
                {
                    if (!token.IsCancellationRequested)
                        Log.Write("Inkbridge", $"Touch link down: {e.Message}; retrying", LogLevel.Debug);
                }
                finally
                {
                    consumer.Reset(); // release any held contacts so nothing sticks down
                    try { client?.Dispose(); } catch { }
                }

                if (!token.IsCancellationRequested) Sleep(1000);
            }
        }

        private static void ReadExact(NetworkStream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        private static void Sleep(int ms)
        {
            try { Thread.Sleep(ms); } catch (ThreadInterruptedException) { }
        }
    }
}
