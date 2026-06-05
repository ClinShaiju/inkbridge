# Touch via an OTD Output-Mode Plugin — Feasibility (addendum)

Follow-up to `docs/touch-feasibility.md`, which concluded "OTD can't do multitouch → use a
standalone `InjectTouchInput` sidecar." This addendum answers the narrower question: **can an
*enhanced OTD output-mode plugin* carry touch instead?** Answer, after reflecting the actual
installed API:

> **OTD models touch on the *input/report* side (it has `ITouchReport`/`TouchPoint`), but its
> *output* side is single-pointer only. So an OTD plugin is an excellent host for the *gesture*
> mode (with near-zero new code), and a poor host for *true multitouch* passthrough (you'd
> re-implement the whole injector inside OTD and inherit its lifecycle fragility for no gain).**

All API facts below are from reflecting the **installed** `OpenTabletDriver.Plugin` **0.6.7**
(`net8.0`, the version `Inkbridge.csproj` references and OTD on this machine runs).

---

## 1. What OTD 0.6.7 actually exposes (verified by reflection)

**Input / report side — touch IS modelled:**

```
OpenTabletDriver.Plugin.Tablet.Touch.ITouchReport : IDeviceReport
    TouchPoint[] Touches
OpenTabletDriver.Plugin.Tablet.Touch.TouchPoint        // class; public fields TouchID + Position(Vector2)
```

So OTD can represent an arbitrary set of contacts in one report. A report parser can emit
`ITouchReport` and it flows through the pipeline like any `IDeviceReport`.

**Output-mode side — a plugin sees every report:**

```
IOutputMode : IPipelineElement<IDeviceReport>, IDisposable
    void Read(IDeviceReport)          // ← receives EVERY report, including ITouchReport
    IList<...> Elements               // filter pipeline
    Matrix3x2 TransformationMatrix    // the area transform (single-point oriented)
    TabletReference Tablet
OutputMode (base) : adds Consume(IDeviceReport)
AbsoluteOutputMode : OutputMode { Area Input; Area Output; IAbsolutePointer Pointer; AreaClipping; AreaLimiting }
RelativeOutputMode : OutputMode { ... IRelativePointer ... }
```

**Output / platform-pointer side — single pointer ONLY:**

```
Platform.Pointer.IAbsolutePointer   { void SetPosition(Vector2) }
Platform.Pointer.IRelativePointer   { ... Translate(Vector2) }
Platform.Pointer.IPressureHandler   { void SetPressure(float) }
Platform.Pointer.ISynchronousPointer{ void Reset(); void Flush() }
```

There is **no** `ITouchPointer`, no multi-contact pointer, nothing that injects more than one
position. `AbsoluteOutputMode` is hardwired to one `IAbsolutePointer`. And the built-in
Absolute/Relative modes only act on **position reports** (`IAbsolutePositionReport`); they
**ignore `ITouchReport`** entirely — a touch report is dropped unless a *custom* output mode
handles it.

Two consequences:

1. To do **anything** with touch in OTD you must write a **custom `IOutputMode`** (or a tool) that
   pattern-matches `report is ITouchReport`. The base classes won't help.
2. To put pixels on screen as **real multitouch**, that custom code must **P/Invoke
   `InjectTouchInput` itself** — OTD's pointer layer offers no assistance. The `TransformationMatrix`
   is available (so you could reuse the area transform per contact), but contact management,
   injection, gesture timing, etc. are all yours.

---

## 2. Path A — custom output mode that injects real multitouch

**Verdict: technically possible, but strictly worse than the standalone sidecar. Not recommended.**

You *can* write `class InkbridgeTouchMode : IOutputMode` whose `Read()` does:

```csharp
public void Read(IDeviceReport report) {
    if (report is ITouchReport t) {
        // map each t.Touches[i].Position through TransformationMatrix → screen px
        // diff against previous contact set → DOWN/UPDATE/UP
        InjectTouchInput(...);   // your own user32 P/Invoke
    }
}
```

But weigh it:

- **Zero leverage from OTD.** You re-implement coordinate mapping for N contacts, contact
  lifecycle, and the entire `InitializeTouchInjection`/`InjectTouchInput` flow — i.e. the sidecar's
  whole body — just hosted inside OTD's process.
- **You inherit OTD's lifecycle fragility.** The pen path already fights OTD's
  detect/settings-apply ordering: `InkbridgeTool.cs` must *not* call `Detect()` on re-init and the
  user must "apply settings twice" or the OutputMode binds to the wrong device and every report is
  dropped (see the comments there and the `otd-output-detect-orphan-fix` note). Putting touch
  injection on the same pipeline means the same re-bind hazards, plus output-mode/profile coupling.
- **One output mode per device tree.** OTD binds a single output mode per profile. The pen already
  uses an absolute pen mode. A touch output mode therefore can't coexist on the same endpoint — it
  needs a **second OTD tablet** (separate endpoint/spec/parser) so OTD treats touch as its own
  "tablet" with its own profile + output mode. Doable, but it's extra surface for no benefit over a
  sidecar.
- **The area UI doesn't fit.** OTD's Input/Output `Area` mapping is designed for one absolute
  pointer; it won't meaningfully configure a 10-contact touch surface.

Net: a custom multitouch output mode is **the sidecar's code in a more fragile host**. If we ever
do true multitouch, keep it in the standalone `inkbridge-touch.exe` (decoupled from OTD's lifetime
and from the pen). The earlier doc's conclusion stands.

---

## 3. Path B — gesture mode through OTD's touch ecosystem (the real win)

**Verdict: this is where an OTD plugin shines — emit `ITouchReport` and reuse OTD's binding +
touch-gesture machinery for near-zero custom Windows code.**

Because OTD already has `ITouchReport`/`TouchPoint` and a mature **bindings** system (keystrokes,
mouse buttons, wheel, plugin actions), the gesture fallback maps onto OTD cleanly:

- Expose touch as a **second inkbridge endpoint** (a `tablet-spec` entry with a touch
  `ReportParser`) that parses the daemon's touch frames into `ITouchReport` (one `TouchPoint` per
  active slot, `TouchID = slot/tracking-id`, `Position` = mapped coords).
- Use the established external **Touch Gestures** OTD plugin (it consumes `ITouchReport` and turns
  multi-finger taps / swipes / pinch / pan into OTD bindings). Then bind, in OTD's own UI:
  2-finger tap → `Ctrl+Z`, 3-finger tap → `Ctrl+Y`, pinch → `Ctrl`+wheel (zoom), 2-finger drag →
  scroll, 3-finger swipe → page back/forward, etc. — all configurable, no recompile.
- If we'd rather not depend on the external plugin, a small **custom `IOutputMode`/tool** that
  recognizes the same gestures and fires OTD bindings (or `SendInput`) is modest and self-contained
  — far smaller than Path A because it emits discrete keystrokes, not per-contact injection.

This keeps everything inside the OTD app the user already runs and configures, and reuses the
binding UI for remapping. It does **not** give true pinch-zoom-as-touch, but pinch→`Ctrl`+wheel
covers the headline "zoom" use case in most apps.

---

## 4. Recommendation

| Goal | Best vehicle | Why |
|---|---|---|
| **True multitouch** (real WM_POINTER, pinch/pan/rotate as touch) | **Standalone `InjectTouchInput` sidecar** (per `touch-feasibility.md`) | OTD's output layer is single-pointer; an output-mode plugin would re-implement the sidecar inside a more fragile host. |
| **Gestures** (2-finger undo, pinch→zoom-via-Ctrl+wheel, swipe pages) | **OTD plugin path**: emit `ITouchReport` from a 2nd endpoint + Touch Gestures plugin / OTD bindings | OTD natively models touch reports and has a binding/gesture ecosystem → near-zero custom code, user-configurable. |

So "enhanced OTD output modes for touch" is **feasible and recommended *for gestures*, not for
true multitouch.** A pragmatic end state runs **both**: the OTD path for configurable gestures
(ships fast, no Win32 code), and — if/when true multitouch is wanted — the decoupled sidecar for
genuine pinch-zoom. They can coexist: the gesture path consumes `ITouchReport` inside OTD; the
sidecar consumes the raw touch frames on `:9294`. Either can be the sole consumer, or the daemon
can fan out both (gate each independently).

### Open items specific to this path
1. **Touch Gestures plugin ↔ OTD 0.6.7 compat** — confirm the external plugin builds/loads against
   the installed 0.6.7 and that its gesture set covers pinch (not just taps/swipes).
2. **`TouchPoint` exact fields/limits in 0.6.7** — reflection shows it's a class with `TouchID`
   (byte) + `Position`; confirm max contacts the report path tolerates (our source gives 10).
3. **Second-endpoint profile** — verify OTD will accept a second inkbridge "tablet" (touch) with
   its own output mode alongside the pen endpoint without the detect/apply churn the pen path hit
   (`otd-output-detect-orphan-fix`). May need the same idempotent-registration care.

---

### Sources
- Reflection of installed `OpenTabletDriver.Plugin` 0.6.7
  (`~/.nuget/packages/opentabletdriver.plugin/0.6.7/lib/net8.0/OpenTabletDriver.Plugin.dll`):
  confirmed `ITouchReport`/`TouchPoint`, `IOutputMode.Read(IDeviceReport)`, `AbsoluteOutputMode`
  bound to a single `IAbsolutePointer`, and the single-pointer platform layer (`IAbsolutePointer`/
  `IRelativePointer`/`IPressureHandler`/`ISynchronousPointer`) with **no** multitouch pointer.
- `otd-plugin/InkbridgeReport.cs` / `InkbridgeReportParser.cs` / `InkbridgeTool.cs` /
  `tablet-spec.json` — current single-pen endpoint + the detect/double-apply lifecycle caveat.
- `docs/touch-feasibility.md` — device-side touch facts (`event3` 10-pt MT-B) and the sidecar plan.
</content>
