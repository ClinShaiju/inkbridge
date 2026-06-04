# inkbridge — Installation & Setup

End-to-end setup: build and install the on-device daemon, build and install the OpenTabletDriver
plugin, wire up Windows-side pressure, and run it. Read [README.md](README.md) first for the big
picture.

> Paths use `10.11.99.1` (the rMPP's USB address) and `/home/root/...` (the device's root home).
> Replace these only if your setup differs. Commands are shown for **PowerShell** on Windows and
> **sh** on the device.

---

## 0. Prerequisites

**On the Windows PC:**
- [OpenTabletDriver **0.6.7**](https://github.com/OpenTabletDriver/OpenTabletDriver/releases/tag/v0.6.7)
  (the plugin is pinned to this version).
- [.NET 8 SDK](https://dotnet.microsoft.com/download) — to build the plugin.
- [Rust toolchain](https://rustup.rs/) — to cross-compile the daemon.
- Python 3 with `paramiko` (`pip install paramiko`) — for the deploy scripts. PySide6 too if you
  want to (re)compile the appload QML bundle (`pip install PySide6`).

**On the reMarkable Paper Pro:**
- Developer mode / root SSH enabled, connected over USB (host reaches it at `10.11.99.1`).
- Entware with Python 3 at `/opt/bin/python3` (only needed for the optional appload visualizer).
- For the appload visualizer: XOVI + `rm-appload` installed (`vellum add xovi rm-appload`).

## 1. Clone and configure secrets

```powershell
git clone <your-fork-url> inkbridge
cd inkbridge
Copy-Item .env.example .env
```

Edit `.env` and set your device's **root SSH password** (unique per device — on the tablet:
**Settings → Help → Copyrights and licenses**, scroll to the bottom):

```env
INKBRIDGE_HOST=10.11.99.1
INKBRIDGE_USER=root
INKBRIDGE_PW=your-device-password
```

`.env` is gitignored — never commit it.

## 2. Build and install the daemon (rMPP)

### Build (cross-compile, static, no C toolchain needed)

```powershell
rustup target add aarch64-unknown-linux-musl
cd daemon
cargo build --release --target aarch64-unknown-linux-musl
cd ..
```

Output: `daemon/target/aarch64-unknown-linux-musl/release/inkbridge-daemon` (a fully static
aarch64 binary; `.cargo/config.toml` links it with Rust's bundled LLVM linker).

### First-time install as a systemd service

Copy the binary, the unit file, and the installer to the device, then run the installer (it
remounts the rootfs so the unit survives reboots — but **not** reMarkable OS updates):

```powershell
# from the repo root; enter the device password when prompted
$dev = "root@10.11.99.1"
ssh $dev "mkdir -p /home/root/inkbridge"
scp daemon/target/aarch64-unknown-linux-musl/release/inkbridge-daemon $dev`:/home/root/inkbridge/
scp daemon/inkbridge-daemon.service daemon/install-service.sh $dev`:/home/root/inkbridge/
ssh $dev "chmod +x /home/root/inkbridge/inkbridge-daemon /home/root/inkbridge/install-service.sh && sh /home/root/inkbridge/install-service.sh"
```

The installer enables and starts `inkbridge-daemon` and reports whether the unit landed on the
persistent rootfs. You should see it listening on `:9292` (pen) and `:9293` (control).

> **After a reMarkable software update**, re-run `install-service.sh` — updates reset `/etc`.

### Updating the daemon later

Once the service exists, push a new build with the deploy script (reads `.env`, uploads, restarts):

```powershell
python daemon/deploy.py
```

## 3. Build and install the OTD plugin (Windows)

### Build

```powershell
cd otd-plugin
dotnet build -c Release
cd ..
```

Output: `otd-plugin/bin/Release/Inkbridge.dll`.

### Install into OpenTabletDriver

Close OpenTabletDriver, then:

```powershell
# 1) the plugin assembly
$otd = "$env:LOCALAPPDATA\OpenTabletDriver"
New-Item -ItemType Directory -Force "$otd\Plugins\Inkbridge" | Out-Null
Copy-Item otd-plugin\bin\Release\Inkbridge.dll "$otd\Plugins\Inkbridge\"

# 2) the tablet descriptor (matches our synthetic VID/PID + report parser)
Copy-Item otd-plugin\tablet-spec.json "$otd\Configurations\inkbridge.json"
```

Start OpenTabletDriver again.

### Enable the plugin (and the "apply twice" gotcha)

1. In the OTD UX, enable the **inkbridge** tool (full type name `Inkbridge.InkbridgeTool`). With
   the console: `otd enabletools Inkbridge.InkbridgeTool`.
2. **Apply settings, then apply settings a second time.** The first apply registers the device but
   leaves it without an output mode; the second binds the output mode to it. This is by design —
   see the long comment in `otd-plugin/InkbridgeTool.cs`. After the second apply, OTD should show
   the **inkbridge rMPP** tablet and live input when the pen is in range.

> **Verify the plugin without a tablet:** set `INKBRIDGE_SYNTHETIC=1` in OTD's environment and the
> plugin drives a built-in oscillating-pressure source. Useful for confirming the Windows Ink /
> pressure path before involving the device.

## 4. Enable pressure on Windows (VMulti + Windows Ink)

Stock OpenTabletDriver does **not** deliver pen pressure on Windows. You need two external pieces
(details in [`docs/feasibility.md` §2](docs/feasibility.md)):

1. **VMulti driver** — install the X9VoiD fork from
   <https://github.com/X9VoiD/vmulti-bin/releases/latest>. If another vendor's VMulti (e.g. an
   XP-Pen "Pentablet HID") is present, it can shadow this one; let the X9VoiD installer replace it.
2. **Windows Ink plugin** — in OTD's **Plugin Manager**, install **VoiDPlugins / Windows Ink**.

Then, in OTD:
- Set the output mode to **Windows Ink Absolute Mode**.
- Bind the pen tip to the **Windows Ink** tip handler (`WindowsInkButtonHandler`, "Pen Tip") — not
  the stock "Tip" binding, or pressure won't register.

Test in Krita with a pressure-sensitive brush: line width should vary with pen pressure (with
`INKBRIDGE_SYNTHETIC=1` you'll get a wavy line whose width pulses, no tablet needed).

## 5. Configure the active area

Use OTD's **Output** tab to map the tablet surface (179 × 239 mm, raw `11180 × 15340`) to your
display region, set the pressure curve, and bind the pen buttons — all standard OTD. The inkbridge
plugin watches OTD's `settings.json` and pushes the active area to the device for the visualizer.

## 6. Deploy the on-device visualizer (optional)

A read-only e-ink overlay showing the active-area box + connection/latency/rate. Requires XOVI +
rm-appload on the device.

```powershell
python appload/deploy.py
```

This compiles the QML bundle, uploads the app to
`/home/root/xovi/exthome/appload/inkbridge/`, and restarts xochitl so AppLoad re-scans. Launch
**inkbridge** from the AppLoad menu on the tablet.

## 7. Run

```powershell
# point this at your OpenTabletDriver install if it isn't C:\OpenTabletDriver
$env:OTD_DIR = "C:\path\to\OpenTabletDriver"
.\start-inkbridge.cmd
```

`start-inkbridge.cmd` (re)starts the OTD daemon and applies `otd-plugin/mouse-mode-settings.json`.
Draw on the reMarkable — the Windows cursor tracks the pen. The tablet keeps working normally
(xochitl is never paused); you'll just see ink strokes on the e-ink as you draw — cosmetic and
harmless.

To stop: `.\stop-inkbridge.cmd`. The daemon notices the dropped connection, releases its wakelock,
and the device can sleep normally again.

---

## Troubleshooting

| Symptom | Likely cause / fix |
|---------|--------------------|
| Cursor doesn't move at all | Apply OTD settings **twice** (§3). Confirm the daemon is up: `ssh root@10.11.99.1 "systemctl status inkbridge-daemon"` and that `:9292`/`:9293` are listening. |
| Cursor moves but **nothing draws** | The pressure path. Install the **X9VoiD VMulti** fork + **Windows Ink** plugin, select Windows Ink Absolute Mode, and bind the Windows Ink **Pen Tip** handler (§4). |
| No pen events after a while | The device slept and powered down the digitizer. The daemon holds a wakelock only while a client is connected — make sure OTD (the plugin) is connected. |
| Daemon gone after a reboot/update | Re-run `install-service.sh` on the device (§2). reMarkable OS updates reset `/etc`. |
| "More than 1 matching device" in OTD log | A stale/duplicate plugin load. Restart OTD; ensure only one `Inkbridge.dll` is installed. |
| `INKBRIDGE_PW not set` from a deploy script | Create `.env` from `.env.example` and set the password (§1). |
| Visualizer box looks wrong / stale | It mirrors OTD's `settings.json`; Save in the OTD GUI to push the current area. It seeds from `appload/area.json` until the first live push. |

See [`docs/`](docs/) for device facts, the architecture audit, and the AppLoad design notes.
