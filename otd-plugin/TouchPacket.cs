using System;

namespace Inkbridge
{
    /// <summary>
    /// Decoder for the inkbridge touch wire packet (see protocol/touch-packet.md).
    /// Fixed 88 bytes, little-endian: an 8-byte header + 10 × 8-byte contact slots.
    /// Single source of truth for the byte layout on the Windows side; must stay in
    /// lockstep with the Rust daemon (daemon/src/touch.rs).
    /// </summary>
    public readonly struct TouchPacket
    {
        public const int Size = 88;
        public const int Slots = 10;

        /// <summary>Raw touch-grid extents (portrait native). See touch-feasibility.md.</summary>
        public const int MaxX = 2064;
        public const int MaxY = 2832;

        public readonly struct Contact
        {
            public readonly bool Active;
            public readonly ushort X;        // 0..2064
            public readonly ushort Y;        // 0..2832
            public readonly byte Pressure;   // 0..255
            public readonly byte Major;      // 0..255 (contact size)

            public Contact(bool active, ushort x, ushort y, byte pressure, byte major)
            {
                Active = active; X = x; Y = y; Pressure = pressure; Major = major;
            }
        }

        public readonly uint TimestampUs;
        /// <summary>0 = portrait native, 1/2/3 = 90/180/270° CW (matches PenPacket.Orientation).</summary>
        public readonly byte Orientation;
        public readonly byte ContactCount;
        /// <summary>Indexed by slot 0..9. Slot index is the stable contact identity.</summary>
        public readonly Contact[] Contacts;

        private TouchPacket(uint ts, byte orientation, byte count, Contact[] contacts)
        {
            TimestampUs = ts; Orientation = orientation; ContactCount = count; Contacts = contacts;
        }

        public static TouchPacket Parse(ReadOnlySpan<byte> b)
        {
            uint ts = (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
            byte orient = b[4];
            byte count = b[5];
            // b[6] = flags (version), b[7] = reserved — not needed by the receiver.

            var contacts = new Contact[Slots];
            for (int i = 0; i < Slots; i++)
            {
                int o = 8 + i * 8;
                // o+0 = slot index (== i, redundant), o+1 = active
                bool active = b[o + 1] != 0;
                ushort x = (ushort)(b[o + 2] | b[o + 3] << 8);
                ushort y = (ushort)(b[o + 4] | b[o + 5] << 8);
                byte pressure = b[o + 6];
                byte major = b[o + 7];
                contacts[i] = new Contact(active, x, y, pressure, major);
            }
            return new TouchPacket(ts, orient, count, contacts);
        }
    }
}
