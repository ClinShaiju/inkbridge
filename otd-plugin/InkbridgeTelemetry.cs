using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// PC-side telemetry for the on-device inkbridge visualizer. Runs for the OTD daemon's lifetime in
    /// its own background thread (started once from <see cref="InkbridgeTool"/>).
    ///
    /// In v2 it no longer owns a socket: it rides the shared <see cref="ConnectionManager"/>'s control
    /// channel (channel 0 of the one muxed connection). It:
    ///   • pushes the active-area <c>config</c> read from OTD's settings.json (re-pushing when the file
    ///     changes, and on every reconnect);
    ///   • every ~1 s measures round-trip latency (ping/pong, timed with a monotonic high-resolution
    ///     <see cref="Stopwatch"/>) and pushes <c>status</c> {connected, latency_ms, rate_hz};
    ///   • stores the device beacon key the daemon hands over on the control channel.
    ///
    /// latency_ms is the network round-trip of the control channel — NOT end-to-end pen input lag.
    /// rate_hz is the pen-packet rate the manager actually receives (fed by <see cref="NotePacket"/>),
    /// i.e. the true PC↔device line rate.
    ///
    /// When the link is down the manager's SendControl is a no-op, so no status flows; the daemon then
    /// flips the on-device UI to "Disconnected" within ~3 s on its own (control.rs staleness).
    /// </summary>
    internal static class InkbridgeTelemetry
    {
        private static readonly object _gate = new();
        private static bool _started;

        // Pen packets received since process start; fed by ConnectionManager's read loop.
        private static long _packets;
        public static void NotePacket() => Interlocked.Increment(ref _packets);

        // Latency measurement state, guarded by _statGate. The daemon echoes our ping verbatim with
        // "ping"→"pong", so we match the exact expected pong string for *this* ping (unique ts).
        private static readonly object _statGate = new();
        private static long _pingSeq;
        private static string _expectedPong = "";
        private static long _pingT0;
        private static double _latencyMs = -1;
        private static readonly ManualResetEventSlim _pong = new(false);

        // rate baseline + config push gate.
        private static long _lastPackets;
        private static long _lastStamp;
        private static DateTime _lastConfigWrite;

        public static void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            var mgr = ConnectionManager.Instance;
            mgr.ControlLine += OnControlLine;
            mgr.Connected += OnConnected;
            mgr.Start();
            new Thread(Run) { IsBackground = true, Name = "InkbridgeTelemetry" }.Start();
        }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenTabletDriver", "settings.json");

        /// <summary>On (re)connect: force a config re-push and reset the latency + rate baselines.</summary>
        private static void OnConnected()
        {
            lock (_statGate)
            {
                _lastConfigWrite = default;
                _latencyMs = -1;
            }
            _lastPackets = Interlocked.Read(ref _packets);
            _lastStamp = Stopwatch.GetTimestamp();
            Log.Write("Inkbridge", "telemetry: connected (authenticated, encrypted)");
        }

        /// <summary>Handle inbound control lines: our pong (latency) and the device beacon key.</summary>
        private static void OnControlLine(string line)
        {
            string t = line.Trim();
            if (t.Length == 0) return;
            if (t.Contains("\"pong\""))
            {
                lock (_statGate)
                {
                    if (t == _expectedPong)
                        _latencyMs = (Stopwatch.GetTimestamp() - _pingT0) * 1000.0 / Stopwatch.Frequency;
                }
                _pong.Set();
            }
            else if (t.Contains("\"beaconkey\""))
            {
                StoreBeaconKeyIfPresent(t);
            }
        }

        private static void Run()
        {
            var ci = CultureInfo.InvariantCulture;
            while (true)
            {
                if (ConnectionManager.Instance.IsConnected())
                {
                    PushConfigIfChanged();

                    // Round-trip latency via ping/pong, matched so the number is real on Wi-Fi too.
                    long seq;
                    lock (_statGate)
                    {
                        seq = ++_pingSeq;
                        _expectedPong = "{\"type\":\"pong\",\"ts\":" + seq + "}";
                        _pingT0 = Stopwatch.GetTimestamp();
                        _latencyMs = -1;
                    }
                    _pong.Reset();
                    ConnectionManager.Instance.SendControl("{\"type\":\"ping\",\"ts\":" + seq + "}");
                    _pong.Wait(800); // wait briefly for this ping's pong (read loop sets it)
                    double latencyMs;
                    lock (_statGate) latencyMs = _latencyMs;

                    // received-packet rate = true PC↔device line throughput as seen here
                    long nowPackets = Interlocked.Read(ref _packets);
                    long nowStamp = Stopwatch.GetTimestamp();
                    double secs = (nowStamp - _lastStamp) / (double)Stopwatch.Frequency;
                    double rateHz = secs > 0 ? (nowPackets - _lastPackets) / secs : 0;
                    _lastPackets = nowPackets;
                    _lastStamp = nowStamp;

                    ConnectionManager.Instance.SendControl(
                        "{\"type\":\"status\",\"data\":{\"connected\":true,\"latency_ms\":"
                        + latencyMs.ToString("F2", ci) + ",\"rate_hz\":" + rateHz.ToString("F0", ci) + "}}");
                }

                Thread.Sleep(1000);
            }
        }

        /// <summary>Persist the device's beacon key (+id) from a {"type":"beaconkey",...} control message.</summary>
        private static void StoreBeaconKeyIfPresent(string line)
        {
            if (!line.Contains("\"beaconkey\"")) return;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string? key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
                string? id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (string.IsNullOrEmpty(key)) return;
                var cfg = PluginConfig.Load();
                if (cfg.beacon_key == key) return; // unchanged
                cfg.beacon_key = key;
                if (!string.IsNullOrEmpty(id)) cfg.device_id = id;
                cfg.Save();
                Log.Write("Inkbridge", "stored device beacon key");
            }
            catch (Exception e)
            {
                Log.Write("Inkbridge", $"beacon-key parse failed: {e.Message}", LogLevel.Debug);
            }
        }

        /// <summary>Read the active-area mapping from OTD's settings.json and push it (channel 0) if it
        /// changed since the last push (or since the last reconnect).</summary>
        private static void PushConfigIfChanged()
        {
            string path = SettingsPath;
            DateTime w;
            try { w = File.GetLastWriteTimeUtc(path); }
            catch { return; }
            lock (_statGate) { if (w == _lastConfigWrite) return; }

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
                ConnectionManager.Instance.SendControl(cfg);
                lock (_statGate) _lastConfigWrite = w;
                Log.Write("Inkbridge", $"pushed area {tw}×{th}mm rot{tr}");
            }
            catch (Exception e)
            {
                Log.Write("Inkbridge", $"settings.json read failed: {e.Message}", LogLevel.Debug);
            }
        }
    }
}
