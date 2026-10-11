//! Bluestone's test plugins (ADR 0033): small VST3 and CLAP plugins that behave like the worker's reference plugins,
//! so the native hosting path can be tested on machines (CI runners) that have no third-party plugins.
//!
//! - **Test Gain** (audio effect): Gain (id 0) maps [0, 1] to x0..x2 (0.5 is unity); Invert (id 1) flips polarity at
//!   0.5 and above. Parameter changes land on their exact sample.
//! - **Test Sine** (instrument): eight voices; a note sounds a sine at its pitch with amplitude
//!   `Level * velocity / 127 * 0.5` (Level is id 0) until its note-off. Every output channel carries the same signal.
//! - **Test Transpose** (MIDI effect): Semitones (id 0) maps [0, 1] to -24..+24 in 48 steps (0.5 is no change). Notes
//!   are transposed; a note-off follows its note-on's transposition; notes pushed outside 0..127 are dropped. In CLAP,
//!   other MIDI messages pass through unchanged.
//!
//! State is the parameter values: `BTPS`, a little-endian u16 version (1), a u16 count, then that many f64 values.

mod clap_plugin;
mod vst3_plugin;

use std::sync::atomic::{AtomicU64, Ordering};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Kind {
    Gain,
    Sine,
    Transpose,
}

impl Kind {
    pub const ALL: [Kind; 3] = [Kind::Gain, Kind::Sine, Kind::Transpose];

    pub fn name(self) -> &'static str {
        match self {
            Kind::Gain => "Test Gain",
            Kind::Sine => "Test Sine",
            Kind::Transpose => "Test Transpose",
        }
    }

    pub fn parameters(self) -> &'static [ParameterSpec] {
        match self {
            Kind::Gain => &[
                ParameterSpec {
                    name: "Gain",
                    default: 0.5,
                    steps: 0,
                },
                ParameterSpec {
                    name: "Invert",
                    default: 0.0,
                    steps: 1,
                },
            ],
            Kind::Sine => &[ParameterSpec {
                name: "Level",
                default: 0.5,
                steps: 0,
            }],
            Kind::Transpose => &[ParameterSpec {
                name: "Semitones",
                default: 0.5,
                steps: 48,
            }],
        }
    }

    pub fn audio_inputs(self) -> u32 {
        if self == Kind::Gain { 1 } else { 0 }
    }

    pub fn audio_outputs(self) -> u32 {
        if self == Kind::Transpose { 0 } else { 1 }
    }

    pub fn event_inputs(self) -> u32 {
        if self == Kind::Gain { 0 } else { 1 }
    }

    pub fn event_outputs(self) -> u32 {
        if self == Kind::Transpose { 1 } else { 0 }
    }
}

pub struct ParameterSpec {
    pub name: &'static str,
    pub default: f64,
    pub steps: i32,
}

pub enum Input {
    Note {
        on: bool,
        channel: u8,
        key: u8,
        velocity: f32,
    },
    Parameter {
        id: u32,
        value: f64,
    },
    Midi([u8; 3]),
}

pub enum Output {
    Note {
        on: bool,
        channel: u8,
        key: u8,
        velocity: f32,
    },
    Midi([u8; 3]),
}

#[derive(Clone, Copy, Default)]
struct Voice {
    active: bool,
    channel: u8,
    key: u8,
    amplitude: f64,
    phase: f64,
    increment: f64,
}

const NOT_SOUNDING: i16 = -1;

/// The plugin's sound and state, shared by both formats. Parameter values are atomics so the controller side may read
/// them while the processor runs.
pub struct Core {
    pub kind: Kind,
    values: Vec<AtomicU64>,
    sample_rate: f64,
    voices: [Voice; 8],
    sounding: Vec<i16>,
}

impl Core {
    pub fn new(kind: Kind) -> Core {
        Core {
            kind,
            values: kind
                .parameters()
                .iter()
                .map(|p| AtomicU64::new(p.default.to_bits()))
                .collect(),
            sample_rate: 48_000.0,
            voices: [Voice::default(); 8],
            sounding: vec![NOT_SOUNDING; 16 * 128],
        }
    }

    pub fn set_sample_rate(&mut self, sample_rate: f64) {
        self.sample_rate = sample_rate.max(1.0);
    }

    pub fn get(&self, id: u32) -> Option<f64> {
        self.values
            .get(id as usize)
            .map(|v| f64::from_bits(v.load(Ordering::Relaxed)))
    }

    pub fn set(&self, id: u32, value: f64) -> bool {
        match self.values.get(id as usize) {
            Some(slot) if (0.0..=1.0).contains(&value) => {
                slot.store(value.to_bits(), Ordering::Relaxed);
                true
            }
            _ => false,
        }
    }

    pub fn save(&self) -> Vec<u8> {
        let mut data = b"BTPS".to_vec();
        data.extend_from_slice(&1u16.to_le_bytes());
        data.extend_from_slice(&(self.values.len() as u16).to_le_bytes());
        for id in 0..self.values.len() {
            data.extend_from_slice(&self.get(id as u32).unwrap_or(0.0).to_le_bytes());
        }
        data
    }

    pub fn load(&self, data: &[u8]) -> bool {
        if data.len() < 8 || &data[..4] != b"BTPS" || u16::from_le_bytes([data[4], data[5]]) != 1 {
            return false;
        }
        let count = u16::from_le_bytes([data[6], data[7]]) as usize;
        if data.len() != 8 + count * 8 || count != self.values.len() {
            return false;
        }
        let values: Vec<f64> = data[8..]
            .chunks_exact(8)
            .map(|c| f64::from_le_bytes([c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]]))
            .collect();
        if values.iter().any(|v| !(0.0..=1.0).contains(v)) {
            return false;
        }
        for (id, value) in values.into_iter().enumerate() {
            self.set(id as u32, value);
        }
        true
    }

    /// Renders `frames` frames. `events` are sorted by offset; each takes effect from its own sample.
    pub fn process(
        &mut self,
        frames: usize,
        inputs: &[&[f32]],
        outputs: &mut [&mut [f32]],
        events: &[(u32, Input)],
        emit: &mut dyn FnMut(u32, Output),
    ) {
        let mut position = 0;
        let mut next = 0;
        while position < frames {
            while next < events.len() && (events[next].0 as usize) <= position {
                self.handle(events[next].0, &events[next].1, emit);
                next += 1;
            }
            let end = events
                .get(next)
                .map(|e| e.0 as usize)
                .unwrap_or(frames)
                .clamp(position + 1, frames);
            self.render(position, end, inputs, outputs);
            position = end;
        }
        while next < events.len() {
            self.handle(events[next].0, &events[next].1, emit);
            next += 1;
        }
    }

    fn handle(&mut self, offset: u32, input: &Input, emit: &mut dyn FnMut(u32, Output)) {
        match *input {
            Input::Parameter { id, value } => {
                self.set(id, value);
            }
            Input::Note {
                on,
                channel,
                key,
                velocity,
            } => match self.kind {
                Kind::Sine => self.sine_note(on, channel, key, velocity),
                Kind::Transpose => {
                    if let Some((on, key)) = self.transpose(on, channel, key) {
                        emit(
                            offset,
                            Output::Note {
                                on,
                                channel,
                                key,
                                velocity,
                            },
                        );
                    }
                }
                Kind::Gain => {}
            },
            Input::Midi(bytes) => {
                let status = bytes[0] & 0xF0;
                let channel = bytes[0] & 0x0F;
                let is_note = status == 0x90 || status == 0x80;
                let on = status == 0x90 && bytes[2] > 0;
                match self.kind {
                    Kind::Transpose if is_note => {
                        if let Some((on, key)) = self.transpose(on, channel, bytes[1]) {
                            emit(
                                offset,
                                Output::Midi([
                                    if on { 0x90 } else { 0x80 } | channel,
                                    key,
                                    bytes[2],
                                ]),
                            );
                        }
                    }
                    Kind::Transpose => emit(offset, Output::Midi(bytes)),
                    Kind::Sine if is_note => {
                        self.sine_note(on, channel, bytes[1], bytes[2] as f32 / 127.0)
                    }
                    _ => {}
                }
            }
        }
    }

    fn transpose(&mut self, on: bool, channel: u8, key: u8) -> Option<(bool, u8)> {
        let slot = (channel as usize & 0x0F) * 128 + (key as usize & 0x7F);
        if on {
            let shift = (self.get(0).unwrap_or(0.5) * 48.0).round() as i32 - 24;
            let note = key as i32 + shift;
            if (0..=127).contains(&note) {
                self.sounding[slot] = note as i16;
                Some((true, note as u8))
            } else {
                self.sounding[slot] = NOT_SOUNDING;
                None
            }
        } else {
            let note = std::mem::replace(&mut self.sounding[slot], NOT_SOUNDING);
            (note != NOT_SOUNDING).then_some((false, note as u8))
        }
    }

    fn sine_note(&mut self, on: bool, channel: u8, key: u8, velocity: f32) {
        if on {
            let index = self.voices.iter().position(|v| !v.active).unwrap_or(0);
            let frequency = 440.0 * 2f64.powf((key as f64 - 69.0) / 12.0);
            self.voices[index] = Voice {
                active: true,
                channel,
                key,
                amplitude: (velocity as f64).clamp(0.0, 1.0) * 0.5,
                phase: 0.0,
                increment: std::f64::consts::TAU * frequency / self.sample_rate,
            };
        } else {
            for voice in self
                .voices
                .iter_mut()
                .filter(|v| v.active && v.channel == channel && v.key == key)
            {
                voice.active = false;
            }
        }
    }

    fn render(&mut self, start: usize, end: usize, inputs: &[&[f32]], outputs: &mut [&mut [f32]]) {
        match self.kind {
            Kind::Gain => {
                let mut gain = (self.get(0).unwrap_or(0.5) * 2.0) as f32;
                if self.get(1).unwrap_or(0.0) >= 0.5 {
                    gain = -gain;
                }
                for (c, output) in outputs.iter_mut().enumerate() {
                    let input = inputs.get(c).or(inputs.last());
                    for (i, sample) in output.iter_mut().enumerate().take(end).skip(start) {
                        *sample = input.map(|x| x[i]).unwrap_or(0.0) * gain;
                    }
                }
            }
            Kind::Sine => {
                let level = self.get(0).unwrap_or(0.5);
                for i in start..end {
                    let mut sample = 0.0;
                    for voice in self.voices.iter_mut().filter(|v| v.active) {
                        sample += voice.phase.sin() * voice.amplitude * level;
                        voice.phase = (voice.phase + voice.increment) % std::f64::consts::TAU;
                    }
                    for output in outputs.iter_mut() {
                        output[i] = sample as f32;
                    }
                }
            }
            Kind::Transpose => {
                for output in outputs.iter_mut() {
                    output[start..end].fill(0.0);
                }
            }
        }
    }
}
