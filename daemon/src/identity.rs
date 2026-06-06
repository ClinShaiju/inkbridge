//! inkbridge device identity: a locator UUID + a long-term P-256 keypair.
//!
//! - **UUIDv4** (`/home/root/inkbridge/identity`): the device *locator*, advertised in the mDNS TXT
//!   `id=` so the plugin can filter discovery to the device it paired with. Public, spoofable — **not**
//!   a credential. Deliberately not the MAC (randomized, spoofable, hardware-leaking, per-interface).
//! - **P-256 keypair** (`/home/root/inkbridge/device-key`, 0600): the actual identity. The plugin
//!   pins this device's public key on first (USB) contact and verifies it on every connection.
//! - **authorized_keys** (`/home/root/inkbridge/authorized_keys`): public keys of PCs allowed to
//!   connect. Trust-on-first-use: the first PC to connect (expected over the USB cable) is pinned;
//!   thereafter only listed PCs are accepted. See docs/security.md / wifi-connectivity-feasibility.md §5.
//!
//! P-256 chosen because it is Matter's pairing curve, native in the .NET 8 plugin (no third-party
//! crypto), and available as pure-Rust (`p256`) so the musl daemon needs no C/`ring` dependency.

use std::collections::HashSet;
use std::fs;
use std::path::Path;
use std::sync::Mutex;

use p256::ecdsa::SigningKey;
use rand_core::{OsRng, RngCore};

const DIR: &str = "/home/root/inkbridge";
const ID_FILE: &str = "/home/root/inkbridge/identity";
const KEY_FILE: &str = "/home/root/inkbridge/device-key";
const AUTH_FILE: &str = "/home/root/inkbridge/authorized_keys";
const BKEY_FILE: &str = "/home/root/inkbridge/beacon-key";

/// The device's long-term identity, loaded once at startup.
pub struct Identity {
    pub device_id: String,
    signing: SigningKey,
    /// Uncompressed SEC1 public key (0x04 ‖ X ‖ Y), 65 bytes.
    public: [u8; 65],
    /// Allow-listed PC public keys (hex of the 65-byte SEC1 form). Guarded for concurrent connects.
    authorized: Mutex<HashSet<String>>,
    /// Device-wide secret for authenticating the presence beacon. Handed to authorized PCs over the
    /// encrypted control channel; the beacon is HMAC'd with it so forged broadcasts are ignored (T9).
    beacon_key: [u8; 32],
}

impl Identity {
    /// Load (or generate + persist) the device id, keypair, and authorized-keys set.
    pub fn load() -> Identity {
        let device_id = device_id();
        let signing = load_or_make_key();
        let public = pub_bytes(&signing);
        let authorized = Mutex::new(load_authorized());
        let beacon_key = load_or_make_beacon_key();
        Identity { device_id, signing, public, authorized, beacon_key }
    }

    /// Device-wide beacon HMAC secret (handed to authorized PCs over the encrypted control channel).
    pub fn beacon_key(&self) -> &[u8; 32] {
        &self.beacon_key
    }

    pub fn signing(&self) -> &SigningKey {
        &self.signing
    }

    pub fn public(&self) -> &[u8; 65] {
        &self.public
    }

    /// Is this PC public key (hex) allowed to connect?
    pub fn is_authorized(&self, pub_hex: &str) -> bool {
        self.authorized.lock().unwrap().contains(pub_hex)
    }

    /// Trust-on-first-use: pin a newly-seen PC key (persist + remember). Returns true if added.
    pub fn authorize(&self, pub_hex: &str) -> bool {
        let mut set = self.authorized.lock().unwrap();
        if !set.insert(pub_hex.to_string()) {
            return false;
        }
        // Append to the persistent file (best-effort).
        let line = format!("{pub_hex}\n");
        let existing = fs::read_to_string(AUTH_FILE).unwrap_or_default();
        if fs::write(AUTH_FILE, existing + &line).is_ok() {
            secure(AUTH_FILE);
        }
        crate::log(&format!("authorized new PC key {}…", &pub_hex[..pub_hex.len().min(16)]));
        true
    }

    /// True if no PC has been paired yet (so the first connection is the trusted bootstrap).
    pub fn no_peers(&self) -> bool {
        self.authorized.lock().unwrap().is_empty()
    }
}

/// Return the persisted device id, generating + storing one on first run.
pub fn device_id() -> String {
    if let Ok(s) = fs::read_to_string(ID_FILE) {
        let t = s.trim();
        if is_uuid(t) {
            return t.to_string();
        }
    }
    let id = uuid::Uuid::new_v4().to_string();
    let _ = fs::create_dir_all(DIR);
    match fs::write(ID_FILE, &id) {
        Ok(()) => {
            secure(ID_FILE);
            crate::log(&format!("generated device id {id}"));
        }
        Err(e) => crate::log(&format!("identity: could not persist id ({e}); using ephemeral {id}")),
    }
    id
}

/// Load the device signing key, generating + persisting a fresh P-256 key on first run.
fn load_or_make_key() -> SigningKey {
    if let Ok(hex) = fs::read_to_string(KEY_FILE) {
        if let Some(bytes) = from_hex(hex.trim()) {
            if let Ok(k) = SigningKey::from_slice(&bytes) {
                return k;
            }
            crate::log("identity: stored device key invalid; regenerating");
        }
    }
    let key = SigningKey::random(&mut OsRng);
    let _ = fs::create_dir_all(DIR);
    if fs::write(KEY_FILE, to_hex(&key.to_bytes())).is_ok() {
        secure(KEY_FILE);
        crate::log("generated device P-256 keypair");
    } else {
        crate::log("identity: could not persist device key; using ephemeral key");
    }
    key
}

fn load_or_make_beacon_key() -> [u8; 32] {
    if let Ok(hex) = fs::read_to_string(BKEY_FILE) {
        if let Some(b) = from_hex(hex.trim()) {
            if b.len() == 32 {
                let mut k = [0u8; 32];
                k.copy_from_slice(&b);
                return k;
            }
        }
    }
    let mut k = [0u8; 32];
    OsRng.fill_bytes(&mut k);
    let _ = fs::create_dir_all(DIR);
    if fs::write(BKEY_FILE, to_hex(&k)).is_ok() {
        secure(BKEY_FILE);
        crate::log("generated beacon key");
    }
    k
}

fn load_authorized() -> HashSet<String> {
    let mut set = HashSet::new();
    if let Ok(s) = fs::read_to_string(AUTH_FILE) {
        for line in s.lines() {
            let t = line.trim();
            if !t.is_empty() {
                set.insert(t.to_string());
            }
        }
    }
    set
}

fn pub_bytes(signing: &SigningKey) -> [u8; 65] {
    let vk = signing.verifying_key();
    let pt = vk.to_encoded_point(false); // uncompressed: 0x04 ‖ X ‖ Y
    let mut out = [0u8; 65];
    out.copy_from_slice(pt.as_bytes());
    out
}

fn is_uuid(s: &str) -> bool {
    s.len() == 36 && s.as_bytes()[8] == b'-' && s.as_bytes()[13] == b'-'
}

/// Restrict a sensitive file to the owner (0600). Best-effort.
fn secure(path: &str) {
    if !Path::new(path).exists() {
        return;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = fs::set_permissions(path, fs::Permissions::from_mode(0o600));
    }
}

pub fn to_hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

pub fn from_hex(s: &str) -> Option<Vec<u8>> {
    if s.len() % 2 != 0 {
        return None;
    }
    let mut out = Vec::with_capacity(s.len() / 2);
    let b = s.as_bytes();
    let mut i = 0;
    while i < b.len() {
        let hi = hex_val(b[i])?;
        let lo = hex_val(b[i + 1])?;
        out.push((hi << 4) | lo);
        i += 2;
    }
    Some(out)
}

fn hex_val(c: u8) -> Option<u8> {
    match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'a'..=b'f' => Some(c - b'a' + 10),
        b'A'..=b'F' => Some(c - b'A' + 10),
        _ => None,
    }
}
