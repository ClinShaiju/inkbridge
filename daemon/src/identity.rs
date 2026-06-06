//! inkbridge device identity.
//!
//! A stable, random **UUIDv4** generated once and persisted to `/home/root/inkbridge/identity`.
//! It is the device's *locator* — advertised in the mDNS TXT record (`id=`) so the Windows plugin
//! can filter discovery to the device it paired with (PC1 ↔ rMPP1) even with several rMPPs on the
//! subnet. See docs/wifi-connectivity-feasibility.md §5.
//!
//! Deliberately **not** the MAC address: MACs are randomized by modern stacks, trivially spoofable,
//! leak a hardware identifier, and differ between the USB and Wi-Fi interfaces. The id is also **not**
//! a credential — it is public and spoofable. Authentication is a separate pinned P-256 key
//! (added in the identity-handshake work); the id only *names* the device.

use std::fs;
use std::path::Path;

const DIR: &str = "/home/root/inkbridge";
const ID_FILE: &str = "/home/root/inkbridge/identity";

/// Return the persisted device id, generating + storing one on first run. Best-effort: if the
/// filesystem is unwritable we still return a (process-lifetime) id so advertising works.
pub fn device_id() -> String {
    if let Ok(s) = fs::read_to_string(ID_FILE) {
        let t = s.trim();
        if is_valid(t) {
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

/// Cheap sanity check that the stored string looks like a UUID (length + hyphen positions),
/// without pulling in UUID parsing for the read path.
fn is_valid(s: &str) -> bool {
    s.len() == 36 && s.as_bytes()[8] == b'-' && s.as_bytes()[13] == b'-'
}

/// Restrict the identity file to the owner (0600). Best-effort; ignored if unsupported.
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
