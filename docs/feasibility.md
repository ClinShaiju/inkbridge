# inkbridge — Feasibility Audit

Audit performed before any implementation code, per `project.md` §9. Sources: live
discovery on the rMPP (see `phase0-findings.md`) and a read of the current
OpenTabletDriver `master` source on GitHub. Date: 2026-06-03.

**Bottom line:** the project is feasible and the architecture is sound, with one
correction to the framing of the OTD plugin path and two risks that can only be retired by
runtime testing on the Windows host. No part of the plan is impossible. Build order below is
revised to validate the single biggest risk (Windows Ink pressure on AtlasOS) *before*
investing in the Rust daemon.

---

## 1. OTD plugin: can it accept a network-sourced device? — **YES, with a caveat**

This was flagged as "the single biggest architectural risk." Verdict: **the assumption is
essentially correct, but it is NOT "for free."** OTD has no documented "plug in a virtual
tablet" extension point; we ride internal-but-`[PublicAPI]` surfaces. Confirmed from source:

- Tablet data flows from an `IDeviceHub` → `IDeviceEndpoint` → `IDeviceEndpointStream.Read()`
  returning `byte[]` HID reports (`OpenTabletDriver/Devices/`).
- The built-in `DeviceHubsProvider` only reflects over **its own assembly**
  (`Assembly.GetExecutingAssembly().DefinedTypes` filtered by `[DeviceHub]`). So a plugin's
  `IDeviceHub` is **not auto-discovered** — this is the caveat that makes it not "free."
- **However**, `RootHub : ICompositeDeviceHub` exposes a public
  `ConnectDeviceHub(IDeviceHub instance)`, and `ICompositeDeviceHub` is registered as a DI
  **singleton** (`Singleton<ICompositeDeviceHub, RootHub>` in `DesktopServiceCollection`).
- Plugins are constructed with `_serviceProvider.CreateInstance(type, args)`
  (`PluginFactory`), i.e. **constructor DI injection works for plugins**.
- `ITool` is a resident plugin: `Initialize()` is called once and it stays alive.

### Resulting design (validated mechanism)

```
otd-plugin/  (one OTD plugin assembly)
├── InkbridgeTool : ITool                 // ctor injects ICompositeDeviceHub
│     Initialize() => hub.ConnectDeviceHub(new InkbridgeHub(transport))
├── InkbridgeHub : IDeviceHub             // GetDevices() => [ InkbridgeEndpoint ]
├── InkbridgeEndpoint : IDeviceEndpoint   // fixed VID/PID, InputReportLength
│     Open() => InkbridgeStream
├── InkbridgeStream : IDeviceEndpointStream
│     Read() => blocks on TCP socket, returns one synthetic HID report
├── InkbridgeReportParser : IReportParser<IDeviceReport>
│     parse bytes => ITabletReport (X,Y,Pressure) + tilt + proximity + buttons
└── tablet-spec.json                      // TabletConfiguration matching our VID/PID
```

Two ways to get reports parsed; we choose the explicit one:
- **(chosen)** ship our own `IReportParser` and a `TabletConfiguration` whose
  `DeviceIdentifier` matches our synthetic endpoint's VID/PID + report length. We fully
  control the byte layout, so the parser is trivial and decoupled from any real tablet.
- (rejected) shape bytes to mimic an existing tablet so a built-in parser handles them —
  brittle, couples us to another device's quirks.

### VALIDATED on OTD 0.6.7 (live, with a working plugin)

The mechanism was built and confirmed end-to-end against the installed 0.6.7. Key specifics
that differ from a naive reading of the source:

- **Plugins cannot inject `ICompositeDeviceHub` directly.** OTD only registers `IDriver` and
  `IDriverDaemon` into the service registry used for plugin `[Resolved]` injection
  (`DriverDaemon` does `PluginManager.AddService<IDriver>` / `<IDriverDaemon>` and nothing
  else). So inject **`[Resolved] IDriver`** and reach the hub via the concrete `Driver`'s
  public `ICompositeDeviceHub CompositeDeviceHub` property **by reflection** (avoids a
  compile-time dependency on OTD core). This is what `otd-plugin/InkbridgeTool.cs` does.
- **The tool must be enabled in settings to initialize.** A fresh daemon does **not**
  auto-initialize enabled tools at startup; tool `Initialize()` only runs when settings are
  *applied* (`enabletools <FullTypeName>` via console, or the UX). Use the **full type name**
  (`Inkbridge.InkbridgeTool`), not the friendly name.
- **Custom report parser resolves from the plugin** by full type name
  (`DesktopReportParserProvider` → `PluginManager.ConstructObject`). The live daemon logged
  `Using report parser type 'Inkbridge.InkbridgeReportParser'` — confirmed.
- A custom tablet config dropped in `…\OpenTabletDriver\Configurations\*.json` is loaded and
  matched by VID/PID + InputReportLength (`DesktopDeviceConfigurationProvider`).

Confirmed log evidence: `Registered inkbridge device hub with OTD` →
`Opening SYNTHETIC packet source` → `Using report parser type 'Inkbridge.InkbridgeReportParser'`,
no exceptions, and OTD produced a mapped Windows pointer output. **Position injection works.**

### Residual risk on this path
- Reflection on `Driver.CompositeDeviceHub` and the `[Resolved] IDriver` contract are
  version-coupled. **Mitigation:** pin OTD 0.6.7; the plugin is small and re-targetable.

**Conclusion:** the OTD input route is proven. Fallbacks (direct VMulti as input, custom
signed HID) are NOT needed for input. The remaining gap is *output* pressure (§2).

---

## 2. Windows pressure path — **OTD has NO built-in pressure on Windows**

Target is general Windows (not AtlasOS), so Tablet/Ink OS components can be assumed present.
That removes the AtlasOS-stripping concern. **But** a concrete, verified finding replaces it:

**Stock OpenTabletDriver 0.6.7 does not deliver pen pressure on Windows at all.** Confirmed
from source + a live test on the installed 0.6.7:
- The only built-in output modes are `AbsoluteMode` and `RelativeMode`
  (`listoutputmodes` shows exactly these two).
- On Windows, `AbsoluteMode`'s pointer is `WindowsAbsolutePointer : WindowsVirtualMouse` —
  it injects `SendInput` **mouse** moves only. It does **not** implement `IPressureHandler`.
  So position works, pressure does not.

Pressure on Windows requires **two external pieces** (OTD cannot bundle them):
1. The **VoiDPlugins "Windows Ink"** plugin (X9VoiD) — adds a *Windows Ink Absolute Mode*
   output that implements pressure. Installed via OTD's Plugin Manager. **Installed & working.**
2. The **VMulti** virtual-HID system driver — the Windows Ink plugin injects through it.
   **This must be the specific X9VoiD `vmulti-bin` fork** (HID `VID 0x00FF / PID 0xBACC`),
   *not* another vendor's VMulti fork.

This is a real dependency the original plan under-specified (it mentioned VMulti only in
passing). It does **not** require us to write or sign a driver — VMulti is pre-existing — but
the **correct** fork must be installed on every host.

### Live test result (synthetic source, host-only) — partial

With the WindowsInk plugin installed and our synthetic pen running, the result was
**cursor moves but nothing draws.** Diagnosis from source + PnP enumeration:
- The WinInk pen-down/pressure is gated by a `TIP_PRESSED` shared-store flag. It is set only
  by the plugin's own binding `VoiDPlugins.OutputMode.WindowsInkButtonHandler` with
  `Button = "Pen Tip"` — **not** the stock `AdaptiveBinding "Tip"`. Fixed the test settings to
  use the WinInk tip binding (confirmed in log: `Tip Binding: [...WindowsInkButtonHandler]@0%`).
- Still no draw, because the WinInk pointer logged `Pointer reference not available, returning
  dummy` → its writable VMulti handle is null. The OS cursor still moves via the plugin's
  "Sync OS cursor" feature (`SetCursorPos`), masking the failure.
- **Why null:** this host has **no `VID_00FF/PID_BACC` device**. Instead an XP-Pen
  **"Pentablet HID"** driver (`pentablet\hid`, `vmulti.inf` "Pentablet HID 1.1", 2023) is
  installed — a *different* VMulti fork. The WinInk plugin's `VMultiInstance.Retrieve()`
  matches the digitizer descriptor (→ "Using extended VMulti digitizer") but cannot open the
  65/65-byte control endpoint the X9VoiD fork exposes → dummy pointer → no injection.

**Resolution:** install the correct VMulti from
`https://github.com/X9VoiD/vmulti-bin/releases/latest` (the exact link the plugin error
prints). The XP-Pen "Pentablet HID" driver likely shadows it (shared VID/PID); may need to
uninstall XP-Pen's VMulti / let the X9VoiD installer replace it. Then re-run the test — the
synthetic source already oscillates pressure, so success = a wavy line whose width pulses.

**Architecture verdict unchanged:** this is a host driver-variant issue, not a flaw in the
inkbridge design. Position injection + WinInk output mode + our parser all work; only the
underlying VMulti binary is the wrong fork.

Source: OTD Windows FAQ ("no built-in pressure on Windows … install VMulti + Windows Ink"),
VoiDPlugins WindowsInk wiki, VoiDPlugins `VMultiInstance.Retrieve` (VID 255 / PID 47820).

---

## 3. Toolchain (Rust on rMPP) — **OK**

- Target is **aarch64**, libc is **glibc** → build `aarch64-unknown-linux-gnu` (dynamic) or
  `aarch64-unknown-linux-musl` (fully static, simplest deploy — no libc version coupling).
  Recommend **musl static** to be immune to firmware glibc changes.
- The `evdev` crate reads standard Linux evdev nodes; `event2` is a normal evdev device.
  Works.
- **systemd** is the init system → the `inkbridge-daemon.service` unit approach is valid.
- Cross-compile from the Windows host with `cross` (Docker) or a Linux builder; the rMPP
  itself need not have a Rust toolchain.

No blockers.

---

## 4. Transport / latency — **reachability OK, latency TBD**

- Non-SSH TCP over RNDIS confirmed working (`INKBRIDGE_TCP_OK` round-trip).
- `TCP_NODELAY` + no batching is the right call. The 3–6 ms target is plausible over USB
  RNDIS but **unproven** until measured end-to-end (Phase 6). RNDIS adds some overhead vs
  raw USB; if we miss the target, fallbacks are a leaner framing or a USB gadget serial /
  raw bulk endpoint. Not a launch blocker for art use; matters for osu!.

---

## 5. Packet format — corrected from discovery

The `project.md` draft struct is close. Corrections from real axis ranges:

```
struct PenPacket {           // little-endian, fixed 16 bytes
  u32 timestamp_us;          // monotonic from daemon
  u16 x;                     // 0..11180  (raw ABS_X)
  u16 y;                     // 0..15340  (raw ABS_Y)
  u16 pressure;              // 0..4096   (NOT 4095)
  u16 distance;              // 0..65535  (ABS_DISTANCE / hover) — added
  i16 tilt_x;                // -9000..9000 centidegrees
  i16 tilt_y;                // -9000..9000
  u8  buttons;               // bit0 touch, bit1 stylus1, bit2 stylus2,
                             // bit3 tool_pen(near), bit4 tool_rubber(eraser)
  u8  flags;                 // bit0 = packet has tilt valid, etc. + version nibble
}
```

`distance` was added (hover is richer than a single bit). Ranges/types now match the device.
The authoritative spec lives in `protocol/packet.md`.

---

## 6. AppLoad on-device UI (Phase 5) — **UNVERIFIED, deferred**

- The device runs systemd + xochitl; AppLoad is a third-party launcher framework. It was
  **not** found installed in this Phase 0 sweep (no `appload` in PATH; `/opt` shows custom
  tinkering incl. `mangaink` but no AppLoad confirmed).
- Whether the current firmware's xochitl can be cleanly suspended by AppLoad, and whether
  AppLoad's version still works on kernel 6.12 / this image, is unverified.
- This is the lowest-priority phase and does **not** block the core tablet. Recommendation:
  keep the on-device UI out of the MVP. The daemon can read config from a JSON file and
  reload via inotify; a host-side UI (or just the OTD GUI) covers configuration. Revisit
  AppLoad only after Phases 1–4 work.
- **Hard requirement retained:** only the daemon ever opens the evdev fd. Any on-device UI
  talks to the daemon over IPC and never reads `event2` itself.

---

## 7. Feature reality check (vs `project.md` §4)

| Feature | Verdict |
|---|---|
| Pen position, pressure, hover, buttons over TCP | ✅ device-side confirmed |
| Tilt | ✅ exposed (was "TBD") — promote from stretch to supported |
| Eraser (BTN_TOOL_RUBBER) | ✅ available — add to scope |
| OTD ingests our network device | ✅ via resident-tool + custom-hub plugin |
| Windows Ink pressure to art apps | ⚠️ must runtime-verify on AtlasOS (Step A) |
| osu! raw cursor | ✅ low risk (absolute pointer path doesn't need Ink) |
| Active-area / pressure-curve / button bind GUI | ✅ free from OTD once device is ingested |
| Auto-detect / reconnect | ✅ standard; hub `DevicesChanged` supports hot-attach |
| 3–6 ms latency | ⚠️ plausible, unproven — measure in Phase 6 |
| AppLoad on-device UI | ⚠️ deferred, not MVP, framework unverified |

Nothing needs to move to non-goals outright. Tilt + eraser move **up** into scope.

---

## 8. Revised build order

Reordered so the highest-uncertainty, cheapest-to-test items gate the expensive work.

- **Step A (host-only) — partially DONE:** OTD 0.6.7 installed. Built the inkbridge plugin +
  config and confirmed live that OTD detects our synthetic tablet, uses our report parser,
  and produces Windows pointer output (position injection proven). **Remaining:** install the
  VoiDPlugins **Windows Ink** plugin + **VMulti** driver, select *Windows Ink Absolute Mode*,
  and confirm pressure-varying strokes in Krita (the synthetic source oscillates pressure, so
  no rMPP needed for this).
- **Step B:** Capture digitizer report rate + confirm physical mm (5-second pen-move
  capture on-device). Lock the `protocol/packet.md` spec.
- **Step C (Phase 2 first half):** Build the OTD plugin skeleton (`InkbridgeTool` +
  `InkbridgeHub` + endpoint/stream + parser + `tablet-spec.json`) and feed it **canned**
  packets (a local replay file). Confirm OTD detects the synthetic tablet and shows live
  input + pressure in its GUI — *without the rMPP in the loop yet*.
- **Step D (Phase 1):** Write the Rust daemon (evdev → PenPacket → TCP). Cross-compile
  musl-static, deploy, systemd unit.
- **Step E:** Connect daemon ↔ plugin over RNDIS. End-to-end pressure in Krita; osu! test.
- **Step F (Phase 3–4):** Area mapping, pressure curve, button binds (OTD GUI), auto-detect
  / reconnect, config persistence.
- **Step G (Phase 6):** Measure latency; tune `TCP_NODELAY`/framing; stress under GPU load.
- **Step H (optional, Phase 5):** AppLoad on-device UI — only if still desired after the
  above, and only after re-verifying the framework on current firmware.

---

## 9. Open items to resolve with the user

1. **Run the Windows Ink pressure test (Step A)** — needs OTD + Krita installed on the host.
   This is the gating risk; worth doing before anything else.
2. **5-second pen-move capture** for report-rate + to sanity-check axes (I drive the capture;
   you move the pen).
3. Confirm physical active-area dimensions (tape measure, or accept 157×210 mm provisionally).
4. Confirm intent on AppLoad: keep it as a later optional phase (recommended) vs MVP.
