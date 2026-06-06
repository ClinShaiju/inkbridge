using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
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
        /// <summary>How long a discovered Wi-Fi IP is reused before re-browsing (mDNS isn't free).</summary>
        private const int WifiCacheMs = 20000;

        private readonly object _gate = new();
        private ConnectionMode _mode = ConnectionMode.Auto;
        private int _generation;
        private string _currentHost = UsbHost;
        private bool _supervisorStarted;
        private readonly List<Action> _aborts = new();
        // Short-lived cache of the last discovered Wi-Fi IP, so per-reconnect ResolveHost calls don't
        // each pay a ~1 s mDNS browse.
        private string? _wifiCache;
        private long _wifiCacheAt;

        private ConnectionConfig() { }

        /// <summary>Bumped whenever the target host may have changed; links can compare to detect a switch.</summary>
        public int Generation => Volatile.Read(ref _generation);

        // Power-user / discovery override; also the Wi-Fi address. Historically INKBRIDGE_HOST was the
        // only knob (and forced everything); now it names the Wi-Fi host. INKBRIDGE_WIFI_HOST is an
        // explicit alias. USB mode ignores both — "USB" means the cable, full stop.
        private static string? Override => FirstNonEmpty(
            Environment.GetEnvironmentVariable("INKBRIDGE_HOST"),
            Environment.GetEnvironmentVariable("INKBRIDGE_WIFI_HOST"));

        /// <summary>
        /// Resolve the Wi-Fi host: explicit override → fresh cached IP → mDNS discovery (filtered to
        /// the paired device id) → last-good IP from inkbridge.json → the `.local` name as last resort.
        /// </summary>
        private string WifiHost()
        {
            var ov = Override;
            if (ov != null) return ov; // power-user / explicit wins

            lock (_gate)
            {
                if (_wifiCache != null && Environment.TickCount64 - _wifiCacheAt < WifiCacheMs)
                    return _wifiCache;
            }

            string? ip = DiscoverWifiIp();
            if (ip != null)
            {
                lock (_gate) { _wifiCache = ip; _wifiCacheAt = Environment.TickCount64; }
                return ip;
            }

            var cfg = PluginConfig.Load();
            if (!string.IsNullOrWhiteSpace(cfg.wifi_host)) return cfg.wifi_host!;
            return "imx8mm-ferrari.local"; // mDNS responder name (works if the OS resolves .local)
        }

        /// <summary>
        /// Browse mDNS for <c>_inkbridge._tcp</c>, pick the device matching our paired id (or adopt the
        /// first one we see — trust-on-first-use — and persist it), and choose its Wi-Fi IPv4. Caches
        /// the id + IP in inkbridge.json. Returns null if nothing usable was found.
        /// </summary>
        private static string? DiscoverWifiIp()
        {
            List<MdnsService> svcs;
            try { svcs = MdnsClient.Discover(1200); } catch { return null; }
            if (svcs.Count == 0) return null;

            var cfg = PluginConfig.Load();
            MdnsService? chosen = null;
            if (!string.IsNullOrWhiteSpace(cfg.device_id))
                chosen = svcs.Find(s => string.Equals(s.Id, cfg.device_id, StringComparison.OrdinalIgnoreCase));
            if (chosen == null)
            {
                chosen = svcs[0]; // TOFU: adopt the first discovered device as the paired one
                if (!string.Equals(cfg.device_id, chosen.Id, StringComparison.OrdinalIgnoreCase))
                    Log.Write("Inkbridge", $"paired with device id {chosen.Id} (first discovered)");
                cfg.device_id = chosen.Id;
            }

            string? ip = PickWifiAddress(chosen.Addresses);
            if (ip == null) return null;
            cfg.wifi_host = ip;
            cfg.Save();
            Log.Write("Inkbridge", $"mDNS discovered {chosen.Id} -> {ip}:{chosen.Port}");
            return ip;
        }

        /// <summary>Choose the best IPv4 for the Wi-Fi link: skip link-local + the USB subnet; prefer
        /// an address on the same subnet as a local interface.</summary>
        private static string? PickWifiAddress(List<IPAddress> addrs)
        {
            IPAddress? fallback = null;
            foreach (var a in addrs)
            {
                if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                byte[] b = a.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;                 // link-local
                if (b[0] == 10 && b[1] == 11 && b[2] == 99) continue;     // USB gadget subnet, not Wi-Fi
                if (OnLocalSubnet(a)) return a.ToString();                // directly reachable — best
                fallback ??= a;
            }
            return fallback?.ToString();
        }

        private static bool OnLocalSubnet(IPAddress ip)
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var mask = ua.IPv4Mask;
                        if (mask == null) continue;
                        if (SameSubnet(ip, ua.Address, mask)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
        {
            byte[] ab = a.GetAddressBytes(), bb = b.GetAddressBytes(), mb = mask.GetAddressBytes();
            if (ab.Length != bb.Length || ab.Length != mb.Length) return false;
            for (int i = 0; i < ab.Length; i++)
                if ((ab[i] & mb[i]) != (bb[i] & mb[i])) return false;
            return true;
        }

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
                ConnectionMode.WiFi => WifiHost(),
                _ => UsbReachable() ? UsbHost : WifiHost(), // Auto: USB first, fall back to Wi-Fi
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
            var c = new TcpClient();
            try
            {
                var t = c.ConnectAsync(UsbHost, PenPort);
                if (t.Wait(UsbProbeTimeoutMs) && c.Connected)
                    return true;
                // Timed out → we're abandoning the probe (the common case when the cable is unplugged).
                // Observe the eventual fault so the aborted ConnectAsync doesn't surface as an
                // unobserved task exception (finalizer-rethrown SocketException spam) when Close() below
                // tears down the pending connect.
                ObserveFault(t);
                return false;
            }
            catch
            {
                // Wait() throws AggregateException for a task that faulted within the timeout (e.g.
                // connection refused) — accessing it here observes it.
                return false;
            }
            finally
            {
                c.Close();
            }
        }

        /// <summary>Swallow a faulted task's exception so it isn't rethrown by the finalizer.</summary>
        private static void ObserveFault(Task t) =>
            t.ContinueWith(
                static x => { _ = x.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

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
