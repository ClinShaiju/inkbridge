using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Inkbridge
{
    /// <summary>
    /// AES-256-GCM record cipher for an inkbridge stream, keyed by ECDH over the pinned P-256 keys.
    /// Mirrors the daemon's crypto.rs: key = HKDF-SHA256(ikm = ECDH(static,static),
    /// salt = nonce_pc‖nonce_dev, info = "inkbridge-enc-v1"‖tag); each record is
    /// <c>[u32 len][ciphertext‖tag]</c> with a 12-byte nonce <c>dir(1)‖counter(8 BE)‖0,0,0</c> and a
    /// per-direction counter (stays in sync because TCP is ordered). Native .NET 8 crypto — no deps.
    ///
    /// For the v2 muxed transport one connection is read by a single read-loop thread while several
    /// writer threads (telemetry ping/config/status, touch sub/unsub) send concurrently. The send and
    /// recv sides therefore use <b>separate</b> <see cref="AesGcm"/> instances (built from the same
    /// key) so encrypt and decrypt never touch the same cipher object, and <see cref="WriteRecord"/>
    /// holds a lock so the counter increment + the framed write stay atomic across writer threads.
    /// </summary>
    internal sealed class CryptoSession
    {
        public const byte DirPcToDev = 0; // the plugin sends these
        public const byte DirDevToPc = 1; // the daemon sends these

        private const int MaxRecord = 64 * 1024;

        private readonly AesGcm _aesSend;
        private readonly AesGcm _aesRecv;
        private readonly object _sendLock = new();
        private readonly byte _sendDir, _recvDir;
        private ulong _sendCtr, _recvCtr;

        public CryptoSession(byte[] shared, byte[] noncePc, byte[] nonceDev, byte[] tag,
            byte sendDir, byte recvDir)
        {
            var salt = new byte[noncePc.Length + nonceDev.Length];
            Buffer.BlockCopy(noncePc, 0, salt, 0, noncePc.Length);
            Buffer.BlockCopy(nonceDev, 0, salt, noncePc.Length, nonceDev.Length);

            var prefix = Encoding.ASCII.GetBytes("inkbridge-enc-v1"); // 16 bytes
            var info = new byte[prefix.Length + tag.Length];
            Buffer.BlockCopy(prefix, 0, info, 0, prefix.Length);
            Buffer.BlockCopy(tag, 0, info, prefix.Length, tag.Length);

            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, info);
            _aesSend = new AesGcm(key, 16);
            _aesRecv = new AesGcm(key, 16);
            _sendDir = sendDir;
            _recvDir = recvDir;
        }

        private static byte[] Nonce(byte dir, ulong ctr)
        {
            var n = new byte[12];
            n[0] = dir;
            for (int i = 0; i < 8; i++) n[1 + i] = (byte)(ctr >> (56 - 8 * i)); // big-endian
            return n;
        }

        /// <summary>Encrypt <paramref name="pt"/> as one record and write it. Thread-safe: serialized
        /// so the per-direction counter and the two writes stay atomic across writer threads.</summary>
        public void WriteRecord(Stream s, byte[] pt)
        {
            lock (_sendLock)
            {
                var nonce = Nonce(_sendDir, _sendCtr++);
                var ct = new byte[pt.Length];
                var tag = new byte[16];
                _aesSend.Encrypt(nonce, pt, ct, tag);
                var blob = new byte[ct.Length + 16];
                Buffer.BlockCopy(ct, 0, blob, 0, ct.Length);
                Buffer.BlockCopy(tag, 0, blob, ct.Length, 16);
                var len = new byte[4];
                len[0] = (byte)(blob.Length >> 24);
                len[1] = (byte)(blob.Length >> 16);
                len[2] = (byte)(blob.Length >> 8);
                len[3] = (byte)blob.Length;
                s.Write(len, 0, 4);
                s.Write(blob, 0, blob.Length);
            }
        }

        /// <summary>Read + decrypt one record. Called only by the single read-loop thread.</summary>
        public byte[] ReadRecord(Stream s)
        {
            var l = new byte[4];
            ReadExact(s, l, 4);
            int n = (l[0] << 24) | (l[1] << 16) | (l[2] << 8) | l[3];
            if (n < 16 || n > MaxRecord) throw new IOException("bad record length");
            var blob = new byte[n];
            ReadExact(s, blob, n);
            int ctLen = n - 16;
            var ct = new byte[ctLen];
            var tag = new byte[16];
            Buffer.BlockCopy(blob, 0, ct, 0, ctLen);
            Buffer.BlockCopy(blob, ctLen, tag, 0, 16);
            var pt = new byte[ctLen];
            var nonce = Nonce(_recvDir, _recvCtr++);
            _aesRecv.Decrypt(nonce, ct, tag, pt); // throws on auth failure
            return pt;
        }

        private static void ReadExact(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int k = s.Read(buf, off, count - off);
                if (k <= 0) throw new EndOfStreamException();
                off += k;
            }
        }
    }
}
