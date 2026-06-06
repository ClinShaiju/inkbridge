//! inkbridge stream encryption — AES-256-GCM records keyed by ECDH over the pinned P-256 keys.
//!
//! After the auth handshake (auth.rs) both sides hold the peer's verified static public key plus the
//! two handshake nonces, so they derive a shared session key with no extra round trip:
//! `key = HKDF-SHA256(ikm = ECDH(static,static), salt = nonce_pc‖nonce_dev, info = "inkbridge-enc-v1"‖tag)`.
//! Every post-handshake message is then an AES-GCM record: `[u32 len][ciphertext‖tag]`. The 12-byte
//! nonce is `dir(1) ‖ counter(8 BE) ‖ 0,0,0`, with a per-direction counter — unique per (key,nonce)
//! because TCP keeps the stream ordered, so a dropped/duplicated record is impossible without the
//! socket breaking. Gives confidentiality for handwriting/touch on the LAN (docs/security.md #4).
//!
//! Static-static ECDH means the key is per-session (nonces vary) but not forward-secret; that's an
//! accepted trade for simplicity here (ephemeral ECDH can layer on later without a wire change to the
//! record format).

use std::io::{self, Read, Write};

use aes_gcm::aead::Aead;
use aes_gcm::{Aes256Gcm, KeyInit, Nonce};
use hkdf::Hkdf;
use sha2::Sha256;

const MAX_RECORD: usize = 64 * 1024;
const INFO_PREFIX: &[u8] = b"inkbridge-enc-v1";

/// Direction byte for PC→device records (the plugin sends these).
pub const DIR_PC_TO_DEV: u8 = 0;
/// Direction byte for device→PC records (the daemon sends these).
pub const DIR_DEV_TO_PC: u8 = 1;

/// An established encrypted session over one connection. Not Sync; lives in the connection thread.
pub struct Session {
    cipher: Aes256Gcm,
    send_dir: u8,
    recv_dir: u8,
    send_ctr: u64,
    recv_ctr: u64,
}

impl Session {
    /// Derive the session from the ECDH shared secret + handshake nonces. `send_dir`/`recv_dir` set
    /// this end's role (daemon: send=DEV_TO_PC, recv=PC_TO_DEV).
    pub fn new(
        shared: &[u8],
        nonce_pc: &[u8],
        nonce_dev: &[u8],
        tag: &[u8; 4],
        send_dir: u8,
        recv_dir: u8,
    ) -> Session {
        let mut salt = Vec::with_capacity(nonce_pc.len() + nonce_dev.len());
        salt.extend_from_slice(nonce_pc);
        salt.extend_from_slice(nonce_dev);
        let mut info = Vec::with_capacity(INFO_PREFIX.len() + 4);
        info.extend_from_slice(INFO_PREFIX);
        info.extend_from_slice(tag);

        let hk = Hkdf::<Sha256>::new(Some(&salt), shared);
        let mut key = [0u8; 32];
        hk.expand(&info, &mut key).expect("hkdf expand (32 <= 255*32)");
        let cipher = Aes256Gcm::new_from_slice(&key).expect("32-byte AES key");
        Session { cipher, send_dir, recv_dir, send_ctr: 0, recv_ctr: 0 }
    }

    fn nonce(dir: u8, ctr: u64) -> [u8; 12] {
        let mut n = [0u8; 12];
        n[0] = dir;
        n[1..9].copy_from_slice(&ctr.to_be_bytes());
        n
    }

    /// Encrypt `pt` as one record and write `[u32 len][ciphertext‖tag]`.
    #[allow(deprecated)] // Nonce::from_slice: aes-gcm 0.10 rides generic-array 0.14
    pub fn write_record<W: Write>(&mut self, w: &mut W, pt: &[u8]) -> io::Result<()> {
        let n = Self::nonce(self.send_dir, self.send_ctr);
        self.send_ctr += 1;
        let blob = self
            .cipher
            .encrypt(Nonce::from_slice(&n), pt)
            .map_err(|_| io::Error::new(io::ErrorKind::Other, "encrypt"))?;
        w.write_all(&(blob.len() as u32).to_be_bytes())?;
        w.write_all(&blob)
    }

    /// Read one record and return the decrypted plaintext.
    #[allow(deprecated)] // Nonce::from_slice: aes-gcm 0.10 rides generic-array 0.14
    pub fn read_record<R: Read>(&mut self, r: &mut R) -> io::Result<Vec<u8>> {
        let mut l = [0u8; 4];
        r.read_exact(&mut l)?;
        let n = u32::from_be_bytes(l) as usize;
        if n < 16 || n > MAX_RECORD {
            return Err(io::Error::new(io::ErrorKind::InvalidData, "bad record length"));
        }
        let mut blob = vec![0u8; n];
        r.read_exact(&mut blob)?;
        let nn = Self::nonce(self.recv_dir, self.recv_ctr);
        self.recv_ctr += 1;
        self.cipher
            .decrypt(Nonce::from_slice(&nn), blob.as_ref())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "decrypt/auth failed"))
    }
}
