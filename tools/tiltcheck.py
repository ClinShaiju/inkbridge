"""Check whether the rMPP pen reports non-zero TILT while in use. Connects as a 2nd
observer (daemon is multi-client) so OTD can stay connected. Draw AND tilt the pen at
various angles during the window. Reports the tilt range seen (degrees).

Usage: python tools/tiltcheck.py [seconds]
"""
import socket, struct, sys, time

HOST, PORT = "10.11.99.1", 9292
dur = float(sys.argv[1]) if len(sys.argv) > 1 else 15.0

def readn(sock, n):
    buf = b""
    while len(buf) < n:
        d = sock.recv(n - len(buf))
        if not d:
            raise EOFError()
        buf += d
    return buf

c = socket.create_connection((HOST, PORT), timeout=5)
c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
c.settimeout(dur + 2)
print("hello:", c.recv(4).decode("ascii", "replace"))
print(f"draw AND tilt the pen at different angles for {dur:.0f}s...")

n_engaged = 0
txmin = tymin = 10**9
txmax = tymax = -10**9
nonzero = 0
samples = []
t_end = time.time() + dur
try:
    while time.time() < t_end:
        pkt = readn(c, 18)
        ts, x, y, pr, dist, tx, ty = struct.unpack("<IHHHHhh", pkt[:16])
        btn = pkt[16]
        if pr > 0 or (btn & 0x08):  # engaged (drawing or in range)
            n_engaged += 1
            txmin = min(txmin, tx); txmax = max(txmax, tx)
            tymin = min(tymin, ty); tymax = max(tymax, ty)
            if tx != 0 or ty != 0:
                nonzero += 1
                if len(samples) < 10:
                    samples.append(f"tiltX={tx/100:.1f}deg tiltY={ty/100:.1f}deg (raw {tx},{ty})")
except (EOFError, socket.timeout):
    pass
c.close()

print(f"\nengaged reports:        {n_engaged}")
print(f"reports with nonzero tilt: {nonzero}")
if n_engaged:
    print(f"tiltX range: {txmin/100:.1f}..{txmax/100:.1f} deg   tiltY range: {tymin/100:.1f}..{tymax/100:.1f} deg")
for s in samples:
    print("  ", s)
if n_engaged and nonzero == 0:
    print("VERDICT: pen reports NO tilt (axis exposed but always 0) -> rMPP pen has no tilt sensor.")
elif nonzero:
    print("VERDICT: pen DOES report tilt.")
else:
    print("VERDICT: no engaged samples -> pen wasn't drawing.")
