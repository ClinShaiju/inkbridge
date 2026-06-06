//! inkbridge v2 muxed transport — one TCP data port (`:9292`) carries pen, touch, and control as
//! channels of a single authenticated, encrypted connection. This replaces the v1 layout of three
//! separate TCP ports (pen :9292, control :9293, touch :9294); the UDP presence beacon (:9291) is
//! unchanged. See port-merge.tmp.md and protocol/mux-v2.md.
//!
//! Two kinds of connection arrive on `:9292`:
//!
//! - **Remote PC plugin** (USB subnet / Wi-Fi): the daemon writes the 4-byte `IBMX` hello, runs the
//!   P-256 mutual handshake (auth.rs) keyed with tag `IBMX`, and from then on every message is an
//!   AES-GCM record whose plaintext is `[channel(1)][payload…]`:
//!     - channel 0 = control (JSON; bidirectional) — sub/unsub, ping/pong, config, status, beaconkey
//!     - channel 1 = pen     (18-byte PenPacket, device→PC only)
//!     - channel 2 = touch   (88-byte TouchPacket, device→PC only)
//!   The send side is shared by the pen + touch + control writer threads behind one mutex (so records
//!   never interleave); the recv side is owned by the single inbound reader thread.
//!
//! - **On-device AppLoad app** (loopback `127.0.0.1`): handled as a plaintext, line-JSON control
//!   subscriber (this *is* channel 0 in the clear) — the daemon special-cases loopback and skips the
//!   handshake, exactly as the old `:9293` IBCS path did. It just moved onto the data port. See
//!   control.rs `handle_loopback_subscriber`.
//!
//! Enable semantics replace "did the client open the socket": the plugin sends `sub`/`unsub` control
//! messages. The pen reader starts on `sub pen`; the touch reader starts on `sub touch` (carrying the
//! `always_on`/`palm` options) and stops on `unsub touch` / mode Disabled.

use std::io::Write;
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

use crate::control::Hub;
use crate::crypto::SendHalf;
use crate::identity::Identity;
use crate::touch::TouchOptions;
use crate::WakeRefcount;

/// Channel tag carried as the first plaintext byte of every encrypted record.
pub const CH_CONTROL: u8 = 0;
pub const CH_PEN: u8 = 1;
pub const CH_TOUCH: u8 = 2;

const PORT: u16 = 9292;
/// v2 muxed protocol hello + auth channel tag (distinct from v1 IBR1/IBT1/IBCP so a captured v1
/// signature can't be replayed, and the wire version is unambiguous).
const MAGIC: &[u8; 4] = b"IBMX";
/// One cap for the single port: a PC plugin (+ OTD's transient 2nd source on settings-apply), the
/// on-device app, and a little headroom for multi-PC. Bounds a connection-storm DoS (T7).
const MAX_DATA_CONNS: usize = 8;

/// Process-wide state every connection handler needs. All fields are `Arc`s, so cloning is cheap and
/// the struct is shared across connection threads.
#[derive(Clone)]
pub struct Shared {
    pub id: Arc<Identity>,
    pub refc: Arc<Mutex<WakeRefcount>>,
    pub orient: Arc<AtomicU8>,
    pub pen_in_range: Arc<AtomicBool>,
    pub app_subs: Arc<AtomicUsize>,
    pub hub: Arc<Mutex<Hub>>,
}

/// Bind the single data port and serve every connection in its own thread.
pub fn serve(shared: Shared) -> std::io::Result<()> {
    let listener = TcpListener::bind(("0.0.0.0", PORT))?;
    crate::log(&format!("data plane listening on 0.0.0.0:{PORT} (muxed pen+touch+control)"));
    let conns = Arc::new(AtomicUsize::new(0));

    for incoming in listener.incoming() {
        let stream = match incoming {
            Ok(s) => s,
            Err(e) => {
                crate::log(&format!("data: accept error: {e}"));
                continue;
            }
        };
        let peer = stream.peer_addr().ok();
        if !crate::access::peer_allowed(peer) {
            crate::log(&format!("data: rejected {peer:?} — Wi-Fi exposure disabled (USB/loopback only)"));
            continue;
        }
        let slot = match crate::access::claim(&conns, MAX_DATA_CONNS) {
            Some(s) => s,
            None => {
                crate::log(&format!("data: at connection cap ({MAX_DATA_CONNS}); dropping {peer:?}"));
                continue;
            }
        };
        let shared = shared.clone();
        thread::spawn(move || {
            let _slot = slot; // frees the connection slot on handler exit
            if is_loopback(peer) {
                // On-device app: plaintext line-JSON control subscriber (channel 0 in the clear).
                crate::log(&format!("data: loopback control subscriber {peer:?}"));
                if let Err(e) =
                    crate::control::handle_loopback_subscriber(stream, &shared.hub, &shared.app_subs)
                {
                    crate::log(&format!("data: loopback subscriber ended: {e}"));
                }
            } else {
                let trusted_usb = crate::access::is_usb_peer(peer);
                crate::log(&format!("data: client connected: {peer:?}"));
                if let Err(e) = handle_remote(stream, peer, &shared, trusted_usb) {
                    crate::log(&format!("data: session ended: {e}"));
                }
                crate::log("data: client disconnected");
            }
        });
    }
    Ok(())
}

/// Remote (PC) connection: hello, authenticate, then (only if authorized) hold the wakelock and run
/// the muxed streams. Authenticates BEFORE the wakelock so an unauthorized peer can't keep the device
/// awake (battery-drain DoS) or read the digitizers.
fn handle_remote(
    mut stream: TcpStream,
    peer: Option<SocketAddr>,
    shared: &Shared,
    trusted_usb: bool,
) -> std::io::Result<()> {
    stream.set_nodelay(true)?; // latency over throughput
    stream.write_all(MAGIC)?;
    let session = match crate::auth::server_handshake(&mut stream, MAGIC, &shared.id, trusted_usb)? {
        Some(s) => s,
        None => return Ok(()), // unauthorized: no wakelock, no digitizer read
    };
    crate::log(&format!("data: client authenticated: {peer:?}"));

    // Authorized: hold the device awake for the whole connection (one wakelock for all channels).
    crate::client_in(&shared.refc);
    let r = run_connection(stream, shared, session);
    shared.pen_in_range.store(false, Ordering::Relaxed); // client gone → unblock touch
    crate::client_out(&shared.refc);
    r
}

/// Drive one authenticated connection: split the session, send the beacon key, then loop reading
/// inbound control records and dispatching them. The pen + touch reader threads run as children,
/// started/stopped by `sub`/`unsub`. Returns when the inbound read fails (client gone).
fn run_connection(
    stream: TcpStream,
    shared: &Shared,
    session: crate::crypto::Session,
) -> std::io::Result<()> {
    let (send_half, mut recv) = session.into_halves();
    let sender = Arc::new(Mutex::new(send_half));
    let conn_alive = Arc::new(AtomicBool::new(true));
    let mut rsock = stream.try_clone()?; // inbound reader's own handle

    // Hand the authenticated PC the beacon key (channel 0) so it can verify our presence beacon (T9).
    // Safe to send only now — the channel is authenticated + encrypted.
    {
        let bk = format!(
            "{{\"type\":\"beaconkey\",\"key\":\"{}\",\"id\":\"{}\"}}",
            crate::identity::to_hex(shared.id.beacon_key()),
            shared.id.device_id
        );
        let mut wsock = stream.try_clone()?;
        send_ctrl(&sender, &mut wsock, &bk, &conn_alive)?;
    }

    let mut pen_ctl = StreamCtl::new();
    let mut touch_ctl = StreamCtl::new();
    let touch_opts = Arc::new(TouchOptions::default());
    touch_opts.palm_reject.store(true, Ordering::Relaxed); // default on until a sub says otherwise

    let mut ctrl_wsock = stream.try_clone()?; // this thread's writer for pongs/acks

    loop {
        let pt = match recv.read_record(&mut rsock) {
            Ok(p) => p,
            Err(_) => break, // EOF / socket gone / bad record
        };
        if pt.is_empty() {
            continue;
        }
        let channel = pt[0];
        let payload = &pt[1..];
        if channel == CH_CONTROL {
            handle_control(
                payload,
                shared,
                &sender,
                &mut ctrl_wsock,
                &conn_alive,
                &mut pen_ctl,
                &mut touch_ctl,
                &touch_opts,
                &stream,
            );
        }
        // Inbound CH_PEN / CH_TOUCH are not expected (those channels are device→PC only); ignore.
    }

    conn_alive.store(false, Ordering::Relaxed);
    pen_ctl.stop();
    touch_ctl.stop();
    Ok(())
}

/// Dispatch one channel-0 control message (a JSON line). Classified by substring (std-only, no serde),
/// matching control.rs's existing approach.
#[allow(clippy::too_many_arguments)]
fn handle_control(
    payload: &[u8],
    shared: &Shared,
    sender: &Arc<Mutex<SendHalf>>,
    ctrl_wsock: &mut TcpStream,
    conn_alive: &Arc<AtomicBool>,
    pen_ctl: &mut StreamCtl,
    touch_ctl: &mut StreamCtl,
    touch_opts: &Arc<TouchOptions>,
    stream: &TcpStream,
) {
    let msg = match std::str::from_utf8(payload) {
        Ok(s) => s.trim(),
        Err(_) => return,
    };
    if msg.is_empty() {
        return;
    }

    if msg.contains("\"type\":\"ping\"") {
        // Echo as pong (same ts) so the PC can measure round-trip latency.
        let pong = msg.replacen("ping", "pong", 1);
        let _ = send_ctrl(sender, ctrl_wsock, &pong, conn_alive);
    } else if msg.contains("\"type\":\"sub\"") {
        if msg.contains("\"ch\":\"pen\"") {
            start_pen(pen_ctl, shared, sender, conn_alive, stream);
        } else if msg.contains("\"ch\":\"touch\"") {
            // Touch options ride the sub message; default palm rejection ON unless "palm":false.
            touch_opts.always_on.store(msg.contains("\"always_on\":true"), Ordering::Relaxed);
            touch_opts.palm_reject.store(!msg.contains("\"palm\":false"), Ordering::Relaxed);
            crate::log(&format!(
                "data: sub touch (always_on={}, palm_reject={})",
                touch_opts.always_on.load(Ordering::Relaxed),
                touch_opts.palm_reject.load(Ordering::Relaxed)
            ));
            start_touch(touch_ctl, shared, sender, conn_alive, touch_opts, stream);
        }
    } else if msg.contains("\"type\":\"unsub\"") {
        if msg.contains("\"ch\":\"pen\"") {
            pen_ctl.stop();
        } else if msg.contains("\"ch\":\"touch\"") {
            touch_ctl.stop();
        }
    } else if msg.contains("\"type\":\"config\"") {
        crate::control::apply_config(&shared.hub, msg);
    } else if msg.contains("\"type\":\"status\"") {
        crate::control::apply_status(&shared.hub, msg);
    }
}

/// Spawn the pen reader (idempotent — no-op while one is already running).
fn start_pen(
    pen_ctl: &mut StreamCtl,
    shared: &Shared,
    sender: &Arc<Mutex<SendHalf>>,
    conn_alive: &Arc<AtomicBool>,
    stream: &TcpStream,
) {
    if pen_ctl.running() {
        return;
    }
    let wsock = match stream.try_clone() {
        Ok(s) => s,
        Err(e) => {
            crate::log(&format!("data: pen socket clone failed: {e}"));
            return;
        }
    };
    let sender = Arc::clone(sender);
    let conn_alive = Arc::clone(conn_alive);
    let orient = Arc::clone(&shared.orient);
    let pen_in_range = Arc::clone(&shared.pen_in_range);
    pen_ctl.start(move |stop| {
        crate::pen::run_reader(stop, conn_alive, sender, wsock, orient, pen_in_range);
    });
}

/// Spawn the touch reader (idempotent). Options are read live from `touch_opts`, so re-subscribing
/// with changed fields takes effect without a restart.
fn start_touch(
    touch_ctl: &mut StreamCtl,
    shared: &Shared,
    sender: &Arc<Mutex<SendHalf>>,
    conn_alive: &Arc<AtomicBool>,
    touch_opts: &Arc<TouchOptions>,
    stream: &TcpStream,
) {
    if touch_ctl.running() {
        return;
    }
    let wsock = match stream.try_clone() {
        Ok(s) => s,
        Err(e) => {
            crate::log(&format!("data: touch socket clone failed: {e}"));
            return;
        }
    };
    let sender = Arc::clone(sender);
    let conn_alive = Arc::clone(conn_alive);
    let orient = Arc::clone(&shared.orient);
    let app_subs = Arc::clone(&shared.app_subs);
    let pen_in_range = Arc::clone(&shared.pen_in_range);
    let opts = Arc::clone(touch_opts);
    touch_ctl.start(move |stop| {
        crate::touch::run_reader(stop, conn_alive, sender, wsock, orient, app_subs, pen_in_range, opts);
    });
}

/// Encrypt+write a channel-0 control message under the shared send mutex.
fn send_ctrl(
    sender: &Arc<Mutex<SendHalf>>,
    wsock: &mut TcpStream,
    msg: &str,
    conn_alive: &Arc<AtomicBool>,
) -> std::io::Result<()> {
    let mut framed = Vec::with_capacity(1 + msg.len());
    framed.push(CH_CONTROL);
    framed.extend_from_slice(msg.as_bytes());
    let r = sender.lock().unwrap().write_record(wsock, &framed);
    if r.is_err() {
        conn_alive.store(false, Ordering::Relaxed);
    }
    r
}

fn is_loopback(peer: Option<SocketAddr>) -> bool {
    peer.map(|p| p.ip().is_loopback()).unwrap_or(false)
}

/// Lifecycle handle for a per-connection reader thread (pen or touch): start on subscribe, stop on
/// unsubscribe or connection teardown. Start is a no-op while the thread is still running.
struct StreamCtl {
    stop: Arc<AtomicBool>,
    handle: Option<JoinHandle<()>>,
}

impl StreamCtl {
    fn new() -> Self {
        Self { stop: Arc::new(AtomicBool::new(false)), handle: None }
    }

    fn running(&self) -> bool {
        self.handle.as_ref().map_or(false, |h| !h.is_finished())
    }

    fn start<F: FnOnce(Arc<AtomicBool>) + Send + 'static>(&mut self, f: F) {
        if self.running() {
            return;
        }
        self.handle.take(); // reap a previously finished handle
        self.stop.store(false, Ordering::Relaxed);
        let stop = Arc::clone(&self.stop);
        self.handle = Some(thread::spawn(move || f(stop)));
    }

    fn stop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}
