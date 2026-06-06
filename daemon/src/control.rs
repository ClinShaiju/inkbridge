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

use std::io::{BufRead, BufReader, Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use crate::auth;
use crate::crypto::Session;
use crate::identity::Identity;

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
pub fn spawn(id: Arc<Identity>) -> Arc<AtomicUsize> {
    let hub = Arc::new(Mutex::new(Hub::default()));
    let app_subs = Arc::new(AtomicUsize::new(0));
    {
        let hub = Arc::clone(&hub);
        thread::spawn(move || broadcast_loop(hub));
    }
    {
        let app_subs = Arc::clone(&app_subs);
        thread::spawn(move || {
            if let Err(e) = run(hub, app_subs, id) {
                crate::log(&format!("control plane stopped: {e}"));
            }
        });
    }
    app_subs
}

fn run(hub: Arc<Mutex<Hub>>, app_subs: Arc<AtomicUsize>, id: Arc<Identity>) -> std::io::Result<()> {
    let listener = TcpListener::bind(("0.0.0.0", CONTROL_PORT))?;
    crate::log(&format!("control plane listening on 0.0.0.0:{CONTROL_PORT}"));
    for incoming in listener.incoming() {
        let stream = match incoming {
            Ok(s) => s,
            Err(_) => continue,
        };
        if !crate::access::peer_allowed(stream.peer_addr().ok()) {
            crate::log(&format!(
                "control: rejected {:?} — Wi-Fi exposure disabled (USB/loopback only)",
                stream.peer_addr().ok()
            ));
            continue;
        }
        let hub = Arc::clone(&hub);
        let app_subs = Arc::clone(&app_subs);
        let id = Arc::clone(&id);
        thread::spawn(move || {
            if let Err(e) = handle(stream, hub, app_subs, id) {
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

fn handle(
    mut stream: TcpStream,
    hub: Arc<Mutex<Hub>>,
    app_subs: Arc<AtomicUsize>,
    id: Arc<Identity>,
) -> std::io::Result<()> {
    stream.set_nodelay(true).ok();
    let peer = stream.peer_addr().ok();
    let mut reader = BufReader::new(stream.try_clone()?);
    let mut role = String::new();
    reader.read_line(&mut role)?;
    match role.trim() {
        "IBCS" => {
            // Only the on-device app (loopback) may subscribe. This is also what gates app_subs
            // (T5): a LAN host can't inflate "app open" / force touch streaming or read config.
            if !is_loopback(peer) {
                crate::log(&format!("control: rejected non-local subscriber {peer:?}"));
                return Ok(());
            }
            handle_subscriber(stream, reader, hub, app_subs)
        }
        "IBCP" => {
            // The PC publisher must authenticate (P-256 handshake) and then the link is encrypted —
            // so a LAN peer can't inject config/status or read the area/latency. Auth frames are
            // read via `reader` (which may already hold buffered post-role bytes).
            let trusted_usb = crate::access::is_usb_peer(peer);
            let session = {
                let mut rw = Rw { r: &mut reader, w: &mut stream };
                match auth::server_handshake(&mut rw, b"IBCP", &id, trusted_usb)? {
                    Some(s) => s,
                    None => return Ok(()),
                }
            };
            handle_publisher(stream, reader, hub, session)
        }
        "" => Ok(()), // throwaway/probe connection — ignore silently
        other => {
            crate::log(&format!("control: unknown role {other:?}"));
            Ok(())
        }
    }
}

fn is_loopback(peer: Option<SocketAddr>) -> bool {
    peer.map(|p| p.ip().is_loopback()).unwrap_or(false)
}

/// Read/Write adapter so the auth handshake can read from the role-line `BufReader` (preserving any
/// buffered bytes) while writing to the raw stream.
struct Rw<'a> {
    r: &'a mut BufReader<TcpStream>,
    w: &'a mut TcpStream,
}
impl Read for Rw<'_> {
    fn read(&mut self, b: &mut [u8]) -> std::io::Result<usize> {
        self.r.read(b)
    }
}
impl Write for Rw<'_> {
    fn write(&mut self, b: &[u8]) -> std::io::Result<usize> {
        self.w.write(b)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.w.flush()
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

/// Publisher: authenticated + encrypted. Echo pings as pongs (PC RTT), store config (fanned out to
/// the local subscribers in plaintext), record link status. Messages are AES-GCM records, one per
/// line of the old protocol.
fn handle_publisher(
    mut writer: TcpStream,
    mut reader: BufReader<TcpStream>,
    hub: Arc<Mutex<Hub>>,
    mut session: Session,
) -> std::io::Result<()> {
    crate::log("control: publisher joined (authenticated, encrypted)");
    loop {
        let pt = match session.read_record(&mut reader) {
            Ok(p) => p,
            Err(_) => break, // EOF / socket gone / bad record
        };
        let line = String::from_utf8_lossy(&pt);
        let msg = line.trim();
        if msg.is_empty() {
            continue;
        }
        if msg.contains("\"type\":\"ping\"") {
            // echo back as pong (same ts) so the PC can measure round-trip latency
            let pong = msg.replacen("ping", "pong", 1);
            if session.write_record(&mut writer, pong.as_bytes()).is_err() {
                break;
            }
            continue;
        }
        let mut h = hub.lock().unwrap();
        if msg.contains("\"type\":\"config\"") {
            h.last_config = Some(msg.to_string());
            // Fan out to the on-device (loopback) subscribers in plaintext — they're local/trusted.
            let payload = format!("{msg}\n");
            h.subs.retain_mut(|w| w.write_all(payload.as_bytes()).is_ok());
        } else if msg.contains("\"type\":\"status\"") {
            h.last_status = Some(msg.to_string());
            h.last_status_at = Some(Instant::now());
        }
    }
    Ok(())
}
