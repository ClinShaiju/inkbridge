"""Measure the real pen report rate from the inkbridge daemon.

The daemon sends one 18-byte packet per SYN_REPORT (a real pen report) plus a
~60 Hz keepalive that resends the current state when the pen is idle. To get the
true digitizer rate we look only at packets where the pen is engaged (pressure>0
or hovering) and de-duplicate consecutive identical states (keepalives), then use
the packet's device-side microsecond timestamp for precise inter-arrival timing.

Usage:  python tools/penrate.py [seconds]
Draw continuously on the rMPP for the whole window for a meaningful number.
"""
import socket, struct, sys, time

HOST, PORT = "10.11.99.1", 9292
dur = float(sys.argv[1]) if len(sys.argv) > 1 else 8.0

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
hello = c.recv(4)
print("hello:", hello.decode("ascii", "replace"))
print(f"draw continuously for {dur:.0f}s ...")

total = 0
active_ts = []          # device ts (us) of distinct, pen-engaged reports
prev_state = None
ps = []
t_end = time.time() + dur
try:
    while time.time() < t_end:
        pkt = readn(c, 18)
        ts, x, y, pr, dist, tx, ty = struct.unpack("<IHHHHhh", pkt[:16])
        btn = pkt[16]; flags = pkt[17]
        total += 1
        engaged = pr > 0 or dist > 0 or btn != 0
        state = (x, y, pr, dist, tx, ty, btn)
        if engaged and state != prev_state:
            active_ts.append(ts)
            ps.append(pr)
        prev_state = state
except (EOFError, socket.timeout):
    pass
c.close()

print(f"total packets:        {total}")
print(f"distinct pen reports: {len(active_ts)}")
if len(active_ts) >= 2:
    span_us = active_ts[-1] - active_ts[0]
    spans = [b - a for a, b in zip(active_ts, active_ts[1:]) if b > a]
    spans.sort()
    mean_ms = (span_us / (len(active_ts) - 1)) / 1000.0
    median_ms = spans[len(spans) // 2] / 1000.0 if spans else float("nan")
    print(f"window (device ts):   {span_us/1e6:.2f}s")
    print(f"mean interval:        {mean_ms:.3f} ms  ->  {1000.0/mean_ms:.0f} Hz")
    print(f"median interval:      {median_ms:.3f} ms  ->  {1000.0/median_ms:.0f} Hz")
    print(f"pressure range:       {min(ps)}..{max(ps)}")
else:
    print("not enough pen activity — was the pen drawing on the rMPP?")
