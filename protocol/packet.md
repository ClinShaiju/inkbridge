# inkbridge wire protocol — `PenPacket` v1

Authoritative binary layout for rMPP daemon → Windows OTD plugin. Derived from real device
discovery (`docs/phase0-findings.md`). All multi-byte fields **little-endian**.

> **v2 transport (shipped):** the 18-byte `PenPacket` payload below is unchanged, but it is no
> longer carried on its own TCP port. In v2 pen, touch, and control share **one muxed connection on
> `:9292`**: the pen stream is **channel 1**, each packet sent as the plaintext of an AES-GCM record
> `[u32 len][ct‖tag]` with plaintext `[channel(1)=1][18-byte PenPacket]`. The connection hello is
> `IBMX` (not `IBR1`) and the pen reader starts on a `sub pen` control message. See
> `protocol/mux-v2.md` for the full muxed framing. The section below describes the **payload** and the
> sampling/keepalive semantics, which are identical in v2.

## Transport (payload + semantics — see mux-v2.md for the v2 framing)

- `TCP_NODELAY` set; no Nagle batching (latency over throughput).
- One `PenPacket` per evdev `SYN_REPORT` (coalesce all axis updates since the last SYN into
  one packet — do not emit a packet per axis event). While the pen is in range the daemon also
  resends the current state at ~60 Hz as a keepalive; it goes quiet once the pen leaves range.
- Fixed size **18 bytes** per packet.
- **v1 (historical):** raw TCP on `:9292`, packets sent unframed (no length prefix) after a one-time
  4-byte `IBR1` hello, with a separate control plane on `:9293`. v2 replaces this — see the banner
  above and `protocol/mux-v2.md`.

### Reconnect + presence beacon

The plugin does **not** retry the pen port forever. On a failed/lost connection it reconnects
with growing backoff (1→2→4→8→15→30 s, ~1 min total); once that budget is spent it stops
hammering the network and parks until it hears the device.

The daemon advertises itself with a **presence beacon**: a UDP datagram broadcast on `:9291`
~once a second, payload the same 4 ASCII bytes `IBR1`. It is sent to every up, broadcast-capable
IPv4 interface's broadcast address (not a single `255.255.255.255`), so the USB-RNDIS subnet is
reached even when Wi-Fi is also up. The plugin's listener wakes on the first valid beacon and
immediately reconnects. The same bounded-backoff-then-beacon policy governs the control plane
(`:9293`) and touch (`:9294`) links. Beacon sender: `daemon/src/beacon.rs`; listener + policy:
`otd-plugin/Reconnect.cs`. Env overrides: `INKBRIDGE_BEACON_PORT` (plugin listener).

## `PenPacket` — 18-byte layout

```
offset size field          notes
0      4    timestamp_us   u32 LE, monotonic µs
4      2    x              u16 LE, 0..11180   (raw ABS_X, short edge ≈157 mm)
6      2    y              u16 LE, 0..15340   (raw ABS_Y, long edge ≈210 mm)
8      2    pressure       u16 LE, 0..4096    (raw ABS_PRESSURE, 4097 levels)
10     2    distance       u16 LE, 0..65535   (raw ABS_DISTANCE, hover proximity)
12     2    tilt_x         i16 LE, -9000..9000 (ABS_TILT_X, centidegrees)
14     2    tilt_y         i16 LE, -9000..9000 (ABS_TILT_Y, centidegrees)
16     1    buttons        bitfield (below)
17     1    flags          version + valid bits (below)
                           total = 18 bytes
```

### `buttons` bitfield
| bit | name | source |
|----:|------|--------|
| 0 | touch | `BTN_TOUCH` — pen contacting surface (pressure > 0) |
| 1 | stylus1 | `BTN_STYLUS` |
| 2 | stylus2 | `BTN_STYLUS2` |
| 3 | tool_pen | `BTN_TOOL_PEN` — pen in range (hovering or touching) |
| 4 | tool_rubber | `BTN_TOOL_RUBBER` — eraser end active |
| 5–7 | reserved | 0 |

> The `stylus1` / `stylus2` bits exist for protocol completeness and forwarding: the reMarkable
> Marker and Marker Plus have **no physical pen buttons**, so the stock pen never sets them. They
> are wired through end-to-end (the digitizer advertises `BTN_STYLUS`/`BTN_STYLUS2`) only so a
> third-party pen that does report buttons would work without a format change.

### `flags` byte
| bits | name | meaning |
|-----:|------|---------|
| 0–3 | version | protocol version, currently `1` |
| 4 | tilt_valid | 1 if tilt fields are meaningful this report |
| 5 | dist_valid | 1 if distance is meaningful (pen in range) |
| 6–7 | orientation | screen orientation: `0`=portrait native, `1`/`2`/`3` = 90/180/270° CW |

> The orientation bits are always valid and carry the device's current screen orientation
> (from the daemon's accelerometer + xochitl-lock detector, `daemon/src/orientation.rs`).
> Added within version `1` using previously-reserved bits, so older receivers that mask them
> off are unaffected. The Windows side reads them via `PenPacket.Orientation` to rotate the
> OTD area to match the device.

## Mapping to OTD (Windows side)

The OTD report parser converts `PenPacket` → `ITabletReport` + tilt/proximity:
- `Position = (x, y)` raw; OTD area mapping uses the tablet descriptor's max
  (`11180 × 15340`) and physical mm in `otd-plugin/tablet-spec.json`.
- `Pressure = pressure` (0..4096). OTD's pressure curve then maps to Windows Ink range.
- Proximity/hover from `buttons.tool_pen && !buttons.touch`; `distance` available for curves.
- Tilt from `tilt_x/tilt_y` (centidegrees → OTD expects degrees; divide by 100).

## Versioning

`flags` low nibble carries the version. Receiver rejects mismatched versions. Any field
addition bumps the version and changes the fixed size; never reinterpret existing offsets.
