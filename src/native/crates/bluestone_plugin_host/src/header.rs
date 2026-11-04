//! The strip Bluestone draws at the top of every editor window it owns, above the plugin's view: Bypass, Pin, and
//! Keys to Bluestone. Platform-neutral: the state, the layout, hit testing, and a small renderer that draws the strip
//! into pixels (the same 16 × 16 stroke icons as the device panel), which each platform only has to blit. What the
//! buttons show is decided by the host (`set_header`); a click flips the button at once and is queued for the host,
//! which answers with the state it settled on.

use std::cell::{Cell, RefCell};

/// Height of the strip in logical units (points on macOS, 96-dpi pixels on Windows, pixels on Linux).
pub const HEIGHT: f64 = 28.0;
const BUTTON_WIDTH: f64 = 26.0;
const BUTTON_HEIGHT: f64 = 22.0;
const MARGIN: f64 = 6.0;
const GAP: f64 = 4.0;
const RADIUS: f64 = 4.0;
const STROKE: f64 = 1.4;

/// The narrowest content width that still shows every button.
pub const MIN_WIDTH: f64 = MARGIN * 2.0 + BUTTON_WIDTH * 3.0 + GAP * 2.0;

/// The theme's colours (`Bluestone.axaml`), as 0xRRGGBB.
mod colour {
    pub const CHROME: u32 = 0x19191B;
    pub const PANEL: u32 = 0x252527;
    pub const RAISED: u32 = 0x2E2E31;
    pub const SECONDARY: u32 = 0xA1A1A7;
    pub const MUTED: u32 = 0x6E6E75;
    pub const DISABLED: u32 = 0x4E4E54;
    pub const ON_ACCENT: u32 = 0xFFFFFF;
    pub const ACCENT: u32 = 0x4C8DDB;
}

/// A header button, with the id it has in the C ABI and the protocol.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Button {
    Bypass = 1,
    Pin = 2,
    Keys = 3,
}

impl Button {
    pub const ALL: [Button; 3] = [Button::Bypass, Button::Pin, Button::Keys];

    /// What the button does, for tooltips.
    #[cfg_attr(target_os = "linux", allow(dead_code))]
    pub fn label(self, state: ButtonState) -> &'static str {
        match (self, state) {
            (Button::Bypass, ButtonState::On) => "Bypassed: click to turn the device back on",
            (Button::Bypass, _) => "Bypass the device",
            (Button::Pin, ButtonState::On) => "Pinned: stays open when another track is selected",
            (Button::Pin, _) => "Pin: keep this window open when another track is selected",
            (Button::Keys, ButtonState::On) => {
                "Keys go to Bluestone: click to give them back to the plugin"
            }
            (Button::Keys, ButtonState::Unavailable) => {
                "This editor's keys cannot be sent to Bluestone on this system"
            }
            (Button::Keys, _) => "Send this window's keys to Bluestone",
        }
    }
}

/// What one button shows. The numbers are the C ABI's and the protocol's.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ButtonState {
    Hidden = 0,
    Off = 1,
    On = 2,
    /// Shown dimmed; clicks do nothing.
    Unavailable = 3,
}

impl ButtonState {
    pub fn from_raw(value: u32) -> Option<ButtonState> {
        match value {
            0 => Some(ButtonState::Hidden),
            1 => Some(ButtonState::Off),
            2 => Some(ButtonState::On),
            3 => Some(ButtonState::Unavailable),
            _ => None,
        }
    }
}

/// What the header shows. A new window shows nothing until the host says.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct HeaderState {
    pub bypass: ButtonState,
    pub pin: ButtonState,
    pub keys: ButtonState,
}

impl Default for HeaderState {
    fn default() -> Self {
        HeaderState {
            bypass: ButtonState::Hidden,
            pin: ButtonState::Hidden,
            keys: ButtonState::Hidden,
        }
    }
}

impl HeaderState {
    pub fn get(&self, button: Button) -> ButtonState {
        match button {
            Button::Bypass => self.bypass,
            Button::Pin => self.pin,
            Button::Keys => self.keys,
        }
    }

    fn set(&mut self, button: Button, state: ButtonState) {
        match button {
            Button::Bypass => self.bypass = state,
            Button::Pin => self.pin = state,
            Button::Keys => self.keys = state,
        }
    }

    /// The buttons shown, left to right, each with its left edge (logical units). Hidden buttons take no room.
    pub fn layout(&self) -> Vec<(Button, f64)> {
        let mut x = MARGIN;
        let mut shown = Vec::with_capacity(3);
        for button in Button::ALL {
            if self.get(button) != ButtonState::Hidden {
                shown.push((button, x));
                x += BUTTON_WIDTH + GAP;
            }
        }
        shown
    }

    /// The button under a point of the strip (logical units from its top-left corner), whether or not it is clickable.
    pub fn button_at(&self, x: f64, y: f64) -> Option<Button> {
        let top = (HEIGHT - BUTTON_HEIGHT) / 2.0;
        if !(top..top + BUTTON_HEIGHT).contains(&y) {
            return None;
        }
        self.layout()
            .into_iter()
            .find(|(_, left)| (*left..*left + BUTTON_WIDTH).contains(&x))
            .map(|(button, _)| button)
    }

    /// Where a button is (left, top, width, height), for tooltips.
    #[cfg_attr(target_os = "linux", allow(dead_code))]
    pub fn frame(&self, button: Button) -> Option<(f64, f64, f64, f64)> {
        self.layout()
            .into_iter()
            .find(|(b, _)| *b == button)
            .map(|(_, left)| {
                (
                    left,
                    (HEIGHT - BUTTON_HEIGHT) / 2.0,
                    BUTTON_WIDTH,
                    BUTTON_HEIGHT,
                )
            })
    }
}

/// A window's header: what it shows and the clicks waiting for the host. Main thread.
#[derive(Default)]
pub struct Header {
    state: Cell<HeaderState>,
    clicks: RefCell<Vec<(Button, bool)>>,
}

impl Header {
    pub fn state(&self) -> HeaderState {
        self.state.get()
    }

    /// Shows what the host decided. True when that changed anything (the strip needs drawing again).
    pub fn set_state(&self, state: HeaderState) -> bool {
        self.state.replace(state) != state
    }

    /// A click at a point of the strip (logical units). A clickable button flips at once and the wanted state is queued
    /// for the host. True when the strip needs drawing again.
    pub fn click(&self, x: f64, y: f64) -> bool {
        let mut state = self.state.get();
        let Some(button) = state.button_at(x, y) else {
            return false;
        };
        let wanted = match state.get(button) {
            ButtonState::Off => true,
            ButtonState::On => false,
            ButtonState::Hidden | ButtonState::Unavailable => return false,
        };
        state.set(
            button,
            if wanted {
                ButtonState::On
            } else {
                ButtonState::Off
            },
        );
        self.state.set(state);
        self.clicks.borrow_mut().push((button, wanted));
        true
    }

    /// The clicks since the last call: each button with the state the user wants.
    pub fn take_clicks(&self) -> Vec<(Button, bool)> {
        std::mem::take(&mut *self.clicks.borrow_mut())
    }
}

/// How wide the drawn part of the strip is (logical units): the buttons and their margins. The rest is plain
/// background, which a platform may fill itself.
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
pub fn drawn_width(state: &HeaderState) -> f64 {
    state
        .layout()
        .last()
        .map_or(MARGIN, |(_, left)| left + BUTTON_WIDTH + MARGIN)
}

/// The strip's background, 0xRRGGBB.
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
pub const BACKGROUND: u32 = colour::CHROME;

/// Draws the strip, `width` × `height` device pixels at `scale` device pixels per logical unit, as 0x00RRGGBB pixels,
/// row by row from the top. Little-endian, that is the B, G, R, X order Windows DIBs and X11 ZPixmaps use.
pub fn render(state: &HeaderState, width: usize, height: usize, scale: f64) -> Vec<u32> {
    let scale = if scale.is_finite() && scale > 0.0 {
        scale
    } else {
        1.0
    };
    let mut pixels = vec![colour::CHROME; width * height];
    // A one-unit line along the bottom separates the strip from the plugin.
    let line = (scale.round() as usize).clamp(1, height.max(1));
    for row in height.saturating_sub(line)..height {
        pixels[row * width..(row + 1) * width].fill(colour::RAISED);
    }
    let top = (HEIGHT - BUTTON_HEIGHT) / 2.0;
    for (button, left) in state.layout() {
        let shown = state.get(button);
        let (fill, ink) = appearance(button, shown);
        let first_x = ((left * scale).floor() as usize).min(width);
        let last_x = (((left + BUTTON_WIDTH) * scale).ceil() as usize).min(width);
        let first_y = ((top * scale).floor() as usize).min(height);
        let last_y = (((top + BUTTON_HEIGHT) * scale).ceil() as usize).min(height);
        let icon_left = left + (BUTTON_WIDTH - 16.0) / 2.0;
        let icon_top = top + (BUTTON_HEIGHT - 16.0) / 2.0;
        for py in first_y..last_y {
            for px in first_x..last_x {
                // The pixel's centre in logical units, then in the button's and the icon's coordinates.
                let x = (px as f64 + 0.5) / scale;
                let y = (py as f64 + 0.5) / scale;
                let pixel = &mut pixels[py * width + px];
                if let Some(fill) = fill {
                    let inside = -rounded_box(
                        x - left - BUTTON_WIDTH / 2.0,
                        y - top - BUTTON_HEIGHT / 2.0,
                        BUTTON_WIDTH / 2.0,
                        BUTTON_HEIGHT / 2.0,
                        RADIUS,
                    );
                    *pixel = blend(*pixel, fill, (inside * scale + 0.5).clamp(0.0, 1.0));
                }
                let distance = icon_distance(button, x - icon_left, y - icon_top);
                let coverage = ((STROKE / 2.0 - distance) * scale + 0.5).clamp(0.0, 1.0);
                *pixel = blend(*pixel, ink, coverage);
            }
        }
    }
    pixels
}

/// The button's fill (if any) and its icon's colour.
fn appearance(button: Button, state: ButtonState) -> (Option<u32>, u32) {
    match (button, state) {
        (_, ButtonState::Unavailable) => (None, colour::DISABLED),
        // The power icon is lit while the device runs, like the device panel's; bypassed, it goes dark.
        (Button::Bypass, ButtonState::On) => (Some(colour::PANEL), colour::MUTED),
        (Button::Bypass, _) => (Some(colour::PANEL), colour::ACCENT),
        (_, ButtonState::On) => (Some(colour::ACCENT), colour::ON_ACCENT),
        _ => (Some(colour::PANEL), colour::SECONDARY),
    }
}

/// Signed distance from a point to a rounded box centred on the origin (negative inside).
fn rounded_box(x: f64, y: f64, half_width: f64, half_height: f64, radius: f64) -> f64 {
    let qx = x.abs() - half_width + radius;
    let qy = y.abs() - half_height + radius;
    let outside = qx.max(0.0).hypot(qy.max(0.0));
    outside + qx.max(qy).min(0.0) - radius
}

fn segment(x: f64, y: f64, (ax, ay): (f64, f64), (bx, by): (f64, f64)) -> f64 {
    let (dx, dy) = (bx - ax, by - ay);
    let length = dx * dx + dy * dy;
    let t = if length == 0.0 {
        0.0
    } else {
        (((x - ax) * dx + (y - ay) * dy) / length).clamp(0.0, 1.0)
    };
    (x - ax - t * dx).hypot(y - ay - t * dy)
}

fn polyline(x: f64, y: f64, points: &[(f64, f64)]) -> f64 {
    points
        .windows(2)
        .map(|pair| segment(x, y, pair[0], pair[1]))
        .fold(f64::INFINITY, f64::min)
}

/// Distance from a point of the 16 × 16 icon box to the icon's strokes: the device panel's `Icon.Power`, `Icon.Pin`,
/// and `Icon.Keyboard` paths.
fn icon_distance(button: Button, x: f64, y: f64) -> f64 {
    match button {
        Button::Bypass => {
            // M8,2.5 V7.5, and the large arc from (5,4.3) to (11,4.3), radius 4.6, round the bottom.
            let stem = segment(x, y, (8.0, 2.5), (8.0, 7.5));
            let (cx, r) = (8.0, 4.6);
            let cy = 4.3 + (r * r - 9.0f64).sqrt();
            let gap_end = (4.3 - cy).atan2(3.0); // the angle of (11, 4.3), measured from the centre
            let angle = (y - cy).atan2(x - cx);
            // The arc leaves out the top, between the angles of (11, 4.3) and (5, 4.3).
            let in_gap = angle < gap_end && angle > -std::f64::consts::PI - gap_end;
            let arc = if in_gap {
                (x - 5.0).hypot(y - 4.3).min((x - 11.0).hypot(y - 4.3))
            } else {
                ((x - cx).hypot(y - cy) - r).abs()
            };
            stem.min(arc)
        }
        Button::Pin => polyline(x, y, &[(6.0, 2.5), (10.0, 2.5)])
            .min(polyline(
                x,
                y,
                &[
                    (7.0, 2.5),
                    (7.0, 7.0),
                    (4.5, 9.5),
                    (11.5, 9.5),
                    (9.0, 7.0),
                    (9.0, 2.5),
                ],
            ))
            .min(segment(x, y, (8.0, 9.5), (8.0, 14.0))),
        Button::Keys => {
            let frame = polyline(
                x,
                y,
                &[
                    (1.5, 4.0),
                    (14.5, 4.0),
                    (14.5, 12.0),
                    (1.5, 12.0),
                    (1.5, 4.0),
                ],
            );
            let keys = [
                (3.8, 6.3),
                (6.2, 6.3),
                (8.6, 6.3),
                (11.0, 6.3),
                (5.0, 8.3),
                (7.4, 8.3),
                (9.8, 8.3),
                (12.2, 8.3),
            ]
            .iter()
            .map(|&(kx, ky)| segment(x, y, (kx, ky), (kx + 0.2, ky)))
            .fold(f64::INFINITY, f64::min);
            frame
                .min(keys)
                .min(segment(x, y, (5.0, 10.2), (11.0, 10.2)))
        }
    }
}

fn blend(under: u32, over: u32, coverage: f64) -> u32 {
    if coverage <= 0.0 {
        return under;
    }
    if coverage >= 1.0 {
        return over;
    }
    let channel = |shift: u32| {
        let a = ((under >> shift) & 0xFF) as f64;
        let b = ((over >> shift) & 0xFF) as f64;
        ((a + (b - a) * coverage).round() as u32) << shift
    };
    channel(16) | channel(8) | channel(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn all(state: ButtonState) -> HeaderState {
        HeaderState {
            bypass: state,
            pin: state,
            keys: state,
        }
    }

    #[test]
    fn hidden_buttons_take_no_room_and_the_others_close_up() {
        let state = HeaderState {
            bypass: ButtonState::Off,
            pin: ButtonState::Hidden,
            keys: ButtonState::Unavailable,
        };
        let layout = state.layout();
        assert_eq!(
            layout.iter().map(|(b, _)| *b).collect::<Vec<_>>(),
            [Button::Bypass, Button::Keys]
        );
        assert_eq!(layout[1].1, MARGIN + BUTTON_WIDTH + GAP);
        assert_eq!(HeaderState::default().layout(), []);
        assert_eq!(drawn_width(&all(ButtonState::Off)), MIN_WIDTH);
    }

    #[test]
    fn a_click_flips_a_clickable_button_and_is_queued_for_the_host() {
        let header = Header::default();
        header.set_state(HeaderState {
            bypass: ButtonState::Off,
            pin: ButtonState::On,
            keys: ButtonState::Unavailable,
        });
        let middle = HEIGHT / 2.0;
        let x = |button| header.state().frame(button).unwrap().0 + 2.0;

        assert!(header.click(x(Button::Bypass), middle));
        assert!(header.click(x(Button::Pin), middle));
        assert!(!header.click(x(Button::Keys), middle), "unavailable");
        assert!(!header.click(x(Button::Bypass), 1.0), "above the buttons");
        assert!(!header.click(MIN_WIDTH + 50.0, middle), "beside them");

        assert_eq!(header.state().bypass, ButtonState::On);
        assert_eq!(header.state().pin, ButtonState::Off);
        assert_eq!(
            header.take_clicks(),
            [(Button::Bypass, true), (Button::Pin, false)]
        );
        assert!(header.take_clicks().is_empty());
        assert!(!header.set_state(header.state()), "nothing changed");
    }

    #[test]
    fn the_renderer_draws_each_shown_button_and_leaves_the_rest_as_background() {
        let scale = 2.0;
        let (width, height) = ((MIN_WIDTH + 40.0) * scale, HEIGHT * scale);
        let (width, height) = (width as usize, height as usize);
        let state = HeaderState {
            bypass: ButtonState::Off,
            pin: ButtonState::On,
            keys: ButtonState::Unavailable,
        };
        let pixels = render(&state, width, height, scale);
        assert_eq!(pixels.len(), width * height);
        let at = |x: f64, y: f64| pixels[(y * scale) as usize * width + (x * scale) as usize];

        let (pin_left, pin_top, _, _) = state.frame(Button::Pin).unwrap();
        assert_eq!(at(pin_left + 2.0, pin_top + 2.0), colour::ACCENT, "pinned");
        let (bypass_left, bypass_top, _, _) = state.frame(Button::Bypass).unwrap();
        assert_eq!(at(bypass_left + 2.0, bypass_top + 2.0), colour::PANEL);
        // The power icon's stem, lit while the device runs.
        let icon_left = bypass_left + (BUTTON_WIDTH - 16.0) / 2.0;
        let icon_top = bypass_top + (BUTTON_HEIGHT - 16.0) / 2.0;
        assert_eq!(at(icon_left + 8.0, icon_top + 5.0), colour::ACCENT);
        // The power arc's lowest point, and the gap at its top.
        assert_eq!(at(icon_left + 8.0, icon_top + 12.35), colour::ACCENT);
        assert_eq!(at(icon_left + 6.0, icon_top + 3.0), colour::PANEL);
        assert_eq!(at(MIN_WIDTH + 20.0, HEIGHT / 2.0), BACKGROUND);
        assert_eq!(at(MIN_WIDTH + 20.0, HEIGHT - 0.25), colour::RAISED);
    }

    #[test]
    fn the_renderer_survives_odd_sizes() {
        let state = all(ButtonState::On);
        assert!(render(&state, 0, 0, 1.0).is_empty());
        assert_eq!(render(&state, 3, 2, f64::NAN).len(), 6);
        assert_eq!(render(&state, 10, 1, 1.5).len(), 10);
    }
}
