# inkbridge AppLoad On-Device UI — Spec

Consolidates the UI requirements from `PROJECT.md` §4/§5 with the new active-area-box requirement,
grounded in the real numbers from `otd-plugin/tablet-spec.json` + the OTD settings, and corrected
by `docs/appload-research.md` (AppLoad runs *inside* xochitl — no EPD code, no xochitl suspend).

## As-built (scaffolded — read-only visualizer)

Decisions locked & implemented. **OTD on the host owns all configuration; the app is a read-only
mirror** — it draws the active-area box on the e-ink surface + corner stats and configures nothing.
Sections 2.3 (in-app calibrate/orientation), 3 (calibration), and 6 (geometry-owner question)
below are **superseded** — those are OTD's job and were intentionally dropped to avoid a second
source of truth.

Data path (host → device, isolated from the latency-critical pen stream):
- `tools/area_push.py` (host) reads OTD `settings.json` area + `tablet-spec.json` surface, measures
  link latency, and PUBLISHES `{"type":"config"|"status","data":{…}}` to the daemon.
- `daemon/src/control.rs` — new pub/sub relay on **TCP :9293** (separate from the pen path on
  :9292; `stream_pen` untouched). Stores latest config/status, replays to subscribers on connect.
- `appload/backend/entry` (Python) subscribes to :9293, forwards messages to the QML frontend,
  and serves `appload/area.json` as a seed when the control link is down.
- `appload/frontend/Main.qml` renders the box (handles 90/270 rotation footprint swap) + stats.

Why the host pusher instead of the OTD plugin: `InkbridgeTool` is an `ITool`/`IDeviceHub` and does
**not** naturally hold the profile's area mapping (it lives in OTD's `AbsoluteModeSettings`, which
the GUI edits and OTD writes to `settings.json`). Reading that file is the robust, low-risk source
of truth and keeps the delicate pen path untouched. A plugin-reflection pusher is a later option.

Deploy: `appload/deploy.py` (SFTP). The daemon must be rebuilt+redeployed to expose :9293.

---

## 0. The key insight: the screen IS the surface

On the rMPP the e-ink panel and the pen digitizer are the same physical surface. So an AppLoad
**fullscreen** app that draws the active-area rectangle at the right pixels puts that box **exactly
over the physical region the pen maps from**. The overlay is literally "draw inside this box."

Measured geometry:
- Digitizer: **179 × 239 mm**, raw `MaxX = 11180`, `MaxY = 15340` (`tablet-spec.json`).
- Screen: **≈ 2160 × 2880 px**, ~12 px/mm — so px ≈ raw_units × (2160/11180) ≈ raw × 0.193.
- Current OTD map: Display `1536×864` (16:9), Tablet area `157×210` centered, `LockAspectRatio:false`.

## 1. Why the box matters now (the new requirement)

The host maps the tablet area onto a 16:9 display region. The tablet surface is portrait
(157:210 ≈ 0.75). To map **without distortion** the active region must be cropped to the display's
aspect ratio, so **the full surface is not used** — a sub-rectangle is. The user can't see that
sub-rectangle on a featureless e-ink sheet, so the app must **draw the configured active-area as a
visible box**, to scale, at its true position on the surface. Everything outside the box is dead
to the pen; everything inside maps to the screen.

Box derivation (the app just renders what the daemon reports; for reference the math is):
given active area `{x, y, w, h}` in raw digitizer units, draw a rectangle at
`px = x*0.193, py = y*0.193, pw = w*0.193, ph = h*0.193` on the 2160×2880 canvas.

## 2. UI elements (what to render)

1. **Active-area box** — primary element. A crisp 2–3 px black outline rectangle at the
   to-scale position of the configured active region. Dead area (outside) optionally shown with a
   light hatch / lighter fill so the usable region reads as "the white box." Center crosshair or
   corner ticks optional. This is the EPD overlay from PROJECT.md §4/§5 "active-area rectangle
   (scaled to physical screen dimensions)."
2. **Stats panel — top-right corner** (PROJECT.md "stats in corner"):
   - Connection status: `● Connected` / `○ Disconnected` (daemon link).
   - Latency: rolling-average ms (daemon reports it in status messages).
   - Report rate: Hz (optional, from daemon).
   - Active-area size: e.g. `157 × 88 mm` and/or `% of surface used`.
   - Orientation: `Landscape` / `Portrait`.
3. **Controls — bottom bar** (large e-ink-friendly buttons):
   - **Calibrate** → enters corner-tap flow (§3).
   - **Orientation toggle** Portrait ↔ Landscape → tells daemon to swap axes; redraw box.
   - **Close** → emits `close` (AppLoad lifecycle).
4. **Title / app id** — small label, top-left.

E-ink rules (match MangaInk): no animation, high contrast B/W, large fonts, and **redraw only on
state change** — PROJECT.md "EPD redraws only on config change; never during active drawing (zero
interference)." The box is static while drawing; it only repaints when config/status changes.

## 3. Calibration flow (tap four corners)

PROJECT.md §5.5: guide the user to tap the four corners that should bound the active area.
- Show a prompt + a target marker at each corner in sequence (TL → TR → BR → BL).
- **Pen taps arrive as ordinary Qt input events** (MouseArea / TapHandler) because the app is a
  window inside xochitl — **the app never opens evdev** (the daemon is the sole evdev reader).
  Tap pixel → raw digitizer unit via the inverse of the scale in §0.
- From the four taps derive `{x, y, w, h}`; if the host needs a locked aspect ratio, snap the
  derived rect to the display aspect (16:9) and show the snapped box.
- Write the new area to the daemon over IPC; daemon hot-reloads (inotify/socket) and the OTD plugin
  picks up the new mapping. Repaint the box.

## 4. Data contract with the daemon (IPC)

The app is pure UI; the daemon owns evdev + config. Per `docs/appload-research.md` §6 (Option A:
a small AppLoad `backend/entry` relays to the daemon), the messages the UI needs:

UI → daemon:
- `get_status` — request current status snapshot.
- `set_area {x, y, w, h, units:"raw"}` — push calibrated active area.
- `set_orientation {mode:"landscape"|"portrait"}` — axis swap.

daemon → UI (`onMessageReceived(type, contents)`):
- `status {connected:bool, latency_ms:float, rate_hz:float}` — pushed ~1–2 Hz.
- `config {area:{x,y,w,h}, surface:{maxX,maxY}, orientation, display_aspect}` — on change, so the
  box and stats always reflect the live mapping.

If no daemon backend yet, fall back to reading a shared JSON config file the daemon writes
(PROJECT.md §5.7) — but Option A keeps the box/stats live.

## 5. AppLoad scaffolding (from research)

- Dir: `appload/` → deployed to `/home/root/xovi/exthome/appload/inkbridge/`.
- `manifest.json`: `{ id:"com.inkbridge.companion", name:"inkbridge", entry:"/frontend/Main.qml",
  loadsBackend:true, canHaveMultipleFrontends:false, supportsScaling:true }`.
- `frontend/Main.qml`: root `Item` with `signal close` + `function unloading(){}`,
  `import net.asivery.AppLoad 1.0`, `AppLoad{ applicationID; onMessageReceived; sendMessage(...) }`.
- `backend/entry`: Python relay (model on `MangaInk/backend/entry`) bridging AppLoad's unix socket
  (`argv[1]`, newline-delimited JSON) to the daemon IPC.
- Deploy via an adapted `MangaInk/deploy.py` (SFTP + xovi-tripletap persistence).

AppLoad is confirmed installed on this device (apps present: anki, koreader, mangaink,
rmpp-browser, …), resolving the `feasibility.md` §6 "not found" uncertainty.

## 6. Open question for build

The active-area box must reflect a **locked aspect ratio** to match the host display. Current OTD
settings have `LockAspectRatio:false` and use the full 157×210. Decide: does the daemon compute &
own the aspect-locked sub-area (UI just displays it), or does the calibration UI compute it and
push it? Recommended: **daemon owns the geometry**, UI displays + calibrates against it — single
source of truth, and the OTD host mapping stays consistent.
