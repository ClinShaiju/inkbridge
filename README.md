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
- **Build tools:** the [Rust toolchain](https://rustup.rs/) (`aarch64-unknown-linux-musl` target),
  the [.NET 8 SDK](https://dotnet.microsoft.com/download), and `paramiko`
  (`pip install paramiko`) for the deploy scripts.

## Running inkbridge

Full step-by-step setup is in **[INSTALL.md](INSTALL.md)**. In short:

1. **Daemon** — cross-compile the Rust daemon (static aarch64 musl) and install it on the tablet as
   a systemd service.
2. **Plugin** — build the C# OTD plugin and drop it into OpenTabletDriver with the tablet config +
   report parser.
3. **Pressure** — install OTD's *Windows Ink* plugin and the *VMulti* driver.
4. **Secrets** — copy [`.env.example`](.env.example) to `.env` and set your device's root SSH
   password (gitignored; never committed).
5. **Go** — run `start-inkbridge.cmd` and draw.

> Tip: set `INKBRIDGE_SYNTHETIC=1` to drive a built-in oscillating-pressure test source with no
> tablet attached — handy for verifying the Windows Ink / pressure path first.

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
- The first time you enable the plugin, **apply OTD settings twice** (see INSTALL.md /
  `otd-plugin/InkbridgeTool.cs` for why).
- Re-run the daemon's `install-service.sh` after a reMarkable software update — updates reset the
  persistent rootfs the unit lives on.

## Contributing

Issues and pull requests are welcome. This is an early prototype, so expect rough edges; if you're
adapting it to a different device or OTD version, the verified device facts in
[`docs/phase0-findings.md`](docs/phase0-findings.md) and the wire spec in
[`protocol/packet.md`](protocol/packet.md) are the best starting points.

## Licence

No licence has been chosen yet. Until one is added, all rights are reserved by the author.

---

<p align="center"><sub>Not affiliated with or endorsed by reMarkable AS or the OpenTabletDriver project.</sub></p>
