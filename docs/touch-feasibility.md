# Touch / Multitouch Passthrough — Feasibility (rMPP → Windows)

Goal: read the reMarkable Paper Pro finger touchscreen and present it to Windows as an
**external multitouch touchscreen** — primarily so pinch-to-zoom / two-finger pan / rotate
work in real apps. Touch must be **gated to the AppLoad app** (active only while the on-device
`inkbridge` app is open) so we never drive the stock xochitl UI by accident, with an advanced
opt-in for always-on. If full multitouch proves impractical, fall back to **discrete gestures**
(à la reMarkable: 2-finger tap = undo, 3-finger tap = redo, etc.).

**Verdict up front:** Full multitouch is **feasible**, but **not through OpenTabletDriver** — OTD
emits a single pointer and cannot carry simultaneous contacts. Touch needs its own transport and
its own tiny Windows consumer that calls the Win32 **`InjectTouchInput`** API (genuine
multitouch, real WM_POINTER → DWM gestures). The device side is essentially free: the touchscreen
is a standard 10-point MT-B evdev device that we can read alongside xochitl, reusing the daemon's
existing wakelock and connection model. All device facts below were read **live over SSH on
2026-06-04**.

---

## 1. Device side — what the rMPP touchscreen actually is (verified)

`/proc/bus/input/devices` → **`event3` "Elan touch input"** (also the SPI sibling of the pen on
`spi1.1`). Full `evtest` capability dump captured today:

```
Input device name: "Elan touch input"
Properties: INPUT_PROP_DIRECT          # direct touchscreen: coords map 1:1 to the panel
EV_KEY:  BTN_TOUCH(330)
EV_ABS (Multitouch protocol B):
  ABS_MT_SLOT        (47)  min 0  max 9        → 10 simultaneous contacts
  ABS_MT_TRACKING_ID (57)  min 0  max 65535    → per-contact identity (protocol B)
  ABS_MT_POSITION_X  (53)  min 0  max 2064
  ABS_MT_POSITION_Y  (54)  min 0  max 2832
  ABS_MT_TOUCH_MAJOR (48)  min 0  max 255      → contact size (blob radius)
  ABS_MT_PRESSURE    (58)  min 0  max 255
  ABS_MT_DISTANCE    (59)  min 0  max 255      → finger hover, like the pen
  ABS_MT_TOOL_TYPE   (55)  min 0  max 2        → finger / palm (0=finger, 2=palm) → palm rejection
```

Key implications:

- **10-finger multitouch, protocol type B** (slotted, with tracking IDs). This is the *exact*
  model `InjectTouchInput` wants — a slot is a Windows "contact", the tracking ID is its
  lifetime. No reconstruction needed; it's a near-direct mapping.
- **Coordinate space is the same physical surface as the pen, just lower resolution.** Touch is
  `2064 × 2832`; the pen (`event2`) is `11180 × 15340`. Aspect `2064:2832 = 0.729` vs pen
  `11180:15340 = 0.729` — **identical**. So touch → pen-surface is a uniform scale of ×5.42, and
  the daemon/plugin's existing physical-area mapping (179 × 239 mm) is **reusable as-is** for
  touch. Native orientation is portrait (X = short edge, Y = long edge), same as the pen — the
  orientation flags already in the pen packet apply unchanged.
- **`ABS_MT_TOOL_TYPE` gives palm vs finger** → we can drop palm contacts at the source, and (if
  we want pen-priority) suppress touch while the pen is in proximity, all on-device.
- Same Elan controller / SPI bus / kernel input stack as the pen → expect the **same ~500 Hz
  ceiling** and the same trivial bandwidth (10 contacts × a few bytes per `SYN_REPORT` ≪ pen's
  9 KB/s budget concern, which was already a non-issue).

### Concurrency & power — already solved by the pen work

Two things had to be true for the pen; both verified again for touch today:

- **xochitl does not exclusively grab `event3`.** A fresh `EVIOCGRAB` on `event3` from a separate
  process **succeeded** (`GRAB_OK`) while xochitl was running. So the daemon can read the
  touchscreen alongside xochitl exactly as it reads the pen — no stopping xochitl (which would
  trip `WatchdogSec=60` and reboot the device).
- **`/sys/power/autosleep = mem`** still applies — the SPI digitizer powers down under
  opportunistic suspend. The daemon **already holds `/sys/power/wake_lock` for the duration of a
  client session** (see `daemon/src/main.rs`), so touch reading is covered with zero new power
  work as long as a touch client is connected.

### A bonus the pen path doesn't have: we *can* grab touch

Because `EVIOCGRAB` on `event3` succeeds, the daemon has an option the pen path deliberately
declines: while touch passthrough is active it can **`EVIOCGRAB` the touchscreen**, so xochitl
receives **no** finger events and the stock UI doesn't scroll/navigate/undo underneath us. That
directly satisfies the "don't click random things in the stock UI" requirement *at the source* —
much stronger than only gating the PC side. The trade-off: with touch grabbed, the user can't tap
to close the AppLoad app, so we must keep a **non-touch exit** (a pen tap on a close target, a
hardware/gesture exit, or AppLoad's own close affordance). The pen is never grabbed, so a pen tap
always works as the escape hatch. (Grab is a per-session, opt-in behavior — see gating below.)

---

## 2. Windows side — why OTD can't do this, and what can

### OpenTabletDriver is a dead end for multitouch

OTD's output pipeline is a **single absolute/relative pointer** (it drives one pen/cursor). It has
no concept of multiple simultaneous contacts and exposes no multitouch output mode. The existing
inkbridge plugin (`InkbridgeDevice.cs` → an OTD digitizer endpoint) is fundamentally one-pointer.
**Pinch/zoom/rotate cannot be expressed through it.** Trying to bend OTD into multitouch is not
viable; touch needs a separate consumer.

### The right mechanism: Win32 Pointer Injection (`InjectTouchInput`)

Windows ships a first-class touch-injection API (user32.dll, Windows 8+):

- `InitializeTouchInjection(maxCount, dwMode)` — once at startup; `maxCount` up to 256 (we need
  10), `dwMode = TOUCH_FEEDBACK_NONE` to suppress the OS touch-visualization circles (or
  `…_DEFAULT` to show them).
- `InjectTouchInput(count, POINTER_TOUCH_INFO[])` — called per frame with the current contacts.
  Each `POINTER_TOUCH_INFO` carries a `POINTER_INFO` (`pointerType = PT_TOUCH`, a stable
  `pointerId`, screen-pixel `ptPixelLocation`, and flags `DOWN | UPDATE | UP | INRANGE |
  INCONTACT`) plus touch extras (`rcContact` from `ABS_MT_TOUCH_MAJOR`, `pressure` from
  `ABS_MT_PRESSURE`, `orientation`).

This produces **real `WM_POINTER` touch input**. Windows' DWM then synthesizes the **gesture
stream** (pinch-zoom, two-finger pan/scroll, rotate, press-and-hold) and the legacy `WM_GESTURE`
messages, so it works in genuinely touch-aware apps: Edge/Chrome, Photos, Maps, PDF/e-book
readers, OneNote, Office, many image viewers, and parts of Krita. This is exactly "external
touchscreen", and pinch-to-zoom — the stated primary goal — falls out for free.

Caveats to design around:

- **Coordinates are virtual-screen pixels.** We map the rMPP touch area → a chosen monitor (reuse
  the same target-display + area-mapping the pen uses). Multi-monitor = pick the display.
- **UIPI / integrity level:** a non-elevated injector cannot inject into elevated (admin) windows.
  Run the injector at the integrity level matching the apps you want to drive; for normal apps,
  standard user is fine.
- **It's a pointer source, not a HID device** — there's no persistent "touchscreen" in Device
  Manager; injection is per-process-session. That's fine for our use and avoids driver signing.
- A message-pump thread is recommended for clean delivery cadence; a tight ~200–500 Hz inject loop
  fed by the network stream works.

### Where the injector lives

A small **standalone Windows sidecar** (`inkbridge-touch.exe`, e.g. C#/.NET P/Invoke or a tiny
Rust/Win32 binary) that:

1. connects to a **new daemon touch port** (proposal: TCP `:9294`, sibling of pen `:9292` /
   control `:9293`),
2. decodes touch frames into the current contact set,
3. transforms coordinates onto the target monitor,
4. calls `InjectTouchInput`.

Keeping it **out of the OTD plugin** is deliberate: the OTD plugin's lifetime is tied to OTD
detecting the tablet, and it runs inside OTD's process; touch is independent of all that. The
installer already bundles/launches helpers, so adding one more sidecar fits the existing packaging.
(If we later want a single tray app, the sidecar can absorb the gesture-mode logic too.)

---

## 3. Gating — "only through the AppLoad app"

The cleanest and safest gate is **device-side**: the daemon only reads/streams `event3` while the
on-device `inkbridge` AppLoad app is open. This means touch can't reach Windows unless the user is
literally in the inkbridge app, and (with the optional grab) the stock UI gets nothing either.

We already have the plumbing for this. The AppLoad app has a backend that speaks to the daemon's
**control plane (`:9293`)** as a subscriber (`IBCS`). The app lifecycle *is* the signal:

- **App opens** → its backend connects → it sends a new control message, e.g.
  `{"type":"touch","data":{"active":true}}` (or a dedicated role token like `IBTA`). The daemon
  flips a `touch_enabled` flag → starts the `event3` reader (and optionally `EVIOCGRAB`s it) and
  begins serving `:9294`.
- **App closes** → AppLoad calls `unloading()` / the backend socket drops → daemon clears the flag
  → stops reading, releases the grab, drops any `:9294` client. Touch is dead the instant you leave
  the app.

Two enable layers, matching the request:

- **Default (gated):** touch flows only while the AppLoad app is foreground, per above.
- **Advanced setting (always-on):** an opt-in (PC-side OTD setting *or* an on-device toggle) that
  sets `touch_enabled` independent of app focus, for users who want the rMPP as a permanent
  Windows touchpad/touchscreen. When always-on, we should **not** grab `event3` (so the device
  stays usable), and instead rely on the PC-side injector being the only consumer.

Defense in depth: even if the device streams, the **PC sidecar only injects when its own enable is
set** (and only to the configured monitor), so a stray stream can't silently drive the desktop.

---

## 4. Proposed architecture (full-multitouch path)

```
rMPP (daemon)                                   Windows
─────────────                                   ───────
event3 (Elan touch, MT-B, 10 slots)
   │  read alongside xochitl (no stop),
   │  wakelock already held,
   │  optional EVIOCGRAB while active
   ▼
touch.rs  ── TouchFrame stream ──▶ TCP :9294 ──▶ inkbridge-touch.exe (sidecar)
   ▲                                                 │  decode slots → contacts
   │ gate (touch_enabled)                            │  map area → target monitor px
   │                                                 ▼
control plane :9293  ◀── "touch active" ──  AppLoad backend  ── InjectTouchInput()  ──▶ WM_POINTER
   (app open/close lifecycle)                                                            → DWM gestures
                                                                                         (pinch/pan/rotate)
```

### Wire format — `TouchFrame` (slot-based, protocol-B native)

Mirror the slotted source directly so the PC does no reconstruction. One frame per `SYN_REPORT`
that changed any contact:

- header: `magic/version`, microsecond timestamp, orientation (reuse pen bits), contact count `n`
- per contact (`n` of them): `slot` (0–9), `tracking_id` (or a `lifted` flag), `x` (u16, 0–2064),
  `y` (u16, 0–2832), `pressure` (u8), `major` (u8), `tool_type` (finger/palm), `flags`
  (down/update/up/hover).

A contact whose `tracking_id` went to −1 (slot released) is sent once with an `up` flag; the
sidecar then issues the `POINTER_FLAG_UP` and drops it. Hello banner mirrors the pen path
(`IBT1`). Bandwidth: 10 contacts × ~10 B × 500 Hz ≈ 50 KB/s — trivial over RNDIS.

### On-device daemon changes (`daemon/`)

- New `touch.rs`: resolve `Elan touch input` by name (not a hardcoded node), maintain the 10-slot
  state table from `ABS_MT_*` events, serialize a `TouchFrame` per relevant `SYN_REPORT`.
- New listener on `:9294`, gated by `touch_enabled`; reuse the existing wakelock refcount (touch
  clients count too) so the digitizer stays powered.
- Gate wiring in `control.rs`: a `touch`/`IBTA` message from the AppLoad backend toggles
  `touch_enabled`; closing the app clears it. Optional `EVIOCGRAB` on enable when in gated mode.
- Palm/pen-priority filtering on-device (drop `tool_type == palm`; optionally suppress touch while
  `BTN_TOOL_PEN` proximity is asserted on `event2`).

### Windows sidecar (`inkbridge-touch/`)

- Connect `:9294`, decode frames, keep a contact map keyed by slot.
- Coordinate transform: normalized `x/2064`, `y/2832` → apply area + orientation → target-monitor
  pixel rect (share the OTD area config, or its own copy).
- `InitializeTouchInjection(10, TOUCH_FEEDBACK_NONE)` once; per frame build `POINTER_TOUCH_INFO[]`
  and call `InjectTouchInput`. Maintain `DOWN`/`UPDATE`/`UP` transitions from frame flags.
- Own enable flag + target-monitor selection; tray/CLI to toggle and to switch gated vs always-on.

### AppLoad app (`appload/`)

- On `Component.onCompleted`, ask the backend to assert touch-active; on `unloading()`, release it.
- (Optional) a small on-screen "Touch passthrough: ON" indicator and a non-touch close target so a
  grabbed session is escapable via the pen.

---

## 5. Fallback — gesture mode (if full multitouch is deferred)

If we want a quick win before the injector is solid, interpret gestures and emit shortcuts. This is
strictly easier (no per-contact injection; just recognize and fire `SendInput`), works in *every*
app, and matches the reMarkable muscle memory the request cites:

| Gesture (on rMPP touch)        | Windows action (default, configurable) |
|--------------------------------|----------------------------------------|
| 2-finger tap                   | Undo (`Ctrl+Z`)                        |
| 3-finger tap                   | Redo (`Ctrl+Y`)                        |
| 2-finger vertical drag         | Mouse wheel scroll                     |
| 2-finger horizontal drag       | Shift+wheel / back-forward             |
| Pinch in/out                   | `Ctrl` + wheel (zoom in most apps)     |
| 3-finger swipe L/R             | Page back/forward, or `Alt+←/→`        |
| 4/5-finger tap                 | user-mapped (e.g. Esc, switch desktop) |

Recognition can live **on the PC sidecar** (it already receives the contact stream — recognize
there, so the same `:9294` stream serves both modes), or on-device. Pinch-as-`Ctrl+wheel` gets
"zoom" working everywhere even where true touch isn't honored, so gesture mode is a useful
*complement* to the injector, not only a fallback. Ship the injector as the primary and let an
advanced setting switch a gesture this is the recommended end state.

---

## 6. Risks & open items

1. **App acceptance of injected touch.** `InjectTouchInput` is well-supported, but a few apps
   distinguish injected vs real HID touch or behave oddly. Validate against the actual targets
   (browser zoom, Photos, a PDF reader) early.
2. **Pen + touch coexistence / palm rejection.** Decide the policy: pen-priority (suppress touch
   while pen in range) vs simultaneous. We have `tool_type` and pen proximity on-device to
   implement either; needs UX testing.
3. **Grab escape hatch.** If we `EVIOCGRAB` `event3` in gated mode, ensure a reliable non-touch
   way out (pen-tap close target / hardware). Never leave a session where the only exit is touch.
4. **Latency feel for gestures.** Pinch-zoom is sensitive to jitter; the ~500 Hz source and
   `TCP_NODELAY` help, but the inject loop cadence and any smoothing (cf. the pen's 1€ filter)
   need tuning. Hover/`ABS_MT_DISTANCE` is available if we want hover-state contacts.
5. **Coordinate/aspect at non-portrait orientations.** Reuse the pen orientation flags, but verify
   90/270 mapping for touch specifically (the pen's 90/270 application is itself still Phase-2
   per memory).
6. **UIPI** — injector integrity vs target windows (admin apps). Document/limit, or offer an
   elevated mode.
7. **Always-on safety.** In always-on mode the device is a live touch source for the desktop;
   make the PC-side enable explicit and obvious (tray state), and prefer no-grab so the rMPP stays
   usable on its own.

---

## 7. Recommended build order

1. **Device reader first (no PC risk):** add `touch.rs` + `:9294`, gate behind a temporary env/flag,
   verify a 10-contact frame stream over RNDIS with a throwaway Python dumper. (Confirms rate,
   coordinate transform, palm filtering.)
2. **Sidecar MVP:** `inkbridge-touch.exe` doing single-contact `InjectTouchInput` → a finger moves
   the Windows cursor/touch point on the chosen monitor. Then scale to all 10 contacts.
3. **Validate pinch-zoom** in a browser and Photos; tune cadence/smoothing.
4. **Gate via AppLoad:** wire the control-plane `touch` message to `touch_enabled`; app open/close
   toggles it; add the optional `EVIOCGRAB` + pen escape hatch.
5. **Advanced always-on setting** (PC + on-device toggle), no-grab.
6. **Gesture mode** as a selectable alternative/complement (2-finger undo, pinch→Ctrl+wheel, …).

None of this touches the pen path; it's additive (new device node, new port, new sidecar). The two
hard unknowns from the original plan — "can we read touch alongside xochitl" and "is it real
10-point MT" — are both **answered yes** by today's on-device probe.

---

### Sources
- Live device probe (SSH `root@10.11.99.1`, 2026-06-04): `evtest /dev/input/event3` capability
  dump; `EVIOCGRAB` success on `event3`; `/sys/power/autosleep = mem`.
- Existing pipeline: `daemon/src/main.rs` (wakelock + un-grabbed read model), `daemon/src/packet.rs`
  (wire-format pattern), `daemon/src/control.rs` (`:9293` pub/sub, `IBCP`/`IBCS`),
  `otd-plugin/InkbridgeDevice.cs` (single-pointer OTD endpoint — the multitouch dead end).
- `docs/phase0-findings.md` (pen facts, xochitl/watchdog/autosleep model),
  `docs/appload-research.md` (AppLoad lifecycle + backend↔daemon IPC used for the gate).
- Win32: `InitializeTouchInjection` / `InjectTouchInput`, `POINTER_TOUCH_INFO` (user32.dll,
  Windows 8+ pointer injection) for genuine WM_POINTER multitouch and DWM gesture synthesis.
</content>
</invoke>
