# inkbridge wire protocol — `TouchPacket` v1

Authoritative binary layout for rMPP daemon → Windows OTD plugin **touch** stream. Independent
of the pen `PenPacket` stream (`protocol/packet.md`). Derived from device discovery
(`docs/touch-feasibility.md`). All multi-byte fields **little-endian**.

## Transport

- Raw TCP over USB RNDIS. The daemon listens on `0.0.0.0:9294` (reach it at `10.11.99.1:9294`).
- `TCP_NODELAY` set. One `TouchPacket` per evdev `SYN_REPORT` **that has fingers down**, plus
  exactly one final all-empty frame on the transition to no-fingers (so the receiver can issue
  the touch-up for every contact). While no fingers are down the stream is silent.
- No length prefix: packets are fixed size (**88 bytes**). The receiver reads in 88-byte frames.
- A 4-byte ASCII hello `IBT1` is sent once by the daemon on connect, before the first packet.
- **Then the client sends one options byte:**
  - `bit0 = always_on` — when clear (default), the daemon forwards touch only while the on-device
    AppLoad app is open (≥1 control-plane subscriber on `:9293`); when set, touch streams regardless.
  - `bit1 = disable_palm_rejection` — when clear (default), the daemon suppresses touch while the pen
    is in range (pen-priority palm rejection); when set, touch and pen are both active.
- `event3` is read **un-grabbed** (like the pen) — the stock reMarkable UI and the AppLoad app
  still receive touch (so you can exit the app); the app-open gate, not a grab, is what keeps touch
  from leaking to Windows. The OTD plugin connects only in Direct or Gesture mode; in Disabled mode
  it never connects, so touch never leaves the tablet.
- On the gated-on → gated-off transition (app closed) the daemon sends a single all-empty frame so
  the receiver releases every contact, then goes quiet.

## `TouchPacket` — 88-byte layout

Full 10-slot snapshot every frame; the receiver diffs successive snapshots to derive
DOWN/UPDATE/UP. Slot index is the stable contact identity for a touch's lifetime (protocol B).

```
── 8-byte header ──
offset size field          notes
0      4    timestamp_us   u32 LE, monotonic µs
4      1    orientation    0=portrait native, 1/2/3 = 90/180/270° CW (same as PenPacket)
5      1    contact_count  number of active (non-palm) contacts this frame
6      1    flags          low nibble = version (currently 1)
7      1    reserved       0

── 10 × 8-byte contact records (slots 0..9) ──
off=8+i*8 size field       notes
+0        1    slot        contact index 0..9 (== i; stable for the touch lifetime)
+1        1    active      1 = finger down in this slot, 0 = empty
+2        2    x           u16 LE, 0..2064  (ABS_MT_POSITION_X, short edge in portrait)
+4        2    y           u16 LE, 0..2832  (ABS_MT_POSITION_Y, long edge in portrait)
+6        1    pressure    u8, 0..255       (ABS_MT_PRESSURE)
+7        1    major       u8, 0..255       (ABS_MT_TOUCH_MAJOR, contact size)

total = 8 + 10*8 = 88 bytes
```

### Notes

- **Palm rejection at the source.** Slots whose `ABS_MT_TOOL_TYPE` is palm (2) are reported with
  `active = 0` and excluded from `contact_count`. The receiver never sees palm contacts.
- **Coordinate space** is the raw touch grid `2064 × 2832` (portrait native, X = short edge).
  It is the *same physical surface* as the pen (`11180 × 15340`) at ×5.42 lower resolution
  (identical 0.729 aspect). The receiver normalizes (`x/2064`, `y/2832`) and applies
  `orientation` before mapping to a target display.
- **Down/up derivation.** A slot transitioning `active 0→1` is a touch-down; `1→1` is a move;
  `1→0` is a touch-up. The receiver keys Windows pointer/contact IDs by `slot`.
- The orientation byte is always valid (from `daemon/src/orientation.rs`), letting the receiver
  rotate touch coordinates to match the device's held orientation.

## Mapping to Windows (receiver side)

The OTD plugin's `TouchService` reads this stream and, per the user-selected **Touch mode**:
- **Direct touch** → `InjectTouchInput` (genuine WM_POINTER multitouch; DWM gestures/pinch-zoom).
  Each active slot becomes a `POINTER_TOUCH_INFO` with `pointerId = slot`, mapped to screen px.
- **Gesture** → a recognizer turns multi-finger taps/pinch/pan into keystrokes/wheel via SendInput.
- **Disabled** → the plugin does not connect; the daemon never reads `event3`.

## Versioning

`flags` low nibble carries the version. Any field addition bumps the version and changes the
fixed size; never reinterpret existing offsets. Kept separate from `PenPacket` versioning.
