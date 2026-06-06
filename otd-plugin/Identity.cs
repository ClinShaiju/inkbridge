using System;
using System.IO;
using System.Security.Cryptography;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// This PC's long-term P-256 identity and the mutual handshake with the rMPP daemon. The PC key
    /// is generated once and stored in inkbridge.json; the device's public key is pinned on first
    /// (USB) contact and verified on every connection thereafter. Authenticates the endpoint so PC1
    /// talks to rMPP1 only and a stranger on the LAN is rejected at the handshake (see
    /// docs/security.md). P-256 is native to .NET 8 — no third-party crypto. Mirrors daemon auth.rs.
    /// </summary>
    internal static class Identity
    {
        private static readonly object _gate = new();
        private static ECDsa? _pc;       // PC long-term signing key
        private static byte[]? _pcPub;   // 65-byte uncompressed SEC1 public key

        private static void Ensure()
        {
            if (_pc != null) return;
            lock (_gate)
            {
                if (_pc != null) return;
                var cfg = PluginConfig.Load();
                ECDsa pc;
                if (!string.IsNullOrEmpty(cfg.pc_key))
                {
                    pc = ECDsa.Create();
                    try { pc.ImportPkcs8PrivateKey(Convert.FromBase64String(cfg.pc_key), out _); }
                    catch { pc.Dispose(); pc = NewKey(cfg); }
                }
                else
                {
                    pc = NewKey(cfg);
                }
                _pc = pc;
                _pcPub = ExportPub(pc);
            }
        }

        private static ECDsa NewKey(PluginConfig cfg)
        {
            var pc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            cfg.pc_key = Convert.ToBase64String(pc.ExportPkcs8PrivateKey());
            cfg.Save();
            Log.Write("Inkbridge", "generated PC P-256 keypair");
            return pc;
        }

        private static byte[] ExportPub(ECDsa ec)
        {
            var p = ec.ExportParameters(false);
            var pub = new byte[65];
            pub[0] = 0x04; // uncompressed point
            Buffer.BlockCopy(p.Q.X!, 0, pub, 1, 32);
            Buffer.BlockCopy(p.Q.Y!, 0, pub, 33, 32);
            return pub;
        }

        /// <summary>This PC's 65-byte uncompressed public key.</summary>
        public static byte[] PublicKey { get { Ensure(); return _pcPub!; } }

        /// <summary>ECDSA-P256/SHA-256 signature over <paramref name="msg"/>, raw r‖s (64 bytes).</summary>
        public static byte[] Sign(byte[] msg)
        {
            Ensure();
            return _pc!.SignData(msg, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        /// <summary>
        /// ECDH shared secret (raw 32-byte X) between this PC's key and the device's 65-byte SEC1
        /// public key — the ikm for the stream-encryption HKDF. Matches Rust p256 `raw_secret_bytes`.
        /// </summary>
        public static byte[] DeriveShared(byte[] devPub65)
        {
            Ensure();
            var mine = _pc!.ExportParameters(true); // includes private D
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportParameters(mine);
            var x = new byte[32];
            var y = new byte[32];
            Buffer.BlockCopy(devPub65, 1, x, 0, 32);
            Buffer.BlockCopy(devPub65, 33, y, 0, 32);
            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            });
            return ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        }

        /// <summary>Verify a device signature against a 65-byte SEC1 public key.</summary>
        public static bool VerifyDevice(byte[] devPub65, byte[] msg, byte[] sig)
        {
            try
            {
                var x = new byte[32];
                var y = new byte[32];
                Buffer.BlockCopy(devPub65, 1, x, 0, 32);
                Buffer.BlockCopy(devPub65, 33, y, 0, 32);
                using var ec = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = x, Y = y },
                });
                return ec.VerifyData(msg, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Client side of the inkbridge mutual handshake. Runs after the channel magic (IBR1/IBT1) and
    /// before any data. See auth.rs for the wire format.
    /// </summary>
    internal static class AuthClient
    {
        private static readonly byte[] RoleDev = { (byte)'D', (byte)'E', (byte)'V' };
        private static readonly byte[] RolePc = { (byte)'P', (byte)'C' };

        /// <summary>
        /// Perform the handshake over <paramref name="s"/> for the given 4-byte channel
        /// <paramref name="tag"/> (IBR1 / IBT1 / IBCP). Returns the established <see cref="CryptoSession"/>
        /// on success. Throws on socket errors so the caller reconnects; returns null on an auth
        /// failure (bad/mismatched device).
        /// </summary>
        public static CryptoSession? Handshake(Stream s, byte[] tag)
        {
            // 1. send pub_pc[65] ‖ nonce_pc[32]
            var noncePc = new byte[32];
            RandomNumberGenerator.Fill(noncePc);
            var hello = new byte[97];
            Buffer.BlockCopy(Identity.PublicKey, 0, hello, 0, 65);
            Buffer.BlockCopy(noncePc, 0, hello, 65, 32);
            s.Write(hello, 0, hello.Length);

            // 2. read pub_dev[65] ‖ nonce_dev[32] ‖ sig_dev[64]
            var resp = new byte[161];
            ReadExact(s, resp, 161);
            var devPub = new byte[65];
            var nonceDev = new byte[32];
            var sigDev = new byte[64];
            Buffer.BlockCopy(resp, 0, devPub, 0, 65);
            Buffer.BlockCopy(resp, 65, nonceDev, 0, 32);
            Buffer.BlockCopy(resp, 97, sigDev, 0, 64);

            // 3. verify the device proved possession of its key over (nonce_pc ‖ tag ‖ "DEV")
            if (!Identity.VerifyDevice(devPub, Msg(noncePc, tag, RoleDev), sigDev))
            {
                Log.Write("Inkbridge", "auth: device signature invalid; refusing", LogLevel.Warning);
                return null;
            }

            // pin the device key (trust-on-first-use), or require it to match the pinned one
            var cfg = PluginConfig.Load();
            string devHex = Hex(devPub);
            if (string.IsNullOrEmpty(cfg.device_pubkey))
            {
                cfg.device_pubkey = devHex;
                cfg.Save();
                Log.Write("Inkbridge", "pinned device key (first pairing)");
            }
            else if (!cfg.device_pubkey.Equals(devHex, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write("Inkbridge",
                    "auth: device key does NOT match the paired device; refusing (re-pair over USB to reset)",
                    LogLevel.Error);
                return null;
            }

            // 4. prove our key over (nonce_dev ‖ tag ‖ "PC")
            var sigPc = Identity.Sign(Msg(nonceDev, tag, RolePc));
            s.Write(sigPc, 0, sigPc.Length);

            // 5. derive the encrypted session (ECDH → HKDF; see CryptoSession). Plugin sends PC→dev,
            //    receives dev→PC.
            var shared = Identity.DeriveShared(devPub);
            return new CryptoSession(shared, noncePc, nonceDev, tag,
                CryptoSession.DirPcToDev, CryptoSession.DirDevToPc);
        }

        private static byte[] Msg(byte[] nonce, byte[] tag, byte[] role)
        {
            var m = new byte[nonce.Length + tag.Length + role.Length];
            Buffer.BlockCopy(nonce, 0, m, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, m, nonce.Length, tag.Length);
            Buffer.BlockCopy(role, 0, m, nonce.Length + tag.Length, role.Length);
            return m;
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
        }

        private static string Hex(byte[] b)
        {
            var sb = new System.Text.StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }
}
