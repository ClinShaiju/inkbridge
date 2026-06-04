# inkbridge

**Use a reMarkable Paper Pro as a pressure-sensitive drawing tablet for Windows.**

inkbridge turns the rMPP's pen into a real graphics-tablet input device on a Windows PC —
position, **4096-level pressure**, hover, tilt, eraser, and pen buttons — by streaming the raw
digitizer over the USB cable into [OpenTabletDriver](https://opentabletdriver.net/) (OTD). You
get OTD's area mapping, pressure-curve editor, and button bindings for free, and pressure reaches
Windows Ink / WinTab apps like Krita, Clip Studio, and Photoshop.

No kernel driver, no driver signing. The rMPP keeps running normally while you draw.

> **Status:** working prototype. Core path (pen → pressure in Krita) is built and verified.
> See [PROJECT.md §0.1](PROJECT.md#01-implementation-status-as-built) for exactly what exists.
> This is an unofficial hobby project and is **not affiliated with reMarkable**. Use at your own
> risk; it requires root SSH access to your device.

---

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
- The **OTD plugin** registers a synthetic, network-sourced tablet inside OpenTabletDriver,
  decodes the packets, and feeds position/pressure/tilt/hover/buttons into OTD's pipeline. It also
  publishes link status + the active area back to the tablet.
- The optional **appload visualizer** draws the configured active-area box on the e-ink surface so
  you can see where the pen is "live", plus connection/latency/rate stats. It is read-only — OTD on
  the PC owns all configuration.

The wire format is specified in [`protocol/packet.md`](protocol/packet.md).

## Getting started

See **[INSTALL.md](INSTALL.md)** for the full setup: building and installing the daemon on the
rMPP, building and installing the OTD plugin, the **VMulti + Windows Ink** pieces required for
pressure on Windows, and running it.

Quick shape of it:

1. Cross-compile the Rust daemon (static aarch64 musl) and deploy it to the tablet as a systemd
   service.
2. Build the C# OTD plugin and drop it into OpenTabletDriver, with the tablet config + report
   parser.
3. Install OTD's **Windows Ink** plugin and the **VMulti** driver (pressure on Windows needs both).
4. Copy `.env.example` to `.env`, set your device's root SSH password.
5. Run `start-inkbridge.cmd` and draw.

## Repository layout

| Path | What it is |
|------|-----------|
| [`daemon/`](daemon/) | Rust daemon for the rMPP — evdev pen reader, TCP pen stream (:9292) + control plane (:9293), systemd unit, deploy script. |
| [`otd-plugin/`](otd-plugin/) | C# / .NET 8 OpenTabletDriver 0.6.7 plugin — synthetic tablet device, packet decoder, report parser, PC-side telemetry, tablet config. |
| [`appload/`](appload/) | On-device AppLoad app — QML frontend + Python backend; read-only active-area visualizer. |
| [`protocol/`](protocol/) | `packet.md` — authoritative PenPacket wire spec. |
| [`tools/`](tools/) | Diagnostic scripts (pen probe, rate/tilt/button checks, HID diag) used during bring-up. |
| [`docs/`](docs/) | `phase0-findings.md` (verified device facts), `feasibility.md` (architecture audit), `appload-research.md`, `appload-ui-spec.md`. |
| [`PROJECT.md`](PROJECT.md) | Original design plan + audit brief, with an "as-built" status section reconciling it with reality. |

## Configuration & secrets

Device connection settings live in a **gitignored `.env`** (copy from
[`.env.example`](.env.example)). The rMPP root SSH password is unique per device — find it on the
tablet under **Settings → Help → Copyrights and licenses** (bottom). Nothing secret is committed.

```env
INKBRIDGE_HOST=10.11.99.1
INKBRIDGE_USER=root
INKBRIDGE_PW=your-device-password
```

The OTD plugin and daemon also honor `INKBRIDGE_HOST` / `INKBRIDGE_PORT` env vars; set
`INKBRIDGE_SYNTHETIC=1` to drive a built-in oscillating-pressure test source with no tablet
attached (handy for verifying the Windows Ink / pressure path).

## Requirements

- **reMarkable Paper Pro** with developer/root SSH access and a USB connection (RNDIS, host reaches
  the tablet at `10.11.99.1`). Python 3 (Entware) on-device for the appload backend.
- **Windows PC** with [OpenTabletDriver **0.6.7**](https://opentabletdriver.net/), the VoiDPlugins
  *Windows Ink* plugin, and the X9VoiD *VMulti* driver (for pressure).
- **Build tools:** Rust (`aarch64-unknown-linux-musl` target) and the .NET 8 SDK. `paramiko` for
  the Python deploy scripts (`pip install paramiko`).

## Caveats

- Pressure on Windows is **not** built into OTD — it requires the Windows Ink plugin **and** the
  correct VMulti fork. See [`docs/feasibility.md` §2](docs/feasibility.md).
- The OTD plugin reflects over OTD internals and is pinned to **0.6.7**; other versions may need
  retargeting.
- After enabling the plugin you must **apply OTD settings twice** the first time (see INSTALL.md /
  `otd-plugin/InkbridgeTool.cs` for why).
- Re-run the daemon's `install-service.sh` after a reMarkable software update (it writes to the
  persistent rootfs, which updates reset).

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author.
