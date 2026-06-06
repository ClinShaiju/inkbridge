using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// PC-side telemetry for the on-device inkbridge visualizer. Runs for the OTD daemon's
    /// lifetime in its own background thread (started once from <see cref="InkbridgeTool"/>).
    ///
    /// Connects to the rMPP daemon control plane (TCP &lt;host&gt;:9293) as a publisher and:
    ///   • pushes the active-area <c>config</c> read from OTD's settings.json (re-pushing whenever the
    ///     file changes, i.e. when you Save in the OTD GUI);
    ///   • every ~1 s measures round-trip latency (ping/pong, timed with a monotonic high-resolution
    ///     <see cref="Stopwatch"/>) and pushes <c>status</c> {connected, latency_ms, rate_hz}.
    ///
    /// latency_ms is the network round-trip of the control socket over USB — NOT end-to-end pen
    /// input lag (which is dominated by the ~480 Hz digitizer sampling, ~2 ms/sample). rate_hz is
    /// the pen-packet rate this plugin actually receives (fed by <see cref="NotePacket"/>) = the true
    /// PC↔device line rate; on this device the digitizer runs ~480–500 Hz (measured), well above a
    /// typical Wacom (~133–200 Hz).
    ///
    /// Because this lives in the always-running OTD plugin there is no separate companion app: if OTD
    /// is up and the device reachable the heartbeat flows; if the USB is pulled or OTD closes the
    /// heartbeat stops and the daemon flips the on-device UI to "Disconnected" within ~3 s.
    /// </summary>
    internal static class InkbridgeTelemetry
    {
        private const int ControlPort = 9293;

        private static readonly object _gate = new();
        private static bool _started;
        // Bounded backoff, then park on the device beacon instead of reconnecting forever.
        private static readonly ReconnectPolicy _reconnect = new("telemetry");

        // Pen packets received since process start; fed by InkbridgeDevice's TcpSource.
        private static long _packets;
        public static void NotePacket() => Interlocked.Increment(ref _packets);

        // Current control socket, tracked so a Connection mode switch can drop it (force reconnect
        // to the newly-resolved host).
        private static TcpClient? _client;

        public static void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
                // On a Connection (Auto/Wi-Fi/USB) switch, close our socket so Run() reconnects to
                // the new host. Registered once for the process lifetime.
                ConnectionConfig.Instance.RegisterAbort(() => { try { _client?.Close(); } catch { } });
            }
            new Thread(Run) { IsBackground = true, Name = "InkbridgeTelemetry" }.Start();
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenTabletDriver", "settings.json");

        private static void Run()
        {
            while (true)
            {
                try { Session(); }
                catch (Exception e)
                {
                    Log.Write("Inkbridge", $"telemetry link down: {e.Message}", LogLevel.Debug);
                }
                _reconnect.Wait(() => false);
            }
        }

        private static void Session()
        {
            string host = ConnectionConfig.Instance.ResolveHost();
            using var client = new TcpClient { NoDelay = true };
            _client = client; // let a mode switch close this to unblock our read
            client.Connect(host, ControlPort);
            client.ReceiveTimeout = 2000;
            using var stream = client.GetStream();
            var utf8 = new UTF8Encoding(false);

            // Send the publisher role line (plaintext), then run the P-256 handshake; everything
            // after is AES-GCM records on the returned session (mirrors control.rs). Closes the
            // config/status-injection + area/latency-eavesdrop holes on the control plane. The daemon
            // echoes each ping back as a pong record, so a unique ts still matches request↔reply.
            var roleBytes = utf8.GetBytes("IBCP\n");
            stream.Write(roleBytes, 0, roleBytes.Length);
            var sess = AuthClient.Handshake(stream, new byte[] { (byte)'I', (byte)'B', (byte)'C', (byte)'P' });
            if (sess == null) throw new IOException("control-plane authentication failed");

            void Send(string line) => sess.WriteRecord(stream, utf8.GetBytes(line));

            _reconnect.Reset(); // connected — reset the backoff schedule
            Log.Write("Inkbridge", $"telemetry connected to {host}:{ControlPort} (authenticated, encrypted)");

            DateTime lastWrite = default;
            PushConfigIfChanged(Send, ref lastWrite); // initial push

            long lastPackets = Interlocked.Read(ref _packets);
            long lastStamp = Stopwatch.GetTimestamp();
            long pingSeq = 0;

            while (true)
            {
                PushConfigIfChanged(Send, ref lastWrite);

                // Round-trip latency via ping/pong, MATCHED so the number is real on Wi-Fi too.
                // The daemon echoes our ping verbatim with "ping"→"pong" (control.rs), so a unique
                // monotonic `ts` lets us accept only *this* ping's pong and drain any stale line. The
                // previous code read a raw chunk and timed "first bytes available" — over Wi-Fi a
                // pong already buffered locally returned in ~0.1 ms (no round trip), so the display
                // alternated between the true latency and a bogus ~0.1 ms. Stopwatch is monotonic
                // (QueryPerformanceCounter-backed), correct for sub-ms intervals.
                double latencyMs = -1;
                long seq = ++pingSeq;
                string expectedPong = "{\"type\":\"pong\",\"ts\":" + seq + "}";
                long t0 = Stopwatch.GetTimestamp();
                Send("{\"type\":\"ping\",\"ts\":" + seq + "}");
                // Read lines until our pong arrives (throws on timeout -> reconnect). Bounded so a
                // chatty/misbehaving peer can't spin us forever within the receive timeout.
                for (int i = 0; i < 8; i++)
                {
                    var rec = sess.ReadRecord(stream); // decrypts one message; throws on timeout -> reconnect
                    string line = utf8.GetString(rec);
                    if (line.Trim() == expectedPong)
                    {
                        latencyMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                        break;
                    }
                    // else: a stale pong from an earlier ping, or some other message — drain and retry.
                }

                // received-packet rate = true PC↔device line throughput as seen here
                long nowPackets = Interlocked.Read(ref _packets);
                long nowStamp = Stopwatch.GetTimestamp();
                double secs = (nowStamp - lastStamp) / (double)Stopwatch.Frequency;
                double rateHz = secs > 0 ? (nowPackets - lastPackets) / secs : 0;
                lastPackets = nowPackets;
                lastStamp = nowStamp;

                var ci = CultureInfo.InvariantCulture;
                Send("{\"type\":\"status\",\"data\":{\"connected\":true,\"latency_ms\":"
                     + latencyMs.ToString("F2", ci) + ",\"rate_hz\":" + rateHz.ToString("F0", ci) + "}}");

                Thread.Sleep(1000);
            }
        }

        /// <summary>Read the active-area mapping from OTD's settings.json and push it if it changed.</summary>
        private static void PushConfigIfChanged(Action<string> send, ref DateTime lastWrite)
        {
            string path = SettingsPath;
            DateTime w;
            try { w = File.GetLastWriteTimeUtc(path); }
            catch { return; }
            if (w == lastWrite) return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var abs = doc.RootElement.GetProperty("Profiles")[0].GetProperty("AbsoluteModeSettings");
                var t = abs.GetProperty("Tablet");
                var d = abs.GetProperty("Display");
                double tw = t.GetProperty("Width").GetDouble();
                double th = t.GetProperty("Height").GetDouble();
                double tx = t.GetProperty("X").GetDouble();
                double ty = t.GetProperty("Y").GetDouble();
                double tr = t.TryGetProperty("Rotation", out var rot) ? rot.GetDouble() : 0;
                double dw = d.GetProperty("Width").GetDouble();
                double dh = d.GetProperty("Height").GetDouble();

                var ci = CultureInfo.InvariantCulture;
                // Surface is fixed device geometry (see otd-plugin/tablet-spec.json + phase0-findings).
                string cfg =
                    "{\"type\":\"config\",\"data\":{"
                    + "\"surface\":{\"max_x\":11180,\"max_y\":15340,\"width_mm\":179.0,\"height_mm\":239.0},"
                    + "\"area\":{\"width_mm\":" + tw.ToString(ci) + ",\"height_mm\":" + th.ToString(ci)
                    + ",\"x_mm\":" + tx.ToString(ci) + ",\"y_mm\":" + ty.ToString(ci)
                    + ",\"rotation\":" + tr.ToString(ci) + "},"
                    + "\"display\":{\"width_px\":" + dw.ToString(ci) + ",\"height_px\":" + dh.ToString(ci) + "}}}";
                send(cfg);
                lastWrite = w;
                Log.Write("Inkbridge", $"pushed area {tw}×{th}mm rot{tr}");
            }
            catch (Exception e)
            {
                Log.Write("Inkbridge", $"settings.json read failed: {e.Message}", LogLevel.Debug);
            }
        }
    }
}
