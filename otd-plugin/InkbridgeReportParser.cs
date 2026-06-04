using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using OpenTabletDriver.Plugin.Tablet;

namespace Inkbridge
{
    /// <summary>
    /// Decodes an 18-byte inkbridge PenPacket into an <see cref="InkbridgeReport"/>.
    /// Referenced by name from tablet-spec.json (DigitizerIdentifiers[].ReportParser)
    /// and constructed via OTD's PluginManager, hence the parameterless-ctor hint.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public class InkbridgeReportParser : IReportParser<IDeviceReport>
    {
        public IDeviceReport Parse(byte[] report)
        {
            var p = PenPacket.Parse(report);

            return new InkbridgeReport
            {
                Raw = report,
                Position = new Vector2(p.X, p.Y),
                Pressure = p.Pressure,
                PenButtons = new[]
                {
                    (p.Buttons & PenPacket.BtnStylus1) != 0,
                    (p.Buttons & PenPacket.BtnStylus2) != 0,
                },
                Tilt = new Vector2(p.TiltX / 100f, p.TiltY / 100f), // centideg -> deg
                NearProximity = (p.Buttons & PenPacket.BtnToolPen) != 0,
                HoverDistance = p.Distance,
            };
        }
    }
}
