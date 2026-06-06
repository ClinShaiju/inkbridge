# Wi-Fi Connectivity — Feasibility (rMPP ↔ Windows, cableless)

Goal: run the inkbridge link **over Wi-Fi instead of the USB cable** — the PC should **discover the
reMarkable Paper Pro on the local network and connect to it automatically**, managed from **inside
OpenTabletDriver / the plugin (no separate tray app)**, with a user-facing **Auto / Wi-Fi / USB**
mode switch. This revision adds the four questions raised after the first pass: **TCP vs UDP per
port**, **device↔PC pairing/identity** (so PC1 talks to rMPP1 *only*), a **full security audit** of
the now-LAN-exposed ports, and the **on-screen latency reading anomaly** (realistic ms vs 0.1 ms).

**Verdict up front:**
- **Transport already works cableless.** Every listener binds `0.0.0.0`; the device is reachable on
  `wlan0` today. Pointing the plugin at the Wi-Fi IP is the whole change to "make it work once."
- **Zero-config discovery is feasible and self-contained** via mDNS (device side is ~1 config line
  or a tiny embedded responder; plugin resolves a name/service in `TcpSource`).
- **The Auto/Wi-Fi/USB dropdown is feasible** and should reuse the exact pattern the shipped *touch
  mode* dropdown already uses (string property + static `PropertyValidated` choices, pushed to a
  static config singleton). Auto = USB-first with live USB-takeover / Wi-Fi-fallback.
- **Security is the blocker that gates making Wi-Fi the default.** Today all four ports are
  **unauthenticated, unencrypted, and bound to every interface**. On a cable that was point-to-point
  and fine; on Wi-Fi it exposes your **handwriting and touch stream** (i.e. everything you write and
  every on-screen-keyboard tap) plus a **battery-draining wakelock DoS** to anything on the LAN.
  Wi-Fi should ship **secure-by-default**: opt-in exposure + a **pairing token** (which also solves
  identity) + ideally encryption.

All device facts below were read **live over SSH on 2026-06-04**. Code references are to the current
tree (post `0b1c264`, bounded reconnect + presence beacon).

---

## 1. What the connection actually is today (verified)

It is already TCP/IP — the "USB cable" is just a **USB-Ethernet gadget**, not a special transport.
Four daemon ports, all bound to **`0.0.0.0`** (every interface, incl. `wlan0`):

| Port | Proto | Purpose | Source | Direction |
|---|---|---|---|---|
| **9291** | **UDP bcast** | presence beacon (`IBR1`, ~1/s, per-interface) | `daemon/src/beacon.rs` | device → subnet |
| **9292** | **TCP** | pen stream (18-byte `PenPacket`, ~150–500 Hz + 60 Hz keepalive) | `daemon/src/main.rs:36` | device → PC |
| **9293** | **TCP** | control plane (pub/sub: config, status, ping/pong) | `daemon/src/control.rs:27` | both |
| **9294** | **TCP** | touch stream (88-byte 10-slot snapshot per `SYN`) | `daemon/src/touch.rs:48` | device → PC |

- The plugin's `TcpSource` connects to **`INKBRIDGE_HOST` (default `10.11.99.1`)** in
  `InkbridgeEndpoint.Open()` (`otd-plugin/InkbridgeDevice.cs`). `10.11.99.1` is the **USB gadget IP**,
  so the default path is "over the cable" — but nothing about it is USB-specific.
- The installer hardcodes the same `10.11.99.1` for the SSH deploy and the user message
  (`install.cmd:21`).

**Implication:** Wi-Fi is not a new transport to build. What's missing is (a) learning the device's
Wi-Fi IP, (b) telling the plugin to use it, (c) keeping it secure now that it's on the LAN.

### Live proof it already works over Wi-Fi

```
usb0   10.11.99.1/27        ← current default the plugin uses
wlan0  192.168.0.189/24     ← device is ON Wi-Fi right now, DHCP address
default via 192.168.0.1 dev wlan0
daemon running (pid 741), bound 0.0.0.0:9292
```

`INKBRIDGE_HOST=192.168.0.189` → `loadsettings` → draw, connects over Wi-Fi with **zero code
changes**. The feature work is **discovery + UX + security**, not transport.

---

## 2. Device side — Wi-Fi & name-resolution facts (verified)

reMarkable Paper Pro, **"Codex Linux" 5.5.125 (Yocto scarthgap)**, BusyBox userland.

- **`systemd-networkd`** + **`wpa_supplicant`** (config `~/.config/remarkable/wifi_networks.conf`)
  manage `wlan0`. The address is **DHCP** → it drifts between sessions/networks. This is why a
  hardcoded IP is wrong for Wi-Fi.
- Hostname is **`imx8mm-ferrari`** — the **SoC codename, identical across same-model units**. Two
  rMPPs on one subnet would **collide** on `imx8mm-ferrari.local`; a discovery/identity design must
  not assume a unique hostname (see §5 pairing).
- **mDNS is 90 % there.** `systemd-resolved` is active and already resolves the device's own name to
  **both** IPs, **but the responder is off on wlan0** (`resolvectl mdns` → Global yes / usb0 resolve
  / **wlan0 no**). `resolve` answers locally but does not publish on the wire. Flip with a one-line
  networkd drop-in `MulticastDNS=yes` on `wlan0` (persists; or runtime `resolvectl mdns wlan0 yes`).
- **Avahi present but inert** (`/opt/sbin/avahi-daemon`, toltec, **no unit, inactive**) — don't
  depend on it.
- `systemd-resolved` answers only the host **A record** — no SRV/PTR, so "browse for the service"
  needs either avahi-with-a-service-file or (preferred) the daemon embedding a small mDNS responder.

---

## 3. Transport — TCP vs UDP, per port

The question: should each of the four ports be TCP or UDP for latency / packet-loss behaviour over
Wi-Fi? Short answer: **the beacon is already correctly UDP; control must stay TCP; pen and touch are
the only real UDP candidates, and only worth it if measurement proves Wi-Fi head-of-line blocking
hurts — at the cost of adding sequence numbers and an app-level connection lifecycle.**

### What TCP currently buys us (and UDP would cost)

The whole daemon design leans on **TCP connection state**:
- **Wakelock refcount** (`main.rs` `client_in/out`) acquires/releases on connect/disconnect.
- **"Client connected"** gating (pen reads event2 only while a client is connected; touch only while
  app subscriber present).
- **Disconnect detection** via `POLLHUP`/EOF and the heartbeat staleness timer.
- **Framing** — the plugin does `ReadExact` of fixed-size frames off an ordered byte stream.

UDP has no connect/disconnect, no ordering, no framing — every one of those becomes app-level work.

### Per-port analysis

- **9291 beacon — UDP (keep).** Presence announcement, one-to-many, loss-tolerant, no state. UDP
  broadcast is exactly right; TCP would be absurd here. ✅ already optimal.

- **9293 control — TCP (keep).** Low-rate config/status/ping. **Config and status must arrive and
  stay ordered** (a dropped "disconnected" or a reordered area-config corrupts the on-device UI).
  Reliability matters more than the sub-ms latency of a 1 Hz heartbeat. UDP would be wrong. ✅ keep
  TCP.

- **9292 pen — TCP today; UDP is a *defensible* optimisation, not a clear win.**
  - *Why UDP could help:* coordinates are **absolute and idempotent** — a lost sample is harmless
    because the next sample (≤2–6 ms later at 150–500 Hz) supersedes it. UDP avoids **TCP
    head-of-line blocking**, where one lost segment stalls *all* later samples until retransmit — the
    classic cause of a "cursor freezes then jumps" feel on a lossy Wi-Fi link.
  - *Why UDP is not free:* (1) **stale-sample reordering** — a delayed datagram arriving after a
    newer one would yank the cursor backward; needs a **monotonic sequence/timestamp** so the
    receiver drops anything older than the newest seen (the packet already carries a `ts_us` we could
    repurpose). (2) **No lifecycle** — wakelock, "client connected", and disconnect detection must
    move to an app-level hello + heartbeat + idle-timeout over UDP. (3) Pressure/proximity
    transitions are also idempotent in the current full-state packet, so loss is still self-healing —
    *good*, that lowers the risk.
  - *Verdict:* keep TCP for v1. Treat UDP as a **measured experiment** (build-order step 1): only
    pursue it if the live Wi-Fi capture shows real HOL-blocking jitter. If pursued, the clean design
    is **hybrid** — keep TCP `:9293` for lifecycle/auth/keepalive, add an *optional* UDP pen channel
    with seq numbers negotiated over the control plane.

- **9294 touch — same shape as pen.** Each packet is a **full 10-slot snapshot**, and the PC **diffs
  successive snapshots** to derive DOWN/UPDATE/UP. So a dropped snapshot self-heals on the next one —
  *except the final "all fingers up" snapshot*: lose that over UDP and a contact sticks. Mitigation
  if UDP: send the lift snapshot 2–3× and/or a periodic idle snapshot. Touch is lower-rate and less
  latency-critical than pen, so TCP HOL blocking matters less — **keep TCP**; UDP only if pen goes
  UDP and we want symmetry.

**Bottom line:** don't churn the transports speculatively. Beacon UDP ✅, control TCP ✅. Measure the
pen path over real Wi-Fi first; only the pen (then maybe touch) is a UDP candidate, and only with
sequence numbers + an app-level lifecycle to replace what TCP gives for free.

---

## 4. Discovery (mDNS auto-discovery) — re-confirmed

| Option | How | Pros | Cons | Self-contained? |
|---|---|---|---|---|
| **mDNS name resolution** | Enable resolved responder on wlan0; plugin resolves `imx8mm-ferrari.local` | Trivial device side (1 line); Win10/11 resolve `.local` natively | Hostname not unique across units; A-record only (no browse); collision with 2 rMPPs | ✅ |
| **mDNS service discovery** *(recommended)* | Daemon advertises `_inkbridge._tcp.local` (host+port+TXT) | True zero-config; survives IP **and** hostname change; carries port + a **unique instance/TXT id** (→ enables identity, §5); multi-device selectable | Need an SRV/PTR responder (embedded Rust crate) + a browser in the plugin | ✅ (daemon-embedded) |
| **UDP broadcast beacon** *(already exists!)* | `:9291` already broadcasts `IBR1` ~1/s | **Already shipped**; dead simple; subnet-local | Payload is just a magic tag — carries **no IP/port/id** today; broadcast dropped across AP/VLAN isolation | ✅ |
| **Subnet scan** | Plugin probes `:9292` across the /24 | No device change | Slow; looks like port-scanning to AV/IDS; fails on /16 or isolated clients | ✅ (PC only) |
| **Manual entry** | env var / `inkbridge.json` / OTD field | Always works as fallback | Defeats "automatic"; IP drifts | ✅ |
| **USB-assisted handoff** | While plugged in, ask daemon over `:9293` for its wlan0 IP + identity, cache it | Reuses the **trusted** USB path to bootstrap Wi-Fi *and* exchange the pairing token (§5); exact, no multicast | Needs one initial cable connect | ✅ |

### 4a. How everyday devices get discovered on a LAN

Worth grounding the choice in what cameras, phones, printers, and speakers actually do — because
inkbridge is the same problem (a service that must be *found*, then *trusted*):

- **mDNS / DNS-SD (Bonjour / Avahi)** — the dominant standard. AirPrint printers (`_ipp._tcp`),
  Chromecast (`_googlecast._tcp`), AirPlay speakers (`_airplay._tcp` / `_raop._tcp`), HomeKit
  accessories (`_hap._tcp`), Spotify Connect, most "smart home" gear. Each advertises
  `_service._tcp.local` with **SRV** (host + port), **TXT** (metadata incl. a stable **id**), and
  **A/AAAA** records. This is exactly the path we're implementing.
- **SSDP / UPnP** — smart TVs, DLNA media servers, Sonos, routers, many consumer IP cameras.
  Multicast `239.255.255.250:1900`, HTTP-over-UDP (`M-SEARCH` / `NOTIFY`); the device is described by
  an XML doc at a URL and identified by a **UUID** in its USN (`uuid:2fac…::urn:…`).
- **WS-Discovery (ONVIF)** — the standard for **IP/security cameras**. SOAP over multicast
  `239.255.255.250:3702`; each camera has an endpoint-reference **UUID**.
- **Cloud rendezvous + QR** — consumer phones/cameras often *don't* do pure-LAN discovery: you scan a
  QR/8-digit code, a cloud broker introduces the peers, then they hole-punch a P2P link. (Out of
  scope here — we're LAN-only and have USB as the introduction channel.)

**The universal security lesson:** *discovery is never trusted to establish identity.* mDNS, SSDP,
and WS-Discovery are all **unauthenticated and trivially spoofable** — anyone on the L2 can answer.
So every serious system pairs discovery with an **out-of-band-bootstrapped cryptographic identity**:

| System | Discovery | Identity / auth (the part that's actually trusted) |
|---|---|---|
| **Apple HomeKit (HAP)** | mDNS `_hap._tcp`, TXT `id=` | **SRP** seeded by an 8-digit setup code (OOB, printed/QR) → long-term **Ed25519** keypair per side; later sessions use the keys |
| **Matter** | DNS-SD `_matterc._udp` (TXT discriminator) | passcode (QR/manual) commissioning → **CASE**: per-device **P-256** operational certs |
| **Chromecast** | mDNS `_googlecast._tcp` | **TLS** with a device certificate |
| **WireGuard** | (none — static endpoints) | **Curve25519 public-key pinning** (trust-on-first-use / manual exchange) |

In every case: **the id in the discovery record is just a locator; possession of a private key is the
identity.** A spoofed mDNS/SSDP record at worst causes a *failed* connect, never a compromise,
because the impostor can't produce the key.

### 4b. Beacon-carries-IP vs mDNS — comparison

We already broadcast a UDP beacon on `:9291` (`beacon.rs`, per-interface, validated by the plugin's
`BeaconListener` in `Reconnect.cs`). Extending its payload to carry IP+port+id is the *cheapest*
self-discovery; mDNS is the *standard* one. Both were evaluated:

| Dimension | **Extend the `:9291` beacon** | **mDNS / DNS-SD** *(chosen)* |
|---|---|---|
| Code to ship | Minimal — infra already exists both sides | Daemon-embedded responder crate + a plugin browser |
| Device config | **None** — already egresses wlan0 today (getifaddrs) | None if daemon-embedded; (avoid the resolved drop-in path — wiped by OS updates) |
| Carries id+port | Yes (after extension) | Yes (SRV+TXT) |
| Reaches across net | Directed broadcast — dropped by AP isolation, no VLAN cross | Multicast — same limits, but more APs **reflect** mDNS (AirPlay/Chromecast plumbing) |
| Interop / tooling | Custom — invisible to standard browsers | **Standard** — `dns-sd -B`, avahi, phones all see it; native Win10 `.local` |
| Windows fragility | None (plugin listens itself) | `.local` can break under VPN split-DNS (mitigated: plugin does its own query) |
| Device attack surface | **Send-only** on device (smaller) | Embedded responder parses hostile multicast (larger) |
| Recon exposure | **Constant** ~1/s presence advert to whole L2 | Mostly query-driven (+ announce on change) |
| Security (spoofing) | Spoofable → redirection risk | Spoofable/poisonable → identical redirection risk |

**Decision (per project owner): mDNS is the primary mechanism** — standards interop (it shows up like
any phone/printer, is debuggable with off-the-shelf tools, and benefits from AP mDNS reflection),
with the beacon kept as the **reconnect-wake + LAN-robustness fallback** it already is. Both are
gated on the identity handshake (§5); neither is trusted to pick the endpoint on its own.

Resolution chain in `TcpSource` / `ConnectionConfig`:
```
explicit override (env / inkbridge.json)  →  mDNS browse _inkbridge._tcp (filtered to paired id)
  →  beacon-learned IP (paired id)  →  USB 10.11.99.1  →  cached last-IP
```
**Re-resolve on every reconnect** — a cached IP is only a fast-path hint; never retry a dead IP as
the primary path. mDNS/beacon are **link-local** — they do not cross subnets/VLANs or guest-Wi-Fi
isolation; those need the manual/USB fallback.

---

## 5. Device ↔ PC pairing & identity (PC1 ↔ rMPP1 only)

**The ask:** discovery finds *every* inkbridge daemon on the subnet; how do we make **PC1 bind to
rMPP1 specifically** — not rMPP1 serving every PC, nor PC1 grabbing whichever device answers first?

**Today there is no identity at all.** The plugin connects to whatever answers `host:9292` and only
validates the 4-byte `IBR1` magic — that's a **protocol version tag, not an identity**. Any inkbridge
daemon on the subnet is interchangeable, and the hostname (`imx8mm-ferrari`) is **identical across
same-model units**, so it can't disambiguate two rMPPs.

**Design (the one we're building) — a UUID *locator* + a pinned P-256 *key* identity.** This mirrors
HomeKit/Matter/WireGuard (§4a): the id finds the device, the key proves it, USB is the out-of-band
channel that bootstraps trust.

#### Why UUID, not MAC, for the locator
- **MAC is the wrong identity.** Modern OSes do **MAC randomization** (so it isn't even stable); it's
  **trivially spoofable** (anyone can claim it); it **leaks a hardware identifier** (privacy); and the
  **Wi-Fi MAC ≠ USB MAC**, so it wouldn't be one value across transports. A MAC in a discovery record
  is a fingerprint, not a credential.
- **Use a random UUIDv4**, generated once on the device and persisted to
  `/home/root/inkbridge/identity` (mode `0600`). It's stable, non-sensitive, hardware-agnostic, and
  carried in the mDNS **TXT `id=`** purely as the *locator* so the plugin can filter discovery to its
  paired device. The id is **public and spoofable** — it is **not** an authenticator (that's the
  key). Filtering on it is convenience (pick the right device among several), not security.

#### The crypto identity (what's actually trusted)
- **Algorithm: NIST P-256 (ECDSA for auth; ECDH for future encryption).** Chosen because it's **what
  Matter uses for device pairing**, it's **native in .NET 8** (`ECDsa` / `ECDiffieHellman`, no
  third-party crypto in the plugin), and it's available as **pure-Rust** (`p256` crate, so the
  musl-cross-compiled daemon needs no C/`ring` dependency). Each side generates a **long-term P-256
  keypair on first run** (device key persisted next to the identity file; PC key in `inkbridge.json`).
- **Pairing = trust-on-first-use over USB.** USB (`10.11.99.1`) is a *physically present,
  point-to-point* channel — a better out-of-band bootstrap than HomeKit's printed code. On first
  connect over the cable the two sides **exchange public keys** over the control plane and **pin**
  them: the PC stores `{id → device_pubkey}` in `inkbridge.json`; the daemon adds the PC's pubkey to
  an **allow-list** (`/home/root/inkbridge/authorized_keys`). (A future "pair this PC?" confirmation
  on the device screen would harden TOFU against an attacker racing the first connect; the cable makes
  that race impractical anyway.)
- **Per-connection mutual authentication.** Every channel handshake (`:9292` pen, `:9294` touch,
  `:9293` control) does a **mutual signed-nonce challenge-response**: each side sends a fresh random
  nonce, the peer returns an **ECDSA signature** over (its-nonce ‖ peer-nonce ‖ channel-tag) with its
  pinned key. The daemon **rejects an unrecognised/invalid signer immediately — before** acquiring the
  wakelock or spawning work (closing the battery-drain + thread-storm DoS in §6). Result: **rMPP1
  serves only PC1**, **PC1 connects only to rMPP1**, and a spoofed mDNS/beacon record at worst yields
  a failed connect, never a capture.
- **Encryption (follow-on, not v1):** the same P-256 keys do **ECDH → HKDF → AES-GCM** to encrypt the
  pen/touch/control streams (handwriting/touch confidentiality, §6 #4). Auth lands first; encryption
  reuses the established keys.

This single mechanism answers the identity question (§5) *and* closes the authentication hole (§6).
The UUID-only interim (filter on id, no key) is explicitly **not** treated as security — it's just
the locator; the key is mandatory for the trust claim.

---

## 6. Security audit — all four ports, now on the LAN

Putting these ports on Wi-Fi changes the threat model from "point-to-point cable" to "**anything on
the LAN**." Audit done against the current tree.

### Posture today (all four ports)
- **Bound `0.0.0.0`** — exposed on USB *and* Wi-Fi *and* any future interface; cannot restrict to
  USB-only without a code change.
- **No authentication** — every connection is trusted. The only "handshake" is the `IBR1`/`IBT1`
  magic and the control-plane role token `IBCP`/`IBCS`, none of which is a secret.
- **No encryption** — plaintext on the wire. On WPA2-PSK / open / shared networks, a passive sniffer
  on the same L2 captures everything.
- **Daemon runs as root** on the device; thread-per-connection with **no connection cap**.

### Assets at risk (ranked)
1. **Pen stream (9292) = your handwriting**, in real time. Highest privacy impact — it's a
   note-taking device; this is literally everything you write.
2. **Touch stream (9294) = every tap/gesture**, including **on-screen-keyboard taps → credential
   capture**.
3. **Device availability / battery** — the wakelock pins the SoC awake.
4. **Control config/status (9293)** — active-area geometry + latency (low sensitivity), but it gates
   touch streaming (see below).

### Findings per port

- **9292 pen — eavesdrop + battery DoS.** Any LAN host can connect and **receive your live
  handwriting** (note exfiltration / IP theft). Two concurrent readers are allowed by design, so the
  attacker doesn't lock you out — but **holds the wakelock** (`client_in`), draining battery and
  keeping the digitizer powered indefinitely. **Connection-storm DoS:** each accept spawns an
  unbounded thread.

- **9294 touch — eavesdrop (keystrokes) + forced streaming + battery DoS.** Same shape: an attacker
  receives your **touch coordinates** (on-screen keyboard → password capture). The client **options
  byte** lets a connector set **always-on** (stream even when the app is closed) and **disable palm
  rejection**. Holds the wakelock; unbounded threads.

- **9293 control — injection + privacy + indirect touch-leak.** A rogue **publisher** (`IBCP`) can
  inject **false config** (mis-scales the on-device visualizer) and a fake **"connected" status**. A
  rogue **subscriber** (`IBCS`) reads your area config/latency **and increments `app_subs`** — which
  is the daemon's **"AppLoad app is open" signal that gates touch passthrough**. So a rogue
  subscriber can **force the touch stream to flow to the legitimate PC even when the app is closed**.
  Unbounded threads.

- **9291 beacon — recon + spoofable wake.** Broadcasts device **presence + (proposed) IP/id** to the
  whole subnet (fingerprints "an rMPP running inkbridge is here"). **Spoofable:** an attacker
  broadcasting `IBR1` makes every parked plugin wake and attempt a reconnect — a mild
  probe-amplifier / nuisance DoS. Payload carries no secret (correct), but is also unauthenticated.

### Cross-cutting
- **Privacy is the headline** — pen + touch together are a full input-capture of the device.
- **No rate-limiting / connection cap** on any TCP listener → resource-exhaustion DoS.
- **Wakelock as a weapon** — any unauthenticated connect drains the battery.
- **RCE risk is low** (Rust memory-safety; parsing is simple/bounded — the control plane classifies
  messages by substring, sloppy but not unsafe), **but the attack surface is now the entire LAN**.

### Recommendations (layered, by leverage)
1. **Secure-by-default binding.** Default to **USB/loopback only**; Wi-Fi exposure is **explicit
   opt-in** (daemon flag / config). "Cableless by choice, not by accident." *(Biggest risk reducer
   for least effort; pairs naturally with the Auto/Wi-Fi/USB dropdown — USB stays zero-config, Wi-Fi
   prompts you to enable exposure.)*
2. **Pairing token on all handshakes** (§5) — authenticate `IBR1`/`IBT1`/`IBCP`/`IBCS`; reject
   unauthenticated connections immediately (before wakelock/threads). **Solves identity + auth in one
   stroke.**
3. **Encrypt the streams** — wrap pen/touch (and control) in TLS (`rustls`) or a lightweight
   Noise/ChaCha channel keyed by the pairing secret. Confidentiality for handwriting/touch; mandatory
   on Wi-Fi, optional on USB.
4. **Bound concurrency** — cap connections per port (e.g. 2), rate-limit accepts; **only hold the
   wakelock for authenticated clients**.
5. **Gate the touch trigger** — count only **authenticated** subscribers toward `app_subs` so a rogue
   `IBCS` can't force touch streaming; require publisher auth before accepting config/status.
6. **Beacon hygiene** — include the (non-secret) device id so plugins only react to their paired
   device; optionally HMAC the beacon to stop spoofed-wake; keep payload non-sensitive (it is).
7. **Document the exposure** prominently regardless of what we automate.

**Minimum viable for a Wi-Fi release:** #1 (opt-in) + #2 (pairing token) + #4 (caps). #3 (encryption)
is the privacy upgrade that should follow quickly given the handwriting/touch sensitivity.

---

## 7. Managing it through OpenTabletDriver — the Auto / Wi-Fi / USB dropdown

**Constraint (unchanged):** OTD's settings UI does **not** reach the device-hub/endpoint layer where
we open the connection — `[Property]` UI exists only on **tools/filters/output modes**, not on
`IDeviceHub`/`IDeviceEndpoint`. That's why the host is an env var today. So the connection itself must
**need no UI** (auto-discovery), with any UI living on the **tool** we already own and pushing a value
down to the connection via a **static singleton** — exactly the shipped touch-mode pattern.

### Reuse the shipped touch-dropdown pattern (re-read from `InkbridgeTool.cs`)
The touch dropdown is the template. Key facts confirmed in the code:
- **OTD 0.6.7 cannot render a raw `enum` property** (throws `NotSupportedException`). The dropdown is a
  **`string` property** validated against a **`static IEnumerable<string>` choices** member via
  `[Property("…"), PropertyValidated(nameof(Choices)), DefaultPropertyValue(…)]`
  (`InkbridgeTool.cs:25-44`). The validation source **must be static** or the values don't populate.
- `Initialize()` maps the string → internal enum and calls `TouchService.Instance.SetMode(...)` — a
  **static singleton** that **no-ops when unchanged** and live-reconfigures when changed
  (`InkbridgeTool.cs:138-170`).
- **Do NOT call `Driver.Detect()` on re-apply** (the *Detect() orphan* trap, `InkbridgeTool.cs:172-208`):
  re-detecting rebuilds the device tree after the output mode was bound to the old tree → cursor
  stops moving. A mode change must **swap only the socket inside the existing endpoint**, never
  re-register the hub or re-detect.

### Design
```csharp
// mirror TouchModeChoices exactly: string property + static PropertyValidated source
private const string ConnAuto = "Auto", ConnWiFi = "Wi-Fi", ConnUsb = "USB";
public static IEnumerable<string> ConnectionChoices => new[] { ConnAuto, ConnWiFi, ConnUsb };

[Property("Connection"), PropertyValidated(nameof(ConnectionChoices)), DefaultPropertyValue(ConnAuto)]
public string ConnectionName { get; set; } = ConnAuto;
```
In `Initialize()`, after the existing telemetry/touch setup, push it to a new static
`ConnectionConfig.Instance.SetMode(mode)` (sibling to `TouchService`). `InkbridgeEndpoint.Open()` /
`TcpSource` read `ConnectionConfig` to choose the host:
- **USB** → `10.11.99.1`.
- **Wi-Fi** → discovered (beacon/mDNS) → cached → explicit override.
- **Auto** → **probe USB first** (connect `10.11.99.1:9292` with a short ~300 ms timeout); if it
  answers, use USB; else use Wi-Fi.

### The "USB takes over / Wi-Fi falls back" live behaviour
The ask: *in Auto, if USB is plugged while on Wi-Fi, USB takes over; if USB unplugs, Wi-Fi retries.*
- **On (re)connect** this is automatic from the Auto probe order above (USB-first).
- **Mid-session takeover** needs a small supervisor: while connected over Wi-Fi in Auto, periodically
  probe USB (`10.11.99.1:9292`, short timeout, e.g. every few seconds); when it appears, **switch**.
  Switching = drop the Wi-Fi socket and reconnect via the new selection — implemented with a
  **generation counter** that makes `TcpSource.Next()` break its current read loop and
  `EnsureConnected()` re-resolve. **Precedent:** `TouchService.SetMode` already stops/restarts its
  worker live; `ConnectionConfig.SetMode` does the same to the pen `TcpSource`.
- **Critical:** all switching swaps the **socket only**. Never call `Driver.Detect()` or re-register
  the hub on a mode/transport change (Detect() orphan trap). The reconnect machinery
  (`Reconnect.cs` `ReconnectPolicy` + `BeaconListener`) already exists to drive re-resolution.

Power-user override stays: `INKBRIDGE_HOST` env (works now) → `inkbridge.json` next to the DLL.

---

## 8. Robustness over Wi-Fi (vs. the cable)

- **Latency & jitter.** `TCP_NODELAY` is already set (pen, control, touch). The 1 € hover filter
  smooths hover. Wi-Fi adds variable RTT; expect usable-but-higher latency. The control-plane
  ping/pong already surfaces `latency_ms`/`rate_hz` for a live read (but see the metric bug below).
- **IP drift / roaming — handled by resolve-by-name, not scanning** (§4). mDNS/beacon return the
  *current* IP; re-resolve on every reconnect. Same L2 subnet required.
- **Wi-Fi power-save.** `wlan0` PSM can add latency spikes when idle. The pen-in-range ~60 Hz
  keepalive keeps the socket warm; consider `iw wlan0 set power_save off` while a client is connected
  (mirror the wakelock refcount). Verify reMarkable's stack honours it.
- **Autosleep before connect — out of scope (awake-only is the accepted model).** Wakelock is held
  only while a client is connected; a no-client device suspends and answers nothing. Using the rMPP
  as a tablet means turning it on (screen must be on to send input anyway), so "wake it, then it
  connects" is the workflow, not a defect. (`iw phy` shows full **WoWLAN** support — magic packet /
  pattern match, driver `wlan_sdio`, `wlan0` wakeup `enabled` — parked as unnecessary, not a dead
  end.)

### The on-screen latency reading: realistic ms vs ~0.1 ms (anomaly explained)
**Cause:** the metric in `InkbridgeTelemetry.Session()` is a **single un-matched `stream.Read`** after
each ping:
```csharp
long t0 = Stopwatch.GetTimestamp();
Send("{\"type\":\"ping\",\"ts\":0}");
int n = stream.Read(pong, 0, pong.Length);   // measures "first bytes available", not "the pong for THIS ping"
latencyMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
```
It is **not robust**: the ping `ts` is hardcoded `0` (no correlation), the read isn't line-delimited
or drained, and the daemon echoes ping→pong with no sequencing. A reading of **~0.1 ms (100 µs) is
below any real network RTT** — it means `Read` returned **bytes already sitting in the local socket
buffer**, i.e. **no round trip happened**: a pong (or a retransmitted/duplicated segment) was already
buffered when `Read` was called, so it returned instantly. On **USB** the true RTT is uniformly
sub-millisecond, so this noise is invisible; on **Wi-Fi** the real value is several ms, so the
artifact stands out as the display **alternating between a realistic number and ~0.1 ms**.

**Fix (small, plugin-side):** give each ping a **unique monotonic `ts`**, read **newline-delimited
lines** (not raw chunks), and accept only the **pong whose `ts` matches** the outstanding ping
(draining/ignoring anything else). That makes the number the true matched round-trip on both
transports. *(A 2-minute live `tcpdump`/log capture over Wi-Fi would confirm the exact alternation
trigger, but the metric is clearly fragile by construction and the fix stands regardless.)*

---

## 9. Proposed architecture

```
rMPP (daemon)                                          Windows (OTD plugin, in-process)
─────────────                                          ────────────────────────────────
inkbridge-daemon  [opt-in Wi-Fi exposure; else USB/loopback]
  ├─ UDP  :9291 beacon   (extend payload: ip+port+id)  ┐  BeaconListener learns paired device addr
  ├─ TCP  :9292 pen      (auth token; cap conns)       │  ConnectionConfig (Auto/Wi-Fi/USB, static)
  ├─ TCP  :9293 control  (auth pub/sub; gate app_subs) │  InkbridgeEndpoint.Open() resolve chain:
  ├─ TCP  :9294 touch    (auth token; cap conns)       │    override → beacon → mDNS(id) → name → USB
  └─ mDNS  _inkbridge._tcp  (TXT: id, ver, port)  ─────┘  TcpSource(host) — socket-only switch on
  + pairing identity (USB bootstrap)                      mode/transport change (NO Driver.Detect())
  + optional power_save off while client connected        InkbridgeTool: "Connection" dropdown
  + (optional) encrypt streams keyed by pairing secret    (string + static PropertyValidated)
```
Everything new lives in **`daemon/`** and **`otd-plugin/`** — no new process on either side.

---

## 10. Build order

1. **Prove Wi-Fi end to end (no code):** `INKBRIDGE_HOST=<wlan ip>` → `loadsettings` → draw.
   **Capture pen rate/latency over real Wi-Fi** — this is also the data that decides the TCP-vs-UDP
   question (§3) before any transport change.
2. **Fix the latency metric** (§8) — matched ping/pong, line-delimited read. Cheap, makes the
   on-screen number trustworthy on Wi-Fi; do this early so later steps have a good gauge.
3. **Auto/Wi-Fi/USB dropdown** (§7) — string property + static choices on `InkbridgeTool`, a
   `ConnectionConfig` static, USB-first Auto with live USB-takeover / Wi-Fi-fallback (socket-only
   switch, no Detect()). Override chain stays env → `inkbridge.json`.
4. **Discovery:** extend the existing `:9291` beacon to carry `ip:port` (+ id) and have
   `BeaconListener` feed the resolve chain; add the `MulticastDNS=yes` drop-in to
   `install-service.sh`; then daemon `_inkbridge._tcp` + plugin browse for true zero-config.
5. **Pairing + identity** (§5) — ✅ **SHIPPED** (pen + touch): random UUID locator in mDNS TXT +
   long-term P-256 keypair, USB-TOFU pinned, mutual signed-nonce challenge-response on `:9292`/`:9294`,
   filtered by id so PC1↔rMPP1. *Remaining:* extend the same handshake to control (`:9293`).
6. **Security hardening** (§6) — ✅ wakelock-only-for-authed is **done** on the authed channels.
   *Remaining:* secure-by-default binding (opt-in Wi-Fi), connection caps, gate `app_subs` on auth,
   then stream encryption (ECDH→HKDF→AES-GCM, reusing the pinned keys).
7. **Power:** `power_save off` while a client is connected.
8. **Docs/installer:** update copy that hardcodes `10.11.99.1`; document the Wi-Fi opt-in + pairing.

---

## 11. Risks & open items

1. **Security gating the default** (§6) — Wi-Fi must not ship as an always-open, unauthenticated LAN
   service. Opt-in + pairing are prerequisites, not nice-to-haves.
2. **TCP vs UDP for pen** (§3) — defer until step 1's capture; UDP only with seq numbers +
   app-level lifecycle.
3. **mDNS/beacon across real networks** — multicast/broadcast dropped by AP/client isolation, guest
   VLANs, some mesh. Keep env/config + USB fallbacks first-class.
4. **OTD lifecycle for the dropdown** — validate the live switch against the Detect()-orphan trap;
   swap socket only.
5. **Latency/jitter feel** at 150–500 Hz over shared Wi-Fi — validate; tune keepalive/filter.
6. **Windows `.local` resolution** — Win10 1809+ resolves mDNS natively, but VPN split-DNS / corp
   images can interfere; the plugin doing its own query (beacon/embedded mDNS) is the robust path.
7. **Two same-model rMPPs collide on hostname** — identity must come from a generated id/TXT, not the
   SoC hostname.

---

### Sources
- Live device probe (SSH `root@10.11.99.1`, 2026-06-04): `ip addr`/`ip route` (usb0 10.11.99.1,
  wlan0 192.168.0.189, default via wlan0); `/etc/os-release` (Codex Linux 5.5.125 scarthgap);
  `systemd-networkd` + `wpa_supplicant`; `resolvectl mdns` (Global yes / usb0 resolve / **wlan0
  no**); `resolvectl query imx8mm-ferrari.local` → both IPs; toltec `/opt/sbin/avahi-daemon` present
  but inactive (no unit); daemon pid 741 on `0.0.0.0:9292`; BusyBox 1.36.1; `iw phy` WoWLAN
  (magic-packet + pattern-match), driver `wlan_sdio`, wakeup `enabled`, `/sys/power/autosleep = mem`.
- Current tree (post `0b1c264`): `daemon/src/main.rs` (`:9292` pen, wakelock refcount, keepalive,
  `TCP_NODELAY`, `POLLHUP` disconnect), `daemon/src/control.rs` (`:9293` pub/sub `IBCP`/`IBCS`,
  ping/pong, `app_subs` touch gate, staleness broadcast), `daemon/src/touch.rs` (`:9294` 88-byte
  snapshot, `IBT1` + options byte always-on/palm), `daemon/src/beacon.rs` (`:9291` UDP `IBR1`
  per-interface broadcast), `otd-plugin/InkbridgeDevice.cs` (`TcpSource`, `INKBRIDGE_HOST` default
  `10.11.99.1`, hub/endpoint with no settings UI), `otd-plugin/InkbridgeTool.cs` (string-dropdown
  pattern, Detect()-orphan "apply twice" lifecycle), `otd-plugin/InkbridgeTelemetry.cs` (the
  single-Read ping/pong latency metric, §8), `otd-plugin/Reconnect.cs` (`BeaconListener` +
  `ReconnectPolicy`), `daemon/install-service.sh` (where the networkd drop-in would go).
- Cross-refs: `docs/phase0-findings.md`, `docs/feasibility.md`, `docs/touch-feasibility.md` /
  `docs/touch-modes.md` (touch needs a sidecar; Wi-Fi does not), `protocol/packet.md`,
  `protocol/touch-packet.md`.
- Mechanisms: `systemd-resolved` mDNS responder (`MulticastDNS=yes`), DNS-SD `_inkbridge._tcp`
  (pure-Rust `libmdns`/`mdns-sd`), Windows native `.local` (Win10 1809+); TLS via `rustls` /
  Noise+ChaCha for stream encryption.
