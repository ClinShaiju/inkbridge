//! inkbridge touch passthrough — reads the rMPP finger digitizer (`/dev/input/event3`,
//! "Elan touch input") and streams a fixed 88-byte `TouchPacket` per evdev `SYN_REPORT`
//! over TCP `:9294`. The Windows side (OTD plugin's TouchService) decides what to do with
//! it: nothing (Disabled — it never connects), genuine multitouch (`InjectTouchInput`), or
//! gesture recognition. A connected client is necessary but not sufficient: touch frames flow
//! only while the on-device AppLoad app is open (the control-plane subscriber count, `app_subs`)
//! — so leaving the app stops touch — unless the client opts into "always on" via the options
//! byte it sends after the hello.
//!
//! Verified device facts (docs/touch-feasibility.md, probed 2026-06-04):
//! - `event3` is a 10-slot **multitouch protocol B** device (`ABS_MT_SLOT` max 9,
//!   `ABS_MT_TRACKING_ID`), pos X 0..2064 / Y 0..2832, pressure/major/distance 0..255,
//!   `ABS_MT_TOOL_TYPE` (2 = palm), `INPUT_PROP_DIRECT`. Same physical surface as the pen at
//!   ×5.42 lower resolution (identical 0.729 aspect).
//! - We read `event3` **un-grabbed**, exactly like the pen: grabbing it would stop the stock
//!   reMarkable UI (and the AppLoad app itself) from seeing touch, so you couldn't even exit the
//!   app. xochitl reacting to touch is the same cosmetic cost as it inking under the pen. The
//!   app-open gate (not a grab) is what keeps touch from leaking to Windows when you're not using
//!   it.
//! - Power: the rMPP autosleeps (`/sys/power/autosleep = mem`); we share main.rs's wakelock
//!   refcount so the digitizer stays powered for the session.

use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::os::unix::io::AsRawFd;
use std::sync::atomic::{AtomicBool, AtomicU8, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Instant;

use crate::{client_in, client_out, WakeRefcount};

const PORT: u16 = 9294;
const TOUCH_NAME: &str = "Elan touch input";

/// Fixed `TouchPacket` size. Must stay byte-for-byte identical to `protocol/touch-packet.md`
/// and the Windows decoder in `otd-plugin/TouchPacket.cs`.
pub const SIZE: usize = 88;
const SLOTS: usize = 10;

// evdev EV_ABS multitouch (protocol B) codes — see the event3 capability dump.
const ABS_MT_SLOT: u16 = 47;
const ABS_MT_TOUCH_MAJOR: u16 = 48;
const ABS_MT_POSITION_X: u16 = 53;
const ABS_MT_POSITION_Y: u16 = 54;
const ABS_MT_TOOL_TYPE: u16 = 55;
const ABS_MT_TRACKING_ID: u16 = 57;
const ABS_MT_PRESSURE: u16 = 58;
const MT_TOOL_PALM: i32 = 2;

/// One contact slot, accumulated from `ABS_MT_*` events between `SYN_REPORT`s.
#[derive(Clone, Copy, Default)]
struct Slot {
    tracking_id: i32, // -1 (or default) = no finger in this slot
    active: bool,
    x: u16,
    y: u16,
    pressure: u8,
    major: u8,
    tool_type: i32,
}

/// 10-slot touch state. Mirrors the protocol-B kernel model: `cur` selects the slot that
/// subsequent `ABS_MT_*` values apply to until the next `ABS_MT_SLOT`.
pub struct TouchState {
    slots: [Slot; SLOTS],
    cur: usize,
}

impl Default for TouchState {
    fn default() -> Self {
        Self { slots: [Slot::default(); SLOTS], cur: 0 }
    }
}

impl TouchState {
    pub fn update_abs(&mut self, code: u16, val: i32) {
        match code {
            ABS_MT_SLOT => {
                if (0..SLOTS as i32).contains(&val) {
                    self.cur = val as usize;
                }
            }
            ABS_MT_TRACKING_ID => {
                let s = &mut self.slots[self.cur];
                if val < 0 {
                    s.active = false;
                    s.tracking_id = -1;
                } else {
                    s.active = true;
                    s.tracking_id = val;
                }
            }
            ABS_MT_POSITION_X => self.slots[self.cur].x = clamp_u16(val),
            ABS_MT_POSITION_Y => self.slots[self.cur].y = clamp_u16(val),
            ABS_MT_PRESSURE => self.slots[self.cur].pressure = clamp_u8(val),
            ABS_MT_TOUCH_MAJOR => self.slots[self.cur].major = clamp_u8(val),
            ABS_MT_TOOL_TYPE => self.slots[self.cur].tool_type = val,
            _ => {}
        }
    }

    /// True while ≥1 non-palm finger is down — used only for logging/debug.
    fn any_active(&self) -> bool {
        self.slots.iter().any(|s| s.active && s.tool_type != MT_TOOL_PALM)
    }

    /// Serialize a full 10-slot snapshot. The PC diffs successive snapshots to derive
    /// DOWN/UPDATE/UP. Palm contacts are reported inactive (rejected at the source).
    fn serialize(&self, ts_us: u32, orientation: u8, buf: &mut [u8; SIZE]) {
        // ── 8-byte header ──
        buf[0..4].copy_from_slice(&ts_us.to_le_bytes());
        buf[4] = orientation & 0x03;
        let count = self
            .slots
            .iter()
            .filter(|s| s.active && s.tool_type != MT_TOOL_PALM)
            .count() as u8;
        buf[5] = count;
        buf[6] = 0x01; // flags: low nibble = version 1
        buf[7] = 0; // reserved

        // ── 10 × 8-byte contact records ──
        for (i, s) in self.slots.iter().enumerate() {
            let off = 8 + i * 8;
            let active = s.active && s.tool_type != MT_TOOL_PALM;
            buf[off] = i as u8; // slot
            buf[off + 1] = active as u8;
            buf[off + 2..off + 4].copy_from_slice(&s.x.to_le_bytes());
            buf[off + 4..off + 6].copy_from_slice(&s.y.to_le_bytes());
            buf[off + 6] = s.pressure;
            buf[off + 7] = s.major;
        }
    }
}

/// Start the touch listener on its own thread. Shares the wakelock refcount with the pen
/// path so either keeps the digitizer powered; reads the live orientation cell the pen
/// stamps, so touch coordinates can be rotated PC-side to match the screen.
pub fn spawn(
    refc: Arc<Mutex<WakeRefcount>>,
    orient: Arc<AtomicU8>,
    app_subs: Arc<AtomicUsize>,
    pen_in_range: Arc<AtomicBool>,
    id: Arc<crate::identity::Identity>,
) {
    thread::spawn(move || {
        let listener = match TcpListener::bind(("0.0.0.0", PORT)) {
            Ok(l) => l,
            Err(e) => {
                crate::log(&format!("touch: bind :{PORT} failed: {e}"));
                return;
            }
        };
        crate::log(&format!("touch plane listening on 0.0.0.0:{PORT}"));
        for incoming in listener.incoming() {
            let stream = match incoming {
                Ok(s) => s,
                Err(e) => {
                    crate::log(&format!("touch: accept error: {e}"));
                    continue;
                }
            };
            let peer = stream.peer_addr().ok();
            if !crate::access::peer_allowed(peer) {
                crate::log(&format!("touch: rejected {peer:?} — Wi-Fi exposure disabled (USB/loopback only)"));
                continue;
            }
            let refc = Arc::clone(&refc);
            let orient = Arc::clone(&orient);
            let app_subs = Arc::clone(&app_subs);
            let pen_in_range = Arc::clone(&pen_in_range);
            let id = Arc::clone(&id);
            thread::spawn(move || {
                crate::log(&format!("touch client connected: {peer:?}"));
                // Authenticates before the wakelock (see handle_client) — an unauthorized peer
                // can't keep the device awake or read the touchscreen.
                if let Err(e) = handle_client(stream, &orient, &app_subs, &pen_in_range, &refc, &id) {
                    crate::log(&format!("touch session ended: {e}"));
                }
                crate::log("touch client disconnected");
            });
        }
    });
}

fn handle_client(
    mut stream: TcpStream,
    orient: &AtomicU8,
    app_subs: &AtomicUsize,
    pen_in_range: &AtomicBool,
    refc: &Arc<Mutex<WakeRefcount>>,
    id: &crate::identity::Identity,
) -> std::io::Result<()> {
    stream.set_nodelay(true)?;
    stream.write_all(b"IBT1")?; // touch protocol hello (version 1) — also the auth channel tag
    if !crate::auth::server_handshake(&mut stream, b"IBT1", id)? {
        return Ok(()); // unauthorized: no wakelock, no touchscreen read
    }

    // Authorized: hold the device awake for the touch session.
    client_in(refc);
    let r = serve(&mut stream, orient, app_subs, pen_in_range);
    client_out(refc);
    r
}

/// Read the client options byte, then stream touch until the socket breaks.
fn serve(
    stream: &mut TcpStream,
    orient: &AtomicU8,
    app_subs: &AtomicUsize,
    pen_in_range: &AtomicBool,
) -> std::io::Result<()> {
    // Client replies with one options byte: bit0 = "always on" (stream even when the AppLoad
    // app is closed); bit1 = "no palm rejection" (don't suppress touch while the pen is in range).
    // Older/absent clients would block here, but plugin + daemon ship together.
    let mut opt = [0u8; 1];
    stream.read_exact(&mut opt)?;
    let always_on = opt[0] & 0x01 != 0;
    let palm_reject = opt[0] & 0x02 == 0; // bit set = DISABLE palm rejection
    crate::log(&format!("touch: client options always_on={always_on} palm_reject={palm_reject}"));

    stream_touch(stream, orient, app_subs, always_on, palm_reject, pen_in_range)
}

/// Open `event3` (un-grabbed) and stream `TouchPacket`s until the socket breaks. Frames are
/// emitted only while gated on (AppLoad app open, or `always_on`); leaving the app stops touch.
fn stream_touch(
    stream: &mut TcpStream,
    orient: &AtomicU8,
    app_subs: &AtomicUsize,
    always_on: bool,
    palm_reject: bool,
    pen_in_range: &AtomicBool,
) -> std::io::Result<()> {
    let mut dev = match find_touch() {
        Some(d) => d,
        None => {
            crate::log("ERROR: touch device 'Elan touch input' not found");
            return Ok(());
        }
    };

    let fd = dev.as_raw_fd();
    unsafe {
        let flags = libc::fcntl(fd, libc::F_GETFL);
        libc::fcntl(fd, libc::F_SETFL, flags | libc::O_NONBLOCK);
    }
    let sock_fd = stream.as_raw_fd();

    let start = Instant::now();
    let mut state = TouchState::default();
    let mut buf = [0u8; SIZE];
    let mut had_active = false; // did the last emitted frame carry contacts?
    let mut was_gated = false; // was passthrough enabled on the previous report?

    loop {
        let mut pfds = [
            libc::pollfd { fd, events: libc::POLLIN, revents: 0 },
            libc::pollfd { fd: sock_fd, events: libc::POLLIN, revents: 0 },
        ];
        let pr = unsafe { libc::poll(pfds.as_mut_ptr(), 2, 100) };
        if pr < 0 {
            let e = std::io::Error::last_os_error();
            if e.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            return Err(e);
        }

        // Client closed (EOF/hangup/error on the socket).
        if pfds[1].revents & (libc::POLLIN | libc::POLLHUP | libc::POLLERR) != 0 {
            return Ok(());
        }

        if pfds[0].revents & libc::POLLIN != 0 {
            match dev.fetch_events() {
                Ok(events) => {
                    use evdev::EventType;
                    for ev in events {
                        match ev.event_type() {
                            EventType::ABSOLUTE => state.update_abs(ev.code(), ev.value()),
                            EventType::SYNCHRONIZATION => {
                                if ev.code() == 0 {
                                    // Gate: forward touch only while the AppLoad app is open
                                    // (≥1 control subscriber), unless the client opted "always on";
                                    // and suppress touch entirely while the pen is in range (palm
                                    // rejection) so a resting palm doesn't register while drawing.
                                    let app_ok = always_on || app_subs.load(Ordering::Relaxed) > 0;
                                    let pen_blocks = palm_reject && pen_in_range.load(Ordering::Relaxed);
                                    let gated = app_ok && !pen_blocks;
                                    let ts = start.elapsed().as_micros() as u32;
                                    let orientation = orient.load(Ordering::Relaxed);
                                    if gated {
                                        let active = state.any_active();
                                        // Emit while fingers are down, plus one final all-empty
                                        // frame on the transition to no-fingers (PC issues UPs).
                                        if active || had_active {
                                            state.serialize(ts, orientation, &mut buf);
                                            stream.write_all(&buf)?;
                                        }
                                        had_active = active;
                                    } else if was_gated || had_active {
                                        // Just gated off (app closed) or contacts were live: send
                                        // one empty frame so the PC releases everything, then stay
                                        // quiet so the rMPP's own touch is unaffected.
                                        empty_frame(ts, orientation, &mut buf);
                                        stream.write_all(&buf)?;
                                        had_active = false;
                                    }
                                    was_gated = gated;
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
    }
}

/// Write an all-empty (no contacts) frame: header only, every slot inactive. Used to release
/// all PC-side contacts when passthrough gates off (app closed).
fn empty_frame(ts_us: u32, orientation: u8, buf: &mut [u8; SIZE]) {
    *buf = [0u8; SIZE];
    buf[0..4].copy_from_slice(&ts_us.to_le_bytes());
    buf[4] = orientation & 0x03;
    buf[6] = 0x01; // flags: version 1
    for i in 0..SLOTS {
        buf[8 + i * 8] = i as u8; // slot index (active byte stays 0)
    }
}

/// Resolve the touch digitizer by name (not a hardcoded event node).
fn find_touch() -> Option<evdev::Device> {
    for (_path, dev) in evdev::enumerate() {
        if dev.name() == Some(TOUCH_NAME) {
            return Some(dev);
        }
    }
    None
}

fn clamp_u16(v: i32) -> u16 {
    v.clamp(0, u16::MAX as i32) as u16
}

fn clamp_u8(v: i32) -> u8 {
    v.clamp(0, u8::MAX as i32) as u8
}
