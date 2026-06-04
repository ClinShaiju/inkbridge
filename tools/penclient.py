import socket, struct, sys, time

HOST, PORT = "10.11.99.1", 9292
dur = float(sys.argv[1]) if len(sys.argv) > 1 else 8.0

c = socket.create_connection((HOST, PORT), timeout=5)
c.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
c.settimeout(dur + 2)

hello = c.recv(4)
print("hello:", hello.decode("ascii", "replace"))

def readn(sock, n):
    buf = b""
    while len(buf) < n:
        d = sock.recv(n - len(buf))
        if not d:
            raise EOFError()
        buf += d
    return buf

n = 0
xs = []; ys = []; ps = []
samples = []
t_end = time.time() + dur
try:
    while time.time() < t_end:
        pkt = readn(c, 18)
        ts, x, y, pr, dist, tx, ty = struct.unpack("<IHHHHhh", pkt[:16])
        btn = pkt[16]; flags = pkt[17]
        n += 1; xs.append(x); ys.append(y); ps.append(pr)
        if n <= 8:
            samples.append(f"x={x} y={y} p={pr} dist={dist} tilt=({tx},{ty}) btn=0x{btn:02X} flags=0x{flags:02X}")
except (EOFError, socket.timeout):
    pass
c.close()

print("packets:", n)
print("--- first samples ---")
for s in samples:
    print(" ", s)
if n:
    print(f"x range:   {min(xs)}..{max(xs)}")
    print(f"y range:   {min(ys)}..{max(ys)}")
    print(f"pressure:  {min(ps)}..{max(ps)}")
