using System;
using System.Diagnostics;
using OpenTabletDriver.Plugin;

namespace Inkbridge
{
    /// <summary>
    /// Direct multitouch + discrete tap commands, done the "additive" way: we recognize a
    /// multi-finger tap on our side and simply never feed Windows the contact pattern that would
    /// make it fire its own gesture (the two-finger press-and-tap right-click). Crucially this adds
    /// NO latency to regular (single-finger) touch — only the SECOND+ finger of a multi-touch is
    /// briefly withheld while it's ambiguous:
    ///
    ///   • 1 finger            → injected immediately (pointer/tap/drag, native).
    ///   • a 2nd finger lands  → withhold injection; it's undecided.
    ///       – fingers move (manipulation) → commit: inject everything → native pinch/pan/zoom.
    ///       – fingers lift quickly, still → it was a tap → CANCEL the withheld/held contacts so
    ///         Windows discards them (no click, no right-click) and fire the command keystroke.
    ///
    /// We MUST withhold (not inject-then-cancel): Windows commits the two-finger press-and-tap
    /// right-click at finger-contact, and POINTER_FLAG_CANCELED on release does NOT retract it, and
    /// there is no OS-level toggle for the touchscreen two-finger right-click. So the only reliable
    /// way to suppress it is to never show Windows the pattern in the first place. Cost: a small
    /// (~CommitMove) engage threshold on two-finger pinch/pan only — single-finger is untouched.
    /// </summary>
    internal sealed class DirectTouchWithTaps : ITouchConsumer
    {
        private const int Slots = TouchPacket.Slots;
        // Per-finger travel (raw touch units, ~11.5/mm) above which a multi-touch is a manipulation
        // (commit → inject) rather than a tap. Low so pan/pinch engages quickly (~2.5 mm dead-zone).
        private const double CommitMove = 30;
        // Max episode duration still counted as a tap, measured from the first finger landing.
        // Generous because two fingers land/lift a little apart — a tight value misses real taps.
        private const long TapMaxMs = 600;

        private readonly TouchInjector _injector;

        // Per-slot landing position + down state, for per-finger movement (not centroid).
        private readonly bool[] _down = new bool[Slots];
        private readonly double[] _sx = new double[Slots];
        private readonly double[] _sy = new double[Slots];

        private enum Phase { Idle, Single, Holding, Committed }
        private Phase _phase = Phase.Idle;
        private int _maxCount;
        private long _startMs;
        private double _maxMove;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public DirectTouchWithTaps(int rotation, int monitor) =>
            _injector = new TouchInjector(rotation, monitor);

        public void OnFrame(in TouchPacket frame)
        {
            int count = 0;
            for (int i = 0; i < Slots; i++)
            {
                var c = frame.Contacts[i];
                if (c.Active)
                {
                    count++;
                    if (!_down[i]) { _down[i] = true; _sx[i] = c.X; _sy[i] = c.Y; }
                    else { double d = Dist(c.X, c.Y, _sx[i], _sy[i]); if (d > _maxMove) _maxMove = d; }
                }
                else _down[i] = false;
            }

            if (count == 0)
            {
                if (_phase == Phase.Holding)
                {
                    ResolveHeldRelease();      // was withheld → tap (cancel + fire) or ambiguous (cancel)
                }
                else if (_phase == Phase.Single || _phase == Phase.Committed)
                {
                    _injector.OnFrame(frame);  // forward the empty frame → injector lifts contacts normally
                }
                ResetEpisode();
                return;
            }

            if (_phase == Phase.Idle)
            {
                _phase = count >= 2 ? Phase.Holding : Phase.Single;
                _maxCount = count;
                _startMs = _clock.ElapsedMilliseconds;
                _maxMove = 0;
            }
            _maxCount = Math.Max(_maxCount, count);

            switch (_phase)
            {
                case Phase.Single:
                    if (count >= 2)
                        _phase = Phase.Holding;     // 2nd finger arrived → freeze (stop injecting)
                    else
                        _injector.OnFrame(frame);   // single finger: immediate, no latency
                    break;

                case Phase.Holding:
                    if (_maxMove > CommitMove)
                    {
                        _phase = Phase.Committed;    // it's a manipulation → start injecting
                        _injector.OnFrame(frame);
                    }
                    // else: withhold — do not inject while it might still be a tap
                    break;

                case Phase.Committed:
                    _injector.OnFrame(frame);        // pass-through (native pinch/pan)
                    break;
            }
        }

        public void Reset()
        {
            _injector.Reset();
            for (int i = 0; i < Slots; i++) _down[i] = false;
            _phase = Phase.Idle;
            _maxMove = 0;
        }

        private void ResolveHeldRelease()
        {
            long dur = _clock.ElapsedMilliseconds - _startMs;
            // Cancel any contact that was injected during the brief Single phase before the 2nd
            // finger arrived, so Windows produces no click/right-click from it.
            _injector.CancelAll();

            bool tap = dur <= TapMaxMs && _maxMove <= CommitMove;
            Log.Write("Inkbridge",
                $"touch held-release: fingers={_maxCount} dur={dur}ms move={_maxMove:F0} tap={tap}",
                LogLevel.Debug);
            if (!tap) return;

            if (_maxCount == 2) { InputKeys.CtrlCombo(InputKeys.VK_Z); Log.Write("Inkbridge", "gesture: 2-finger tap → Undo"); }
            else if (_maxCount == 3) { InputKeys.CtrlCombo(InputKeys.VK_Y); Log.Write("Inkbridge", "gesture: 3-finger tap → Redo"); }
        }

        private void ResetEpisode()
        {
            for (int i = 0; i < Slots; i++) _down[i] = false;
            _phase = Phase.Idle;
            _maxCount = 0;
            _maxMove = 0;
        }

        private static double Dist(double x0, double y0, double x1, double y1)
        {
            double dx = x0 - x1, dy = y0 - y1;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
