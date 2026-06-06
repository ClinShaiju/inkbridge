using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>One discovered inkbridge service instance.</summary>
    internal sealed class MdnsService
    {
        public string Id = "";                       // TXT id= (device UUID locator)
        public int Port;                             // SRV port
        public readonly List<IPAddress> Addresses = new(); // A records (IPv4)
    }

    /// <summary>
    /// Minimal, dependency-free mDNS/DNS-SD client: browses <c>_inkbridge._tcp.local</c> and returns
    /// the discovered instances (device id from TXT, port from SRV, IPv4s from A). Deliberately tiny
    /// so the plugin stays a single DLL (no Bonjour/Zeroconf NuGet to deploy alongside it).
    ///
    /// We send a one-shot PTR query with the QU ("unicast response") bit set, out of <b>every</b>
    /// local IPv4 interface (so both the Wi-Fi and USB subnets are queried), and collect the unicast
    /// answers for a short window. The daemon's responder (mdns-sd) puts the SRV/TXT/A in the same
    /// response, so one round trip is enough. Discovery only <i>locates</i> the device — the pinned-key
    /// handshake authenticates it (see docs/security.md), so a spoofed reply can at worst mislead the
    /// address, not impersonate the device.
    /// </summary>
    internal static class MdnsClient
    {
        private static readonly IPAddress MulticastV4 = IPAddress.Parse("224.0.0.251");
        private const int MdnsPort = 5353;
        private const string ServiceType = "_inkbridge._tcp.local";

        // DNS record types we care about.
        private const ushort TypeA = 1, TypePTR = 12, TypeTXT = 16, TypeSRV = 33;

        /// <summary>
        /// Browse for inkbridge services. Blocks up to <paramref name="timeoutMs"/> collecting replies.
        /// Returns one entry per discovered instance (deduped by id). Never throws — returns empty on error.
        /// </summary>
        public static List<MdnsService> Discover(int timeoutMs = 1200)
        {
            var byInstance = new Dictionary<string, Parsed>(StringComparer.OrdinalIgnoreCase);
            byte[] query = BuildQuery(ServiceType);

            // mdns-sd answers via MULTICAST (224.0.0.251:5353), so we must bind 5353 and join the
            // group on each interface to hear the reply — a socket on an ephemeral port never sees it.
            // One socket bound to ANY:5353 (ReuseAddress — shares the port with Windows' own mDNS),
            // joined + sending out every local IPv4 interface so both the Wi-Fi and USB subnets are
            // queried. Verified against the rMPP responder.
            Socket? sock = null;
            try
            {
                sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                sock.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);

                var dest = new IPEndPoint(MulticastV4, MdnsPort);
                bool sentAny = false;
                foreach (IPAddress local in LocalIPv4())
                {
                    try
                    {
                        // Join the group on this interface (ignore if already joined) and egress the
                        // query through it.
                        try
                        {
                            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                                new MulticastOption(MulticastV4, local));
                        }
                        catch { /* already a member / unsupported on this iface */ }
                        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface,
                            local.GetAddressBytes());
                        sock.SendTo(query, dest);
                        sentAny = true;
                    }
                    catch { /* skip this interface */ }
                }
                if (!sentAny) return new List<MdnsService>();

                var buf = new byte[4096];
                long deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    var ready = new List<Socket> { sock };
                    int remainMs = (int)Math.Max(1, deadline - Environment.TickCount64);
                    Socket.Select(ready, null, null, remainMs * 1000);
                    if (ready.Count == 0) break; // window elapsed with nothing more
                    try
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n = sock.ReceiveFrom(buf, ref from);
                        if (n > 0) ParseResponse(buf, n, byInstance); // our own reflected query has 0 answers → ignored
                    }
                    catch { /* ignore one bad datagram */ }
                }
            }
            catch (Exception e)
            {
                Log.Write("Inkbridge", $"mDNS browse error: {e.Message}", LogLevel.Debug);
            }
            finally
            {
                try { sock?.Dispose(); } catch { }
            }

            // Stitch the parsed records into services: instance -> SRV(target,port) + TXT(id), target -> A[].
            var outList = new List<MdnsService>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in byInstance.Values)
            {
                if (p.Port == 0 || string.IsNullOrEmpty(p.Id)) continue;
                if (!seenIds.Add(p.Id)) continue;
                var svc = new MdnsService { Id = p.Id, Port = p.Port };
                if (p.Target != null && byInstance.TryGetValue("@" + p.Target, out var host))
                    svc.Addresses.AddRange(host.Addresses);
                if (svc.Addresses.Count > 0) outList.Add(svc);
            }
            return outList;
        }

        // --- response parsing ---

        // Accumulator. Instance records are keyed by the instance FQDN; A-record hosts are keyed by
        // "@"+hostname so they don't collide with instance keys.
        private sealed class Parsed
        {
            public string Id = "";
            public int Port;
            public string? Target;
            public readonly List<IPAddress> Addresses = new();
        }

        private static void ParseResponse(byte[] msg, int len, Dictionary<string, Parsed> acc)
        {
            int pos = 0;
            if (len < 12) return;
            ushort qd = ReadU16(msg, 4);
            int an = ReadU16(msg, 6) + ReadU16(msg, 8) + ReadU16(msg, 10);
            pos = 12;
            // Skip questions.
            for (int i = 0; i < qd; i++)
            {
                SkipName(msg, ref pos, len);
                pos += 4; // qtype + qclass
                if (pos > len) return;
            }
            for (int i = 0; i < an && pos < len; i++)
            {
                string name = ReadName(msg, ref pos, len);
                if (pos + 10 > len) return;
                ushort type = ReadU16(msg, pos); pos += 2;
                pos += 2;                                // class
                pos += 4;                                // ttl
                ushort rdlen = ReadU16(msg, pos); pos += 2;
                int rdEnd = pos + rdlen;
                if (rdEnd > len) return;

                switch (type)
                {
                    case TypeSRV:
                    {
                        int p = pos;
                        p += 4; // priority + weight
                        ushort port = ReadU16(msg, p); p += 2;
                        string target = ReadName(msg, ref p, len);
                        var e = Get(acc, name);
                        e.Port = port;
                        e.Target = target;
                        break;
                    }
                    case TypeTXT:
                    {
                        string id = ParseTxtId(msg, pos, rdEnd);
                        if (id.Length > 0) Get(acc, name).Id = id;
                        break;
                    }
                    case TypeA:
                    {
                        if (rdlen == 4)
                        {
                            var ip = new IPAddress(new[] { msg[pos], msg[pos + 1], msg[pos + 2], msg[pos + 3] });
                            Get(acc, "@" + name).Addresses.Add(ip);
                        }
                        break;
                    }
                    // PTR is implied by the SRV/TXT owner name; we don't need to parse it separately.
                }
                pos = rdEnd;
            }
        }

        private static Parsed Get(Dictionary<string, Parsed> acc, string key)
        {
            if (!acc.TryGetValue(key, out var p)) { p = new Parsed(); acc[key] = p; }
            return p;
        }

        private static string ParseTxtId(byte[] msg, int start, int end)
        {
            int p = start;
            while (p < end)
            {
                int l = msg[p++];
                if (l == 0 || p + l > end) break;
                string kv = Encoding.UTF8.GetString(msg, p, l);
                p += l;
                if (kv.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
                    return kv.Substring(3);
            }
            return "";
        }

        // --- DNS name + integer helpers (with compression-pointer support) ---

        private static string ReadName(byte[] msg, ref int pos, int len)
        {
            var sb = new StringBuilder();
            int p = pos;
            bool jumped = false;
            int safety = 0;
            while (p < len && safety++ < 128)
            {
                int l = msg[p];
                if (l == 0) { p++; break; }
                if ((l & 0xC0) == 0xC0) // compression pointer
                {
                    if (p + 1 >= len) break;
                    int ptr = ((l & 0x3F) << 8) | msg[p + 1];
                    if (!jumped) { pos = p + 2; jumped = true; }
                    p = ptr;
                    continue;
                }
                p++;
                if (p + l > len) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(msg, p, l));
                p += l;
            }
            if (!jumped) pos = p;
            return sb.ToString();
        }

        private static void SkipName(byte[] msg, ref int pos, int len)
        {
            int p = pos;
            while (p < len)
            {
                int l = msg[p];
                if (l == 0) { p++; break; }
                if ((l & 0xC0) == 0xC0) { p += 2; break; }
                p += 1 + l;
            }
            pos = p;
        }

        private static ushort ReadU16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);

        private static byte[] BuildQuery(string serviceType)
        {
            var ms = new List<byte>(64);
            ms.AddRange(new byte[] { 0, 0 });        // ID
            ms.AddRange(new byte[] { 0, 0 });        // flags: standard query
            ms.AddRange(new byte[] { 0, 1 });        // QDCOUNT = 1
            ms.AddRange(new byte[] { 0, 0, 0, 0, 0, 0 }); // AN/NS/AR = 0
            foreach (string label in serviceType.Split('.'))
            {
                var lb = Encoding.UTF8.GetBytes(label);
                ms.Add((byte)lb.Length);
                ms.AddRange(lb);
            }
            ms.Add(0);                               // name terminator
            ms.AddRange(new byte[] { 0, (byte)TypePTR }); // QTYPE = PTR
            ms.AddRange(new byte[] { 0x00, 0x01 });  // QCLASS = IN (multicast response; we join the group)
            return ms.ToArray();
        }

        private static IEnumerable<IPAddress> LocalIPv4()
        {
            var list = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                            list.Add(ua.Address);
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
