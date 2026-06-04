using System;

namespace Inkbridge
{
    /// <summary>
    /// Decoder for the inkbridge wire packet (see protocol/packet.md).
    /// Fixed 18 bytes, little-endian. This is the single source of truth for the
    /// byte layout on the Windows side; it must stay in lockstep with the Rust daemon.
    /// </summary>
    public readonly struct PenPacket
    {
        public const int Size = 18;

        // buttons bitfield
        public const byte BtnTouch   = 1 << 0; // BTN_TOUCH (tip on surface)
        public const byte BtnStylus1 = 1 << 1; // BTN_STYLUS
        public const byte BtnStylus2 = 1 << 2; // BTN_STYLUS2
        public const byte BtnToolPen = 1 << 3; // BTN_TOOL_PEN (in range / hovering)
        public const byte BtnEraser  = 1 << 4; // BTN_TOOL_RUBBER

        public readonly uint  TimestampUs;
        public readonly ushort X;          // 0..11180
        public readonly ushort Y;          // 0..15340
        public readonly ushort Pressure;   // 0..4096
        public readonly ushort Distance;   // 0..65535 (hover)
        public readonly short  TiltX;      // -9000..9000 centidegrees
        public readonly short  TiltY;
        public readonly byte   Buttons;
        public readonly byte   Flags;

        /// <summary>Screen orientation from flags bits 6-7: 0=portrait native, 1/2/3 = 90/180/270° CW.</summary>
        public byte Orientation => (byte)((Flags >> 6) & 0x03);

        public PenPacket(uint ts, ushort x, ushort y, ushort pressure, ushort distance,
                         short tiltX, short tiltY, byte buttons, byte flags)
        {
            TimestampUs = ts; X = x; Y = y; Pressure = pressure; Distance = distance;
            TiltX = tiltX; TiltY = tiltY; Buttons = buttons; Flags = flags;
        }

        public static PenPacket Parse(ReadOnlySpan<byte> b)
        {
            return new PenPacket(
                ts:       (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24),
                x:        (ushort)(b[4]  | b[5]  << 8),
                y:        (ushort)(b[6]  | b[7]  << 8),
                pressure: (ushort)(b[8]  | b[9]  << 8),
                distance: (ushort)(b[10] | b[11] << 8),
                tiltX:    (short)(b[12] | b[13] << 8),
                tiltY:    (short)(b[14] | b[15] << 8),
                buttons:  b[16],
                flags:    b[17]);
        }

        public void Write(Span<byte> b)
        {
            b[0] = (byte)TimestampUs;       b[1] = (byte)(TimestampUs >> 8);
            b[2] = (byte)(TimestampUs >> 16); b[3] = (byte)(TimestampUs >> 24);
            b[4] = (byte)X;        b[5] = (byte)(X >> 8);
            b[6] = (byte)Y;        b[7] = (byte)(Y >> 8);
            b[8] = (byte)Pressure; b[9] = (byte)(Pressure >> 8);
            b[10] = (byte)Distance; b[11] = (byte)(Distance >> 8);
            b[12] = (byte)TiltX;   b[13] = (byte)(TiltX >> 8);
            b[14] = (byte)TiltY;   b[15] = (byte)(TiltY >> 8);
            b[16] = Buttons;       b[17] = Flags;
        }
    }
}
