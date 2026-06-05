# Wi-Fi Connectivity — Feasibility (rMPP ↔ Windows, cableless)

Goal: run the inkbridge pen link **over Wi-Fi instead of the USB cable** — the PC should
**discover the reMarkable Paper Pro on the local network and connect to it automatically**, with
the whole thing **managed from inside OpenTabletDriver / the plugin, no separate tray app or
external tooling** (or, failing that, the discovery logic folded into the existing plugin/daemon).

**Verdict up front:** Cableless operation is **already working at the transport layer** — the
daemon binds `0.0.0.0` and the device is reachable on `wlan0` *today*; pointing the plugin at the
Wi-Fi IP is the entire change to "make it work once." The real feature is **zero-config**: finding
the device when its IP is unknown/changing, and doing it without asking the user to type an IP.
That is **feasible and self-contained** via **mDNS** — the device side needs essentially no new
software (its `systemd-resolved` already maps the name to both IPs; one networkd drop-in turns on
the responder, or the daemon embeds a tiny mDNS service advertiser), and the plugin side resolves
that name in `TcpSource`. The one genuine constraint is that **OpenTabletDriver has no settings UI
at the device-hub layer where we connect**, so a "type the IP in OTD" box is awkward — which is
exactly why **auto-discovery (no UI at all) is the right design**, with an env-var / config-file
override for power users. All device facts below were read **live over SSH on 2026-06-04**.

---

## 1. What the connection actually is today (verified)

It is already TCP/IP — the "USB cable" is just a **USB-Ethernet gadget**, not a special transport:

- The daemon binds **`0.0.0.0:9292`** (`daemon/src/main.rs:56`), i.e. **every interface**, not just
  USB. Control plane `:9293` is the same (`daemon/src/control.rs`).
- The plugin's `TcpSource` connects to a host from **`INKBRIDGE_HOST` env var, default
  `10.11.99.1`** (`otd-plugin/InkbridgeDevice.cs:179`). `10.11.99.1` is the **USB gadget IP**, so
  the default path is "over the cable" — but nothing about it is USB-specific.
- The installer/launcher hardcode the same `10.11.99.1` for SSH deploy and the user-facing message
  (`install.cmd:21`, `start-inkbridge.cmd:40`).

**Implication:** Wi-Fi is not a new transport to build. The link is interface-agnostic already.
What's missing is (a) knowing the device's Wi-Fi IP and (b) telling the plugin to use it.

### Live proof it already works over Wi-Fi

```
usb0   10.11.99.1/27        ← current default the plugin uses
wlan0  192.168.0.189/24     ← device is ON Wi-Fi right now, DHCP address
default via 192.168.0.1 dev wlan0
daemon running (pid 741), bound 0.0.0.0:9292
```

Setting `INKBRIDGE_HOST=192.168.0.189` (or `loadsettings` with that host) connects over Wi-Fi with
**zero code changes**. The feature work is purely **discovery + configuration UX**, not transport.

---

## 2. Device side — Wi-Fi & name-resolution facts (verified)

reMarkable Paper Pro, **"Codex Linux" 5.5.125 (Yocto scarthgap)**, BusyBox userland
(no `ss`/`netstat`; `head` is busybox). Networking stack:

- **`systemd-networkd`** (pid 445) + **`wpa_supplicant`** (pid 658, config
  `/home/root/.config/remarkable/wifi_networks.conf`) manage `wlan0`. The Wi-Fi address is **DHCP**
  → it can change between sessions and across networks. This is the core reason a hardcoded IP is
  wrong for Wi-Fi.
- Hostname is **`imx8mm-ferrari`** (SoC codename; per-unit it's the same model string, **not
  unique or branded** — a discovery design must not assume a friendly/unique name).

### mDNS is 90 % already there

- **`systemd-resolved` is active** and already resolves the device's own name to **both** addresses:
  ```
  resolvectl query imx8mm-ferrari.local
    imx8mm-ferrari.local: 10.11.99.1     -- link: usb0
                          192.168.0.189  -- link: wlan0
  ```
- But the **mDNS *responder* is off on wlan0**:
  ```
  resolvectl mdns →  Global: yes | usb0: resolve | wlan0: no
  ```
  `resolve` answers queries locally but does **not** publish/respond on the wire; only **`yes`**
  enables the responder that answers incoming `*.local` queries from other hosts. So **from the PC,
  `imx8mm-ferrari.local` will not resolve over Wi-Fi until we flip wlan0 to `yes`.**
- Flipping it is a **one-line networkd drop-in** (no new package, persists across reboot):
  ```ini
  # /etc/systemd/network/30-wifi-mdns.network   (or a drop-in on the existing wlan0 .network)
  [Match]
  Name=wlan0
  [Network]
  MulticastDNS=yes
  ```
  (Runtime test without reboot: `resolvectl mdns wlan0 yes`.) The daemon's `install-service.sh`
  can write this drop-in, so enabling it is part of the existing one-shot install — **no external
  tooling**.
- **Avahi is present but inert.** `libavahi-*` ship in the OS; a toltec `/opt/sbin/avahi-daemon`
  (0.8) is installed but **inactive** and has **no systemd unit** wired. We *can* use it for full
  service advertisement, but depending on a toltec daemon adds a moving part. See §4 for why the
  daemon advertising its own service is cleaner.

**Net:** name **resolution** over Wi-Fi is one config line away. **Service discovery** (browse and
find the device without knowing its name) needs SRV/PTR records, which `systemd-resolved` does
**not** publish (it only answers the host A record) — that tier wants either avahi-with-a-service-
file or, better, the daemon embedding a small mDNS responder (§4).

---

## 3. The three real sub-problems

1. **Discovery** — the PC must learn the device's current Wi-Fi IP without the user reading it off
   the tablet. (DHCP ⇒ it drifts.)
2. **Configuration plumbing** — the plugin must *use* the discovered/entered address. Today that's an
   env var read in `InkbridgeEndpoint.Open()`; there is **no OTD UI at that layer** (§5).
3. **Robustness over Wi-Fi** — reconnect on IP change/roam, higher and jitterier latency than RNDIS,
   Wi-Fi power-save, and the device's autosleep before a client connects (§6).

---

## 4. Discovery options (compared)

| Option | How | Pros | Cons | Self-contained? |
|---|---|---|---|---|
| **mDNS name resolution** | Enable resolved responder on wlan0; plugin resolves `imx8mm-ferrari.local` | Trivial device side (1 config line); Windows 10/11 resolve `.local` natively | Name is per-SoC, not branded/unique; A-record only, no "browse"; user/installer must know the name | ✅ |
| **mDNS service discovery** *(recommended)* | Daemon advertises `_inkbridge._tcp.local` (host+port+IP); plugin **browses** for it | True zero-config; survives IP **and** hostname change; carries the port; multi-device selectable | Need an SRV/PTR responder (daemon-embedded crate, or avahi service file) + a browser in the plugin | ✅ (daemon-embedded) |
| **UDP broadcast beacon** | Daemon broadcasts `inkbridge@<ip>:<port>` every ~2 s on the subnet; plugin listens | No mDNS stack; dead simple; firewall-friendlier than multicast on some nets | Broadcast often dropped across APs/VLANs/guest isolation; chatty; our own protocol | ✅ |
| **Subnet scan** | Plugin probes `:9292` across the local /24 | No device change at all | Slow, noisy, looks like port-scanning to AV/IDS; fails on /16 or isolated clients | ✅ (PC only) |
| **Manual entry** | User types the Wi-Fi IP (env var / config file / OTD field) | Always works as a fallback | Defeats "cableless & automatic"; IP drifts with DHCP | ✅ |
| **USB-assisted handoff** | While plugged in (10.11.99.1), ask the daemon over `:9293` for its wlan0 IP, cache it | Reuses the trusted USB path to bootstrap Wi-Fi; exact, no multicast | Requires one initial cable connect; cached IP still drifts (needs re-handoff or mDNS) | ✅ |

**Recommendation:** **mDNS service discovery as primary**, with a **resolution fallback chain** so
it always connects:

```
INKBRIDGE_HOST env var  →  mDNS browse _inkbridge._tcp  →  imx8mm-ferrari.local
  →  USB default 10.11.99.1  →  (last-known cached IP)
```

Advertising the service **from the Rust daemon** (a small pure-Rust mDNS responder such as
`libmdns` / `mdns-sd`, advertising `_inkbridge._tcp` with port 9292 and a TXT record for
version/area) is the cleanest "no external tooling" answer: it ships in the binary we already
deploy, needs no avahi, no toltec, no networkd edit, and publishes the port and a stable instance
name we control (e.g. `inkbridge rMPP`) instead of the ugly SoC hostname. The plugin then does a
short mDNS query at `Open()` time. (If we'd rather not add a Rust mDNS dep, the `MulticastDNS=yes`
drop-in + resolving the host name is the low-effort 80 % path.)

---

## 5. Managing it through OpenTabletDriver — the architectural constraint

This is the part that needs care. **OTD's plugin settings UI does not reach the layer where we
open the connection.**

- We connect at the **device layer**: `InkbridgeHub : IDeviceHub` → `InkbridgeEndpoint :
  IDeviceEndpoint` → `Open()` constructs the `TcpSource` (`InkbridgeDevice.cs`). **`[Property]` /
  settings-bound UI does not exist for hubs/endpoints** — OTD shows configurable properties only on
  **output modes, filters, tools, and binding handlers**. So there is no native "Device IP" textbox
  for our hub. That's why the host is an **env var** today.
- The pipeline-level plugin we *do* own, `InkbridgeTool` (an `ITool`, the thing the user enables in
  OTD), **can** carry a `[Property("Device address")] string Host`. But there's a **lifecycle/order
  problem**: the hub enumerates the endpoint and `Open()` can run on device-detect **before/independent
  of** the tool's settings being applied — and this plugin already has a known finicky
  re-initialisation dance (the *Detect() orphan* fix: "apply settings twice"). Bolting connection
  config onto the tool means racing that lifecycle to push the value down to the hub via a shared
  static. Workable, but fragile, and it makes the connection depend on the tool being enabled.

**Conclusion:** Don't fight OTD for a connection-config UI. Make the connection **need no UI**:

1. **Primary:** auto-discovery in `TcpSource` (mDNS, §4) — the plugin Just Connects, cable or Wi-Fi,
   no field to set. This is the most "managed through OTD" because the user does nothing beyond
   enabling the tool they already enable.
2. **Override (power users), in priority order:** `INKBRIDGE_HOST` env var (works now) → a small
   **`inkbridge.json` next to the plugin DLL** (`%LOCALAPPDATA%\OpenTabletDriver\Plugins\Inkbridge\`)
   that `Open()` reads. A config file is honest about the constraint, edit-once, and survives plugin
   reloads — better than wrestling a value through the tool's settings lifecycle.
3. **If a true in-OTD field is wanted:** expose `[Property] Host` on `InkbridgeTool` purely as an
   override that writes the config file / a static, clearly documented as "leave blank for
   auto-discover." Treat it as nice-to-have, not the mechanism.

No tray app, no separate executable — discovery and override both live in the daemon + plugin we
already ship and install. (Contrast the **touch** path, which *does* need a sidecar because
`InjectTouchInput` can't live in OTD — see `docs/touch-feasibility.md`. Wi-Fi has no such forcing
function; it stays inside the plugin.)

---

## 6. Robustness over Wi-Fi (vs. the cable)

The pen path was tuned for RNDIS (point-to-point, ~no loss). Wi-Fi changes the assumptions:

- **Latency & jitter.** `TCP_NODELAY` is already set (`main.rs:115`); the 1 € hover filter already
  smooths hover. Wi-Fi adds variable RTT — the existing control-plane `ping`/`pong` (`control.rs`)
  already measures real round-trip and surfaces `latency_ms`/`rate_hz`, so we get a Wi-Fi health
  read for free. Expect a usable but measurably higher-latency feel; validate the ~150–500 Hz pen
  stream survives the link.
- **IP drift / roaming — handled by resolve-by-name, not scanning.** This is the key to "it just
  reconnects when the IP changes." mDNS is **not** a subnet sweep: the plugin sends one multicast
  query and the device answers with its *current* IP, so connecting to the **name** transparently
  follows DHCP changes. Requirements for it to be automatic: (a) **re-resolve on every
  connect/reconnect** — a cached IP is only a fast-path hint; on failure, fall back to a fresh mDNS
  query (never retry a dead IP as the primary path); (b) **same L2 subnet** — mDNS is link-local
  multicast and does not cross subnets/VLANs or **guest-Wi-Fi isolation** (those need the
  manual/USB fallback). Cases: *between sessions* (new IP since last time) → next connect resolves
  fresh, fully automatic; *mid-session* DHCP renew/roam → socket drops, `EnsureConnected` reconnects
  and re-resolves; *OTD idle with link already down* → add a slow retry-with-reresolve loop while
  disconnected so it self-heals without an OTD restart. A subnet scan of `:9292` is only the
  last-resort fallback when multicast is blocked.
- **Wi-Fi power-save.** `wlan0` PSM can add latency spikes / drops when idle. The daemon's
  pen-in-range ~60 Hz keepalive (`main.rs:199`) keeps the socket warm; we may additionally want to
  hint `iw wlan0 set power_save off` while a client is connected (mirrors the wakelock pattern —
  acquire on first client, restore on last). Verify whether reMarkable's stack honours it.
- **Autosleep before connect — out of scope (awake-only is the accepted model).** The wakelock is
  held **only while a client is connected** (`main.rs:83-103`); under `autosleep=mem` a device with
  no client suspends and answers nothing (not mDNS, not the TCP accept). We deliberately **do not**
  solve cold-sleep connect: using the rMPP as a tablet means turning it on (the screen must be on to
  send pen input anyway), so "wake it, then it connects" is the expected workflow, not a defect.
  (For the record, the hardware *could* do it — `iw phy` shows full **WoWLAN** support, driver
  `wlan_sdio`, `wlan0` wakeup `enabled` — magic packet / pattern match. It's parked as unnecessary
  complexity, not a dead end.)
- **Security.** USB was effectively point-to-point; Wi-Fi puts `:9292`/`:9293` on the **LAN**. The
  pen stream is unauthenticated. At minimum document the exposure; consider binding a token (the
  control plane already has a role-token handshake `IBCP`/`IBCS` we could extend), or a
  loopback/USB-only default with Wi-Fi as explicit opt-in.

---

## 7. Proposed architecture (recommended path)

```
rMPP (daemon)                                            Windows (OTD plugin, in-process)
─────────────                                            ────────────────────────────────
inkbridge-daemon
  ├─ TCP :9292 pen     (already 0.0.0.0 → wlan0 works)        InkbridgeEndpoint.Open()
  ├─ TCP :9293 control (already 0.0.0.0)                         │ resolve host:
  └─ mDNS responder  ──advertises──▶ _inkbridge._tcp.local ◀──── │  1 env INKBRIDGE_HOST
       instance "inkbridge rMPP", port 9292,                     │  2 mDNS browse _inkbridge._tcp
       TXT: ver=1, area=…                                        │  3 imx8mm-ferrari.local
  (wlan0 already DHCP'd; resolved active)                        │  4 USB 10.11.99.1
  + optional: power_save off while client connected             │  5 cached last-IP
                                                                 ▼
                                                            TcpSource(host, 9292)  ── pen stream ──▶ OTD
```

Everything new lives in **`daemon/`** (mDNS advertise + optional power-save toggle + the
`MulticastDNS=yes` drop-in in `install-service.sh`) and **`otd-plugin/`** (resolution chain in
`TcpSource`). No new process on either side.

---

## 8. Build order

1. **Prove Wi-Fi end to end (no code):** `INKBRIDGE_HOST=192.168.0.189` (current wlan0 IP) →
   `loadsettings` → draw. Confirms rate/latency over the real link before investing in discovery.
2. **Resolution chain in `TcpSource`** (plugin-only, no device change): env → `imx8mm-ferrari.local`
   → `10.11.99.1`. Enable the responder once (`resolvectl mdns wlan0 yes`) to test name resolution.
   This alone delivers "works over Wi-Fi if the name resolves."
3. **Persist the responder:** add the `MulticastDNS=yes` networkd drop-in to `install-service.sh`.
4. **Daemon mDNS service advertisement** (`_inkbridge._tcp`, pure-Rust crate) + **plugin browse**
   step ahead of the name lookup → true zero-config, IP/hostname-proof, multi-device-aware.
5. **Reconnect = re-resolve**, last-known-IP cache, and surface link health via existing
   `:9293` ping/pong.
6. **Power:** Wi-Fi `power_save off` while a client is connected (mirror the wakelock refcount);
   decide the sleeping-tablet-connect policy (§6).
7. **Override UX:** `inkbridge.json` next to the DLL (and optionally a blank-means-auto `[Property]`
   on `InkbridgeTool`); update installer copy that currently says "10.11.99.1".
8. **Security:** document LAN exposure; optional token on the pen/control handshake.

---

## 9. Risks & open items

1. **Connecting to a sleeping tablet over Wi-Fi** (§6) — explicitly **out of scope**: the user turns
   the tablet on to draw (screen on is required to send input anyway), so wake-then-connect is the
   accepted workflow. WoWLAN exists as a fallback if ever wanted, but isn't built.
2. **mDNS across real networks** — multicast is dropped by client/AP isolation, guest VLANs, and some
   mesh setups. Keep the env-var/config-file and USB fallbacks first-class; consider the unicast
   USB-handoff bootstrap (§4) for hostile networks.
3. **OTD lifecycle for any in-UI override** (§5) — if we expose a tool `[Property]`, validate it against
   the known "apply settings twice" Detect() race before relying on it; prefer the config file.
4. **Latency/jitter feel** at 150–500 Hz over Wi-Fi — validate; tune keepalive/filter; the link is
   shared with other Wi-Fi traffic unlike the dedicated cable.
5. **Windows `.local` resolution** — Windows 10 1809+ resolves mDNS natively, but corporate images /
   older builds / VPN split-DNS can interfere. The daemon-embedded responder + the plugin doing its
   own mDNS query (not relying solely on the OS resolver) is the robust choice.
6. **Security** — unauthenticated input stream now reachable by anything on the LAN. Decide opt-in vs
   default, and whether to add a token.
7. **Avahi vs. daemon-embedded vs. resolved-only** — three ways to publish; embedding in the daemon
   avoids depending on the inactive toltec avahi and the per-SoC hostname, at the cost of a Rust dep.

---

### Sources
- Live device probe (SSH `root@10.11.99.1`, 2026-06-04): `ip addr`/`ip route` (usb0 10.11.99.1,
  wlan0 192.168.0.189, default via wlan0); `/etc/os-release` (Codex Linux 5.5.125 scarthgap);
  `systemd-networkd` + `wpa_supplicant` (`wifi_networks.conf`); `resolvectl mdns`
  (Global yes / usb0 resolve / **wlan0 no**); `resolvectl query imx8mm-ferrari.local` → both IPs;
  `MulticastDNS=resolve` in `/etc/systemd/network/10-usb.network`; `systemd-resolved` active;
  toltec `/opt/sbin/avahi-daemon` present but **inactive**, no systemd unit; daemon pid 741 on
  `0.0.0.0:9292`; BusyBox 1.36.1 (no ss/netstat); `iw phy` WoWLAN support (magic packet + pattern
  match + disconnect/anything), Wi-Fi driver `wlan_sdio`, `/sys/class/net/wlan0/device/power/wakeup
  = enabled`, `/sys/power/autosleep = mem`.
- Existing pipeline: `daemon/src/main.rs` (`bind 0.0.0.0:9292`, wakelock refcount, keepalive,
  `TCP_NODELAY`), `daemon/src/control.rs` (`:9293` ping/pong/status, role-token handshake),
  `otd-plugin/InkbridgeDevice.cs` (`TcpSource`, `INKBRIDGE_HOST` default `10.11.99.1`, hub/endpoint
  layer with no settings UI), `otd-plugin/InkbridgeTool.cs` (the `ITool` the user enables;
  Detect()-orphan "apply twice" lifecycle), `install.cmd` / `start-inkbridge.cmd` (hardcoded
  `10.11.99.1`), `daemon/install-service.sh` (where a networkd drop-in would be written).
- Cross-refs: `docs/phase0-findings.md` (device/power/watchdog model), `docs/feasibility.md`
  (overall plan), `docs/touch-feasibility.md` (contrast: touch *needs* a sidecar; Wi-Fi does not).
- Mechanisms: `systemd-resolved` mDNS responder (`MulticastDNS=yes` per-link), DNS-SD
  `_inkbridge._tcp` service advertisement (pure-Rust `libmdns`/`mdns-sd`), Windows native `.local`
  mDNS resolution (Win10 1809+).
</content>
</invoke>
