using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Owns the single v2 muxed connection to the rMPP daemon (TCP &lt;host&gt;:9292) and demultiplexes
    /// it to the three consumers. This replaces the v1 layout where the pen source, touch service, and
    /// telemetry each opened their own TCP connection (to :9292/:9294/:9293) and each ran its own
    /// handshake + reconnect.
    ///
    /// One connection, one P-256 handshake (tag <c>IBMX</c>), one <see cref="CryptoSession"/>, one
    /// reconnect path. After the handshake every message is an AES-GCM record whose plaintext is
    /// <c>[channel(1)][payload…]</c>:
    ///   • channel 0 = control — JSON lines, bidirectional (sub/unsub, ping/pong, config, status,
    ///     beaconkey). Raised to subscribers via <see cref="ControlLine"/>; sent via
    ///     <see cref="SendControl"/>.
    ///   • channel 1 = pen — 18-byte PenPacket payloads, queued for <see cref="NextPenPacket"/>.
    ///   • channel 2 = touch — 88-byte TouchPacket payloads, dispatched to the active touch consumer.
    ///
    /// "Enable" is explicit: the pen endpoint calls <see cref="OpenPen"/> (→ <c>sub pen</c>) and the
    /// touch service calls <see cref="SetTouch"/> (→ <c>sub touch</c> with options / <c>unsub touch</c>).
    /// On every (re)connect the manager re-asserts the active subscriptions, so a dropped link, mode
    /// switch, or USB-takeover transparently resumes all three streams.
    /// </summary>
    internal sealed class ConnectionManager
    {
        public static readonly ConnectionManager Instance = new();

        public const byte ChControl = 0;
        public const byte ChPen = 1;
        public const byte ChTouch = 2;

        private const int DefaultPort = 9292;
        private static readonly byte[] Magic = { (byte)'I', (byte)'B', (byte)'M', (byte)'X' };

        private static int Port =>
            int.TryParse(Environment.GetEnvironmentVariable("INKBRIDGE_PORT"), out var p) ? p : DefaultPort;

        // Host is resolved per attempt by the shared ConnectionConfig (Auto/Wi-Fi/USB).
        private static string Host => ConnectionConfig.Instance.ResolveHost();

        private readonly object _startGate = new();
        private bool _started;

        // Live connection state, guarded by _connGate. _session/_stream are non-null only while up.
        private readonly object _connGate = new();
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CryptoSession? _session;
        private bool _connected;

        // Bounded backoff, then park on the device presence beacon (shared listener) — same policy the
        // three v1 links used, now once for the whole connection.
        private readonly ReconnectPolicy _reconnect = new("data");

        // Pen frames decoded off channel 1, consumed by the OTD endpoint via NextPenPacket().
        private readonly BlockingCollection<byte[]> _penQueue = new();
        private volatile bool _penWanted;

        // Touch subscription + the consumer for the active mode, guarded by _touchGate.
        private readonly object _touchGate = new();
        private bool _touchWanted;
        private TouchOptions _touchOpts;
        private volatile ITouchConsumer? _touchConsumer;

        private static readonly UTF8Encoding Utf8 = new(false);

        /// <summary>Raised (on the read-loop thread) for each channel-0 control line received.</summary>
        public event Action<string>? ControlLine;
        /// <summary>Raised after a successful (re)connect, so telemetry can force a config re-push.</summary>
        public event Action? Connected;
        /// <summary>Raised after the connection drops.</summary>
        public event Action? Disconnected;

        private ConnectionManager()
        {
            // On a Connection (Auto/Wi-Fi/USB) switch, drop the live socket so the loop reconnects to
            // the newly-resolved host. Socket-only; process-lifetime registration (singleton).
            ConnectionConfig.Instance.RegisterAbort(AbortCurrent);
        }

        private void AbortCurrent()
        {
            lock (_connGate) { try { _client?.Close(); } catch { } }
        }

        /// <summary>Start the connection thread (idempotent).</summary>
        public void Start()
        {
            lock (_startGate)
            {
                if (_started) return;
                _started = true;
            }
            new Thread(Run) { IsBackground = true, Name = "InkbridgeConnection" }.Start();
        }

        // ── pen consumer ──

        /// <summary>The pen endpoint opened: want the pen channel (subscribe now if connected).</summary>
        public void OpenPen()
        {
            Start();
            _penWanted = true;
            if (IsConnected()) SendControl("{\"type\":\"sub\",\"ch\":\"pen\"}");
        }

        /// <summary>The pen endpoint closed: stop wanting the pen channel.</summary>
        public void ClosePen()
        {
            _penWanted = false;
            if (IsConnected()) SendControl("{\"type\":\"unsub\",\"ch\":\"pen\"}");
        }

        /// <summary>Block until the next 18-byte pen packet arrives (fed off channel 1).</summary>
        public byte[] NextPenPacket()
        {
            try { return _penQueue.Take(); }
            catch (Exception) { return new byte[PenPacket.Size]; }
        }

        // ── touch consumer ──

        /// <summary>
        /// Enable or disable the touch channel. When <paramref name="enabled"/>, the manager sends
        /// <c>sub touch</c> (carrying <paramref name="opts"/>) and routes channel-2 frames to
        /// <paramref name="consumer"/>; re-calling with changed options re-sends <c>sub touch</c>
        /// (the daemon updates options live). When disabled it sends <c>unsub touch</c> and clears the
        /// consumer.
        /// </summary>
        public void SetTouch(bool enabled, TouchOptions opts, ITouchConsumer? consumer)
        {
            Start();
            lock (_touchGate)
            {
                _touchWanted = enabled;
                _touchOpts = opts;
                _touchConsumer = enabled ? consumer : null;
            }
            if (!IsConnected()) return; // reconnect will re-assert
            if (enabled) SendControl(SubTouch(opts));
            else SendControl("{\"type\":\"unsub\",\"ch\":\"touch\"}");
        }

        // ── control consumer (telemetry) ──

        /// <summary>Send a channel-0 control line (best-effort; a no-op while disconnected — the read
        /// loop will notice a write failure and reconnect).</summary>
        public void SendControl(string json)
        {
            CryptoSession? sess;
            NetworkStream? stream;
            lock (_connGate) { sess = _session; stream = _stream; }
            if (sess == null || stream == null) return;
            try { sess.WriteRecord(stream, Frame(ChControl, Utf8.GetBytes(json))); }
            catch { /* read loop will observe the broken connection and reconnect */ }
        }

        public bool IsConnected()
        {
            lock (_connGate) return _connected;
        }

        // ── connection loop ──

        private void Run()
        {
            while (true)
            {
                string host = Host; // resolve once per attempt (Auto probes USB → don't repeat)
                TcpClient? client = null;
                try
                {
                    client = new TcpClient { NoDelay = true };
                    client.Connect(host, Port);
                    lock (_connGate) _client = client;
                    var stream = client.GetStream();

                    var hello = new byte[4];
                    ReadExact(stream, hello, 4);
                    if (hello[0] != Magic[0] || hello[1] != Magic[1] ||
                        hello[2] != Magic[2] || hello[3] != Magic[3])
                        throw new IOException("bad inkbridge v2 hello");

                    var sess = AuthClient.Handshake(stream, Magic);
                    if (sess == null) throw new IOException("inkbridge authentication failed");

                    lock (_connGate) { _session = sess; _stream = stream; _connected = true; }
                    _reconnect.Reset();
                    Log.Write("Inkbridge", $"Connected to daemon at {host}:{Port} (muxed)");

                    // Re-assert active subscriptions for this fresh connection.
                    if (_penWanted) SendControl("{\"type\":\"sub\",\"ch\":\"pen\"}");
                    lock (_touchGate) { if (_touchWanted) SendControl(SubTouch(_touchOpts)); }
                    Connected?.Invoke();

                    // Read loop: demux records by channel until the connection breaks.
                    while (true)
                    {
                        var pt = sess.ReadRecord(stream);
                        if (pt.Length == 0) continue;
                        byte ch = pt[0];
                        var payload = new byte[pt.Length - 1];
                        Buffer.BlockCopy(pt, 1, payload, 0, payload.Length);

                        if (ch == ChControl)
                        {
                            ControlLine?.Invoke(Utf8.GetString(payload));
                        }
                        else if (ch == ChPen)
                        {
                            _penQueue.Add(payload);
                            InkbridgeTelemetry.NotePacket(); // feed the PC↔device line-rate metric
                        }
                        else if (ch == ChTouch)
                        {
                            try { _touchConsumer?.OnFrame(TouchPacket.Parse(payload)); }
                            catch (Exception e)
                            {
                                Log.Write("Inkbridge", $"touch frame error: {e.Message}", LogLevel.Debug);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Inkbridge", $"link down: {e.Message}; reconnecting", LogLevel.Debug);
                }
                finally
                {
                    lock (_connGate) { _connected = false; _session = null; _stream = null; _client = null; }
                    try { _touchConsumer?.Reset(); } catch { } // release any held contacts
                    try { client?.Dispose(); } catch { }
                    DrainPenQueue();
                    try { Disconnected?.Invoke(); } catch { }
                }

                _reconnect.Wait(() => false); // park on the beacon after the backoff budget
            }
        }

        private void DrainPenQueue()
        {
            while (_penQueue.TryTake(out _)) { }
        }

        private static string SubTouch(TouchOptions opts) =>
            "{\"type\":\"sub\",\"ch\":\"touch\",\"always_on\":"
            + (opts.AlwaysOn ? "true" : "false")
            + ",\"palm\":" + (opts.PalmReject ? "true" : "false") + "}";

        /// <summary>Prepend the channel byte to a payload (the muxed record plaintext).</summary>
        private static byte[] Frame(byte channel, byte[] payload)
        {
            var framed = new byte[payload.Length + 1];
            framed[0] = channel;
            Buffer.BlockCopy(payload, 0, framed, 1, payload.Length);
            return framed;
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }
    }
}
