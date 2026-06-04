# AppLoad Research — On-Device Companion App for inkbridge (rMPP)

Research output for Phase 5 (`appload/`). Sources: the AppLoad framework
([asivery/rm-appload](https://github.com/asivery/rm-appload)), a working reference app
([asivery/appload-rmstream](https://github.com/asivery/appload-rmstream)), and a deployed
real-world app on disk (`H:\Projects\MangaInk`, a complete AppLoad app). Cross-checked against
our own `docs/phase0-findings.md` and `docs/feasibility.md`.

---

## 1. What AppLoad actually is (and the key correction)

AppLoad is **not** a launcher that suspends xochitl. It is a **XOVI extension that injects into
the running `xochitl` process** and loads your app's QML *inside xochitl's own Qt/QML scene*.
Apps appear as windowed or fullscreen surfaces managed by xochitl, rendered through xochitl's
existing e-ink (EPD) pipeline.

**Consequence: AppLoad REQUIRES xochitl to be running.** This directly corrects the assumption in
`PROJECT.md` §3/§5 ("AppLoad app suspends xochitl, takes ownership of EPD") and resolves the
"deferred/unverified" status in `feasibility.md` §6:

- You do **not** write framebuffer/EPD code. Rendering is plain QML; xochitl + Qt drive the
  e-ink panel. No `mxcfb` ioctls, no SWTCON handling, no display ownership to manage.
- You do **not** suspend xochitl — you can't, the app lives inside it.

This is good news and it dovetails with our verified pen-read mechanism (next section).

## 2. Why this is now compatible with our daemon (the important synthesis)

There is an apparent conflict between AppLoad and our Phase 0 finding, and it resolves cleanly:

| Source | Claim |
|---|---|
| `phase0-findings.md` (older) | xochitl `EVIOCGRAB`s `event2`; daemon must **stop/STOP xochitl** to read the pen. |
| Verified memory (newer) | **Never** SIGSTOP xochitl — its systemd `WatchdogSec=60` reboots the device. Instead **read `event2` alongside a running xochitl without grabbing**, while holding a **wakelock** (else autosleep kills the digitizer). |
| AppLoad (this research) | App runs **inside a running xochitl**. |

All three reconcile under the **newer verified model**: xochitl stays up, the daemon reads
`event2` un-grabbed + wakelock, and **because xochitl is alive, the AppLoad companion app can run
at the same time as the daemon streams**. The earlier "stop xochitl" plan would have made the
on-device UI impossible; the verified un-grabbed approach makes it possible. Treat the old
"stop xochitl" notes in feasibility/phase0 as superseded.

**Hard constraint retained:** the AppLoad app must **never open `event2`/evdev itself.** It gets
(a) pen *taps for calibration* as ordinary Qt input events delivered by xochitl to its QML
(MouseArea/TapHandler — no evdev), and (b) live status (connection/latency/area) from the daemon
over IPC. The daemon remains the sole evdev reader.

## 3. App layout & manifest (verified API)

An AppLoad app is a directory under `/home/root/xovi/exthome/appload/<appname>/`:

```
inkbridge/                 (our appload/ folder, deployed to the path above)
├── manifest.json
├── icon.png
├── resources.rcc          (optional: compiled QML bundle; or ship raw .qml files)
├── frontend/
│   └── Main.qml           (root QML — entry point)
└── backend/               (OPTIONAL)
    └── entry              (executable; argv[1] = unix socket path)
```

`manifest.json` fields (all present in MangaInk's working manifest):

```json
{
    "id": "com.inkbridge.companion",
    "name": "inkbridge",
    "loadsBackend": false,
    "entry": "/frontend/Main.qml",
    "canHaveMultipleFrontends": false,
    "supportsScaling": true
}
```

- **id** — unique app id (used as `applicationID` in the QML AppLoad element).
- **name** — launcher display name.
- **entry** — path to root QML, relative to the app dir.
- **loadsBackend** — `true` to have AppLoad spawn `backend/entry`; `false` if no backend (or you
  run your own systemd helper, as MangaInk does).
- **canHaveMultipleFrontends** / **supportsScaling** — windowing behavior.

## 4. Root QML contract

The root QML **must** declare two things AppLoad calls on lifecycle:

```qml
import QtQuick 2.15

Item {
    id: root
    anchors.fill: parent

    signal close            // emit to ask AppLoad to close the app
    function unloading() {}  // AppLoad calls this right before teardown — flush/cleanup here
    // ... UI ...
}
```

(Verbatim pattern from `MangaInk/frontend/Main.qml`.) E-ink UI conventions that MangaInk follows
and we should too: no animations, high-contrast black/white palette, large fonts, full-page
redraws only on state change (matches PROJECT.md "redraw EPD only on config change").

Screen: **2160×2880 px**, 229 PPI, 11.8". Pure QML only (Qt Quick) — no Qt Widgets.

## 5. Frontend ↔ backend IPC — the `net.asivery.AppLoad` module

If you use an AppLoad backend (`loadsBackend: true`), QML talks to it via:

```qml
import net.asivery.AppLoad 1.0

AppLoad {
    id: endpoint
    applicationID: "com.inkbridge.companion"   // must match manifest "id"
    onMessageReceived: (type, contents) => {
        // type: string tag; contents: string (JSON-encoded payload)
    }
}

// send to backend:
endpoint.sendMessage("set_area", JSON.stringify({ x0: .., y0: .., x1: .., y1: .. }))
```

> Note: the framework docs spell the method `sendMesssage` (triple-s) in one place and
> `sendMessage` elsewhere — **verify the exact spelling against the installed module on-device**
> before relying on it.

**Backend protocol** (`backend/entry`, any language; MangaInk's is Python):
- AppLoad starts the backend with **`argv[1]` = path to a temporary AF_UNIX socket** it created.
- The backend `connect()`s to that socket.
- Messages are **newline-delimited JSON**: `{"type": "<tag>", "contents": "<json string>"}`.
- Frontend writes → backend reads, and vice-versa.

MangaInk's `backend/entry` is a clean, copyable reference implementation of this protocol
(`AppLoadBackend` class: `send_message`, `listen`, newline-framed JSON over the unix socket).

## 6. Two viable IPC designs for inkbridge (pick one)

**Option A — AppLoad backend talks to the daemon (recommended).**
`loadsBackend: true`; `backend/entry` is a tiny shim that bridges the AppLoad unix socket to the
inkbridge daemon's own IPC (its config JSON + status socket from PROJECT.md §5/§8). QML stays
pure UI. Clean separation, matches the framework's intended model.

**Option B — No AppLoad backend; QML/daemon share a file + the daemon owns a status socket.**
`loadsBackend: false` (like MangaInk, which instead runs a systemd `epub_server.py`). The
companion would read/write the daemon's config JSON directly and poll a status endpoint. Simpler
to deploy, but pushes logic into QML and duplicates MangaInk's "separate systemd helper" pattern.

Given PROJECT.md already specifies a daemon with a Unix-socket/JSON-config IPC, **Option A** is the
smallest new surface: the AppLoad backend is a ~100-line relay, identical in shape to MangaInk's.

## 7. Deployment (proven path from MangaInk)

MangaInk ships two mechanisms; reuse them:

1. **`deploy.py`** (paramiko/SFTP) — uploads the app tree to
   `/home/root/xovi/exthome/appload/<app>/`, compiles `resources.rcc` with PySide6's `rcc.exe`
   (`rcc -binary application.qrc -o resources.rcc`, must start with magic `qres`), installs any
   systemd helper into **`/opt`** (persistent ext4) because `/etc` is a tmpfs overlay wiped on
   reboot, and re-installs on boot via **`xovi-tripletap` `PRE_START_COMMANDS`**. Restart with
   `systemctl restart xochitl` (detached) or `xovi/start` to reload AppLoad.
2. **`deploy.sh`** — simpler scp-based variant; documents prereqs: developer mode, XOVI + AppLoad
   installed via `vellum add xovi rm-appload`, app dir under the appload path.
3. **`vellum/APKBUILD`** — optional packaging; `depends="rm-appload qt-resource-rebuilder
   remarkable-os>=3.22 !rm1 !rm2"`, installs into the appload path.

`resources.rcc` is **optional** — you can deploy raw `.qml` files (MangaInk's `deploy.py` actually
uploads the raw frontend tree and skips the qrc). For a small companion app, raw files are fine.

## 8. Open items to verify on-device (next session, device plugged in)

1. **AppLoad installed?** `feasibility.md` §6 found no AppLoad in this firmware sweep. Check
   `ls /home/root/xovi/exthome/appload/` and whether XOVI/AppLoad is installed
   (`vellum add xovi rm-appload`). MangaInk's presence suggests the toolchain exists here.
2. **Exact `sendMessage` spelling** of the installed `net.asivery.AppLoad` module.
3. **AppLoad version vs kernel 6.12** on this image (issue #40 notes hook breakage on some
   3.26.x pre-releases — confirm our firmware works).
4. **Concurrent operation:** confirm the daemon reading `event2` (un-grabbed, wakelock held) and
   xochitl+AppLoad rendering coexist with no event loss and no latency spike (PROJECT.md §6,
   Phase 6 validation).
5. **Calibration taps:** confirm pen taps reach the AppLoad QML as Qt input events (MouseArea/
   TapHandler) while the daemon is also reading event2 un-grabbed — i.e. both see the pen.

## 9. Recommended build order for `appload/`

1. Scaffold `appload/manifest.json` + `appload/frontend/Main.qml` (status-only, read from a
   daemon JSON status file) — get it launching in AppLoad first.
2. Add `appload/backend/entry` (Python relay, modeled on MangaInk) bridging AppLoad's unix socket
   to the daemon's IPC. Set `loadsBackend: true`.
3. Add the active-area overlay rectangle + connection/latency badge (redraw on change only).
4. Add corner-tap calibration → push area to daemon → daemon hot-reloads config.
5. Orientation toggle → daemon axis swap.
6. Reuse MangaInk's `deploy.py` (adapted paths) for upload + persistence.

This phase stays **strictly optional / post-MVP** per feasibility — the daemon + OTD plugin + a
host-side config path remain the critical path. But the framework is a fit and the earlier
blockers ("must suspend xochitl", "must write EPD code") were based on a wrong mental model and do
not apply.

---

### Sources
- AppLoad framework — https://github.com/asivery/rm-appload
- Reference app (rmstream) — https://github.com/asivery/appload-rmstream
- On-disk reference app — `H:\Projects\MangaInk` (manifest.json, frontend/Main.qml, backend/entry, deploy.py, deploy.sh, vellum/APKBUILD)
- Our findings — `docs/phase0-findings.md`, `docs/feasibility.md`
