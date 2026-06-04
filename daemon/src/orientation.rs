//! Device-orientation detection for the rMPP.
//!
//! Publishes the current screen orientation (0=portrait native, 1/2/3 = 90/180/270° CW)
//! into an `AtomicU8`. The pen stream packs it into PenPacket flags bits 6-7 (see
//! protocol/packet.md) so the Windows side can rotate the area to match.
//!
//! Two sources, combined to respect what the user actually wants:
//!  - **xochitl rotation LOCKED** (`OrientationLocked=true` in xochitl.conf) → follow the
//!    locked value (`PreviousLockedOrientation`) and do NOT auto-rotate. Verified: xochitl
//!    persists this to the conf live (within our poll interval).
//!  - **UNLOCKED** → *latch-on-tilt* from the LIS2DW12 accelerometer (`iio:device0`). When
//!    the device is tilted enough that gravity has a clear in-plane component we read its
//!    angle and latch the orientation; while it lies flat (gravity all on Z) we hold the
//!    last value. This is the only thing that can work for desk use: flat-on-desk has no
//!    orientation signal at all, but *reorienting the device always involves a tilt*, and
//!    that tilt is easy to read (measured ~14000 LSB in-plane when held vs ~250 when flat).
//!
//! Set `INKBRIDGE_ORIENT_DEBUG=1` to log the live angle/axes for calibration.

use std::sync::atomic::{AtomicU8, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

const ACCEL_X: &str = "/sys/bus/iio/devices/iio:device0/in_accel_x_raw";
const ACCEL_Y: &str = "/sys/bus/iio/devices/iio:device0/in_accel_y_raw";
const ACCEL_Z: &str = "/sys/bus/iio/devices/iio:device0/in_accel_z_raw";
const XOCHITL_CONF: &str = "/home/root/.config/remarkable/xochitl.conf";

/// Minimum in-plane gravity |(x,y)| (raw LSB) to trust the angle. ~1g ≈ 10400 LSB here, so
/// 3500 ≈ a ~20° tilt off flat — below this we can't tell orientation, so hold the last.
const TILT_MIN: f64 = 3500.0;
/// Extra degrees past a 45° quadrant boundary required before switching, to stop flapping.
const HYST_DEG: f64 = 12.0;
/// Rotates the raw accel angle so quadrant 0 lands on portrait-native. Calibration constant
/// (degrees); tune from the INKBRIDGE_ORIENT_DEBUG log if a pose maps to the wrong quadrant.
const ANGLE_OFFSET: f64 = 90.0;
const PERIOD: Duration = Duration::from_millis(100);

/// Spawn the detector thread and return the shared current-orientation cell (0..3).
pub fn spawn() -> Arc<AtomicU8> {
    let cur = Arc::new(AtomicU8::new(0));
    let worker = Arc::clone(&cur);
    thread::spawn(move || run(worker));
    cur
}

fn run(cur: Arc<AtomicU8>) {
    let debug = std::env::var("INKBRIDGE_ORIENT_DEBUG").as_deref() == Ok("1");
    let mut orient: u8 = 0;
    let mut last_logged: i32 = -1;
    let mut last_dbg = Instant::now();

    loop {
        match read_xochitl_lock() {
            Some((true, locked_val)) => {
                orient = map_xochitl(locked_val); // follow the user's locked choice
            }
            _ => {
                // Unlocked (or conf unreadable): auto-rotate via the accelerometer.
                if let Some((x, y, _z)) = read_accel() {
                    let mag = (x * x + y * y).sqrt();
                    if mag >= TILT_MIN {
                        let angle = y.atan2(x).to_degrees();
                        if let Some(cand) = quadrant(angle, orient) {
                            orient = cand;
                        }
                        if debug && last_dbg.elapsed() >= Duration::from_secs(1) {
                            log(&format!(
                                "calib: x={x:.0} y={y:.0} mag={mag:.0} angle={angle:.1} -> {orient}"
                            ));
                            last_dbg = Instant::now();
                        }
                    }
                }
            }
        }

        cur.store(orient, Ordering::Relaxed);
        if orient as i32 != last_logged {
            log(&format!("orientation -> {orient}"));
            last_logged = orient as i32;
        }
        thread::sleep(PERIOD);
    }
}

/// Map a raw in-plane gravity angle to an orientation 0..3, with hysteresis so the value
/// doesn't flap at the 45° boundaries. Returns `None` to keep the current orientation.
fn quadrant(angle_deg: f64, current: u8) -> Option<u8> {
    let a = wrap360(angle_deg + ANGLE_OFFSET);
    let cur_center = current as f64 * 90.0;
    let mut delta = (a - cur_center).abs();
    if delta > 180.0 {
        delta = 360.0 - delta;
    }
    if delta <= 45.0 + HYST_DEG {
        return None; // still comfortably within the current quadrant
    }
    let cand = (((a + 45.0) / 90.0) as i64).rem_euclid(4) as u8;
    if cand == current {
        None
    } else {
        Some(cand)
    }
}

fn wrap360(d: f64) -> f64 {
    let m = d % 360.0;
    if m < 0.0 {
        m + 360.0
    } else {
        m
    }
}

/// xochitl's locked-orientation enum -> our 0..3 convention. Identity until calibrated
/// against the device (0 = portrait native is confirmed; 1/2/3 to be verified).
fn map_xochitl(v: u8) -> u8 {
    v & 0x03
}

/// Read accel x/y/z raw (LSB). Best-effort; `None` if any axis is unreadable.
fn read_accel() -> Option<(f64, f64, f64)> {
    let x = read_i32(ACCEL_X)?;
    let y = read_i32(ACCEL_Y)?;
    let z = read_i32(ACCEL_Z)?;
    Some((x as f64, y as f64, z as f64))
}

fn read_i32(path: &str) -> Option<i32> {
    std::fs::read_to_string(path).ok()?.trim().parse().ok()
}

/// Parse (locked, PreviousLockedOrientation) from xochitl.conf. `None` if unreadable, which
/// the caller treats as "unlocked" (fall back to the accelerometer).
fn read_xochitl_lock() -> Option<(bool, u8)> {
    let conf = std::fs::read_to_string(XOCHITL_CONF).ok()?;
    let mut locked = false;
    let mut val: u8 = 0;
    for line in conf.lines() {
        if let Some(v) = line.strip_prefix("OrientationLocked=") {
            locked = v.trim().eq_ignore_ascii_case("true");
        } else if let Some(v) = line.strip_prefix("PreviousLockedOrientation=") {
            val = v.trim().parse().unwrap_or(0);
        }
    }
    Some((locked, val))
}

fn log(msg: &str) {
    eprintln!("[inkbridge] {msg}");
}
