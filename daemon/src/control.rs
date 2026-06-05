//! inkbridge control plane — pub/sub relay on TCP :9293, separate from the pen stream (:9292).
//!
//! All status now originates on the PC: the OpenTabletDriver plugin (which is always running with
//! OTD and already holds the pen link) connects here as a PUBLISHER ("IBCP") and:
//!   - pushes the active-area `config` (read from OTD's settings.json), re-pushing on change;
//!   - every ~1 s sends a `ping` (we echo `pong` so it can measure round-trip latency) and a
//!     `status` message {connected, latency_ms, rate_hz} reflecting the real PC<->device link.
//!
//! The daemon just relays to subscribers ("IBCS", the on-device app backend) and, crucially,
//! detects when the plugin's heartbeat goes stale (USB pulled / OTD closed) and broadcasts a
//! "disconnected" status within HOST_TIMEOUT — so the on-device UI reflects the true link state.
//!
//! Wire format: newline-delimited UTF-8. First line is the role token. Publisher lines are compact
//! JSON: {"type":"config",...}, {"type":"ping","ts":N}, {"type":"status","data":{...}}.
//! std-only: messages are opaque strings (classified by substring); no serde.

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

const CONTROL_PORT: u16 = 9293;
/// A status heartbeat older than this means the PC<->device link is down.
const HOST_TIMEOUT: Duration = Duration::from_secs(3);
const DISCONNECTED: &str =
    "{\"type\":\"status\",\"data\":{\"connected\":false,\"latency_ms\":-1.0,\"rate_hz\":0.0}}";

#[derive(Default)]
struct Hub {
    /// Writer clones of connected subscribers; dead ones are reaped on the next fanout.
    subs: Vec<TcpStream>,
    last_config: Option<String>,
    /// Most recent status the plugin pushed, and when (host-liveness clock).
    last_status: Option<String>,
    last_status_at: Option<Instant>,
    /// What we last broadcast (replayed to a subscriber on connect).
    last_broadcast: Option<String>,
}

/// Start the control plane (listener + staleness broadcaster) in their own threads.
/// Returns a counter of currently-connected on-device app subscribers (`IBCS`). The touch
/// stream uses this as the "AppLoad app is open" signal to gate touch passthrough.
pub fn spawn() -> Arc<AtomicUsize> {
    let hub = Arc::new(Mutex::new(Hub::default()));
    let app_subs = Arc::new(AtomicUsize::new(0));
    {
        let hub = Arc::clone(&hub);
        thread::spawn(move || broadcast_loop(hub));
    }
    {
        let app_subs = Arc::clone(&app_subs);
        thread::spawn(move || {
            if let Err(e) = run(hub, app_subs) {
                crate::log(&format!("control plane stopped: {e}"));
            }
        });
    }
    app_subs
}

fn run(hub: Arc<Mutex<Hub>>, app_subs: Arc<AtomicUsize>) -> std::io::Result<()> {
    let listener = TcpListener::bind(("0.0.0.0", CONTROL_PORT))?;
    crate::log(&format!("control plane listening on 0.0.0.0:{CONTROL_PORT}"));
    for incoming in listener.incoming() {
        let stream = match incoming {
            Ok(s) => s,
            Err(_) => continue,
        };
        let hub = Arc::clone(&hub);
        let app_subs = Arc::clone(&app_subs);
        thread::spawn(move || {
            if let Err(e) = handle(stream, hub, app_subs) {
                crate::log(&format!("control client ended: {e}"));
            }
        });
    }
    Ok(())
}

/// Every second, broadcast the plugin's latest status if it's fresh, else "disconnected".
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

fn handle(stream: TcpStream, hub: Arc<Mutex<Hub>>, app_subs: Arc<AtomicUsize>) -> std::io::Result<()> {
    stream.set_nodelay(true).ok();
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut role = String::new();
    reader.read_line(&mut role)?;
    match role.trim() {
        "IBCS" => handle_subscriber(stream, reader, hub, app_subs),
        "IBCP" => handle_publisher(stream, reader, hub),
        "" => Ok(()), // throwaway/probe connection — ignore silently
        other => {
            crate::log(&format!("control: unknown role {other:?}"));
            Ok(())
        }
    }
}

/// Subscriber: register a writer, replay the latest config + last broadcast, then read until EOF.
fn handle_subscriber(
    stream: TcpStream,
    mut reader: BufReader<TcpStream>,
    hub: Arc<Mutex<Hub>>,
    app_subs: Arc<AtomicUsize>,
) -> std::io::Result<()> {
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
    // An on-device app subscriber being connected == the AppLoad app is open. The touch stream
    // reads this to gate passthrough (touch only flows while the app is open, unless overridden).
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

/// Publisher: echo pings as pongs (for PC RTT), store config (fanned out), record link status.
fn handle_publisher(
    mut writer: TcpStream,
    mut reader: BufReader<TcpStream>,
    hub: Arc<Mutex<Hub>>,
) -> std::io::Result<()> {
    crate::log("control: publisher joined");
    let mut line = String::new();
    loop {
        line.clear();
        let n = reader.read_line(&mut line)?;
        if n == 0 {
            break;
        }
        let msg = line.trim();
        if msg.is_empty() {
            continue;
        }
        if msg.contains("\"type\":\"ping\"") {
            // echo back as pong (same ts) so the PC can measure round-trip latency
            let _ = writeln!(writer, "{}", msg.replacen("ping", "pong", 1));
            continue;
        }
        let mut h = hub.lock().unwrap();
        if msg.contains("\"type\":\"config\"") {
            h.last_config = Some(msg.to_string());
            let payload = format!("{msg}\n");
            h.subs.retain_mut(|w| w.write_all(payload.as_bytes()).is_ok());
        } else if msg.contains("\"type\":\"status\"") {
            h.last_status = Some(msg.to_string());
            h.last_status_at = Some(Instant::now());
        }
    }
    Ok(())
}
