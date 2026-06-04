# inkbridge wire protocol — `PenPacket` v1

Authoritative binary layout for rMPP daemon → Windows OTD plugin. Derived from real device
discovery (`docs/phase0-findings.md`). All multi-byte fields **little-endian**.

## Transport

- Raw TCP over USB RNDIS. Daemon listens on `10.11.99.1:<PORT>` (PORT TBD, default 9292).
- `TCP_NODELAY` set; no Nagle batching (latency over throughput).
- One `PenPacket` per evdev `SYN_REPORT` (coalesce all axis updates since the last SYN into
  one packet — do not emit a packet per axis event).
- No length prefix: packets are fixed size (16 bytes). Receiver reads in 16-byte frames.
- A 4-byte ASCII hello `IBR1` is sent once by the daemon on connect, before the first packet,
  so the client can validate the stream and protocol version (`1`).

## `PenPacket` (16 bytes)

| off | type | field | range | meaning |
|----:|------|-------|-------|---------|
| 0 | u32 | `timestamp_us` | — | daemon monotonic clock, microseconds |
| 4 | u16 | `x` | 0..11180 | raw `ABS_X` (short edge ≈157 mm) |
| 6 | u16 | `y` | 0..15340 | raw `ABS_Y` (long edge ≈210 mm) |
| 8 | u16 | `pressure` | 0..4096 | raw `ABS_PRESSURE` (4097 levels) |
| 10 | u16 | `distance` | 0..65535 | raw `ABS_DISTANCE` (hover proximity) |
| 12 | i16 | `tilt_x` | -9000..9000 | `ABS_TILT_X`, centidegrees |
| 14 | i16 | `tilt_y` | -9000..9000 | `ABS_TILT_Y`, centidegrees |
| — | — | — | — | (buttons/flags fold into the 16 bytes — see note) |

> Layout note: the table above is 16 bytes through `tilt_y`. Buttons + flags require 2 more
> bytes → the packet is **18 bytes**, not 16. Final committed layout:

## `PenPacket` — committed 18-byte layout

```
offset size field          notes
0      4    timestamp_us   u32 LE, monotonic µs
4      2    x              u16 LE, 0..11180
6      2    y              u16 LE, 0..15340
8      2    pressure       u16 LE, 0..4096
10     2    distance       u16 LE, 0..65535 (hover)
12     2    tilt_x         i16 LE, -9000..9000
14     2    tilt_y         i16 LE, -9000..9000
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

### `flags` byte
| bits | name | meaning |
|-----:|------|---------|
| 0–3 | version | protocol version, currently `1` |
| 4 | tilt_valid | 1 if tilt fields are meaningful this report |
| 5 | dist_valid | 1 if distance is meaningful (pen in range) |
| 6–7 | reserved | 0 |

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
