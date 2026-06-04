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

        // Pen packets received since process start; fed by InkbridgeDevice's TcpSource.
        private static long _packets;
        public static void NotePacket() => Interlocked.Increment(ref _packets);

        public static void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            new Thread(Run) { IsBackground = true, Name = "InkbridgeTelemetry" }.Start();
        }

        private static string Host =>
            Environment.GetEnvironmentVariable("INKBRIDGE_HOST") ?? "10.11.99.1";

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
                    Log.Write("Inkbridge", $"telemetry link down: {e.Message}; retrying", LogLevel.Debug);
                }
                Thread.Sleep(2000);
            }
        }

        private static void Session()
        {
            using var client = new TcpClient { NoDelay = true };
            client.Connect(Host, ControlPort);
            client.ReceiveTimeout = 2000;
            using var stream = client.GetStream();
            var utf8 = new UTF8Encoding(false);

            void Send(string line)
            {
                var b = utf8.GetBytes(line + "\n");
                stream.Write(b, 0, b.Length);
            }

            Send("IBCP"); // publisher role
            Log.Write("Inkbridge", $"telemetry connected to {Host}:{ControlPort}");

            DateTime lastWrite = default;
            PushConfigIfChanged(Send, ref lastWrite); // initial push

            long lastPackets = Interlocked.Read(ref _packets);
            long lastStamp = Stopwatch.GetTimestamp();
            var pong = new byte[256];

            while (true)
            {
                PushConfigIfChanged(Send, ref lastWrite);

                // round-trip latency via ping/pong (the daemon echoes our ping back as pong).
                // Stopwatch = monotonic, QueryPerformanceCounter-backed — correct for sub-ms intervals.
                double latencyMs = -1;
                long t0 = Stopwatch.GetTimestamp();
                Send("{\"type\":\"ping\",\"ts\":0}");
                int n = stream.Read(pong, 0, pong.Length); // throws on timeout -> reconnect
                if (n > 0)
                    latencyMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

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
