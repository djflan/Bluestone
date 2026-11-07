//! Types shared by the C ABI and both plugin formats.

use std::ffi::c_char;
use std::slice;

/// A MIDI 1.0 channel event at a sample offset. `kind` uses the numbers of Bluestone's `PluginEventKind`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct BphEvent {
    pub offset: i32,
    pub kind: u8,
    pub channel: u8,
    pub data1: u8,
    pub data2: u8,
}

pub mod event_kind {
    pub const NOTE_OFF: u8 = 1;
    pub const NOTE_ON: u8 = 2;
    pub const POLY_PRESSURE: u8 = 3;
    pub const CONTROL_CHANGE: u8 = 4;
    pub const PROGRAM_CHANGE: u8 = 5;
    pub const CHANNEL_PRESSURE: u8 = 6;
    pub const PITCH_BEND: u8 = 7;
}

/// A normalized parameter value that takes effect at a sample offset.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct BphParamChange {
    pub offset: i32,
    pub id: u32,
    pub value: f64,
}

/// One block. Audio is planar: channel c of `input` is `[c * frames, (c + 1) * frames)`, the same for `output`.
/// Events and changes are sorted by offset. The callee writes every output sample and sets `out_event_count`.
#[repr(C)]
pub struct BphProcess {
    pub frames: u32,
    pub input_channels: u32,
    pub output_channels: u32,
    pub event_count: u32,
    pub input: *const f32,
    pub output: *mut f32,
    pub events: *const BphEvent,
    pub changes: *const BphParamChange,
    pub change_count: u32,
    pub out_event_capacity: u32,
    pub out_events: *mut BphEvent,
    pub out_event_count: u32,
    pub out_events_dropped: u32,
    pub position: i64,
    pub tempo: f64,
    pub playing: u32,
    /// Channels in `sidechain` (0 for none): audio for the plugin's first auxiliary input bus (ABI version 3).
    pub sidechain_channels: u32,
    /// Planar float32 like `input`, `sidechain_channels` channels of `frames`; may be null when there are none.
    pub sidechain: *const f32,
}

impl BphProcess {
    pub fn events(&self) -> &[BphEvent] {
        if self.events.is_null() || self.event_count == 0 {
            return &[];
        }
        // SAFETY: the caller passes `event_count` valid events (ABI contract).
        unsafe { slice::from_raw_parts(self.events, self.event_count as usize) }
    }

    pub fn changes(&self) -> &[BphParamChange] {
        if self.changes.is_null() || self.change_count == 0 {
            return &[];
        }
        // SAFETY: as above.
        unsafe { slice::from_raw_parts(self.changes, self.change_count as usize) }
    }

    pub fn input_channel(&self, channel: u32) -> *const f32 {
        // SAFETY: the caller passes `input_channels * frames` samples.
        unsafe { self.input.add((channel * self.frames) as usize) }
    }

    pub fn sidechain_channel(&self, channel: u32) -> *const f32 {
        // SAFETY: the caller passes `sidechain_channels * frames` samples.
        unsafe { self.sidechain.add((channel * self.frames) as usize) }
    }

    pub fn output_channel(&mut self, channel: u32) -> &mut [f32] {
        // SAFETY: the caller passes `output_channels * frames` samples.
        unsafe {
            slice::from_raw_parts_mut(
                self.output.add((channel * self.frames) as usize),
                self.frames as usize,
            )
        }
    }

    pub fn push_output_event(&mut self, event: BphEvent) {
        if self.out_events.is_null() || self.out_event_count >= self.out_event_capacity {
            self.out_events_dropped += 1;
            return;
        }
        // SAFETY: index is below the capacity the caller gave.
        unsafe { *self.out_events.add(self.out_event_count as usize) = event };
        self.out_event_count += 1;
    }
}

/// Something the plugin did on its own, reported from `bph_plugin_idle`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct BphNotification {
    pub kind: u32,
    pub id: u32,
    pub value: f64,
}

pub mod notification_kind {
    /// The plugin changed a parameter itself (editor, preset): `id`, `value` (normalized).
    pub const PARAMETER_EDITED: u32 = 1;
    /// The plugin's latency changed: `value` is the new latency in frames.
    pub const LATENCY_CHANGED: u32 = 2;
    /// The plugin's state changed in a way that is not a parameter (it asks to be saved).
    pub const STATE_DIRTY: u32 = 3;
    /// The editor window was closed by the user or by the plugin.
    pub const EDITOR_CLOSED: u32 = 4;
    /// The plugin's parameter list or titles changed.
    pub const PARAMETERS_CHANGED: u32 = 5;
    /// A key pressed or released in the editor while its keys go to the host: `id` is the USB HID usage, `value` the
    /// modifier bits, plus 256 when down and 512 for a repeat (see `keys`).
    pub const EDITOR_KEY: u32 = 6;
    /// A button in the editor window's header was clicked: `id` is the button (1 Bypass, 2 Pin, 3 Keys), `value` the
    /// state the user wants (1 on, 0 off).
    pub const EDITOR_BUTTON: u32 = 7;
}

pub mod param_flags {
    pub const AUTOMATABLE: u32 = 1;
    pub const READ_ONLY: u32 = 2;
    pub const HIDDEN: u32 = 4;
    pub const BYPASS: u32 = 8;
    pub const LIST: u32 = 16;
    /// A stand-in for a MIDI controller (VST3 MIDI mapping); Bluestone does not show it as a parameter.
    pub const MIDI_CONTROLLER: u32 = 32;
}

pub mod plugin_kind {
    pub const INSTRUMENT: u32 = 1;
    pub const MIDI_EFFECT: u32 = 2;
    pub const AUDIO_EFFECT: u32 = 3;
}

#[derive(Clone, Debug)]
pub struct PluginInfo {
    pub id: String,
    pub name: String,
    pub vendor: String,
    pub version: String,
    pub kind: u32,
    pub category: String,
}

#[derive(Clone, Debug)]
pub struct ParamInfo {
    pub id: u32,
    pub name: String,
    pub units: String,
    pub default_value: f64,
    pub step_count: i32,
    pub flags: u32,
}

/// A loaded plugin instance, whatever its format. Threading: `process` runs in the audio role (one thread at a
/// time, never concurrently with `activate`, state, or itself); everything else on the worker's main thread.
pub trait Instance {
    fn parameters(&self) -> Vec<ParamInfo>;
    fn get_parameter(&self, id: u32) -> Option<f64>;
    fn set_parameter(&self, id: u32, value: f64) -> bool;
    fn activate(
        &self,
        sample_rate: f64,
        max_frames: u32,
        input_channels: u32,
        output_channels: u32,
    ) -> Result<(), String>;
    fn process(&self, block: &mut BphProcess) -> bool;
    fn latency(&self) -> u32;
    /// The channels of the plugin's sidechain (first auxiliary input bus) once activated; 0 when it has none.
    fn sidechain_channels(&self) -> u32;
    fn save_state(&self) -> Result<Vec<u8>, String>;
    fn load_state(&self, data: &[u8]) -> Result<(), String>;
    fn idle(&self, notifications: &mut Vec<BphNotification>);
    fn has_editor(&self) -> bool;
    fn open_editor(&self, title: &str) -> Result<(), String>;
    fn close_editor(&self);
    fn is_editor_open(&self) -> bool;
    /// Hides the editor's window (the editor and its state stay) or shows it again without taking focus. False when it
    /// could not (a plugin-owned window that refused), so the host keeps treating it as shown.
    fn set_editor_visible(&self, visible: bool) -> bool;
    /// Sends the editor window's keys to the host instead of the plugin; false when it cannot (no editor, a window the
    /// plugin owns, or a platform without interception).
    fn route_editor_keys_to_host(&self, to_host: bool) -> bool;
    /// Shows what the host decided in the editor window's header; false when there is no window of Bluestone's (no
    /// editor, or a window the plugin owns).
    fn set_editor_header(&self, state: crate::gui::HeaderState) -> bool;
}

/// Queues what the user did in an editor window for the host: the keys it took, then its header's clicks.
pub fn push_editor_input(window: &crate::gui::Window, notifications: &mut Vec<BphNotification>) {
    for key in window.take_keys() {
        notifications.push(BphNotification {
            kind: notification_kind::EDITOR_KEY,
            id: key.usage,
            value: key.packed(),
        });
    }
    for (button, wanted) in window.take_header_clicks() {
        notifications.push(BphNotification {
            kind: notification_kind::EDITOR_BUTTON,
            id: button as u32,
            value: if wanted { 1.0 } else { 0.0 },
        });
    }
}

/// Plugin-side channel buffers adapted to the host's planar layout. The host always sees the channel counts it asked
/// for; a plugin with fewer channels gets the first host channels (a mono output feeds every host channel), extra
/// plugin inputs repeat the last host channel or get silence, and extra plugin outputs are dropped.
pub struct ChannelAdapter {
    pub max_frames: usize,
    /// Per bus: the plugin's channel count.
    pub input_buses: Vec<usize>,
    pub output_buses: Vec<usize>,
    input_scratch: Vec<f32>,
    output_scratch: Vec<f32>,
    pub input_pointers: Vec<*mut f32>,
    pub output_pointers: Vec<*mut f32>,
}

impl ChannelAdapter {
    pub fn new(max_frames: usize, input_buses: Vec<usize>, output_buses: Vec<usize>) -> Self {
        let inputs: usize = input_buses.iter().sum();
        let outputs: usize = output_buses.iter().sum();
        let mut adapter = ChannelAdapter {
            max_frames,
            input_buses,
            output_buses,
            input_scratch: vec![0.0; (inputs.max(1)) * max_frames],
            output_scratch: vec![0.0; (outputs.max(1)) * max_frames],
            input_pointers: vec![std::ptr::null_mut(); inputs],
            output_pointers: vec![std::ptr::null_mut(); outputs],
        };
        for c in 0..outputs {
            adapter.output_pointers[c] = adapter.output_scratch[c * max_frames..].as_mut_ptr();
        }
        adapter
    }

    /// Points the plugin's inputs at the host's input (main bus), the host's sidechain (the first auxiliary bus), or
    /// at silence (other buses). A bus with more channels than the host sends repeats the host's last channel.
    pub fn prepare_inputs(&mut self, block: &BphProcess) {
        self.input_scratch.iter_mut().for_each(|s| *s = 0.0);
        let silence = self.input_scratch.as_mut_ptr();
        let mut index = 0;
        for (bus, &count) in self.input_buses.iter().enumerate() {
            for c in 0..count {
                self.input_pointers[index] =
                    if bus == 0 && block.input_channels > 0 && !block.input.is_null() {
                        let host = (c as u32).min(block.input_channels - 1);
                        block.input_channel(host) as *mut f32
                    } else if bus == 1 && block.sidechain_channels > 0 && !block.sidechain.is_null()
                    {
                        let host = (c as u32).min(block.sidechain_channels - 1);
                        block.sidechain_channel(host) as *mut f32
                    } else {
                        silence
                    };
                index += 1;
            }
        }
    }

    /// The channels of the plugin's first auxiliary input bus (its sidechain), or 0 when it has none.
    pub fn sidechain_channels(&self) -> u32 {
        self.input_buses.get(1).copied().unwrap_or(0) as u32
    }

    /// Copies the plugin's main output bus into the host's output.
    pub fn deliver_outputs(&self, block: &mut BphProcess) {
        let frames = block.frames as usize;
        let main = self.output_buses.first().copied().unwrap_or(0);
        for h in 0..block.output_channels {
            let out = block.output_channel(h);
            if main == 0 {
                out.fill(0.0);
                continue;
            }
            let source = (h as usize).min(main - 1);
            out.copy_from_slice(
                &self.output_scratch[source * self.max_frames..source * self.max_frames + frames],
            );
        }
    }

    pub fn clear_outputs(&mut self, frames: usize) {
        let max = self.max_frames;
        for c in 0..self.output_pointers.len() {
            self.output_scratch[c * max..c * max + frames].fill(0.0);
        }
    }
}

/// Copies a NUL-terminated or full fixed-size C string.
pub fn c_chars_to_string(chars: &[c_char]) -> String {
    let bytes: Vec<u8> = chars
        .iter()
        .take_while(|&&c| c != 0)
        .map(|&c| c as u8)
        .collect();
    String::from_utf8_lossy(&bytes).trim().to_string()
}

/// Copies a NUL-terminated or full fixed-size UTF-16 string.
pub fn utf16_to_string(chars: &[u16]) -> String {
    let len = chars.iter().position(|&c| c == 0).unwrap_or(chars.len());
    String::from_utf16_lossy(&chars[..len]).trim().to_string()
}

pub fn to_json_plugins(plugins: &[PluginInfo]) -> String {
    let list: Vec<serde_json::Value> = plugins
        .iter()
        .map(|p| {
            serde_json::json!({
                "id": p.id, "name": p.name, "vendor": p.vendor, "version": p.version, "kind": p.kind, "category": p.category,
            })
        })
        .collect();
    serde_json::Value::Array(list).to_string()
}

/// A normalized value for the ABI: clamped to [0, 1], and 0 when it is not a number (JSON has no NaN; .NET would see
/// null).
pub fn unit_value(value: f64) -> f64 {
    if value.is_finite() {
        value.clamp(0.0, 1.0)
    } else {
        0.0
    }
}

pub fn to_json_params(params: &[ParamInfo]) -> String {
    let list: Vec<serde_json::Value> = params
        .iter()
        .map(|p| {
            serde_json::json!({
                "id": p.id, "name": p.name, "units": p.units, "default": unit_value(p.default_value),
                "steps": p.step_count.max(0), "flags": p.flags,
            })
        })
        .collect();
    serde_json::Value::Array(list).to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parameter(default_value: f64) -> ParamInfo {
        ParamInfo {
            id: 3,
            name: "P".into(),
            units: String::new(),
            default_value,
            step_count: 0,
            flags: 0,
        }
    }

    #[test]
    fn parameter_defaults_are_always_numbers_in_the_unit_range() {
        let json = to_json_params(&[
            parameter(f64::NAN),
            parameter(f64::INFINITY),
            parameter(-2.0),
            parameter(0.25),
        ]);
        let parsed: serde_json::Value = serde_json::from_str(&json).unwrap();
        let defaults: Vec<f64> = parsed
            .as_array()
            .unwrap()
            .iter()
            .map(|p| p["default"].as_f64().expect("a number"))
            .collect();
        assert_eq!(defaults, [0.0, 0.0, 0.0, 0.25]);
    }
}

#[cfg(test)]
mod sidechain_tests {
    use super::*;

    fn block(input: &[f32], sidechain: &[f32], frames: u32, sidechain_channels: u32) -> BphProcess {
        BphProcess {
            frames,
            input_channels: 2,
            output_channels: 0,
            event_count: 0,
            input: input.as_ptr(),
            output: std::ptr::null_mut(),
            events: std::ptr::null(),
            changes: std::ptr::null(),
            change_count: 0,
            out_event_capacity: 0,
            out_events: std::ptr::null_mut(),
            out_event_count: 0,
            out_events_dropped: 0,
            position: 0,
            tempo: 120.0,
            playing: 0,
            sidechain_channels,
            sidechain: if sidechain.is_empty() {
                std::ptr::null()
            } else {
                sidechain.as_ptr()
            },
        }
    }

    #[test]
    fn the_first_auxiliary_bus_gets_the_hosts_sidechain_and_later_buses_get_silence() {
        let input = [1.0f32, 1.0, 2.0, 2.0];
        let sidechain = [3.0f32, 3.0];
        let mut adapter = ChannelAdapter::new(2, vec![2, 2, 1], vec![2]);
        assert_eq!(adapter.sidechain_channels(), 2);

        // A mono sidechain feeds both channels of a stereo aux bus.
        adapter.prepare_inputs(&block(&input, &sidechain, 2, 1));
        let read = |pointer: *mut f32| unsafe { *pointer };
        assert_eq!(read(adapter.input_pointers[0]), 1.0);
        assert_eq!(read(adapter.input_pointers[1]), 2.0);
        assert_eq!(read(adapter.input_pointers[2]), 3.0);
        assert_eq!(read(adapter.input_pointers[3]), 3.0);
        assert_eq!(read(adapter.input_pointers[4]), 0.0);

        // Without one, the aux bus is silent.
        adapter.prepare_inputs(&block(&input, &[], 2, 0));
        assert_eq!(read(adapter.input_pointers[2]), 0.0);
        assert_eq!(
            ChannelAdapter::new(2, vec![2], vec![2]).sidechain_channels(),
            0
        );
    }
}
