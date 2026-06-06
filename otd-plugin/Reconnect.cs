using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Listens for the rMPP daemon's UDP presence beacon (broadcast on :9291, payload "IBR1").
    /// A single process-wide listener shared by every reconnecting link (pen / touch / telemetry):
    /// once a link has exhausted its bounded reconnect budget it parks on <see cref="WaitForBeacon"/>
    /// instead of hammering the network, and resumes the instant the device announces itself.
    ///
    /// The socket is bound lazily — only when something first waits on a beacon — so a healthy,
    /// always-connected session never opens the UDP port.
    /// </summary>
    internal static class BeaconListener
    {
        private const int DefaultPort = 9291;

        private static readonly object _gate = new();
        private static bool _started;
        // Monotonic timestamp (Stopwatch ticks) of the most recent valid beacon. Waiters compare
        // against the value they captured on entry to detect a *fresh* beacon.
        private static long _lastBeaconTicks;
        // Pulsed on each beacon so waiters wake promptly instead of polling.
        private static readonly ManualResetEventSlim _pulse = new(false);

        private static int Port =>
            int.TryParse(Environment.GetEnvironmentVariable("INKBRIDGE_BEACON_PORT"), out var p) ? p : DefaultPort;

        private static void EnsureStarted()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
            }
            new Thread(Run) { IsBackground = true, Name = "InkbridgeBeacon" }.Start();
        }

        // Cached paired beacon credentials (loaded lazily once the device hands us its key over the
        // control channel). Until paired, we accept the bare magic so first-run/bootstrap still wakes.
        private static byte[]? _beaconKey;
        private static string? _deviceId;

        /// <summary>
        /// A beacon counts only if it carries the magic AND (once we're paired) the right device id and
        /// a valid HMAC, so a forged <c>IBR1</c> broadcast can't make us churn reconnects (T9).
        /// Payload: <c>IBR1 ‖ id(36) ‖ HMAC-SHA256(beacon_key, IBR1‖id)[32]</c>.
        /// </summary>
        private static bool IsValidBeacon(byte[] d)
        {
            if (d.Length < 4 || d[0] != (byte)'I' || d[1] != (byte)'B' || d[2] != (byte)'R' || d[3] != (byte)'1')
                return false;

            if (_beaconKey == null)
            {
                var cfg = PluginConfig.Load();
                if (!string.IsNullOrEmpty(cfg.beacon_key))
                {
                    try { _beaconKey = Convert.FromHexString(cfg.beacon_key); _deviceId = cfg.device_id; }
                    catch { _beaconKey = null; }
                }
            }
            if (_beaconKey == null)
                return true; // not paired yet — accept bare magic (bootstrap / legacy daemon)

            if (d.Length < 4 + 36 + 32) return false;
            string id = Encoding.ASCII.GetString(d, 4, 36);
            if (!string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase)) return false;
            using var h = new HMACSHA256(_beaconKey);
            byte[] mac = h.ComputeHash(d, 0, 4 + 36);
            var got = new byte[32];
            Buffer.BlockCopy(d, 40, got, 0, 32);
            return CryptographicOperations.FixedTimeEquals(mac, got);
        }

        private static void Run()
        {
            while (true)
            {
                try
                {
                    using var udp = new UdpClient();
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    Log.Write("Inkbridge", $"beacon listener bound on :{Port}", LogLevel.Debug);
                    while (true)
                    {
                        byte[] data = udp.Receive(ref remote); // blocks until a datagram arrives
                        if (IsValidBeacon(data))
                        {
                            Volatile.Write(ref _lastBeaconTicks, Stopwatch.GetTimestamp());
                            _pulse.Set();
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Write("Inkbridge", $"beacon listener error: {e.Message}; rebinding", LogLevel.Debug);
                    Thread.Sleep(1000);
                }
            }
        }

        /// <summary>
        /// Block until a beacon arrives *after* this call, or until <paramref name="cancelled"/>
        /// returns true. Returns true if a fresh beacon was heard, false if cancelled first.
        /// </summary>
        public static bool WaitForBeacon(Func<bool> cancelled)
        {
            EnsureStarted();
            long seen = Volatile.Read(ref _lastBeaconTicks);
            while (!cancelled())
            {
                // Short slices so cancellation is observed even if no beacon ever comes. A beacon
                // that races the Reset() is still caught by the timestamp compare below.
                _pulse.Wait(500);
                _pulse.Reset();
                if (Volatile.Read(ref _lastBeaconTicks) != seen)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reconnect schedule for a single link. On each failed connect, <see cref="Wait"/> sleeps a
    /// growing interval (1→2→4→8→15→30 s, ~1 min total). Once that budget is spent it stops actively
    /// retrying and parks on the device presence beacon (<see cref="BeaconListener"/>); when the
    /// beacon is heard it resets and the caller reconnects. Call <see cref="Reset"/> after a
    /// successful connect so the next outage starts the schedule fresh.
    /// </summary>
    internal sealed class ReconnectPolicy
    {
        // Cumulative ~60 s before we go quiet and wait for the beacon.
        private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000 };

        private readonly string _tag;
        private int _attempt;

        public ReconnectPolicy(string tag) { _tag = tag; }

        public void Reset() => _attempt = 0;

        /// <summary>
        /// Wait before the next reconnect attempt. Honors <paramref name="cancelled"/> (checked
        /// during the sleep and while parked on the beacon) so a dispose / mode-switch returns promptly.
        /// </summary>
        public void Wait(Func<bool> cancelled)
        {
            if (_attempt < BackoffMs.Length)
            {
                CancellableSleep(BackoffMs[_attempt++], cancelled);
                return;
            }

            Log.Write("Inkbridge",
                $"{_tag}: no connection for ~1 min; pausing reconnects, waiting for device beacon",
                LogLevel.Warning);
            if (BeaconListener.WaitForBeacon(cancelled))
                Log.Write("Inkbridge", $"{_tag}: device beacon heard; resuming", LogLevel.Info);
            _attempt = 0;
        }

        private static void CancellableSleep(int ms, Func<bool> cancelled)
        {
            int slept = 0;
            while (slept < ms && !cancelled())
            {
                int slice = Math.Min(200, ms - slept);
                Thread.Sleep(slice);
                slept += slice;
            }
        }
    }
}
