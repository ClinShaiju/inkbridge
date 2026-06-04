//! Wire packet construction. Must stay byte-for-byte identical to
//! `protocol/packet.md` and the Windows decoder in `otd-plugin/PenPacket.cs`.

/// Fixed PenPacket size, little-endian, one per evdev SYN_REPORT.
pub const SIZE: usize = 18;

/// Current pen state, accumulated from evdev events between SYN_REPORTs.
#[derive(Default, Clone)]
pub struct PenState {
    x: u16,
    y: u16,
    pressure: u16,
    distance: u16,
    tilt_x: i16,
    tilt_y: i16,
    touch: bool,
    stylus1: bool,
    stylus2: bool,
    tool_pen: bool,
    eraser: bool,
}

impl PenState {
    /// Apply an EV_ABS event. Codes per docs/phase0-findings.md (event2).
    pub fn update_abs(&mut self, code: u16, val: i32) {
        match code {
            0 => self.x = clamp_u16(val),         // ABS_X        0..11180
            1 => self.y = clamp_u16(val),         // ABS_Y        0..15340
            24 => self.pressure = clamp_u16(val), // ABS_PRESSURE 0..4096
            25 => self.distance = clamp_u16(val), // ABS_DISTANCE 0..65535
            26 => self.tilt_x = clamp_i16(val),   // ABS_TILT_X   -9000..9000
            27 => self.tilt_y = clamp_i16(val),   // ABS_TILT_Y
            _ => {}
        }
    }

    /// Apply an EV_KEY (button) event.
    pub fn update_key(&mut self, code: u16, val: i32) {
        let pressed = val != 0;
        match code {
            330 => self.touch = pressed,   // BTN_TOUCH
            331 => self.stylus1 = pressed, // BTN_STYLUS
            332 => self.stylus2 = pressed, // BTN_STYLUS2
            320 => self.tool_pen = pressed, // BTN_TOOL_PEN (in range)
            321 => self.eraser = pressed,  // BTN_TOOL_RUBBER
            _ => {}
        }
    }

    /// True while the pen (or eraser end) is within range of the digitizer. The daemon
    /// only resends keepalive state while this holds, so once the pen is lifted away it
    /// stops re-asserting the absolute cursor position and the physical mouse is free —
    /// matching how a real absolute tablet behaves.
    pub fn in_range(&self) -> bool {
        self.tool_pen || self.eraser
    }

    /// Serialize the current state into an 18-byte little-endian PenPacket. `orientation`
    /// (0..3 = portrait/90/180/270) rides in flags bits 6-7 (see protocol/packet.md).
    pub fn serialize(&self, ts_us: u32, orientation: u8, buf: &mut [u8; SIZE]) {
        buf[0..4].copy_from_slice(&ts_us.to_le_bytes());
        buf[4..6].copy_from_slice(&self.x.to_le_bytes());
        buf[6..8].copy_from_slice(&self.y.to_le_bytes());
        buf[8..10].copy_from_slice(&self.pressure.to_le_bytes());
        buf[10..12].copy_from_slice(&self.distance.to_le_bytes());
        buf[12..14].copy_from_slice(&self.tilt_x.to_le_bytes());
        buf[14..16].copy_from_slice(&self.tilt_y.to_le_bytes());

        let mut buttons = 0u8;
        if self.touch {
            buttons |= 1 << 0;
        }
        if self.stylus1 {
            buttons |= 1 << 1;
        }
        if self.stylus2 {
            buttons |= 1 << 2;
        }
        if self.tool_pen {
            buttons |= 1 << 3;
        }
        if self.eraser {
            buttons |= 1 << 4;
        }
        buf[16] = buttons;

        // flags: low nibble = version 1; bit4 tilt_valid; bit5 dist_valid; bits6-7 orientation
        buf[17] = 0x01 | 0x10 | 0x20 | ((orientation & 0x03) << 6);
    }
}

fn clamp_u16(v: i32) -> u16 {
    v.clamp(0, u16::MAX as i32) as u16
}

fn clamp_i16(v: i32) -> i16 {
    v.clamp(i16::MIN as i32, i16::MAX as i32) as i16
}
