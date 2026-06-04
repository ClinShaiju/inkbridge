<p align="center">
  <img src="logo.png" alt="inkbridge" width="220">
</p>

<h1 align="center">inkbridge</h1>

<p align="center">
  Turn a <b>reMarkable Paper Pro</b> into a pressure-sensitive drawing tablet for Windows.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows-0078D6" alt="platform">
  <img src="https://img.shields.io/badge/device-reMarkable%20Paper%20Pro-1a1a1a" alt="device">
  <img src="https://img.shields.io/badge/daemon-Rust-CE412B" alt="rust">
  <img src="https://img.shields.io/badge/plugin-C%23%20.NET%208-512BD4" alt="dotnet">
  <img src="https://img.shields.io/badge/OpenTabletDriver-0.6.7-2ea44f" alt="otd">
  <img src="https://img.shields.io/badge/status-prototype-yellow" alt="status">
</p>

---

inkbridge streams the rMPP's pen digitizer over the USB cable into
[OpenTabletDriver](https://opentabletdriver.net/) (OTD), so the tablet acts as a real graphics
tablet on a Windows PC — position, **4096-level pressure**, hover, tilt, eraser, and pen buttons.
You get OTD's area mapping, pressure-curve editor, and button bindings for free, and pressure
reaches Windows Ink / WinTab apps like Krita, Clip Studio, and Photoshop.

No kernel driver, no driver signing, no e-ink hacking. The reMarkable keeps running normally while
you draw on it.

## Status

This is a **working prototype** and an unofficial hobby project — it is **not affiliated with
reMarkable**. The core path (pen → pressure-sensitive strokes in Krita) is built and verified. It
requires root SSH access to your device; use at your own risk.

See [`PROJECT.md`](PROJECT.md#01-implementation-status-as-built) for a precise breakdown of what
exists today versus the original design plan.

## How it works

```
reMarkable Paper Pro (USB)                         Windows PC
┌───────────────────────────┐                ┌──────────────────────────────┐
│ inkbridge-daemon (Rust)    │   TCP :9292    │ inkbridge OTD plugin (C#)     │
│  reads /dev/input/event2   │ ─────packets──▶│  decodes PenPacket, feeds OTD │
│  (pen), holds a wakelock   │                │            │                  │
│  18-byte PenPacket / report│   TCP :9293    │            ▼                  │
│                            │ ◀──status/area─│  OpenTabletDriver pipeline    │
│ appload visualizer (QML)   │   (control)    │   area map · pressure curve   │
│  read-only on-device box   │                │   · Windows Ink (VMulti)      │
└───────────────────────────┘                │            ▼                  │
                                              │   Krita / Clip Studio / …     │
                                              └──────────────────────────────┘
```

- The **daemon** on the tablet reads the Elan pen digitizer (~480–500 Hz) and streams a compact
  18-byte binary packet per report over raw TCP on the USB network link. It reads the pen
  *alongside* a running `xochitl` (it never pauses it) and holds a kernel wakelock so the device
  doesn't sleep and power down the digitizer.
- The **OTD plugin** registers a synthetic, network-sourced tablet inside OpenTabletDriver, decodes
  the packets, and feeds position/pressure/tilt/hover/buttons into OTD's pipeline. It also publishes
  link status + the active area back to the tablet.
- The optional **appload visualizer** draws the configured active-area box on the e-ink surface so
  you can see where the pen is "live", plus connection/latency/rate stats. It is read-only — OTD on
  the PC owns all configuration.

The wire format is specified in [`protocol/packet.md`](protocol/packet.md).

## Requirements

- A **reMarkable Paper Pro** with developer/root SSH access, connected over USB (RNDIS; host reaches
  the tablet at `10.11.99.1`). Python 3 (Entware) on-device for the optional visualizer.
- A **Windows PC** with [OpenTabletDriver **0.6.7**](https://opentabletdriver.net/), the VoiDPlugins
  *Windows Ink* plugin, and the X9VoiD *VMulti* driver (the last two are what make pressure work on
  Windows — OTD doesn't bundle them).

## Getting started

Using the prebuilt binaries from the [latest release](../../releases/latest). To build them
yourself instead, see [Building from source](#building-from-source).

You'll need two prebuilt files from the release — `inkbridge-daemon` (the tablet binary) and
`Inkbridge.dll` (the OTD plugin) — plus this repo for the scripts and config files.

**1. Clone the repo and set your device password**

```powershell
git clone https://github.com/ClinShaiju/inkbridge.git
cd inkbridge
Copy-Item .env.example .env
# edit .env and set INKBRIDGE_PW to your device's root SSH password
# (on the tablet: Settings > Help > Copyrights and licenses, at the bottom)
```

**2. Install the daemon on the tablet** (place the downloaded `inkbridge-daemon` in the repo root first)

```powershell
$dev = "root@10.11.99.1"
ssh $dev "mkdir -p /home/root/inkbridge"
scp inkbridge-daemon daemon/inkbridge-daemon.service daemon/install-service.sh ${dev}:/home/root/inkbridge/
ssh $dev "chmod +x /home/root/inkbridge/inkbridge-daemon /home/root/inkbridge/install-service.sh && sh /home/root/inkbridge/install-service.sh"
```

This installs `inkbridge-daemon` as a systemd service listening on `:9292` (pen) and `:9293`
(control). Re-run `install-service.sh` after a reMarkable software update — updates reset the
rootfs the unit lives on.

**3. Install the OTD plugin** (place the downloaded `Inkbridge.dll` in the repo root first; close OTD)

```powershell
$otd = "$env:LOCALAPPDATA\OpenTabletDriver"
New-Item -ItemType Directory -Force "$otd\Plugins\Inkbridge" | Out-Null
Copy-Item Inkbridge.dll "$otd\Plugins\Inkbridge\"
Copy-Item otd-plugin\tablet-spec.json "$otd\Configurations\inkbridge.json"
```

**4. Install the Windows pressure pieces** (OTD has no built-in pressure on Windows)

- **VMulti driver** — install the X9VoiD fork from
  <https://github.com/X9VoiD/vmulti-bin/releases/latest>. If another vendor's VMulti is present
  (e.g. an XP-Pen "Pentablet HID"), let the X9VoiD installer replace it.
- **Windows Ink plugin** — in OTD's **Plugin Manager**, install **VoiDPlugins / Windows Ink**.

**5. Enable the plugin and turn on pressure**

1. Start OpenTabletDriver and enable the **inkbridge** tool (`Inkbridge.InkbridgeTool`).
2. **Apply settings, then apply settings a second time.** The first apply registers the device;
   the second binds the output mode to it (by design — see `otd-plugin/InkbridgeTool.cs`). After
   the second apply, OTD should show the **inkbridge rMPP** tablet with live input.
3. Set the output mode to **Windows Ink Absolute Mode** and bind the pen tip to the **Windows Ink**
   tip handler ("Pen Tip"), not the stock "Tip" — otherwise pressure won't register.

**6. Run it**

```powershell
$env:OTD_DIR = "C:\path\to\OpenTabletDriver"   # if not C:\OpenTabletDriver
.\start-inkbridge.cmd
```

Draw on the reMarkable — the Windows cursor tracks the pen with pressure. To stop:
`.\stop-inkbridge.cmd`.

> No tablet handy? Set `INKBRIDGE_SYNTHETIC=1` in OTD's environment and the plugin drives a
> built-in oscillating-pressure source — a quick way to confirm the Windows Ink / pressure path.

**Optional — the on-device visualizer:** with XOVI + `rm-appload` installed on the tablet, run
`python appload/deploy.py` (needs `pip install paramiko PySide6`) to deploy the read-only
active-area overlay, then launch **inkbridge** from the AppLoad menu.

## Building from source

Prerequisites: the [Rust toolchain](https://rustup.rs/), the
[.NET 8 SDK](https://dotnet.microsoft.com/download), and `paramiko` (`pip install paramiko`) for the
deploy scripts.

**Daemon (rMPP, cross-compiled static aarch64 — no C toolchain needed):**

```powershell
rustup target add aarch64-unknown-linux-musl
cargo build --release --target aarch64-unknown-linux-musl --manifest-path daemon/Cargo.toml
# output: daemon/target/aarch64-unknown-linux-musl/release/inkbridge-daemon
```

Install it as in [Getting started](#getting-started) step 2 (the binary is at the path above), or —
once the service already exists — push updates with `python daemon/deploy.py` (reads `.env`,
uploads, restarts the service).

**OTD plugin (Windows):**

```powershell
dotnet build otd-plugin -c Release
# output: otd-plugin/bin/Release/Inkbridge.dll
```

Install it as in [Getting started](#getting-started) step 3.

**On-device visualizer:** `python appload/deploy.py` compiles the QML bundle and deploys the app.

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| Cursor doesn't move at all | Apply OTD settings **twice**. Confirm the daemon is up: `ssh root@10.11.99.1 "systemctl status inkbridge-daemon"` and that `:9292`/`:9293` are listening. |
| Cursor moves but **nothing draws** | The pressure path. Install the X9VoiD **VMulti** fork + **Windows Ink** plugin, select Windows Ink Absolute Mode, and bind the Windows Ink **Pen Tip** handler. |
| No pen events after a while | The device slept and powered down the digitizer. The daemon holds a wakelock only while a client (the plugin) is connected — make sure OTD is connected. |
| Daemon gone after a reboot/update | Re-run `install-service.sh` on the device. reMarkable OS updates reset `/etc`. |
| "More than 1 matching device" in OTD log | A stale/duplicate plugin load. Restart OTD; ensure only one `Inkbridge.dll` is installed. |
| `INKBRIDGE_PW not set` from a deploy script | Create `.env` from `.env.example` and set the password. |
| Visualizer box looks wrong / stale | It mirrors OTD's `settings.json`; Save in the OTD GUI to push the current area. It seeds from `appload/area.json` until the first live push. |

## Repository structure

| Path | What it is |
|------|-----------|
| [`daemon/`](daemon/) | Rust daemon for the rMPP — evdev pen reader, TCP pen stream (:9292) + control plane (:9293), systemd unit, deploy script. |
| [`otd-plugin/`](otd-plugin/) | C# / .NET 8 OpenTabletDriver 0.6.7 plugin — synthetic tablet device, packet decoder, report parser, PC-side telemetry, tablet config. |
| [`appload/`](appload/) | On-device AppLoad app — QML frontend + Python backend; read-only active-area visualizer. |
| [`protocol/`](protocol/) | `packet.md` — authoritative PenPacket wire spec. |
| [`tools/`](tools/) | Diagnostic scripts (pen probe, rate/tilt/button checks, HID diag) from bring-up. |
| [`docs/`](docs/) | Verified device facts, architecture audit, and AppLoad design notes. |
| [`PROJECT.md`](PROJECT.md) | Original design plan + audit brief, with an "as-built" status section. |

## Configuration & secrets

Device connection settings live in a **gitignored `.env`** (copy from
[`.env.example`](.env.example)). The rMPP root SSH password is unique per device — find it on the
tablet under **Settings → Help → Copyrights and licenses** (bottom). Nothing secret is committed.

```env
INKBRIDGE_HOST=10.11.99.1
INKBRIDGE_USER=root
INKBRIDGE_PW=your-device-password
```

The OTD plugin and daemon also honor `INKBRIDGE_HOST` / `INKBRIDGE_PORT`; `INKBRIDGE_SYNTHETIC=1`
enables the host-only pressure test source.

## Caveats

- Pressure on Windows requires the **Windows Ink** plugin **and** the correct **VMulti** fork — see
  [`docs/feasibility.md` §2](docs/feasibility.md).
- The OTD plugin reflects over OTD internals and is pinned to **0.6.7**; other versions may need
  retargeting.

## Contributing

Issues and pull requests are welcome. This is an early prototype, so expect rough edges; if you're
adapting it to a different device or OTD version, the verified device facts in
[`docs/phase0-findings.md`](docs/phase0-findings.md) and the wire spec in
[`protocol/packet.md`](protocol/packet.md) are the best starting points.

## Licence

No licence has been chosen yet. Until one is added, all rights are reserved by the author.

---

<p align="center"><sub>Not affiliated with or endorsed by reMarkable AS or the OpenTabletDriver project.</sub></p>
