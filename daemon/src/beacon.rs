//! inkbridge presence beacon — UDP broadcast on :9291.
//!
//! The OTD plugin connects to the pen port (:9292). When it can't reach us (USB unplugged,
//! device asleep, daemon restarting) it retries with growing backoff for ~1 min and then
//! STOPS hammering the network, going quiet to wait for the device to announce itself.
//!
//! That announcement is this beacon: we broadcast a tiny `IBR1` datagram ~1/s so the plugin's
//! listener wakes and reconnects immediately once we're reachable again — instead of either
//! retrying forever or staying dark until the user re-saves OTD settings.
//!
//! Best-effort and std-only except a `getifaddrs()` call to find each interface's broadcast
//! address: a single send to 255.255.255.255 only egresses one interface, so the USB-RNDIS
//! subnet (10.11.99.0/24) would be missed whenever Wi-Fi is also up. We instead send to every
//! up, broadcast-capable IPv4 interface. The payload mirrors the pen hello bytes (`IBR1`).

use std::net::{Ipv4Addr, SocketAddrV4, UdpSocket};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

use hmac::{Hmac, Mac};
use sha2::Sha256;

use crate::identity::Identity;

const BEACON_PORT: u16 = 9291;
const BEACON_INTERVAL: Duration = Duration::from_millis(1000);
/// Magic prefix (also the pen-stream hello), kept so a legacy/unpaired plugin still recognizes us.
const BEACON_MAGIC: &[u8] = b"IBR1";

/// Start broadcasting the presence beacon in its own thread. Never touches the pen stream.
/// Payload: `IBR1 ‖ device_id(36 ascii) ‖ HMAC-SHA256(beacon_key, IBR1‖device_id)[32]` — so a plugin
/// that holds the paired beacon key reacts only to its own device and ignores forged broadcasts (T9).
pub fn spawn(id: Arc<Identity>) {
    thread::spawn(move || beacon_loop(id));
}

fn beacon_loop(id: Arc<Identity>) {
    let sock = match UdpSocket::bind(("0.0.0.0", 0)) {
        Ok(s) => s,
        Err(e) => {
            crate::log(&format!("beacon: bind failed: {e}"));
            return;
        }
    };
    if let Err(e) = sock.set_broadcast(true) {
        crate::log(&format!("beacon: set_broadcast failed: {e}"));
        return;
    }

    // Precompute the fixed payload: magic ‖ id ‖ HMAC(beacon_key, magic‖id).
    let mut payload = Vec::with_capacity(4 + 36 + 32);
    payload.extend_from_slice(BEACON_MAGIC);
    payload.extend_from_slice(id.device_id.as_bytes());
    let mut mac = <Hmac<Sha256>>::new_from_slice(id.beacon_key()).expect("hmac key");
    mac.update(&payload);
    payload.extend_from_slice(&mac.finalize().into_bytes());

    crate::log(&format!("presence beacon broadcasting on :{BEACON_PORT} (authenticated)"));
    loop {
        let targets = broadcast_addrs();
        if targets.is_empty() {
            // No enumerable interface broadcast addr — fall back to limited broadcast.
            let _ = sock.send_to(&payload, (Ipv4Addr::BROADCAST, BEACON_PORT));
        } else {
            for b in targets {
                let _ = sock.send_to(&payload, SocketAddrV4::new(b, BEACON_PORT));
            }
        }
        thread::sleep(BEACON_INTERVAL);
    }
}

/// Broadcast address of every up, broadcast-capable IPv4 interface (via getifaddrs).
/// Sending to each (rather than 255.255.255.255 once) guarantees the USB-RNDIS subnet is
/// reached even when Wi-Fi is also up, since limited broadcast egresses only one interface.
fn broadcast_addrs() -> Vec<Ipv4Addr> {
    let mut out = Vec::new();
    unsafe {
        let mut ifap: *mut libc::ifaddrs = std::ptr::null_mut();
        if libc::getifaddrs(&mut ifap) != 0 {
            return out;
        }
        let mut cur = ifap;
        while !cur.is_null() {
            let ifa = &*cur;
            cur = ifa.ifa_next;

            let flags = ifa.ifa_flags as i32;
            if flags & libc::IFF_UP == 0 || flags & libc::IFF_BROADCAST == 0 {
                continue;
            }
            if ifa.ifa_addr.is_null() || ifa.ifa_ifu.is_null() {
                continue;
            }
            if (*ifa.ifa_addr).sa_family as i32 != libc::AF_INET {
                continue;
            }
            // For IFF_BROADCAST interfaces ifa_ifu carries the broadcast address. s_addr is
            // already in network byte order; to_ne_bytes() reads it as the octets in order
            // (endianness-correct on both LE and BE hosts).
            let bcast = &*(ifa.ifa_ifu as *const libc::sockaddr_in);
            let addr = Ipv4Addr::from(bcast.sin_addr.s_addr.to_ne_bytes());
            if addr.is_loopback() || addr.is_unspecified() {
                continue;
            }
            out.push(addr);
        }
        libc::freeifaddrs(ifap);
    }
    out
}
