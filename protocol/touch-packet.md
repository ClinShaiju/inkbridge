# inkbridge wire protocol — `TouchPacket` v1

Authoritative binary layout for rMPP daemon → Windows OTD plugin **touch** stream. Independent
of the pen `PenPacket` stream (`protocol/packet.md`). Derived from device discovery
(`docs/touch-feasibility.md`). All multi-byte fields **little-endian**.

> **v2 transport (shipped):** the 88-byte `TouchPacket` payload below is unchanged, but it is no
> longer carried on its own TCP port. In v2 touch shares the **one muxed connection on `:9292`** as
> **channel 2**, each frame sent as the plaintext of an AES-GCM record `[u32 len][ct‖tag]` with
> plaintext `[channel(1)=2][88-byte TouchPacket]`. The touch reader starts on a `sub touch` control
> message (channel 0) that **carries the options as fields** — `{"type":"sub","ch":"touch",
> "always_on":<bool>,"palm":<bool>}` — replacing the standalone options byte. See `protocol/mux-v2.md`.

## Transport (payload + semantics — see mux-v2.md for the v2 framing)

- `TCP_NODELAY` set. One `TouchPacket` per evdev `SYN_REPORT` **that has fingers down**, plus
  exactly one final all-empty frame on the transition to no-fingers (so the receiver can issue
  the touch-up for every contact). While no fingers are down the stream is silent.
- Fixed size **88 bytes** per frame.
- **Options (v2: fields on the `sub touch` control message; v1: a byte after the `IBT1` hello):**
  - `always_on` — when false (default), the daemon forwards touch only while the on-device AppLoad
    app is open (≥1 loopback control subscriber); when true, touch streams regardless.
  - `palm` — when true (default), the daemon suppresses touch while the pen is in range (pen-priority
    palm rejection); when false, touch and pen are both active.
- **v1 (historical):** raw TCP on `:9294`, unframed 88-byte packets after a one-time `IBT1` hello,
  then a single options byte (`bit0=always_on`, `bit1=disable_palm_rejection`). v2 replaces this.
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
