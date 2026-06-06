//! inkbridge control plane (v2) — pub/sub relay multiplexed onto the data port's control channel.
//!
//! In v1 this was a standalone TCP listener on :9293. In v2 the control logic is split:
//!   - the PC publisher's messages arrive on the muxed connection's **control channel** (channel 0,
//!     authenticated + encrypted): `mux.rs` answers `ping` with `pong`, and calls [`apply_config`] /
//!     [`apply_status`] here. The beacon key is handed to the PC by `mux.rs` over the same channel.
//!   - the on-device AppLoad app still connects over **loopback** as a plaintext, line-JSON
//!     subscriber ("IBCS") — now on the data port `:9292` ([`handle_loopback_subscriber`]).
//!
//! This module owns the shared [`Hub`] (last config/status + subscriber writers) and the staleness
//! broadcaster that flips the on-device UI to "disconnected" when the PC heartbeat goes stale.
//!
//! Wire format on the loopback path is unchanged from v1: newline-delimited UTF-8 JSON, classified by
//! substring (std-only; no serde).

use std::io::{self, BufRead, BufReader, Write};
use std::net::TcpStream;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

/// A status heartbeat older than this means the PC<->device link is down.
const HOST_TIMEOUT: Duration = Duration::from_secs(3);
const DISCONNECTED: &str =
    "{\"type\":\"status\",\"data\":{\"connected\":false,\"latency_ms\":-1.0,\"rate_hz\":0.0}}";

#[derive(Default)]
pub struct Hub {
    /// Writer clones of connected (loopback) subscribers; dead ones are reaped on the next fanout.
    subs: Vec<TcpStream>,
    last_config: Option<String>,
    /// Most recent status the PC pushed, and when (host-liveness clock).
    last_status: Option<String>,
    last_status_at: Option<Instant>,
    /// What we last broadcast (replayed to a subscriber on connect).
    last_broadcast: Option<String>,
}

/// Start the staleness broadcaster and return the shared [`Hub`] plus the on-device-app subscriber
/// count (`app_subs`, the touch passthrough gate). The data-port listener (`mux.rs`) feeds both: PC
/// control messages via [`apply_config`] / [`apply_status`], loopback subscribers via
/// [`handle_loopback_subscriber`].
pub fn spawn_hub() -> (Arc<Mutex<Hub>>, Arc<AtomicUsize>) {
    let hub = Arc::new(Mutex::new(Hub::default()));
    let app_subs = Arc::new(AtomicUsize::new(0));
    {
        let hub = Arc::clone(&hub);
        thread::spawn(move || broadcast_loop(hub));
    }
    (hub, app_subs)
}

/// Every second, broadcast the PC's latest status to loopback subscribers if it's fresh, else
/// "disconnected".
fn broadcast_loop(hub: Arc<Mutex<Hub>>) {
    loop {
        thread::sleep(Duration::from_millis(1000));
        let mut h = hub.lock().unwrap();
        let fresh = h.last_status_at.map_or(false, |t| t.elapsed() < HOST_TIMEOUT);
        let msg = if fresh {
            h.last_status.clone().unwrap_or_else(|| DISCONNECTED.to_string())
        } else {
            DISCONNECTED.to_string()
        };
        let payload = format!("{msg}\n");
        h.last_broadcast = Some(msg);
        h.subs.retain_mut(|w| w.write_all(payload.as_bytes()).is_ok());
    }
}

/// PC published a new area config (channel 0): store it and fan out to loopback subscribers in
/// plaintext (they're local/trusted).
pub fn apply_config(hub: &Arc<Mutex<Hub>>, msg: &str) {
    let mut h = hub.lock().unwrap();
    h.last_config = Some(msg.to_string());
    let payload = format!("{msg}\n");
    h.subs.retain_mut(|w| w.write_all(payload.as_bytes()).is_ok());
}

/// PC pushed a link status (channel 0): record it (+ timestamp) for the staleness broadcaster.
pub fn apply_status(hub: &Arc<Mutex<Hub>>, msg: &str) {
    let mut h = hub.lock().unwrap();
    h.last_status = Some(msg.to_string());
    h.last_status_at = Some(Instant::now());
}

/// On-device app (loopback only — `mux.rs` gates on the peer being loopback): read the `IBCS` role
/// line, register as a subscriber, replay the latest config + last broadcast, then read until EOF.
/// A connected subscriber counts toward `app_subs`, which gates touch passthrough ("AppLoad app open").
pub fn handle_loopback_subscriber(
    stream: TcpStream,
    hub: &Arc<Mutex<Hub>>,
    app_subs: &Arc<AtomicUsize>,
) -> io::Result<()> {
    stream.set_nodelay(true).ok();
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut role = String::new();
    reader.read_line(&mut role)?;
    if role.trim() != "IBCS" {
        crate::log(&format!("control: unknown loopback role {:?}", role.trim()));
        return Ok(());
    }

    {
        let mut h = hub.lock().unwrap();
        let mut w = stream.try_clone()?;
        if let Some(c) = h.last_config.clone() {
            let _ = writeln!(w, "{c}");
        }
        let snapshot = h.last_broadcast.clone().unwrap_or_else(|| DISCONNECTED.to_string());
        let _ = writeln!(w, "{snapshot}");
        h.subs.push(w);
        crate::log(&format!("control: subscriber joined ({} total)", h.subs.len()));
    }

    let n = app_subs.fetch_add(1, Ordering::Relaxed) + 1;
    crate::log(&format!("control: app present (subscribers={n})"));
    let mut line = String::new();
    loop {
        line.clear();
        match reader.read_line(&mut line) {
            Ok(0) | Err(_) => break,
            Ok(_) => {}
        }
    }
    let left = app_subs.fetch_sub(1, Ordering::Relaxed) - 1;
    crate::log(&format!("control: app gone (subscribers={left})"));
    Ok(())
}
