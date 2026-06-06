# inkbridge — Security model & audit

**Scope:** the network attack surface of the inkbridge daemon (rMPP) and OTD plugin (Windows), with
emphasis on what changes when the link moves from the **USB cable** to **Wi-Fi / the LAN**. Audited
against the current tree (post `0b1c264`). Companion to
[`wifi-connectivity-feasibility.md`](./wifi-connectivity-feasibility.md) (§6 there is the condensed
version; this is the full writeup).

> **TL;DR** — On USB the link was effectively point-to-point and the lack of auth/encryption was
> acceptable. On Wi-Fi the same four ports are reachable by **anything on the LAN**, and they carry
> **your handwriting and every touch** in plaintext with **no authentication**, plus a
> **battery-draining wakelock** any stranger can pin. **Wi-Fi must therefore ship secure-by-default:
> opt-in exposure + a pairing token (auth + identity) + connection caps, with stream encryption close
> behind.** None of these are required for the USB path to keep working as-is.

---

## 1. Threat model

**What we're protecting**

| Asset | Where | Sensitivity |
|---|---|---|
| Pen stream — **everything you write** | TCP `:9292` (device → PC) | **High** (it's a note-taking device) |
| Touch stream — **every tap/gesture**, incl. on-screen-keyboard | TCP `:9294` (device → PC) | **High** (credential capture via OSK) |
| Device availability / battery (the wakelock) | held while any client is connected | Medium |
| Active-area config + link latency | TCP `:9293` (pub/sub) | Low (but gates touch — see §3) |
| Device presence + (future) IP/id | UDP `:9291` broadcast | Low (recon) |

**Who we're defending against**

- **Passive sniffer on the same L2** (open Wi-Fi, shared WPA2-PSK, a compromised host on the LAN) —
  reads plaintext streams.
- **Active host on the LAN** — connects to the ports, eavesdrops, injects, or denies service.
- **Out of scope:** a fully compromised PC or a rooted/hostile device (they already hold the data);
  off-LAN / internet attackers (the ports are link-local, not routed — but see "bound 0.0.0.0").

**Trust boundary today:** *being on the same network = full trust.* That is the core problem on
Wi-Fi.

---

## 2. Current posture (all four ports)

- **Bound `0.0.0.0`** — every listener (`main.rs`, `control.rs`, `touch.rs`) and the beacon
  (`beacon.rs`) is exposed on **USB *and* Wi-Fi *and* any future interface**. There is no way to
  restrict to USB-only without a code change.
- **No authentication.** The only "handshakes" are magic tags / role tokens — **none is a secret**:
  - pen: 4-byte `IBR1` hello (a protocol *version*, sent device→PC).
  - touch: 4-byte `IBT1` hello, then the client sends **one options byte** (always-on, palm-reject).
  - control: first line is a **role token** `IBCP` (publisher) or `IBCS` (subscriber).
  Anyone who knows the (public, documented) protocol is accepted.
- **No encryption.** Plaintext framed binary (pen/touch) and newline JSON (control) on the wire.
- **No connection limits.** Each listener is **thread-per-connection, unbounded** (`thread::spawn`
  per accept). No accept rate-limit.
- **Daemon runs as root** on the device. (Parsing is in safe Rust and bounded, so memory-safety RCE
  risk is low — but the *reachability* of that code is now the whole LAN.)

---

## 3. Findings per port

### 3.1 `:9292` pen (TCP) — handwriting eavesdrop + battery DoS
- **Eavesdrop / IP theft.** Any LAN host can connect and **receive your live pen stream** — position,
  pressure, tilt, in real time. That is a faithful copy of everything you write.
- **No lockout, but battery drain.** The daemon intentionally allows multiple concurrent readers
  (event2 is read un-grabbed), so an attacker connecting does **not** lock out the legit PC — but
  every connection calls `client_in()` and **holds the wakelock**, keeping the SoC awake and the
  digitizer powered. A single idle attacker socket = the tablet never sleeps = battery drain.
- **Thread-storm DoS.** Unbounded `thread::spawn` per accept; a flood of connects exhausts threads.

### 3.2 `:9294` touch (TCP) — keystroke eavesdrop + forced streaming + battery DoS
- **Eavesdrop.** An attacker receives your **touch coordinates**. On the on-screen keyboard this is
  effectively a **keylogger** (tap positions → typed characters), including passwords.
- **Abuse of the options byte.** A connector can set **always-on** (stream even when the AppLoad app
  is closed) and **disable palm rejection** — i.e. force the device to stream touch it otherwise
  wouldn't.
- Holds the wakelock; unbounded threads (same as pen).

### 3.3 `:9293` control (TCP) — injection + privacy + *indirect touch-leak*
- **Rogue publisher (`IBCP`).** Can push **false `config`** (mis-scales the on-device visualizer
  box) and a fake **`status` {connected:true}** (lies to the on-device UI about link health).
- **Rogue subscriber (`IBCS`).** Reads your **area config + latency**, *and* — critically —
  **increments `app_subs`**, the daemon's "**AppLoad app is open**" signal that **gates touch
  passthrough**. So a rogue subscriber can **make the touch stream start flowing to the legitimate
  PC even when the on-device app is closed**, defeating the app-open privacy gate. (`control.rs`
  `handle_subscriber` → `app_subs.fetch_add`.)
- Unbounded threads.

### 3.4 `:9291` beacon (UDP broadcast) — recon + spoofable wake
- **Recon.** Broadcasts device presence (and, in the proposed extension, IP + id) to the whole
  subnet — fingerprints "an rMPP running inkbridge is here."
- **Spoofable wake / nuisance DoS.** The payload is just `IBR1`; an attacker broadcasting it makes
  every parked plugin (one that exhausted its bounded reconnect budget and is waiting on the beacon)
  **wake and attempt a reconnect**. A probe-amplifier, not catastrophic.
- Correctly carries **no secret** today — keep it that way (the id is a non-secret identifier, not a
  credential).

---

## 4. Cross-cutting issues

- **Privacy is the headline.** Pen + touch together are a **complete input capture** of the device.
  This is the difference that makes "no auth/encryption" unacceptable on Wi-Fi when it was tolerable
  on a cable.
- **Wakelock as a weapon.** Any unauthenticated connect on `:9292`/`:9294` pins the battery. Auth
  must happen **before** `client_in()`.
- **Resource exhaustion.** No connection cap / accept rate-limit on any TCP listener.
- **`0.0.0.0` everywhere.** Can't currently prefer USB and keep Wi-Fi closed.
- **Plaintext on shared L2.** WPA2-PSK is not confidential between clients on the same SSID; open
  networks are wide open.

---

## 5. Recommendations (layered, by leverage)

Ordered so each item is independently shippable; the first three are the "minimum viable for a Wi-Fi
release."

1. **Secure-by-default binding *(highest leverage, least effort)*.** Default the daemon to **USB /
   loopback only**; make Wi-Fi exposure an **explicit opt-in** (daemon flag or config file on the
   device). Pairs naturally with the plugin's Auto/Wi-Fi/USB dropdown: USB stays zero-config; Wi-Fi
   is a deliberate choice the user makes (and is told carries LAN exposure). This alone removes the
   accidental-exposure case entirely.

2. **Pinned-key identity = authentication + identity *(one mechanism, two problems)*.** Following the
   HomeKit/Matter/WireGuard pattern — discovery *locates*, a key *authenticates*:
   - **Locator:** a random **UUIDv4** generated once on the device (`/home/root/inkbridge/identity`,
     `0600`), advertised in the mDNS **TXT `id=`** so the plugin filters discovery to its paired
     device → *PC1 ↔ rMPP1* among several. The id is **public/spoofable — not a credential** (the SoC
     hostname `imx8mm-ferrari` is identical across units and can't disambiguate either; that's why we
     mint our own). **Not MAC** (randomized, spoofable, hardware-leaking, differs per interface).
   - **Identity:** a long-term **P-256 keypair** per side (native `ECDsa` on .NET 8; pure-Rust `p256`
     on the daemon — Matter's choice). **Bootstrap over USB** (trusted, point-to-point): exchange and
     **pin** public keys on first cable connect — PC stores `{id → device_pubkey}` in `inkbridge.json`,
     daemon allow-lists the PC key (`/home/root/inkbridge/authorized_keys`).
   - **Per-connection mutual signed-nonce challenge-response** on every handshake
     (`IBR1`/`IBT1`/`IBCP`/`IBCS`): ECDSA over (nonce ‖ peer-nonce ‖ channel-tag). **Reject an
     unknown/invalid signer immediately — before** `client_in()` / thread work / wakelock.
   - A spoofed mDNS/beacon record then at worst causes a *failed* connect, never a capture.
   See [`wifi-connectivity-feasibility.md`](./wifi-connectivity-feasibility.md) §5.

3. **Bound concurrency.** Cap connections per port (e.g. 2), rate-limit accepts, and **acquire the
   wakelock only for authenticated clients**. Closes the thread-storm and battery-drain DoS.

4. **Encrypt the streams.** Wrap pen/touch (and control) in **TLS (`rustls`)** or a lightweight
   **Noise / ChaCha20-Poly1305** channel keyed by the pairing secret. **Mandatory on Wi-Fi**
   (handwriting/touch confidentiality), optional on USB. This is the privacy upgrade that should
   follow the auth work quickly.

5. **Gate the touch trigger on auth.** Count only **authenticated** `IBCS` subscribers toward
   `app_subs`, and require publisher auth before accepting `config`/`status`. Removes the
   rogue-subscriber forced-touch-streaming and config-injection vectors (§3.3).

6. **Beacon hygiene.** Include the non-secret device id so plugins react only to their paired device;
   optionally **HMAC the beacon** to stop spoofed wake. Never put a secret in the broadcast.

7. **Document the exposure.** Whatever we automate, state plainly in the README/installer that Wi-Fi
   mode places an input stream on the LAN and what protects it.

---

## 6. What's safe to leave for now

- **USB-only users are unaffected** — none of the above is needed for the cable path; it stays
  zero-config. The work is scoped to *enabling Wi-Fi safely*, not retrofitting the existing USB flow.
- **RCE hardening** is low priority: parsing is safe-Rust and bounded. The right mitigation for the
  enlarged attack surface is **don't accept unauthenticated peers** (#1–#3), not rewriting parsers.
- **WoWLAN / cold-sleep wake** is explicitly out of scope (the tablet is awake when you draw); it is
  not a security item.

---

## 7. Mapping to the build order

These map onto [`wifi-connectivity-feasibility.md`](./wifi-connectivity-feasibility.md) §10 build
order steps **5 (pairing + identity)** and **6 (security hardening: opt-in binding, conn caps,
wakelock-for-authed, app_subs gating, then encryption)**. The current implementation pass (latency
metric fix + Auto/Wi-Fi/USB dropdown, steps 2–3) is **UX/plumbing only and does not itself widen the
attack surface** — it selects *which already-open port* to connect to. Shipping Wi-Fi as a
*recommended default*, however, is gated on steps 5–6 here.
