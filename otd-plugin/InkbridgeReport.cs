using System.Numerics;
using OpenTabletDriver.Plugin.Tablet;

namespace Inkbridge
{
    /// <summary>
    /// A single decoded pen report. Implements every report facet OTD's pipeline
    /// inspects: absolute position + pressure + buttons (ITabletReport), tilt
    /// (ITiltReport), and hover/proximity (IProximityReport).
    /// </summary>
    public class InkbridgeReport : ITabletReport, ITiltReport, IProximityReport
    {
        public byte[] Raw { get; set; } = System.Array.Empty<byte>();

        // ITabletReport / IAbsolutePositionReport
        public Vector2 Position { get; set; }
        public uint Pressure { get; set; }
        public bool[] PenButtons { get; set; } = new bool[2];

        // ITiltReport (degrees)
        public Vector2 Tilt { get; set; }

        // IProximityReport
        public bool NearProximity { get; set; }
        public uint HoverDistance { get; set; }
    }
}
