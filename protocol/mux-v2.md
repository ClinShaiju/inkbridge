# inkbridge wire protocol — v2 muxed transport

Authoritative description of the **v2** transport: the three v1 TCP ports (pen `:9292`, control
`:9293`, touch `:9294`) are collapsed into **one muxed TCP "data" connection on `:9292`**. The UDP
presence beacon (`:9291`) is unchanged. Net result: **2 ports** (TCP 9292 data + UDP 9291 beacon).

Implemented by `daemon/src/mux.rs` (+ `pen.rs`, `touch.rs`, `control.rs`, `crypto.rs`) and the
plugin's `otd-plugin/ConnectionManager.cs`. Plugin and daemon ship together — this is a flag-day wire
change, no v1 back-compat.

## Ports

```
UDP 9291  presence beacon   (unchanged — beacon.rs / Reconnect.cs; payload IBR1‖id‖HMAC)
TCP 9292  muxed data         (pen + touch + control as channels of one connection)
```

## Two connection kinds on :9292

The daemon branches on the peer address at accept time:

### Remote PC plugin (USB subnet / Wi-Fi) — authenticated + encrypted

1. Daemon writes the 4-byte hello **`IBMX`** (v2 magic; distinct from v1 `IBR1`/`IBT1`/`IBCP` so a
   captured v1 signature can't be replayed and the wire version is unambiguous).
2. P-256 mutual handshake (`auth.rs` / `AuthClient`), keyed with tag `IBMX`. Identical to v1 except
   for the tag. Yields one `Session` (ECDH → HKDF → AES-256-GCM), split into a send half (shared by
   the writer threads behind a mutex) and a recv half (owned by the single reader thread).
3. Every subsequent message is one AES-GCM record `[u32 len][ciphertext‖tag]` whose **plaintext is
   `[channel(1)][payload…]`**:

   | channel | name    | direction   | payload                                   |
   |---------|---------|-------------|-------------------------------------------|
   | 0       | control | both ways   | JSON line (see below)                     |
   | 1       | pen     | device→PC   | 18-byte `PenPacket` (`protocol/packet.md`)|
   | 2       | touch   | device→PC   | 88-byte `TouchPacket` (`protocol/touch-packet.md`) |

   The channel byte is inside the authenticated plaintext, so it's covered by GCM. The encryption is
   otherwise unchanged from v1: one key, one per-direction counter for the whole connection.

   **Invariant:** pen and touch are strictly device→PC; *all* PC→device traffic is channel 0.

### On-device AppLoad app (loopback `127.0.0.1`) — plaintext, line-JSON

The daemon special-cases loopback: no `IBMX`, no handshake, no encryption. The app sends the role line
`IBCS\n` and then reads newline-delimited JSON — exactly the v1 `:9293` subscriber protocol, just moved
onto `:9292`. This *is* control channel 0 in the clear (local/trusted; it has no key to do ECDH). Only
the port changed in `appload/backend/entry`.

## Control channel (channel 0) messages

JSON lines, classified by substring (std-only, no serde), matching v1's control plane.

**PC → daemon:**
- `{"type":"sub","ch":"pen"}` — start the pen reader. Sent when the OTD endpoint opens.
- `{"type":"sub","ch":"touch","always_on":<bool>,"palm":<bool>}` — start the touch reader with its
  options. `always_on` = stream even when no on-device subscriber; `palm` = pen-priority palm
  rejection (default true). Re-sending with changed fields updates the options live (no unsub/resub).
- `{"type":"unsub","ch":"pen"|"touch"}` — stop that reader.
- `{"type":"ping","ts":N}` — latency probe (daemon echoes as `pong`).
- `{"type":"config","data":{…}}` — active-area mapping; stored and fanned to loopback subscribers.
- `{"type":"status","data":{…}}` — link status; recorded for the staleness broadcaster.

**daemon → PC:**
- `{"type":"beaconkey","key":"…","id":"…"}` — sent once right after the handshake (so the plugin can
  verify the presence beacon's HMAC). Was the first encrypted record on v1's control plane.
- `{"type":"pong","ts":N}` — echo of a ping.

**daemon → loopback subscribers:** `config` (relayed) and `status` (broadcast each second, or
`disconnected` when the PC heartbeat goes stale within ~3 s).

## Concurrency model (daemon)

One connection = one inbound reader thread (owns the recv half) + on-demand pen/touch reader threads
(each writes framed records through the shared `Arc<Mutex<SendHalf>>`, so records never interleave on
the wire). The mutex makes the GCM send-counter increment and the framed socket write atomic across
writers. Pen/touch readers stop on `unsub`, on connection teardown (`conn_alive` flips false), or on a
write error. The wakelock is taken once for the whole connection (not per stream).

## Migration notes

- v1's separate `IBR1`/`IBT1`/`IBCP` handshakes and the touch "options byte" (sent after the touch
  hello) are gone; touch options now ride the `sub touch` message on channel 0.
- The pen path is no longer polled for socket disconnect (inbound bytes are control records on the
  shared socket); the reader thread detects EOF and flips `conn_alive`.
