//! inkbridge network access policy — secure-by-default binding.
//!
//! The listeners bind `0.0.0.0` (so they work regardless of when an interface comes up), but by
//! default we only *serve* peers on the **USB cable** (`10.11.99.0/24`) or loopback. Exposing the
//! pen/touch/control ports on Wi-Fi is an **explicit opt-in** — "cableless by choice, not by
//! accident" (docs/security.md). Enable it by creating `/home/root/inkbridge/wifi-enabled` (or
//! setting `INKBRIDGE_WIFI=1`); the inkbridge installer, which already runs over USB, is the natural
//! place to flip it on.
//!
//! Identity auth (auth.rs) still runs on top of this — access control narrows *who can reach* the
//! ports; the handshake decides *who is trusted*.

use std::net::SocketAddr;
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, OnceLock};

const WIFI_FLAG_FILE: &str = "/home/root/inkbridge/wifi-enabled";

static WIFI_ENABLED: OnceLock<bool> = OnceLock::new();

/// Whether Wi-Fi exposure is opted in (read once at startup).
pub fn wifi_enabled() -> bool {
    *WIFI_ENABLED.get_or_init(|| {
        let by_file = Path::new(WIFI_FLAG_FILE).exists();
        let by_env = std::env::var("INKBRIDGE_WIFI")
            .map(|v| v == "1" || v.eq_ignore_ascii_case("true"))
            .unwrap_or(false);
        let on = by_file || by_env;
        crate::log(&format!(
            "Wi-Fi exposure {} (USB/loopback always allowed)",
            if on { "ENABLED (opt-in)" } else { "disabled — default; create /home/root/inkbridge/wifi-enabled to allow Wi-Fi" }
        ));
        on
    })
}

/// A claimed connection slot; dropping it frees the slot. Held for the connection's lifetime (moved
/// into the handler thread) so concurrency is capped without an explicit decrement at every exit.
pub struct Slot(Arc<AtomicUsize>);
impl Drop for Slot {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::Relaxed);
    }
}

/// Try to claim a connection slot under `max`. Returns None at capacity (caller drops the peer) —
/// bounds threads/memory against a connection-storm DoS (T7). Each listener owns its own counter.
pub fn claim(counter: &Arc<AtomicUsize>, max: usize) -> Option<Slot> {
    if counter.fetch_add(1, Ordering::Relaxed) >= max {
        counter.fetch_sub(1, Ordering::Relaxed);
        None
    } else {
        Some(Slot(Arc::clone(counter)))
    }
}

/// True if the peer is on the USB-RNDIS cable subnet (10.11.99.0/24) — the physically-present,
/// point-to-point channel we treat as trusted for *pairing an additional PC* (T1).
pub fn is_usb_peer(peer: Option<SocketAddr>) -> bool {
    match peer {
        Some(SocketAddr::V4(a)) => {
            let o = a.ip().octets();
            o[0] == 10 && o[1] == 11 && o[2] == 99
        }
        _ => false,
    }
}

/// True if a freshly-accepted peer is allowed to be served. USB-subnet (10.11.99.0/24) and loopback
/// are always allowed; anything else requires Wi-Fi exposure to be opted in. An unknown peer address
/// is allowed only when Wi-Fi is enabled (fail-closed otherwise).
pub fn peer_allowed(peer: Option<SocketAddr>) -> bool {
    if wifi_enabled() {
        return true;
    }
    match peer {
        Some(SocketAddr::V4(a)) => {
            let ip = a.ip();
            let o = ip.octets();
            ip.is_loopback() || (o[0] == 10 && o[1] == 11 && o[2] == 99)
        }
        Some(SocketAddr::V6(a)) => a.ip().is_loopback(),
        None => false,
    }
}
