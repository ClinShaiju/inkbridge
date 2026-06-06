//! inkbridge-daemon — runs on the reMarkable Paper Pro.
//!
//! Listens on TCP. When the Windows OTD plugin connects, it holds a wakelock (so the
//! rMPP doesn't autosleep) and reads the pen digitizer (`/dev/input/event2`, "Elan
//! marker input"), streaming one 18-byte PenPacket per SYN_REPORT plus a ~60 Hz idle
//! keepalive.
//!
//! Verified device mechanism (do not change without re-verifying):
//! - xochitl does NOT EVIOCGRAB the pen (a fresh EVIOCGRAB succeeds), so we can read
//!   event2 *alongside* a running xochitl. We never pause/stop xochitl: SIGSTOP-ing it
//!   makes it miss its systemd WatchdogSec=60 sd_notify, which trips the failure path
//!   and reboots the device. Cost of leaving xochitl up: it also inks the e-ink while
//!   we read (cosmetic only; this is an input-only use).
//! - The rMPP uses Android-style opportunistic suspend (`/sys/power/autosleep` = mem).
//!   With no wakelock held the SoC suspends to RAM and the SPI digitizer powers down,
//!   so we get ZERO pen events. We therefore hold a `/sys/power/wake_lock` for the
//!   duration of each client session. (The hardware watchdog is petted by systemd's
//!   RuntimeWatchdog, not by us or xochitl, so the wakelock is purely about keeping the
//!   digitizer powered.)
//!
//! See docs/phase0-findings.md for device facts and protocol/packet.md for the wire format.

mod access;
mod auth;
mod beacon;
mod control;
mod identity;
mod mdns;
mod orientation;
mod packet;
mod touch;

use std::io::Write;
use std::net::{TcpListener, TcpStream};
use std::os::unix::io::AsRawFd;
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::{Duration, Instant};

use packet::PenState;

const PORT: u16 = 9292;
const PEN_NAME: &str = "Elan marker input";
/// Wakelock tag written to /sys/power/wake_lock while ≥1 client is connected.
const WAKELOCK_TAG: &str = "inkbridge";
/// Resend the current state at least this often when idle (keeps OTD's reader fed and
/// makes disconnects detectable promptly).
const KEEPALIVE: Duration = Duration::from_millis(16);

/// Ref-counts connected clients and holds the wakelock while any are active. OTD's
/// settings-apply briefly opens a second TCP source before disposing the first; serving
/// each connection in its own thread (instead of a single-client accept loop) lets that
/// transient succeed cleanly instead of triggering a broken-pipe/reconnect storm. Two
/// concurrent evdev readers on event2 is fine — there is no grab, so each gets all events.
/// Shared with the touch listener (`touch.rs`) so a pen OR touch client keeps the device awake.
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
    let listener = TcpListener::bind(("0.0.0.0", PORT))?;
    log(&format!("listening on 0.0.0.0:{PORT}"));

    // Recover from any previous unclean exit that left the wakelock held.
    wakelock(false);

    // Load the device identity once (UUID locator + P-256 keypair + authorized-PC allow-list).
    // Shared with every connection handler for the mutual auth handshake.
    let id = Arc::new(identity::Identity::load());

    // Start the control plane (TCP :9293) for the on-device visualizer. It relays the area config
    // + link status pushed by the OTD plugin (PC side) and broadcasts "disconnected" when the
    // plugin heartbeat goes stale. Own threads; never touches the pen stream below. Returns the
    // on-device-app subscriber count, used to gate touch passthrough to "AppLoad app is open".
    let app_subs = control::spawn();

    // Broadcast a UDP presence beacon (:9291) so a plugin that gave up its bounded pen-port
    // reconnect attempts wakes and reconnects the moment we're reachable again. Own thread.
    beacon::spawn();

    // Advertise over mDNS/DNS-SD (_inkbridge._tcp) so the plugin discovers us on Wi-Fi with no
    // hardcoded IP. Carries the persisted device id (UUID) for PC1<->rMPP1 filtering. Own thread.
    mdns::spawn(id.device_id.clone());

    // Detect screen orientation (accelerometer + xochitl lock) and publish it; the pen
    // stream below stamps it into every packet so OTD can rotate the area to match.
    let orient = orientation::spawn();

    let refc = Arc::new(Mutex::new(WakeRefcount::default()));

    // Pen-priority palm rejection: the pen stream sets this true while the pen/eraser is in range;
    // the touch stream suppresses touch while it's set, so a palm resting on the screen during
    // drawing doesn't register as touches.
    let pen_in_range = Arc::new(AtomicBool::new(false));

    // Touch passthrough listener (:9294), independent of the pen stream. Reads event3 only
    // while a client is connected; shares the wakelock refcount and the orientation cell, gates
    // streaming on the AppLoad app being open (app_subs) unless the client opts out, and suppresses
    // touch while the pen is in range (pen_in_range).
    touch::spawn(Arc::clone(&refc), Arc::clone(&orient), app_subs, Arc::clone(&pen_in_range),
        Arc::clone(&id));

    for incoming in listener.incoming() {
        match incoming {
            Ok(stream) => {
                let peer = stream.peer_addr().ok();
                if !access::peer_allowed(peer) {
                    log(&format!("pen: rejected {peer:?} — Wi-Fi exposure disabled (USB/loopback only)"));
                    continue; // drop before any work
                }
                let refc = Arc::clone(&refc);
                let orient = Arc::clone(&orient);
                let pen_in_range = Arc::clone(&pen_in_range);
                let id = Arc::clone(&id);
                thread::spawn(move || {
                    log(&format!("client connected: {peer:?}"));
                    // handle_client authenticates BEFORE taking the wakelock, so an unauthorized
                    // peer can't keep the device awake (battery-drain DoS) or read the digitizer.
                    if let Err(e) = handle_client(stream, &orient, &pen_in_range, &refc, &id) {
                        log(&format!("session ended: {e}"));
                    }
                    log("client disconnected");
                });
            }
            Err(e) => log(&format!("accept error: {e}")),
        }
    }
    Ok(())
}

/// One client session: hello, authenticate, then (only if authorized) hold the wakelock and stream.
fn handle_client(
    mut stream: TcpStream,
    orient: &AtomicU8,
    pen_in_range: &AtomicBool,
    refc: &Arc<Mutex<WakeRefcount>>,
    id: &identity::Identity,
) -> std::io::Result<()> {
    stream.set_nodelay(true)?; // latency over throughput
    stream.write_all(b"IBR1")?; // protocol hello (version 1) — also the auth channel tag
    if !auth::server_handshake(&mut stream, b"IBR1", id)? {
        return Ok(()); // unauthorized: no wakelock, no digitizer read
    }

    // Authorized: now hold the device awake (xochitl left running — pausing it would reboot).
    client_in(refc);
    let r = stream_pen(&mut stream, orient, pen_in_range);
    pen_in_range.store(false, Ordering::Relaxed); // pen client gone → unblock touch
    client_out(refc);
    r
}

/// Open the pen device and stream PenPackets until the socket write fails (client gone)
/// or the device read errors.
fn stream_pen(
    stream: &mut TcpStream,
    orient: &AtomicU8,
    pen_in_range: &AtomicBool,
) -> std::io::Result<()> {
    let mut dev = match find_pen() {
        Some(d) => d,
        None => {
            log("ERROR: pen device 'Elan marker input' not found");
            return Ok(());
        }
    };

    // Read the evdev fd non-blocking and drive the loop with poll(), so we can both
    // forward events at full rate and emit keepalives while the pen is in range.
    let fd = dev.as_raw_fd();
    unsafe {
        let flags = libc::fcntl(fd, libc::F_GETFL);
        libc::fcntl(fd, libc::F_SETFL, flags | libc::O_NONBLOCK);
    }
    // Also poll the client socket so we notice a disconnect even when the pen is idle
    // (we no longer rely on a keepalive write failing). OTD only reads, never writes, so
    // any readability/hangup on this fd means the client closed.
    let sock_fd = stream.as_raw_fd();

    let start = Instant::now();
    let mut state = PenState::default();
    let mut buf = [0u8; packet::SIZE];
    let mut last_send = Instant::now();

    loop {
        let mut pfds = [
            libc::pollfd { fd, events: libc::POLLIN, revents: 0 },
            libc::pollfd { fd: sock_fd, events: libc::POLLIN, revents: 0 },
        ];
        let pr = unsafe { libc::poll(pfds.as_mut_ptr(), 2, 8) }; // wake on event or after 8ms
        if pr < 0 {
            let e = std::io::Error::last_os_error();
            if e.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            return Err(e);
        }

        // Client closed the connection (EOF/hangup/error on the socket).
        if pfds[1].revents & (libc::POLLIN | libc::POLLHUP | libc::POLLERR) != 0 {
            return Ok(());
        }

        if pfds[0].revents & libc::POLLIN != 0 {
            match dev.fetch_events() {
                Ok(events) => {
                    use evdev::EventType;
                    for ev in events {
                        let code = ev.code();
                        let val = ev.value();
                        match ev.event_type() {
                            EventType::ABSOLUTE => state.update_abs(code, val),
                            EventType::KEY => state.update_key(code, val),
                            EventType::SYNCHRONIZATION => {
                                if code == 0 {
                                    // Publish pen proximity for the touch stream's palm rejection.
                                    pen_in_range.store(state.in_range(), Ordering::Relaxed);
                                    let ts = start.elapsed().as_micros() as u32;
                                    state.serialize(ts, orient.load(Ordering::Relaxed), &mut buf);
                                    stream.write_all(&buf)?; // Err = client disconnected
                                    last_send = Instant::now();
                                }
                            }
                            _ => {}
                        }
                    }
                }
                Err(ref e) if e.raw_os_error() == Some(libc::EAGAIN) => {}
                Err(e) => return Err(e),
            }
        }

        // Resend the current state ~60 Hz ONLY while the pen is in range. Once the pen is
        // lifted away we go quiet, so OTD stops re-asserting the absolute cursor position
        // and the physical mouse is usable again (a continuous keepalive would otherwise
        // pin the cursor to the pen's last spot). The real out-of-range SYN_REPORT above is
        // still forwarded, so OTD sees the pen leave proximity.
        if state.in_range() && last_send.elapsed() >= KEEPALIVE {
            let ts = start.elapsed().as_micros() as u32;
            state.serialize(ts, orient.load(Ordering::Relaxed), &mut buf);
            stream.write_all(&buf)?;
            last_send = Instant::now();
        }
    }
}

/// Find the pen digitizer by device name (resolve-by-name, not a hardcoded event node).
fn find_pen() -> Option<evdev::Device> {
    for (_path, dev) in evdev::enumerate() {
        if dev.name() == Some(PEN_NAME) {
            return Some(dev);
        }
    }
    None
}

/// Acquire/release an opportunistic-suspend wakelock via the kernel wakelock interface.
/// Required because the rMPP autosleeps (`/sys/power/autosleep` = mem); without this the
/// SoC suspends and the SPI digitizer stops producing events. Best-effort.
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
