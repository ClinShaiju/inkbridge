"""Print the first N pen reports that are ENGAGED (pressure>0 or hovering), so we can
see the actual buttons/flags during real drawing — specifically whether BtnToolPen
(0x08 -> OTD NearProximity) is set. Draw on the rMPP while this runs."""
import socket, struct, sys, time

HOST, PORT = "10.11.99.1", 9292
dur = float(sys.argv[1]) if len(sys.argv) > 1 else 12.0

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
print(f"draw on the rMPP now ({dur:.0f}s)...")

shown = 0
t_end = time.time() + dur
try:
    while time.time() < t_end and shown < 12:
        pkt = readn(c, 18)
        ts, x, y, pr, dist, tx, ty = struct.unpack("<IHHHHhh", pkt[:16])
        btn = pkt[16]; flags = pkt[17]
        if pr > 0 or dist > 0 or btn != 0:
            toolpen = "TOOL_PEN" if (btn & 0x08) else "-"
            touch = "TOUCH" if (btn & 0x01) else "-"
            print(f"  x={x} y={y} p={pr} dist={dist} btn=0x{btn:02X} [{toolpen} {touch}] flags=0x{flags:02X}")
            shown += 1
except (EOFError, socket.timeout):
    pass
c.close()
if shown == 0:
    print("  NO engaged samples — pen wasn't drawing or device asleep")
