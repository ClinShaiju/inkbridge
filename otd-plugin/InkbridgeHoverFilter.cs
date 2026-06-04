using System;
using System.Diagnostics;
using System.Numerics;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace Inkbridge
{
    /// <summary>
    /// A "1€ filter" (Casiez et al. 2012) smoothing pass for the inkbridge pen.
    ///
    /// The rMPP digitizer scans HOVER far slower than contact (~150-200 Hz vs ~500 Hz
    /// tip-down), and the hover intervals are uneven, so the cursor feels choppy/jittery
    /// while the pen is off the surface. We can't raise the firmware's hover scan rate,
    /// but the 1€ filter evens it out: it low-passes position with a cutoff that RISES
    /// with pen speed. Slow/near-still motion gets heavy smoothing (kills jitter); fast
    /// flicks get almost none (keeps latency negligible — important for osu! aim, where
    /// the cursor is driven entirely by hover).
    ///
    /// By default it touches hover samples only and passes tip-down through untouched, so
    /// drawing pressure strokes keep their full 500 Hz fidelity and gain no lag.
    ///
    /// Enable it in OpenTabletDriver under Filters; it runs PreTransform (raw tablet
    /// units, before area mapping). Tune Beta up if fast moves feel laggy, or MinCutoff
    /// down if slow moves still look jittery.
    /// </summary>
    [PluginName("inkbridge Hover Smoothing")]
    public class InkbridgeHoverFilter : IPositionedPipelineElement<IDeviceReport>
    {
        // Raw-coordinate normaliser so the Hz/Beta sliders read in familiar 1€ ranges
        // regardless of the digitizer's large native units (ABS_X 0..11180, ABS_Y 0..15340).
        private const float Ref = 10000f;

        [SliderProperty("Min cutoff (Hz)", 0.1f, 10f, 1.0f)]
        [ToolTip("Cutoff frequency when the pen is nearly still. Lower = smoother but more\n" +
                 "lag on slow moves. Raise if the cursor feels sluggish; lower if it jitters.")]
        public float MinCutoff { get; set; } = 1.0f;

        [SliderProperty("Beta (speed)", 0f, 2f, 0.5f)]
        [ToolTip("How fast the cutoff opens up as the pen speeds up. Higher = less lag on\n" +
                 "quick moves (but less smoothing). This is the main knob for osu! aim feel.")]
        public float Beta { get; set; } = 0.5f;

        [SliderProperty("Derivative cutoff (Hz)", 0.1f, 10f, 1.0f)]
        [ToolTip("Cutoff for the speed estimate itself. 1 Hz is fine for almost everyone.")]
        public float DerivativeCutoff { get; set; } = 1.0f;

        [BooleanProperty("Hover only",
            "Smooth only while the pen is hovering (pressure 0). Leave on so tip-down\n" +
            "strokes keep their full report rate and gain no lag.")]
        public bool HoverOnly { get; set; } = true;

        public PipelinePosition Position => PipelinePosition.PreTransform;

        public event Action<IDeviceReport>? Emit;

        private readonly OneEuro _x = new();
        private readonly OneEuro _y = new();
        private long _lastTicks;
        private bool _primed;

        public void Consume(IDeviceReport value)
        {
            // Reset on proximity loss so re-entry doesn't smear from the pen's old spot.
            if (value is IProximityReport prox && !prox.NearProximity)
            {
                _primed = false;
                Emit?.Invoke(value);
                return;
            }

            bool contact = value is ITabletReport tr && tr.Pressure > 0;
            if (value is not IAbsolutePositionReport pos || (HoverOnly && contact))
            {
                // Not filtering this sample; drop our history so the next filtered sample
                // (e.g. lifting back into hover) starts clean instead of snapping.
                _primed = false;
                Emit?.Invoke(value);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (!_primed)
            {
                _x.Reset(pos.Position.X / Ref);
                _y.Reset(pos.Position.Y / Ref);
                _lastTicks = now;
                _primed = true;
                Emit?.Invoke(value);
                return;
            }

            float dt = (float)((now - _lastTicks) / (double)Stopwatch.Frequency);
            _lastTicks = now;
            if (dt <= 0f || dt > 0.5f) // bogus/huge gap: re-prime rather than spike the derivative
            {
                _x.Reset(pos.Position.X / Ref);
                _y.Reset(pos.Position.Y / Ref);
                Emit?.Invoke(value);
                return;
            }

            float fx = _x.Filter(pos.Position.X / Ref, dt, MinCutoff, Beta, DerivativeCutoff) * Ref;
            float fy = _y.Filter(pos.Position.Y / Ref, dt, MinCutoff, Beta, DerivativeCutoff) * Ref;
            pos.Position = new Vector2(fx, fy);

            Emit?.Invoke(value);
        }

        /// <summary>Per-axis 1€ filter state (one low-pass on value, one on its derivative).</summary>
        private sealed class OneEuro
        {
            private float _xPrev;
            private float _dxPrev;

            public void Reset(float x)
            {
                _xPrev = x;
                _dxPrev = 0f;
            }

            public float Filter(float x, float dt, float minCutoff, float beta, float dCutoff)
            {
                float dx = (x - _xPrev) / dt;
                float edx = LowPass(dx, _dxPrev, Alpha(dCutoff, dt));
                _dxPrev = edx;

                float cutoff = minCutoff + beta * Math.Abs(edx);
                float hat = LowPass(x, _xPrev, Alpha(cutoff, dt));
                _xPrev = hat;
                return hat;
            }

            private static float Alpha(float cutoff, float dt)
            {
                float r = 2f * (float)Math.PI * cutoff * dt;
                return r / (r + 1f);
            }

            private static float LowPass(float value, float prev, float alpha)
                => alpha * value + (1f - alpha) * prev;
        }
    }
}
