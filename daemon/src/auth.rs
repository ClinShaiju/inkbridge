//! inkbridge mutual authentication — P-256 signed-nonce challenge-response.
//!
//! Runs on every connection (pen / touch / control) right after the channel magic, before any work
//! (wakelock, streaming). Proves to the PC that this is the paired device, and to the device that the
//! PC is allow-listed — so PC1 talks to rMPP1 only, and a stranger on the LAN is rejected at the
//! handshake (closing the eavesdrop + wakelock-DoS holes in docs/security.md for the sensitive
//! streams). Discovery (mDNS/beacon) only *locates*; this is what *authenticates*.
//!
//! Wire format (fixed sizes, after the 4-byte channel magic), all binary:
//! ```text
//!   PC  -> dev : pub_pc[65] ‖ nonce_pc[32]                      (97)
//!   dev -> PC  : pub_dev[65] ‖ nonce_dev[32] ‖ sig_dev[64]      (161)
//!   PC  -> dev : sig_pc[64]                                     (64)
//! ```
//! `sig_dev = ECDSA-P256/SHA-256( nonce_pc ‖ tag ‖ "DEV" )` over the device key;
//! `sig_pc  = ECDSA-P256/SHA-256( nonce_dev ‖ tag ‖ "PC"  )` over the PC key.
//! `tag` is the 4-byte channel magic (IBR1/IBT1/IBCP/IBCS) so a signature can't be replayed on
//! another channel. Trust-on-first-use: the device pins the first PC key it sees (expected over the
//! USB cable); thereafter only pinned keys are accepted.

use std::io::{self, Read, Write};

use p256::ecdsa::signature::{Signer, Verifier};
use p256::ecdsa::{Signature, VerifyingKey};
use rand_core::{OsRng, RngCore};

use crate::identity::{self, Identity};

/// Authenticate a freshly-accepted client. Returns Ok(true) if the peer is the paired/authorized PC.
/// On Ok(false) the caller must drop the connection without doing any work.
pub fn server_handshake<S: Read + Write>(
    stream: &mut S,
    tag: &[u8; 4],
    id: &Identity,
) -> io::Result<bool> {
    // 1. Read the PC hello: pub_pc[65] ‖ nonce_pc[32].
    let mut hello = [0u8; 97];
    stream.read_exact(&mut hello)?;
    let pub_pc = &hello[..65];
    let nonce_pc = &hello[65..97];

    let pub_pc_hex = identity::to_hex(pub_pc);
    let pc_key = match VerifyingKey::from_sec1_bytes(pub_pc) {
        Ok(k) => k,
        Err(_) => {
            crate::log("auth: malformed PC public key");
            return Ok(false);
        }
    };

    // 2. Send our hello + our signature over (nonce_pc ‖ tag ‖ "DEV").
    let mut nonce_dev = [0u8; 32];
    OsRng.fill_bytes(&mut nonce_dev);
    let sig_dev: Signature = id.signing().sign(&msg(nonce_pc, tag, b"DEV"));

    let mut resp = Vec::with_capacity(161);
    resp.extend_from_slice(id.public());
    resp.extend_from_slice(&nonce_dev);
    resp.extend_from_slice(&sig_dev.to_bytes());
    stream.write_all(&resp)?;

    // 3. Read the PC's signature over (nonce_dev ‖ tag ‖ "PC") and verify.
    let mut sig_pc_bytes = [0u8; 64];
    stream.read_exact(&mut sig_pc_bytes)?;
    let sig_pc = match Signature::from_slice(&sig_pc_bytes) {
        Ok(s) => s,
        Err(_) => return Ok(false),
    };
    if pc_key.verify(&msg(&nonce_dev, tag, b"PC"), &sig_pc).is_err() {
        crate::log("auth: PC signature failed");
        return Ok(false);
    }

    // 4. Authorize: trust-on-first-use for the first PC, else require it to be pinned.
    if id.is_authorized(&pub_pc_hex) {
        Ok(true)
    } else if id.no_peers() {
        id.authorize(&pub_pc_hex); // bootstrap (expected over USB)
        Ok(true)
    } else {
        crate::log("auth: PC key not authorized (not the paired PC); rejecting");
        Ok(false)
    }
}

/// Build the signed message: nonce ‖ channel-tag ‖ role. Binding the tag stops a signature captured
/// on one channel from being replayed on another.
fn msg(nonce: &[u8], tag: &[u8; 4], role: &[u8]) -> Vec<u8> {
    let mut m = Vec::with_capacity(nonce.len() + 4 + role.len());
    m.extend_from_slice(nonce);
    m.extend_from_slice(tag);
    m.extend_from_slice(role);
    m
}
