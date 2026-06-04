# Phase 0 — Discovery Findings (rMPP)

All values below were read directly from the device over SSH (`root@10.11.99.1`) on
2026-06-03. They supersede every assumption in `project.md`. **Do not hardcode anything
that contradicts this file.**

## Device / OS

| Property | Value |
|---|---|
| Kernel | `Linux 6.12.34+git-imx8mm-ferrari-g95c3acd37afa #1 SMP PREEMPT aarch64` (built 2026-02-04) |
| Board / SoC | `imx8mm-ferrari` (reMarkable Paper Pro, NXP i.MX8MM, ARMv8.0) |
| libc | **glibc** — `/lib/libc.so.6` (1.65 MB). No musl present. |
| init | **systemd** — `/sbin/init -> ../lib/systemd/systemd`, `systemctl` present. |
| Entware | present (`/opt`, busybox 1.36.1). `python3` at `/opt/bin/python3`. |
| Host tools available | `evtest`, `hexdump`, `nc` (`/usr/bin/nc`), `python3`. **No `timeout`.** |

Note: `head -N` / `grep -A` GNU shortcuts are **not** supported by the on-device busybox.
Use `head -n N`; avoid `grep -A/-B`. `setsid <cmd> &` is the reliable way to background a
process that must outlive the SSH command.

## Input devices (`/proc/bus/input/devices`)

| event node | name | role |
|---|---|---|
| event0 | `30370000.snvs:snvs-powerkey` | power button |
| event1 | `Hall effect sensors` | folio/cover magnet |
| **event2** | **`Elan marker input`** | **PEN DIGITIZER — this is our source** |
| event3 | `Elan touch input` | finger touchscreen (ignore) |

`event2` is also symlinked as `/dev/input/touchscreen0`. The node is stable across the
reboots observed, but the daemon should resolve it **by name** (`Elan marker input`) via
`/proc/bus/input/devices` rather than hardcoding `event2`, in case enumeration order changes
across firmware updates.

## Pen capability dump (`evtest /dev/input/event2`)

```
Input device name: "Elan marker input"
EV_KEY: BTN_TOOL_PEN(320) BTN_TOOL_RUBBER(321) BTN_TOUCH(330) BTN_STYLUS(331) BTN_STYLUS2(332)
EV_ABS:
  ABS_X        (0)   min 0      max 11180    res 2832
  ABS_Y        (1)   min 0      max 15340    res 2064
  ABS_PRESSURE (24)  min 0      max 4096
  ABS_DISTANCE (25)  min 0      max 65535
  ABS_TILT_X   (26)  min -9000  max 9000
  ABS_TILT_Y   (27)  min -9000  max 9000
Properties: (none)   # NOT marked INPUT_PROP_DIRECT/POINTER
```

### What this resolves from `project.md`'s open questions

- **Tilt IS exposed** — `ABS_TILT_X/Y`, range ±9000 (centidegrees, i.e. ±90.00°). The plan
  listed tilt as "TBD"; it is available. Fits `i16`.
- **Hover IS exposed** — `ABS_DISTANCE` 0..65535. True proximity, not just a binary
  near/far. (`BTN_TOOL_PEN` going 1 with `BTN_TOUCH` 0 is the "hovering" state.)
- **Pressure range is 0..4096** (4097 levels, ~12-bit) — note the plan said `0..4095`. The
  max is **4096**, not 4095. Use `u16`.
- **Eraser tool exists** — `BTN_TOOL_RUBBER`. Not in the original plan; expose it as a flag.
- Both stylus buttons present (`BTN_STYLUS`, `BTN_STYLUS2`).
- **Resolution fields are unreliable.** `res 2832` for X over a ~157 mm edge would imply
  ~3.9 mm of travel — physically wrong. Do **not** trust the evdev `resolution` field for
  mm mapping. Use raw `max` (11180 × 15340) plus the known physical active area for the OTD
  tablet descriptor. Physical size still needs a tape-measure confirmation (see open items).
- **Native orientation is portrait:** X is the short edge (max 11180 ≈ 157 mm), Y is the
  long edge (max 15340 ≈ 210 mm). Aspect 11180:15340 = 0.729 vs 157:210 = 0.748 (≈3% off,
  consistent with active-area vs glass differences).

## Transport (USB RNDIS)

```
usb0: inet 10.11.99.1/27  (UP, MTU 1500)
wlan0: inet 192.168.0.189/24 (also online — WiFi stretch goal is viable)
```

**Non-SSH TCP over RNDIS is confirmed reachable.** A one-shot Python listener was bound to
`0.0.0.0:9999` on the rMPP; a Windows `TcpClient` connected and received the banner
`INKBRIDGE_TCP_OK`. So arbitrary TCP ports work over the USB tether — the daemon does not
need to tunnel through SSH.

## CRITICAL: xochitl holds an exclusive grab on the pen device

Measured live: `xochitl` (the reMarkable UI) opens `/dev/input/event2` and **EVIOCGRAB**s it.
While xochitl runs, **no other process receives pen events** — `evtest` opens the node and
prints capabilities but gets zero events. Confirmed: `pidof xochitl` → that pid is the only
holder of event2; pausing it (`kill -STOP`) immediately releases events to evtest.

**Implication for the daemon (not just the AppLoad phase):** the inkbridge daemon MUST stop
or pause xochitl to read the pen. Options:
- `systemctl stop xochitl` (clean; UI gone until restarted), or
- `kill -STOP $(pidof xochitl)` / `kill -CONT` (pause/resume; reversible, fast — used for the
  rate capture), or
- the daemon takes its own EVIOCGRAB *after* xochitl is stopped.

This is acceptable: inkbridge is input-only, the e-ink screen is not used as a tablet display,
so suspending xochitl in "tablet mode" costs nothing. The daemon should restore xochitl on
exit. This moves "manage xochitl" from the optional AppLoad phase into the **core daemon**.

## Digitizer report rate — MEASURED

Captured ~10 s of active scribbling (xochitl paused) on `event2`:
- **4792 intervals, mean 2.09 ms → ~479 Hz**, sd 1.20 ms, min 0.27 ms, max 16.05 ms.
- Far above the plan's assumed 150–200 Hz. Excellent for low-latency, smooth input.
- Jitter: occasional 16 ms gaps (pen moving slowly / lifting); typical interval ~2 ms.
- Pressure values during capture looked clean (e.g. 546 → 504 → 447 on lift).

Implication: the TCP path must keep up with ~500 packets/s × 18 bytes = ~9 KB/s — trivial.
`TCP_NODELAY` + one packet per `SYN_REPORT` is the right design; no batching needed.

## Still UNVERIFIED after Phase 0

1. **Exact physical active-area dimensions in mm** — measure the glass, or pull from the
   panel datasheet. Needed for accurate OTD area mapping (157×210 mm assumed).
2. **End-to-end latency** — measurable only once daemon + plugin exist (Phase 6).
