# inkbridge — reMarkable Paper Pro as a Pressure-Sensitive Drawing Tablet for Windows

## 0. Summary

Turn a reMarkable Paper Pro (rMPP) into a full-featured graphics tablet input device for
Windows — matching the capabilities of a real Wacom/Huion tablet (position, pressure, hover,
pen buttons, optional tilt), with a proper management UI for active-area mapping and pressure
curves.

The approach: a lightweight **Rust daemon on the rMPP** reads the raw pen digitizer via evdev and
streams compact binary packets over the USB network link to a **Windows receiver**. The receiver
feeds those events into the **OpenTabletDriver (OTD) pipeline** via a custom OTD plugin, which
gives us Windows Ink / WinTab output, area mapping, pressure-curve editing, and a battle-tested
management UI essentially for free — without writing or signing a kernel-mode driver.

Primary target use case: raw, low-latency cursor input and digital art
(Krita / Clip Studio / Photoshop, requiring real pressure sensitivity).

---

## 1. Goals & Non-Goals

### Goals
- Sub-10ms input latency over USB (target 3–6ms).
- Full pressure sensitivity (4096 levels via USI 2.0) delivered to Windows Ink / WinTab apps.
- Hover (pen-near) detection.
- Pen button mapping (BTN_STYLUS / BTN_STYLUS2).
- Configurable active area (crop a sub-region of the 157×210mm surface).
- Editable pressure curve (bezier / LUT).
- Windows management UI: connection status, area mapper, pressure curve, button bindings.
- Auto-detect + auto-reconnect when the rMPP is plugged in over USB.
- Persisted configuration.

### Non-Goals (initial scope)
- Screen mirroring onto the rMPP display (e-ink refresh makes this impractical — input-only).
- Wireless/WiFi operation as the primary path (USB tether is the supported transport; WiFi may
  be a stretch goal).
- Multi-tablet / multi-device support.
- macOS / Linux host support (Windows-first; OTD is cross-platform so this is a possible later
  extension).

---

## 2. Target Hardware / Software Context

- **Tablet:** reMarkable Paper Pro (rMPP), ARMv8.0, Codex Linux, root SSH access already
  established, Entware installed. USI 2.0 active pen (Marker Plus).
- **Host:** Windows (AtlasOS), Lenovo Legion 5 15APH9, RTX 4060 Laptop.
- **Transport:** USB RNDIS network link, host reaches the tablet at `10.11.99.1`.
- **Pen protocol:** USI 2.0 — 4096 pressure levels, tilt support (exposure on rMPP TBD), hover.

---

## 3. Architecture

```
┌──────────────────────────────────────────────┐
│ reMarkable Paper Pro                           │
│                                                │
│  ┌──────────────────────────────────────────┐ │
│  │ inkbridge-daemon (Rust)                    │ │
│  │  • open /dev/input/eventN (pen digitizer)  │ │
│  │  • parse evdev: ABS_X, ABS_Y, ABS_PRESSURE,│ │
│  │    ABS_TILT_X/Y, BTN_TOUCH, BTN_TOOL_PEN,  │ │
│  │    BTN_STYLUS, BTN_STYLUS2                  │ │
│  │  • serialize compact binary packet          │ │
│  │  • TCP server on 10.11.99.1:PORT            │ │
│  │  • systemd unit, auto-start on boot         │ │
│  └───────────────────┬──────────────────────┘ │
│                       │ (evdev fd, read-only)   │
│  ┌────────────────────▼──────────────────────┐ │
│  │ inkbridge-appload (AppLoad app, C/Qt Quick) │ │
│  │  • suspends xochitl (owns display cleanly)  │ │
│  │  • EPD overlay: active-area rectangle,      │ │
│  │    connection status, latency readout        │ │
│  │  • on-device area calibration (tap corners) │ │
│  │  • orientation toggle (portrait/landscape)  │ │
│  │  • sends config changes → daemon via IPC    │ │
│  │  • redraws EPD only on config change        │ │
│  └───────────────────────────────────────────┘ │
└──────────────────────┬───────────────────────┘
                        │  USB RNDIS, raw TCP (no SSH overhead)
                        ▼
┌──────────────────────────────────────────────┐
│ Windows Host                                   │
│  ┌──────────────────────────────────────────┐ │
│  │ inkbridge OTD plugin (C#)                  │ │
│  │  • TCP client → connects to daemon          │ │
│  │  • deserialize packets                      │ │
│  │  • implement OTD tablet/device interface    │ │
│  │  • feed position + pressure into OTD        │ │
│  └──────────────────┬───────────────────────┘ │
│                     ▼                           │
│  OpenTabletDriver pipeline                      │
│   • active-area mapping                         │
│   • pressure curve                              │
│   • button bindings                             │
│   • output mode: Windows Ink (VMulti) / WinTab  │
│                     ▼                           │
│   Krita / Clip Studio / Photoshop         │
│                                                 │
│  ┌──────────────────────────────────────────┐ │
│  │ inkbridge-ui companion app (WinUI3/egui)   │ │
│  │  • connection status + latency readout      │ │
│  │  • area config sync with AppLoad app        │ │
│  │  • (area/curve/buttons handled by OTD GUI)  │ │
│  └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

### Key design decision: OTD plugin vs standalone virtual HID
Rather than writing a custom kernel-mode HID driver (which requires driver signing and is the
hardest path), implement a **custom OpenTabletDriver device/driver plugin**. OTD already provides:
- A virtual HID layer (VMulti) and Windows Ink + WinTab output modes.
- Active-area mapping with a screen-preview GUI.
- Pressure-curve editing.
- Pen button → keybind/action mapping.
- A polished management GUI.

This collapses the hardest and most time-consuming parts of the project into "feed OTD a stream of
position + pressure + button samples."

---

## 4. Planned Features

### Core (MVP)
- [ ] rMPP daemon reads pen digitizer evdev events.
- [ ] Compact binary packet protocol (x, y, pressure, buttons, hover, timestamp).
- [ ] Raw TCP transport over USB.
- [ ] Windows receiver connects and deserializes.
- [ ] OTD plugin feeds X/Y + pressure into OTD pipeline.
- [ ] Windows Ink output verified in a drawing app.

### Full parity with Wacom
- [ ] Pressure sensitivity end-to-end into Windows Ink / WinTab.
- [ ] Hover (cursor moves without click while pen is near).
- [ ] Pen button 1 / button 2 mapping.
- [ ] Active-area cropping (sub-region of the surface).
- [ ] Pressure curve editing.
- [ ] Tilt support (if rMPP exposes ABS_TILT_X/Y — to be verified).

### Quality-of-life
- [ ] Auto-detect rMPP on USB plug-in.
- [ ] Auto-reconnect on disconnect/sleep.
- [ ] Config persistence across reboots.
- [ ] systemd unit on rMPP for daemon auto-start.
- [ ] Connection status / latency readout in UI.
- [ ] Low-jitter mode (tune TCP_NODELAY, packet batching off).

### AppLoad On-Device UI (`appload/`)
- [ ] Suspend xochitl cleanly on launch; restore on exit.
- [ ] EPD overlay showing configured active-area rectangle.
- [ ] Connection status indicator (connected / disconnected / latency ms).
- [ ] On-device area calibration: tap four corners to define active region, push config to daemon.
- [ ] Orientation toggle (portrait / landscape) with immediate EPD redraw.
- [ ] EPD redraws only on config change (never during active drawing — zero interference).
- [ ] IPC channel to daemon for config sync (Unix socket or shared config file).


- [ ] WiFi transport option.
- [ ] On-screen static reference image push to the rMPP (slow-refresh "what am I drawing" glance).
- [ ] macOS / Linux host support (leverage OTD cross-platform).
- [ ] Configurable polling/report rate.

---

## 5. Implementation Steps (Phased)

### Phase 0 — Discovery / Verification (do this FIRST)
1. SSH into rMPP. Enumerate input devices:
   ```bash
   cat /proc/bus/input/devices
   ls -la /dev/input/
   ```
2. Identify the pen digitizer event node (try each with `evtest /dev/input/eventN`, move pen).
3. Record which event codes are emitted: confirm `ABS_X`, `ABS_Y`, `ABS_PRESSURE`,
   `BTN_TOUCH`, `BTN_TOOL_PEN`, `BTN_STYLUS`, and CHECK whether `ABS_TILT_X` / `ABS_TILT_Y`
   appear. Record min/max/resolution for each axis (`evtest` prints these).
4. Confirm USB RNDIS connectivity from Windows to `10.11.99.1` and that arbitrary TCP ports
   are reachable (not just SSH:22).
5. Document the digitizer's effective polling/report rate (timestamp deltas between events).

### Phase 1 — rMPP Daemon (Rust)
1. Cross-compile target: `aarch64-unknown-linux-gnu` (or musl) for ARMv8.0.
2. Use the `evdev` crate to open the pen event node.
3. Maintain current pen state; on each evdev event, update and emit a packet.
4. Define a compact, fixed-size binary packet (little-endian), e.g.:
   ```
   struct PenPacket {
     u32 timestamp_us;
     u16 x;            // raw digitizer units
     u16 y;
     u16 pressure;     // 0..4095
     i16 tilt_x;       // optional, 0 if unsupported
     i16 tilt_y;
     u8  buttons;      // bitfield: touch, stylus1, stylus2, hover
     u8  flags;
   }
   ```
5. TCP server bound to `10.11.99.1:PORT`; on client connect, stream packets.
6. Set `TCP_NODELAY`; do not batch (latency over throughput).
7. systemd unit for auto-start; handle Entware/Codex specifics (these were established in prior
   rMPP work).
8. Handle reconnection (client drops → keep listening).

### Phase 2 — Windows OTD Plugin (C#)
1. Set up an OTD plugin project against the current OTD plugin SDK / interfaces.
2. Implement the device/driver interface so OTD treats inkbridge as a tablet source.
3. TCP client connects to the rMPP daemon, deserializes `PenPacket`.
4. Map raw digitizer units → OTD's expected coordinate/report format using the
   min/max/resolution recorded in Phase 0.
5. Feed position, pressure, hover, and buttons into the OTD pipeline.
6. Verify OTD detects the "tablet" and shows live input in its GUI.
7. Enable Windows Ink output mode (VMulti) and confirm pressure in a drawing app.

### Phase 3 — Mapping, Curves, Buttons
1. Use OTD's built-in active-area mapper to crop the surface (verify coordinate transform).
2. Use OTD's pressure-curve editor; verify the 4096→Windows Ink range mapping is smooth.
3. Bind pen buttons to actions/keys via OTD.
4. Tune pressure threshold for "click" vs "hover."

### Phase 4 — UX / Robustness
1. Auto-detect rMPP presence on USB (poll for `10.11.99.1` reachability or RNDIS adapter).
2. Auto-reconnect logic on both ends after sleep/unplug.
3. Persist config (rely on OTD's settings store; add a small companion app only if needed).
4. Optional: small companion UI (`inkbridge-ui/`, WinUI 3 / WPF, or `egui` in Rust) for
   connection status + latency readout if OTD's GUI is insufficient.

### Phase 5 — AppLoad On-Device UI (`appload/`)
1. Confirm AppLoad framework version available on rMPP and its API for suspending xochitl.
2. Scaffold a minimal AppLoad app in C / Qt Quick (match existing rMPP AppLoad conventions).
3. On launch: suspend xochitl, take ownership of EPD display.
4. Implement EPD overlay rendering:
   - Active-area rectangle (scaled to physical screen dimensions).
   - Connection status badge (polling daemon's IPC endpoint).
   - Latency readout (daemon reports rolling average in status messages).
5. On-device area calibration flow:
   - Guide user to tap four corners with the pen.
   - Derive active-area rect from tap coordinates.
   - Write config to shared location; daemon reloads without restart.
6. Orientation toggle button: portrait ↔ landscape; triggers EPD redraw + daemon axis swap.
7. IPC with daemon: Unix domain socket or a small JSON config file with inotify-based reload
   on the daemon side — keep it simple.
8. On exit: restore xochitl.
9. Ensure the AppLoad app never reads evdev itself — the daemon owns that fd exclusively.

### Phase 6 — Validation
1. Measure end-to-end latency (instrument timestamps; compare pen-down to cursor event).
3. Test pressure in Krita / Clip Studio (pressure curve, line weight variation).
4. Stress test under host GPU load (game/render running) for input starvation.
5. Verify AppLoad app EPD redraws do not cause any input event drops or latency spikes.

---

## 6. Open Questions / Risks (to be resolved during Phase 0)

- **Tilt exposure:** Does the rMPP kernel expose `ABS_TILT_X/Y` for the USI 2.0 pen? If not,
  tilt is dropped (many art workflows don't need it).
- **Polling rate:** Actual digitizer report rate on rMPP is unconfirmed (assumed ~150–200Hz).
  Verify in Phase 0; it caps the achievable smoothness.
- **OTD plugin extensibility:** Confirm the current OTD version's plugin API actually allows a
  fully synthetic/network-sourced device (vs only USB-HID-backed tablets). If OTD cannot ingest
  an arbitrary software source, fall back to: (a) VMulti directly, or (b) a custom virtual HID.
  This is the single biggest architectural risk — validate early.
- **VMulti / Windows Ink pressure path:** Confirm VMulti delivers true pressure to Windows Ink
  and WinTab apps on the target Windows build (AtlasOS may have stripped components VMulti needs).
- **USB TCP port reachability:** Confirm non-SSH TCP ports are reachable over RNDIS.
- **Driver signing:** The OTD-plugin route should avoid kernel-driver signing entirely; if the
  fallback to a custom HID driver is needed, signing becomes a real obstacle.
- **AppLoad xochitl suspend:** Confirm the AppLoad framework on the current rMPP firmware can
  cleanly suspend xochitl and that the daemon's evdev fd is unaffected. If AppLoad changed
  in recent firmware (the rMPP has had active updates), the AppLoad phase may need adjustment.
- **Daemon + AppLoad evdev coexistence:** Verify two processes can open the same evdev node
  simultaneously, or confirm the AppLoad app truly never needs to read it (IPC-only approach).
  If evdev is exclusive, the daemon must be the sole reader and the AppLoad app must get all
  pen data from the daemon via IPC.
- **rMPP firmware updates:** Future rMPP OS updates may change the event node or break the
  daemon/systemd setup.

---

## 7. Tech Stack

- **rMPP daemon:** Rust (`evdev` crate, std TCP), cross-compiled for aarch64 (`daemon/`).
- **rMPP AppLoad UI:** C / Qt Quick, AppLoad framework, EPD rendering (`appload/`).
- **Windows receiver/plugin:** C# (.NET), OpenTabletDriver plugin SDK (`otd-plugin/`).
- **Virtual HID / output:** OpenTabletDriver → VMulti (Windows Ink) + WinTab.
- **Windows companion UI:** WinUI 3 / WPF or `egui` in Rust, optional (`companion-ui/`).
- **Shared protocol:** binary packet spec, IPC config schema (`protocol/`).
- **Tablet auto-start:** systemd unit on the rMPP, lives in `daemon/`.

---

## 8. Repository Layout (proposed)

```
inkbridge/
├── PROJECT.md                        # this file
│
├── daemon/                           # Rust — runs on rMPP, owns evdev + TCP server
│   ├── src/
│   │   ├── main.rs
│   │   ├── evdev.rs                  # pen event reading + parsing
│   │   ├── transport.rs              # TCP server, packet serialization
│   │   ├── config.rs                 # active-area, orientation, IPC config reload
│   │   └── ipc.rs                    # Unix socket / inotify config watcher
│   ├── Cargo.toml
│   └── inkbridge-daemon.service      # systemd unit for auto-start on rMPP
│
├── appload/                          # C/Qt Quick — AppLoad app, runs on rMPP display
│   ├── src/
│   │   ├── main.c                    # AppLoad entry, xochitl suspend/restore
│   │   ├── overlay.qml               # EPD overlay: area rect, status, latency
│   │   ├── calibration.qml           # corner-tap calibration flow
│   │   └── ipc_client.c              # talks to daemon Unix socket
│   └── CMakeLists.txt
│
├── otd-plugin/                       # C# — OpenTabletDriver plugin, runs on Windows
│   ├── InkbridgePlugin.cs            # IDriver / IDeviceHub implementation
│   ├── InkbridgeTransport.cs         # TCP client, PenPacket deserialization
│   ├── InkbridgePlugin.csproj
│   └── tablet-spec.json              # OTD tablet descriptor (digitizer dimensions etc.)
│
├── companion-ui/                     # Optional Windows companion app
│   ├── src/                          # WinUI3 / WPF  —or—  Rust + egui
│   │   ├── main.rs / App.xaml.cs
│   │   ├── connection_view.*         # status, latency, reconnect button
│   │   └── area_sync.*               # receives area config from AppLoad, shows on host
│   └── Cargo.toml / .csproj
│
├── protocol/                         # Language-agnostic specs shared by all components
│   ├── packet.md                     # PenPacket binary layout, field ranges, versioning
│   └── ipc_config.md                 # daemon ↔ AppLoad config schema (JSON)
│
└── docs/
    ├── phase0-findings.md            # evdev codes, axis ranges, polling rate (fill in Phase 0)
    └── feasibility.md                # Claude Code audit output (generated before Phase 1)
```

---

## 9. NOTICE TO CLAUDE CODE — AUDIT BEFORE BUILDING

Before writing any implementation code, **audit this entire plan for feasibility and correctness.**
Do not assume the architecture above is valid just because it is written down. Specifically:

1. **Verify the OTD plugin assumption (highest priority).** Confirm against the *current*
   OpenTabletDriver version whether its plugin API can accept a fully software/network-sourced
   input device, rather than only enumerated USB HID tablets. If it cannot, the central
   architectural assumption is wrong — surface this immediately and propose the best alternative
   (direct VMulti integration, a signed/test-signed custom HID driver, or another virtual-tablet
   layer), with the tradeoffs spelled out.

2. **Verify the Windows Ink / WinTab pressure path** actually works on the target Windows
   environment (AtlasOS), where components VMulti or Windows Ink rely on may have been removed.
   If pressure cannot reach apps, full Wacom parity is not achievable as described.

3. **Run Phase 0 discovery before committing to packet format.** The packet layout, axis ranges,
   tilt support, and polling rate are all *assumptions* until confirmed on the actual device.
   Do not hardcode ranges or event nodes; derive them from `evtest` output.

4. **Confirm transport assumptions:** that non-SSH TCP ports are reachable over USB RNDIS, and
   that raw TCP with `TCP_NODELAY` genuinely hits the 3–6ms target rather than just being assumed.

5. **Check the toolchain:** that the rMPP (ARMv8.0 / Codex Linux) can run a statically- or
   musl-linked Rust binary, that the `evdev` crate works there, and that the systemd unit
   approach is valid on this OS image.

6. **Flag any feature in Section 4 that is not actually achievable** with this architecture, and
   either propose a corrected approach or move it to non-goals. Do not silently skip infeasible
   items.

7. **Produce a written feasibility report** (e.g. `docs/feasibility.md`) summarizing what is
   confirmed possible, what is uncertain, what is blocked, and a revised build order — before
   starting Phase 1. If something here is impossible or significantly harder than stated, say so
   plainly rather than working around it quietly.

8. **Verify the AppLoad app assumptions.** Confirm that the AppLoad framework version available
   on the rMPP supports suspending xochitl and taking sole ownership of the EPD display. Confirm
   that an AppLoad app and the daemon can coexist as separate processes where the daemon holds the
   evdev fd exclusively and the AppLoad app communicates with it only via IPC (never by reading
   evdev itself — that would cause event loss). If AppLoad's xochitl-suspend mechanism has changed
   in recent rMPP firmware, flag it.

9. **Each component must be self-contained in its folder.** `daemon/` owns all rMPP daemon code
   and the systemd unit. `appload/` owns all on-device UI code. `otd-plugin/` owns all Windows
   OTD plugin code. `companion-ui/` owns all Windows companion UI code. `protocol/` owns the
   shared specs. No component should reach into another component's folder for source files.
   If shared types are needed between Rust components, use a shared crate inside `protocol/`
   or `daemon/` as appropriate, documented in `protocol/packet.md`.

The goal is a real, working, pressure-sensitive tablet at Wacom parity — not a plausible-looking
plan. If the plan needs to change to reach that goal, change it and explain why.
