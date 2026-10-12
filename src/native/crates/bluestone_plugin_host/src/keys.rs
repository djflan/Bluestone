//! Keys pressed in a plugin editor whose keyboard goes to Bluestone instead of the plugin. They cross the process
//! boundary as USB HID keyboard usage IDs (a published, platform-neutral numbering) with a small modifier mask, so the
//! host never sees AppKit key codes or Win32 virtual keys. Only the keys Bluestone's shortcuts and computer-keyboard MIDI
//! use are mapped; anything else is 0 and is dropped by the host.

/// Modifier bits sent with each key.
#[cfg_attr(target_os = "linux", allow(dead_code))]
pub mod modifier {
    pub const SHIFT: u32 = 1;
    pub const CONTROL: u32 = 2;
    pub const ALT: u32 = 4;
    pub const META: u32 = 8;
}

/// A key pressed (or released) while an editor's keys go to the host.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct KeyEvent {
    /// The USB HID keyboard usage ID (page 7), or 0 for a key Bluestone does not use.
    pub usage: u32,
    /// [`modifier`] bits held with it.
    pub modifiers: u32,
    pub down: bool,
    /// A repeat from holding the key down.
    pub repeat: bool,
}

impl KeyEvent {
    /// The notification value: modifier bits, 256 when down, 512 when a repeat.
    pub fn packed(&self) -> f64 {
        (self.modifiers | if self.down { 256 } else { 0 } | if self.repeat { 512 } else { 0 })
            as f64
    }
}

/// The HID usage for an AppKit virtual key code (kVK_*), or 0.
#[cfg_attr(not(target_os = "macos"), allow(dead_code))]
pub fn from_macos(code: u16) -> u32 {
    match code {
        0x00 => 4,  // A
        0x0B => 5,  // B
        0x08 => 6,  // C
        0x02 => 7,  // D
        0x0E => 8,  // E
        0x03 => 9,  // F
        0x05 => 10, // G
        0x04 => 11, // H
        0x22 => 12, // I
        0x26 => 13, // J
        0x28 => 14, // K
        0x25 => 15, // L
        0x2E => 16, // M
        0x2D => 17, // N
        0x1F => 18, // O
        0x23 => 19, // P
        0x0C => 20, // Q
        0x0F => 21, // R
        0x01 => 22, // S
        0x11 => 23, // T
        0x20 => 24, // U
        0x09 => 25, // V
        0x0D => 26, // W
        0x07 => 27, // X
        0x10 => 28, // Y
        0x06 => 29, // Z
        0x12 => 30, // 1
        0x13 => 31, // 2
        0x14 => 32, // 3
        0x15 => 33, // 4
        0x17 => 34, // 5
        0x16 => 35, // 6
        0x1A => 36, // 7
        0x1C => 37, // 8
        0x19 => 38, // 9
        0x1D => 39, // 0
        0x24 => 40, // Return
        0x35 => 41, // Escape
        0x33 => 42, // Delete (backspace)
        0x30 => 43, // Tab
        0x31 => 44, // Space
        0x1B => 45, // -
        0x18 => 46, // =
        0x21 => 47, // [
        0x1E => 48, // ]
        0x2A => 49, // \
        0x29 => 51, // ;
        0x27 => 52, // '
        0x32 => 53, // `
        0x2B => 54, // ,
        0x2F => 55, // .
        0x2C => 56, // /
        0x7A => 58, // F1
        0x78 => 59,
        0x63 => 60,
        0x76 => 61,
        0x60 => 62,
        0x61 => 63,
        0x62 => 64,
        0x64 => 65,
        0x65 => 66,
        0x6D => 67,
        0x67 => 68,
        0x6F => 69, // F12
        0x73 => 74, // Home
        0x74 => 75, // Page Up
        0x75 => 76, // Forward Delete
        0x77 => 77, // End
        0x79 => 78, // Page Down
        0x7C => 79, // Right
        0x7B => 80, // Left
        0x7D => 81, // Down
        0x7E => 82, // Up
        _ => 0,
    }
}

/// The HID usage for a Win32 virtual-key code (VK_*), or 0.
#[cfg_attr(not(windows), allow(dead_code))]
pub fn from_windows(vk: u32) -> u32 {
    match vk {
        0x41..=0x5A => vk - 0x41 + 4,  // A–Z
        0x31..=0x39 => vk - 0x31 + 30, // 1–9
        0x30 => 39,                    // 0
        0x0D => 40,                    // Return
        0x1B => 41,                    // Escape
        0x08 => 42,                    // Backspace
        0x09 => 43,                    // Tab
        0x20 => 44,                    // Space
        0xBD => 45,                    // -
        0xBB => 46,                    // =
        0xDB => 47,                    // [
        0xDD => 48,                    // ]
        0xDC => 49,                    // \
        0xBA => 51,                    // ;
        0xDE => 52,                    // '
        0xC0 => 53,                    // `
        0xBC => 54,                    // ,
        0xBE => 55,                    // .
        0xBF => 56,                    // /
        0x70..=0x7B => vk - 0x70 + 58, // F1–F12
        0x24 => 74,                    // Home
        0x21 => 75,                    // Page Up
        0x2E => 76,                    // Delete
        0x23 => 77,                    // End
        0x22 => 78,                    // Page Down
        0x27 => 79,                    // Right
        0x25 => 80,                    // Left
        0x28 => 81,                    // Down
        0x26 => 82,                    // Up
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn both_platforms_agree_on_the_keys_bluestone_uses() {
        // Space, Z, X, 1, Escape, Left, F12, and the backtick (computer-keyboard toggle).
        let pairs = [
            (0x31, 0x20),
            (0x06, 0x5A),
            (0x07, 0x58),
            (0x12, 0x31),
            (0x35, 0x1B),
            (0x7B, 0x25),
            (0x6F, 0x7B),
            (0x32, 0xC0),
        ];
        for (mac, win) in pairs {
            assert_ne!(from_macos(mac), 0);
            assert_eq!(
                from_macos(mac),
                from_windows(win),
                "mac {mac:#x} / win {win:#x}"
            );
        }
    }

    #[test]
    fn every_letter_and_digit_maps_to_its_usage() {
        let letters = "abcdefghijklmnopqrstuvwxyz";
        for (i, c) in letters.chars().enumerate() {
            let vk = c.to_ascii_uppercase() as u32;
            assert_eq!(from_windows(vk), 4 + i as u32, "{c}");
        }
        assert_eq!(from_windows(0x30), 39);
        assert_eq!(from_windows(0x39), 38);
        let mac_letters: Vec<u32> = [
            0x00, 0x0B, 0x08, 0x02, 0x0E, 0x03, 0x05, 0x04, 0x22, 0x26, 0x28, 0x25, 0x2E, 0x2D,
            0x1F, 0x23, 0x0C, 0x0F, 0x01, 0x11, 0x20, 0x09, 0x0D, 0x07, 0x10, 0x06,
        ]
        .iter()
        .map(|&c| from_macos(c))
        .collect();
        assert_eq!(mac_letters, (4..30).collect::<Vec<u32>>());
    }

    #[test]
    fn unknown_keys_are_zero_and_packing_keeps_the_flags() {
        assert_eq!(from_macos(0x3F), 0);
        assert_eq!(from_windows(0x5B), 0);
        let key = KeyEvent {
            usage: 44,
            modifiers: modifier::SHIFT | modifier::META,
            down: true,
            repeat: true,
        };
        assert_eq!(key.packed(), (1 | 8 | 256 | 512) as f64);
    }
}
