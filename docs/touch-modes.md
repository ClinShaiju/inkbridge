# Touch passthrough — three modes (as built)

inkbridge can use the rMPP **finger touchscreen** (`event3`, "Elan touch input") in addition to
the pen. The behavior is chosen from a single **Touch mode** dropdown in OpenTabletDriver (on the
inkbridge tool). Background/feasibility: `docs/touch-feasibility.md`,
`docs/touch-otd-output-mode-feasibility.md`. Wire format: `protocol/touch-packet.md`.

## The dropdown

OTD → Tools → **inkbridge** → **Touch mode**:

| Mode | What happens |
|---|---|
| **Disabled** (default) | The plugin never connects to the daemon's touch port. The daemon never reads `event3`, so the rMPP touchscreen keeps driving its own reMarkable UI. No touch reaches Windows. |
| **Direct touch** | Genuine Windows multitouch: fingers are injected as real `WM_POINTER` touch contacts (up to 10), so **pinch-zoom / two-finger pan / rotate** work in touch-aware apps. The rMPP behaves like an external touchscreen mapped to your primary monitor. |
| **Gesture** | Multi-finger gestures are recognized on the PC and emitted as ordinary keystrokes / mouse-wheel, so they work in **every** app: 2-finger tap → `Ctrl+Z`, 3-finger tap → `Ctrl+Y`, pinch → `Ctrl`+wheel (zoom), 2-finger drag → scroll. |

Switching **away from Disabled** routes fingers to Windows — but only while the **on-device
inkbridge AppLoad app is open**. Leaving the app stops touch, so the rMPP works normally when
you're not using it. The touchscreen is **not grabbed** (you can still use it to drive the device
and exit the app); the app-open gate is what keeps touch from leaking to Windows.

### Settings (on the inkbridge tool)

| Setting | Effect |
|---|---|
| **Touch mode** | Disabled / Direct touch / Gesture (above). |
| **Touch without app open** | Off (default) = touch only while the AppLoad app is open. On = the rMPP is a permanent Windows touch surface regardless of the app. |
| **Palm rejection** | On (default) = touch is suppressed while the pen/eraser is in range, so a palm resting on the screen while drawing doesn't register. Off = touch and pen both active. (Plus: the daemon always drops contacts the panel firmware tags as palm.) |
| **Tap gestures in Direct touch** | On (default) = in Direct touch, fire 2-finger tap → Undo and 3-finger tap → Redo. The tap's contacts are withheld until they prove to be a manipulation, so a tap is *canceled* (Windows sees no click and no two-finger right-click) while pinch/pan commit through natively. Single-finger touch keeps zero latency. No effect in Gesture mode. |
| **Touch rotation** | *Follow OTD area* (default — reuses the rotation set on the pen's tablet area, so touch lines up like the pen), *Follow device* (rotate with the tablet's detected orientation — unreliable, see note), or a fixed *0° / 90° / 180° / 270°*. |
| **Touch monitor** | *Follow OTD area* (default — same screen + region as the pen) or pin the full touch surface to a whole monitor: *Primary / Monitor 1–4* (numbered left-to-right). Uses real in-process monitor bounds, so it's correct on multi-monitor / mixed-DPI setups. |

> **Note on rotation:** the touch panel's evdev coordinates are *fixed to the physical panel* (always portrait-native) and do **not** change when the rMPP screen rotates — only the daemon's accelerometer-derived orientation byte does. So "Follow device" depends on flaky accel detection and is inconsistent with the pen (which uses a fixed area rotation). Prefer **Follow OTD area** or a fixed angle.

Touch maps onto the **same monitor the pen targets** (OTD's configured Display area), not just the
primary monitor.

## How it works (data path)

```
event3 (10-pt MT-B) ──daemon/src/touch.rs──▶ TCP :9294 (88-byte TouchPacket/SYN_REPORT, IBT1 hello)
                                                  │
                                   OTD plugin TouchService (connects only when mode != Disabled)
                                                  │
                        Direct ──▶ TouchInjector  → InjectTouchInput → WM_POINTER → DWM gestures
                        Gesture ─▶ TouchGestures  → recognizer → SendInput (keys / wheel)
```

- **The daemon reads `event3` un-grabbed** (like the pen) and holds the shared wakelock while a
  client is connected. It streams a full 10-slot snapshot per report **only while gated on** — the
  AppLoad app is open (≥1 control-plane subscriber) or the client sent the "always on" options
  byte. Palm contacts (`ABS_MT_TOOL_TYPE = palm`) are dropped at the source. On gate-off it sends
  one empty frame (PC releases contacts) and goes quiet.
- **The plugin decides interpretation.** `TouchService` (a static singleton, so OTD's
  per-settings-apply reconstruction of the tool just re-asserts the mode) connects to `:9294` and
  hands frames to `TouchInjector` (Direct) or `TouchGestures` (Gesture). Exactly one consumer runs,
  so there's no double-input. On a mode switch it force-closes the socket and releases any held
  contacts so nothing sticks down.
- **OTD's own pipeline is bypassed for touch.** OTD is single-pointer and can't carry multitouch
  (`docs/touch-otd-output-mode-feasibility.md`); the injector/gesture code calls user32 directly,
  hosted inside the plugin process. OTD provides only the dropdown + the tool host.

## Coordinates & orientation

Touch is the same physical surface as the pen at lower resolution (`2064 × 2832`, portrait native,
0.729 aspect).

In the default **Follow OTD area** configuration (both *Touch rotation* and *Touch monitor*), Direct
mode reuses OTD's exact tablet-area → display affine transform (`TouchTarget.TryGetTransform`): the
touch grid is converted to millimetres on the shared physical surface (`179 × 239 mm`), **cropped to
the configured Tablet area** and rotated, then scaled to the Display area — i.e. touch is cropped and
rotated *identically to the pen* and lands on the same screen + region. A finger outside the Tablet
area clamps to the nearest display edge (as the pen does outside its area).

Picking an explicit **Touch monitor** (Primary / Monitor 1–4) or an explicit **Touch rotation**
instead stretches the **full** touch grid onto the chosen monitor, rotated by the frame's orientation
byte / selected angle (no crop) — the documented "pin the whole surface to a monitor" behavior.
90/270 orientation mapping in that path should be confirmed on-device (same open item as the pen's
90/270 area rotation).

## Build & deploy

No packaging changes — touch compiles into the existing single daemon binary and single plugin DLL.

```
# daemon (rMPP)
cargo build --release --target aarch64-unknown-linux-musl --manifest-path daemon/Cargo.toml
python daemon/deploy.py            # upload + restart; now also listens on :9294

# plugin (Windows)
dotnet build otd-plugin -c Release  # → otd-plugin/bin/Release/Inkbridge.dll
```

Then in OTD pick the **Touch mode** and Apply/Save. Env overrides: `INKBRIDGE_HOST` (default
`10.11.99.1`), `INKBRIDGE_TOUCH_PORT` (default `9294`).

## Known limitations / open items

- **Direct touch app support is uneven** — pinch/pan/rotate work in touch-aware apps (browsers,
  Photos, PDF/readers, maps); touch-unaware apps treat touch as a single click. Expected.
- **Gesture thresholds are first-pass** (`TouchGestures.cs` constants: pinch/scroll step, tap
  tolerance/time) — tune on-device.
- **Aspect is stretched** to fill the target monitor in Direct mode (fine for absolute touch);
  aspect-preserve is a future setting.
- **DPI**: coordinates use `GetSystemMetrics(SM_CXSCREEN/CYSCREEN)`; on HiDPI a per-monitor-DPI
  pass may be needed.
- **UIPI**: a non-elevated OTD can't inject touch into elevated (admin) windows.
- **Rapid mode toggling** can briefly leave Direct running un-grabbed if a switch races the old
  daemon reader's grab release (best-effort grab); it self-heals on the next reconnect.
- **App-open gating** uses the control-plane subscriber count as the "AppLoad app is open" signal.
  It depends on the on-device app's backend connecting to `:9293` (it does today). If you run a
  build without that app/backend, use **Touch without app open** to stream regardless.
