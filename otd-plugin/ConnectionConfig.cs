using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>Where the plugin should connect, chosen from the OTD "Connection" dropdown.</summary>
    internal enum ConnectionMode
    {
        /// <summary>USB-first: use the cable if reachable, else Wi-Fi. Switches live when the cable comes/goes.</summary>
        Auto,
        /// <summary>Wi-Fi only (discovered / INKBRIDGE_HOST / imx8mm-ferrari.local).</summary>
        WiFi,
        /// <summary>USB cable only (the 10.11.99.1 RNDIS gadget).</summary>
        Usb,
    }

    /// <summary>
    /// Single source of truth for the daemon host across every link (pen <see cref="TcpSource"/>,
    /// <see cref="InkbridgeTelemetry"/>, <see cref="TouchService"/>). A static singleton — OTD
    /// reconstructs <see cref="InkbridgeTool"/> on every settings-apply, so the dropdown value is
    /// just re-asserted here (idempotent), exactly like <see cref="TouchService"/> and the static
    /// device hub.
    ///
    /// All three links resolve their host through <see cref="ResolveHost"/> (instead of reading the
    /// env var directly), so one dropdown drives the whole connection. On a mode change — or when the
    /// USB cable appears while we're on Wi-Fi in Auto — we bump a generation and fire each link's
    /// registered abort, which closes its current socket so it reconnects and re-resolves. Crucially
    /// this is a **socket-only** switch: nothing here touches OTD's device tree, so it never trips the
    /// Detect()-orphan trap documented in <see cref="InkbridgeTool"/>.
    /// </summary>
    internal sealed class ConnectionConfig
    {
        public static readonly ConnectionConfig Instance = new();

        /// <summary>USB-RNDIS gadget IP — the cable endpoint.</summary>
        public const string UsbHost = "10.11.99.1";
        /// <summary>Pen port, probed to detect whether the USB link is live.</summary>
        private const int PenPort = 9292;
        private const int UsbProbeTimeoutMs = 300;
        private const int TakeoverPollMs = 3000;

        private readonly object _gate = new();
        private ConnectionMode _mode = ConnectionMode.Auto;
        private int _generation;
        private string _currentHost = UsbHost;
        private bool _supervisorStarted;
        private readonly List<Action> _aborts = new();

        private ConnectionConfig() { }

        /// <summary>Bumped whenever the target host may have changed; links can compare to detect a switch.</summary>
        public int Generation => Volatile.Read(ref _generation);

        // Power-user / discovery override; also the Wi-Fi address. Historically INKBRIDGE_HOST was the
        // only knob (and forced everything); now it names the Wi-Fi host. INKBRIDGE_WIFI_HOST is an
        // explicit alias. USB mode ignores both — "USB" means the cable, full stop.
        private static string? Override => FirstNonEmpty(
            Environment.GetEnvironmentVariable("INKBRIDGE_HOST"),
            Environment.GetEnvironmentVariable("INKBRIDGE_WIFI_HOST"));

        // mDNS name; resolves once the responder is enabled on wlan0 (see wifi feasibility doc).
        private static string WifiHost => Override ?? "imx8mm-ferrari.local";

        /// <summary>Apply the selected mode. No-op if unchanged; otherwise force every live link to reconnect.</summary>
        public void SetMode(ConnectionMode mode)
        {
            lock (_gate)
            {
                EnsureSupervisorLocked(mode);
                if (mode == _mode) return;
                _mode = mode;
                Log.Write("Inkbridge", $"Connection mode: {mode}");
                SwitchLocked();
            }
        }

        /// <summary>Resolve the host the next connection should use, honoring the current mode.</summary>
        public string ResolveHost()
        {
            ConnectionMode mode;
            lock (_gate) mode = _mode;

            string host = mode switch
            {
                ConnectionMode.Usb => UsbHost,
                ConnectionMode.WiFi => WifiHost,
                _ => UsbReachable() ? UsbHost : WifiHost, // Auto: USB first, fall back to Wi-Fi
            };
            lock (_gate) _currentHost = host;
            return host;
        }

        /// <summary>
        /// Register a callback that drops a link's current socket so it reconnects and re-resolves.
        /// Dispose the returned token to unregister (e.g. when a per-connection <see cref="TcpSource"/>
        /// is disposed) so the list doesn't grow across reconnects.
        /// </summary>
        public IDisposable RegisterAbort(Action abort)
        {
            lock (_gate) _aborts.Add(abort);
            return new Registration(this, abort);
        }

        // --- internals ---

        private void SwitchLocked()
        {
            Interlocked.Increment(ref _generation);
            foreach (var a in _aborts)
            {
                try { a(); } catch { }
            }
        }

        private void EnsureSupervisorLocked(ConnectionMode mode)
        {
            // The live USB-takeover watch only makes sense in Auto (cable plugged while on Wi-Fi).
            // Start it once; thereafter it idles whenever the mode isn't Auto.
            if (_supervisorStarted || mode != ConnectionMode.Auto) return;
            _supervisorStarted = true;
            new Thread(Supervise) { IsBackground = true, Name = "InkbridgeConnSupervisor" }.Start();
        }

        private void Supervise()
        {
            while (true)
            {
                Thread.Sleep(TakeoverPollMs);
                bool auto;
                string current;
                lock (_gate) { auto = _mode == ConnectionMode.Auto; current = _currentHost; }
                if (!auto) continue;              // not in Auto — nothing to take over
                if (current == UsbHost) continue; // already on USB
                if (!UsbReachable()) continue;    // cable still absent
                Log.Write("Inkbridge", "USB cable appeared; switching from Wi-Fi to USB");
                lock (_gate)
                {
                    if (_mode == ConnectionMode.Auto && _currentHost != UsbHost)
                        SwitchLocked(); // links reconnect → ResolveHost picks USB-first
                }
            }
        }

        /// <summary>True if a daemon answers on the USB gadget IP within a short timeout.</summary>
        private static bool UsbReachable()
        {
            try
            {
                using var c = new TcpClient();
                var ar = c.BeginConnect(UsbHost, PenPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(UsbProbeTimeoutMs)) return false;
                c.EndConnect(ar);
                return c.Connected;
            }
            catch { return false; }
        }

        private void Unregister(Action abort)
        {
            lock (_gate) _aborts.Remove(abort);
        }

        private static string? FirstNonEmpty(params string?[] vals)
        {
            foreach (var v in vals)
                if (!string.IsNullOrWhiteSpace(v)) return v;
            return null;
        }

        private sealed class Registration : IDisposable
        {
            private readonly ConnectionConfig _owner;
            private readonly Action _abort;
            private bool _done;
            public Registration(ConnectionConfig owner, Action abort) { _owner = owner; _abort = abort; }
            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _owner.Unregister(_abort);
            }
        }
    }
}
