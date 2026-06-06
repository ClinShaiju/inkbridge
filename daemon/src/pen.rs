//! inkbridge pen reader — opens the rMPP pen digitizer (`/dev/input/event2`, "Elan marker input")
//! and streams one 18-byte `PenPacket` per evdev `SYN_REPORT`, plus a ~60 Hz keepalive while the pen
//! is in range. In the v2 muxed transport this runs as one reader thread of a single connection: it
//! writes each packet as channel `CH_PEN` through the connection's shared encrypted send half
//! (`mux.rs`), rather than owning its own socket.
//!
//! Verified device mechanism (do not change without re-verifying — see docs/phase0-findings.md and
//! the header of main.rs): xochitl does NOT grab event2, so we read it un-grabbed alongside a running
//! xochitl (never pause it — that reboots the device); the rMPP autosleeps, so the connection holds a
//! wakelock for the digitizer to stay powered.

use std::net::TcpStream;
use std::os::unix::io::AsRawFd;
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::crypto::SendHalf;
use crate::mux::CH_PEN;
use crate::packet::{self, PenState};

const PEN_NAME: &str = "Elan marker input";
/// Resend the current state at least this often while the pen is in range (keeps OTD's reader fed and
/// makes disconnects detectable promptly). Matches the v1 cadence.
const KEEPALIVE: Duration = Duration::from_millis(16);

/// Find the pen digitizer by device name (resolve-by-name, not a hardcoded event node).
pub fn find_pen() -> Option<evdev::Device> {
    for (_path, dev) in evdev::enumerate() {
        if dev.name() == Some(PEN_NAME) {
            return Some(dev);
        }
    }
    None
}

/// Stream PenPackets over the shared send half until the connection dies, `stop` is set (pen
/// unsubscribed), or the device read errors. Each packet is framed as `[CH_PEN][18-byte packet]` and
/// written through `sender` (the connection's `Arc<Mutex<SendHalf>>`); `wsock` is this thread's own
/// clone of the connection socket, written only while the mutex is held so records never interleave.
///
/// Unlike v1 this does NOT poll the socket for disconnect: inbound bytes are control records consumed
/// by the reader thread, so a readable socket here is normal. We instead exit when `conn_alive` goes
/// false (the reader saw EOF) or a framed write fails.
pub fn run_reader(
    stop: Arc<AtomicBool>,
    conn_alive: Arc<AtomicBool>,
    sender: Arc<Mutex<SendHalf>>,
    mut wsock: TcpStream,
    orient: Arc<AtomicU8>,
    pen_in_range: Arc<AtomicBool>,
) {
    let mut dev = match find_pen() {
        Some(d) => d,
        None => {
            crate::log("ERROR: pen device 'Elan marker input' not found");
            return;
        }
    };

    // Read the evdev fd non-blocking and drive the loop with poll(), so we can both forward events at
    // full rate and emit keepalives while the pen is in range.
    let fd = dev.as_raw_fd();
    unsafe {
        let flags = libc::fcntl(fd, libc::F_GETFL);
        libc::fcntl(fd, libc::F_SETFL, flags | libc::O_NONBLOCK);
    }

    crate::log("pen: reader started");
    let start = Instant::now();
    let mut state = PenState::default();
    let mut buf = [0u8; packet::SIZE];
    let mut framed = [0u8; 1 + packet::SIZE];
    framed[0] = CH_PEN;
    let mut last_send = Instant::now();

    loop {
        if stop.load(Ordering::Relaxed) || !conn_alive.load(Ordering::Relaxed) {
            break;
        }

        let mut pfds = [libc::pollfd { fd, events: libc::POLLIN, revents: 0 }];
        let pr = unsafe { libc::poll(pfds.as_mut_ptr(), 1, 8) }; // wake on event or after 8ms
        if pr < 0 {
            let e = std::io::Error::last_os_error();
            if e.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            crate::log(&format!("pen: poll error: {e}"));
            break;
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
                                    framed[1..].copy_from_slice(&buf);
                                    if send(&sender, &mut wsock, &framed, &conn_alive).is_err() {
                                        return;
                                    }
                                    last_send = Instant::now();
                                }
                            }
                            _ => {}
                        }
                    }
                }
                Err(ref e) if e.raw_os_error() == Some(libc::EAGAIN) => {}
                Err(e) => {
                    crate::log(&format!("pen: device read error: {e}"));
                    break;
                }
            }
        }

        // Resend the current state ~60 Hz ONLY while the pen is in range. Once the pen is lifted away
        // we go quiet, so OTD stops re-asserting the absolute cursor position and the physical mouse is
        // usable again. The real out-of-range SYN_REPORT above is still forwarded.
        if state.in_range() && last_send.elapsed() >= KEEPALIVE {
            let ts = start.elapsed().as_micros() as u32;
            state.serialize(ts, orient.load(Ordering::Relaxed), &mut buf);
            framed[1..].copy_from_slice(&buf);
            if send(&sender, &mut wsock, &framed, &conn_alive).is_err() {
                return;
            }
            last_send = Instant::now();
        }
    }

    // Pen reader gone → unblock touch (a stale pen-in-range must not keep suppressing touch).
    pen_in_range.store(false, Ordering::Relaxed);
    crate::log("pen: reader stopped");
}

/// Encrypt+write one framed record under the shared mutex. On failure the connection is dead — flag
/// it so the other reader threads exit too.
fn send(
    sender: &Arc<Mutex<SendHalf>>,
    wsock: &mut TcpStream,
    framed: &[u8],
    conn_alive: &Arc<AtomicBool>,
) -> std::io::Result<()> {
    let r = sender.lock().unwrap().write_record(wsock, framed);
    if r.is_err() {
        conn_alive.store(false, Ordering::Relaxed);
    }
    r
}
