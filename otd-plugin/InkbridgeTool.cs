using System.Reflection;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.DependencyInjection;

namespace Inkbridge
{
    /// <summary>
    /// Resident plugin that registers the inkbridge device hub with OTD so our
    /// network/synthetic pen source is enumerated like any USB tablet.
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
