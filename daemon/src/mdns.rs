//! inkbridge mDNS / DNS-SD advertiser.
//!
//! Publishes `_inkbridge._tcp.local.` (SRV port 9292 + A record + TXT `id=`,`ver=`) so the Windows
//! plugin can discover the device on Wi-Fi with no hardcoded IP — the same mechanism phones,
//! printers (`_ipp._tcp`), and Chromecast (`_googlecast._tcp`) use. The plugin browses for the
//! service, reads the IP/port from SRV/A, and filters to its paired `id` (TXT). Because DHCP changes
//! the Wi-Fi address, resolve-by-service transparently follows it.
//!
//! This complements the UDP presence beacon (`beacon.rs`): the beacon is the lightweight
//! reconnect-wake signal; mDNS is the standards-based discovery that also carries the port and id.
//!
//! Pure-Rust (`mdns-sd`) so it cross-compiles to aarch64-musl with no C/`ring` dependency. The
//! advertised address set is auto-detected and kept in sync with interface changes (so wlan0's DHCP
//! address is published as it comes/goes) via `enable_addr_auto()`.
//!
//! NOTE: discovery is never trusted for identity — a spoofed mDNS record only points the plugin at a
//! host; the per-connection pinned-key handshake is what authenticates. See docs/security.md.

use std::time::Duration;

use mdns_sd::{ServiceDaemon, ServiceInfo};

/// DNS-SD service type. Trailing dot + `.local.` are required by the mDNS library.
const SERVICE_TYPE: &str = "_inkbridge._tcp.local.";
/// Advertised port = the pen stream (`main.rs` PORT). The plugin reads it from SRV.
const PORT: u16 = 9292;

/// Start advertising in a background thread. Best-effort: on failure we log and the daemon keeps
/// running over USB / the beacon. `device_id` is the persisted UUID (see `identity.rs`).
pub fn spawn(device_id: String) {
    std::thread::spawn(move || {
        if let Err(e) = run(&device_id) {
            crate::log(&format!("mdns: advertiser unavailable: {e}"));
        }
    });
}

fn run(device_id: &str) -> Result<(), Box<dyn std::error::Error>> {
    let mdns = ServiceDaemon::new()?;

    // A short, unique instance/host derived from the id so two rMPPs on one subnet don't collide
    // (the SoC hostname imx8mm-ferrari is identical across units). Full id goes in TXT for filtering.
    let short = device_id.get(..8).unwrap_or(device_id);
    let instance = format!("inkbridge-{short}");
    let host = format!("inkbridge-{short}.local.");

    let props = [("id", device_id), ("ver", "1")];
    // Empty address + enable_addr_auto(): the library detects every interface address and keeps the
    // record updated as interfaces change (so the Wi-Fi DHCP address is advertised when up).
    let info = ServiceInfo::new(SERVICE_TYPE, &instance, &host, "", PORT, &props[..])?
        .enable_addr_auto();

    mdns.register(info)?;
    crate::log(&format!(
        "mdns advertising {SERVICE_TYPE} as {instance} (id={device_id}) port {PORT}"
    ));

    // Keep the ServiceDaemon alive — dropping it tears down the advertisement. It runs its own
    // background threads; this one just parks.
    loop {
        std::thread::sleep(Duration::from_secs(3600));
    }
}
