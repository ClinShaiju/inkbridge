//! inkbridge-daemon — runs on the reMarkable Paper Pro.
//!
//! Listens on one TCP "data" port (`:9292`) that carries the pen, touch, and control streams as
//! channels of a single authenticated, encrypted connection (see `mux.rs`). When the Windows OTD
//! plugin connects and authenticates, the daemon holds a wakelock (so the rMPP doesn't autosleep)
//! and — on the plugin's `sub` requests — reads the pen digitizer (`/dev/input/event2`, "Elan marker
//! input") and/or the touch digitizer (`event3`), framing each into the muxed stream. The on-device
//! AppLoad app shares the same port over loopback as a plaintext control subscriber.
//!
//! v2 transport: this collapses the v1 three-TCP-port layout (pen :9292 / control :9293 / touch
//! :9294) into the single muxed `:9292`; the UDP presence beacon (:9291) is unchanged. See
//! port-merge.tmp.md and protocol/mux-v2.md.
//!
//! Verified device mechanism (do not change without re-verifying):
//! - xochitl does NOT EVIOCGRAB the pen (a fresh EVIOCGRAB succeeds), so we can read event2
//!   *alongside* a running xochitl. We never pause/stop xochitl: SIGSTOP-ing it makes it miss its
//!   systemd WatchdogSec=60 sd_notify, which trips the failure path and reboots the device. Cost of
//!   leaving xochitl up: it also inks the e-ink while we read (cosmetic only; input-only use).
//! - The rMPP uses Android-style opportunistic suspend (`/sys/power/autosleep` = mem). With no
//!   wakelock held the SoC suspends to RAM and the SPI digitizer powers down, so we get ZERO pen
//!   events. We therefore hold a `/sys/power/wake_lock` for the duration of each client session.
//!
//! See docs/phase0-findings.md for device facts and protocol/packet.md for the wire format.

mod access;
mod auth;
mod beacon;
mod control;
mod crypto;
mod identity;
mod mdns;
mod mux;
mod orientation;
mod packet;
mod pen;
mod touch;

use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};

/// Wakelock tag written to /sys/power/wake_lock while ≥1 client is connected.
const WAKELOCK_TAG: &str = "inkbridge";

/// Ref-counts connected clients and holds the wakelock while any are active. OTD's settings-apply
/// briefly opens a second TCP source before disposing the first; serving each connection in its own
/// thread lets that transient succeed cleanly instead of triggering a reconnect storm. Two concurrent
/// evdev readers on event2 is fine — there is no grab, so each gets all events.
#[derive(Default)]
pub(crate) struct WakeRefcount {
    clients: u32,
}

/// First client in: hold the device awake so autosleep doesn't power down the digitizer.
pub(crate) fn client_in(refc: &Arc<Mutex<WakeRefcount>>) {
    let mut r = refc.lock().unwrap();
    r.clients += 1;
    if r.clients == 1 {
        wakelock(true);
        log("wakelock acquired (first client)");
    }
}

/// Last client out: release the wakelock and let the device sleep.
pub(crate) fn client_out(refc: &Arc<Mutex<WakeRefcount>>) {
    let mut r = refc.lock().unwrap();
    r.clients = r.clients.saturating_sub(1);
    if r.clients == 0 {
        wakelock(false);
        log("wakelock released (last client gone)");
    }
}

fn main() -> std::io::Result<()> {
    // Recover from any previous unclean exit that left the wakelock held.
    wakelock(false);

    // Load the device identity once (UUID locator + P-256 keypair + authorized-PC allow-list).
    // Shared with every connection handler for the mutual auth handshake.
    let id = Arc::new(identity::Identity::load());

    // Start the control plane: the shared Hub + the staleness broadcaster. Returns the Hub (fed by
    // the muxed control channel + loopback subscribers) and the on-device-app subscriber count, used
    // to gate touch passthrough to "AppLoad app is open".
    let (hub, app_subs) = control::spawn_hub();

    // Broadcast a UDP presence beacon (:9291) so a plugin that gave up its bounded reconnect attempts
    // wakes and reconnects the moment we're reachable again. Own thread; unchanged in v2.
    beacon::spawn(Arc::clone(&id));

    // Advertise over mDNS/DNS-SD (_inkbridge._tcp) so the plugin discovers us on Wi-Fi with no
    // hardcoded IP. Carries the persisted device id (UUID) for PC1<->rMPP1 filtering. Own thread.
    mdns::spawn(id.device_id.clone());

    // Detect screen orientation (accelerometer + xochitl lock) and publish it; the pen/touch readers
    // stamp it into every packet so OTD can rotate the area to match.
    let orient = orientation::spawn();

    let refc = Arc::new(Mutex::new(WakeRefcount::default()));

    // Pen-priority palm rejection: the pen reader sets this true while the pen/eraser is in range;
    // the touch reader suppresses touch while it's set, so a palm resting on the screen during
    // drawing doesn't register as touches. Shared process-wide (parity with v1).
    let pen_in_range = Arc::new(AtomicBool::new(false));

    // Serve the single muxed data port (:9292): pen + touch + control over one authenticated,
    // encrypted connection per PC, plus loopback control subscribers for the on-device app.
    mux::serve(mux::Shared { id, refc, orient, pen_in_range, app_subs, hub })
}

/// Acquire/release an opportunistic-suspend wakelock via the kernel wakelock interface. Required
/// because the rMPP autosleeps (`/sys/power/autosleep` = mem); without this the SoC suspends and the
/// SPI digitizer stops producing events. Best-effort.
fn wakelock(acquire: bool) {
    let path = if acquire {
        "/sys/power/wake_lock"
    } else {
        "/sys/power/wake_unlock"
    };
    let _ = std::fs::write(path, WAKELOCK_TAG);
}

pub(crate) fn log(msg: &str) {
    eprintln!("[inkbridge] {msg}");
}
