"""Correlate pen engagement with Windows cursor movement in ONE process, so there is
no timing coordination problem: just draw on the rMPP whenever you like during the
window and watch your primary monitor. At the end it reports whether the cursor moved
while the pen was engaged.

Usage: python tools/monitor.py [seconds]
"""
import socket, struct, sys, time, ctypes

HOST, PORT = "10.11.99.1", 9292
dur = float(sys.argv[1]) if len(sys.argv) > 1 else 60.0

class POINT(ctypes.Structure):
    _fields_ = [("x", ctypes.c_long), ("y", ctypes.c_long)]

def cursor():
    p = POINT()
    ctypes.windll.user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y

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
print(f"DRAW on the rMPP anytime in the next {dur:.0f}s and watch your PRIMARY monitor...")

engaged_reports = 0
cursor_moves_while_engaged = 0
last_cursor = cursor()
cur_min = [10**9, 10**9]; cur_max = [-10**9, -10**9]
moved_positions = []
t_end = time.time() + dur
last_print = 0
try:
    while time.time() < t_end:
        pkt = readn(c, 18)
        ts, x, y, pr, dist, tx, ty = struct.unpack("<IHHHHhh", pkt[:16])
        btn = pkt[16]
        engaged = pr > 0 or (btn & 0x08) != 0  # pressure or TOOL_PEN (NearProximity)
        cx, cy = cursor()
        if engaged:
            engaged_reports += 1
            if (cx, cy) != last_cursor:
                cursor_moves_while_engaged += 1
                cur_min[0] = min(cur_min[0], cx); cur_min[1] = min(cur_min[1], cy)
                cur_max[0] = max(cur_max[0], cx); cur_max[1] = max(cur_max[1], cy)
                if len(moved_positions) < 10:
                    moved_positions.append((cx, cy))
            # periodic heartbeat so the user knows it's seeing the pen
            now = time.time()
            if now - last_print > 1.0:
                print(f"  pen engaged: pkt x={x} y={y} p={pr} -> cursor=({cx},{cy})")
                last_print = now
        last_cursor = (cx, cy)
except (EOFError, socket.timeout):
    pass
c.close()

print("\n=== RESULT ===")
print(f"pen-engaged reports:           {engaged_reports}")
print(f"cursor moves while engaged:     {cursor_moves_while_engaged}")
if cursor_moves_while_engaged:
    print(f"cursor X range while engaged:  {cur_min[0]} .. {cur_max[0]}")
    print(f"cursor Y range while engaged:  {cur_min[1]} .. {cur_max[1]}")
    print(f"sample moved positions:        {moved_positions}")
    print("VERDICT: cursor IS tracking the pen.")
elif engaged_reports:
    print("VERDICT: pen WAS engaged but cursor did NOT move -> OTD output not reaching cursor.")
else:
    print("VERDICT: no pen engagement detected -> pen wasn't drawing / device asleep.")
