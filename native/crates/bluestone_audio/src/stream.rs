//! One output stream: cpal owns the audio thread; each callback hands the buffer to a function registered by .NET.
//!
//! The data callback does no allocation, locking, or logging: it reads and writes atomics, converts samples
//! into a buffer allocated when the stream opens, and calls the registered function.

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{
    BufferSize, Device, ErrorKind, FromSample, OutputCallbackInfo, SampleFormat, SizedSample,
    StreamConfig, StreamInstant, SupportedBufferSize,
};
use std::ffi::c_void;
use std::sync::Arc;
use std::sync::atomic::{AtomicI64, AtomicU32, AtomicU64, Ordering};

/// Called once per device callback with an interleaved buffer of `frames * channels` samples to fill.
pub type RenderFn = unsafe extern "C" fn(
    user_data: *mut c_void,
    buffer: *mut f32,
    frames: u32,
    channels: u32,
    info: *const BlockInfo,
);

/// Set in [`BlockInfo::flags`] when the time since the previous callback was longer than the previous
/// block lasts (by half a block and a millisecond): the device probably dropped audio.
pub const FLAG_DISCONTINUITY: u32 = 1;

/// What the device says about one block. Passed by pointer; valid only during the call.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct BlockInfo {
    /// Frames handed to the device before this block since the stream started.
    pub frame_position: u64,
    /// Time from this callback until the block's first frame is heard, in nanoseconds (0 when unknown).
    pub output_latency_ns: i64,
    /// Zero-based callback count.
    pub callback_index: u64,
    pub sample_rate: u32,
    pub flags: u32,
}

/// What a stream asks for. Zero means "the device's default".
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct StreamRequest {
    pub struct_size: u32,
    pub sample_rate: u32,
    pub buffer_frames: u32,
    pub channels: u32,
}

pub const STATE_STOPPED: u32 = 0;
pub const STATE_RUNNING: u32 = 1;
pub const STATE_FAILED: u32 = 2;

pub const ERROR_DEVICE_NOT_AVAILABLE: u32 = 1;
pub const ERROR_STREAM_INVALIDATED: u32 = 2;
pub const ERROR_DEVICE_CHANGED: u32 = 3;
pub const ERROR_BACKEND: u32 = 4;
pub const ERROR_OTHER: u32 = 5;

/// A snapshot of a stream's state, filled by `bluestone_audio_stream_status`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct StreamStatus {
    pub struct_size: u32,
    pub state: u32,
    pub sample_rate: u32,
    pub channels: u32,
    /// The buffer size the stream was opened with (0 when the device chose).
    pub requested_buffer_frames: u32,
    pub last_buffer_frames: u32,
    pub min_buffer_frames: u32,
    pub max_buffer_frames: u32,
    pub callbacks: u64,
    pub frames: u64,
    /// Underruns and overloads the backend reported.
    pub xruns: u64,
    /// Callbacks that came later than the previous block lasted (see [`FLAG_DISCONTINUITY`]).
    pub discontinuities: u64,
    /// Callbacks whose buffer was larger than the conversion buffer; they were output as silence.
    pub oversized_blocks: u64,
    pub output_latency_ns: i64,
    /// 0 none, 1 device not available, 2 stream invalidated, 3 device changed, 4 backend, 5 other.
    pub last_error: u32,
    pub errors: u32,
    /// The device's sample format: 0 f32, 1 i16, 2 i32, 3 u16.
    pub sample_format: u32,
    pub reserved: u32,
}

#[derive(Default)]
pub(crate) struct Shared {
    state: AtomicU32,
    callbacks: AtomicU64,
    frames: AtomicU64,
    xruns: AtomicU64,
    discontinuities: AtomicU64,
    oversized: AtomicU64,
    last_frames: AtomicU32,
    min_frames: AtomicU32,
    max_frames: AtomicU32,
    latency_ns: AtomicI64,
    last_error: AtomicU32,
    errors: AtomicU32,
}

struct UserData(*mut c_void);

// SAFETY: the pointer is opaque to Rust; the .NET side guarantees it stays valid and may be used from the
// audio thread until the stream is closed.
unsafe impl Send for UserData {}

/// Per-callback bookkeeping, owned by the data callback.
struct Clock {
    sample_rate: u32,
    position: u64,
    index: u64,
    previous: Option<(StreamInstant, u32)>,
}

impl Clock {
    fn block(&mut self, shared: &Shared, info: &OutputCallbackInfo, frames: u32) -> BlockInfo {
        let stamp = info.timestamp();
        let latency = stamp
            .playback
            .checked_duration_since(stamp.callback)
            .map_or(0, |d| i64::try_from(d.as_nanos()).unwrap_or(i64::MAX));
        let mut flags = 0;
        if let Some((previous, previous_frames)) = self.previous
            && let Some(elapsed) = stamp.callback.checked_duration_since(previous)
            && is_discontinuity(elapsed.as_nanos(), previous_frames, self.sample_rate)
        {
            flags |= FLAG_DISCONTINUITY;
            shared.discontinuities.fetch_add(1, Ordering::Relaxed);
        }
        self.previous = Some((stamp.callback, frames));
        let block = BlockInfo {
            frame_position: self.position,
            output_latency_ns: latency,
            callback_index: self.index,
            sample_rate: self.sample_rate,
            flags,
        };
        self.position += u64::from(frames);
        self.index += 1;
        shared.callbacks.fetch_add(1, Ordering::Relaxed);
        shared
            .frames
            .fetch_add(u64::from(frames), Ordering::Relaxed);
        shared.last_frames.store(frames, Ordering::Relaxed);
        shared.min_frames.fetch_min(frames, Ordering::Relaxed);
        shared.max_frames.fetch_max(frames, Ordering::Relaxed);
        shared.latency_ns.store(latency, Ordering::Relaxed);
        block
    }
}

/// A callback is late when it comes more than half a block plus a millisecond after the previous block's period.
pub(crate) fn is_discontinuity(elapsed_ns: u128, previous_frames: u32, sample_rate: u32) -> bool {
    if sample_rate == 0 {
        return false;
    }
    let period_ns = u128::from(previous_frames) * 1_000_000_000 / u128::from(sample_rate);
    elapsed_ns > period_ns + period_ns / 2 + 1_000_000
}

pub struct OutputStream {
    stream: cpal::Stream,
    shared: Arc<Shared>,
    sample_rate: u32,
    channels: u32,
    requested_frames: u32,
    format: u32,
}

impl OutputStream {
    pub fn open(
        device_id: Option<&str>,
        request: &StreamRequest,
        render: RenderFn,
        user_data: *mut c_void,
    ) -> Result<OutputStream, String> {
        let host = cpal::default_host();
        let device = match device_id {
            Some(id) if !id.is_empty() => {
                let parsed = id
                    .parse()
                    .map_err(|e| format!("Not a device id: {id} ({e})"))?;
                host.device_by_id(&parsed)
                    .ok_or_else(|| format!("The audio device {id} is not available."))?
            }
            _ => host
                .default_output_device()
                .ok_or_else(|| "There is no default audio output device.".to_string())?,
        };
        let (config, format, buffer_range) = choose_config(&device, request)?;
        let shared = Arc::new(Shared {
            min_frames: AtomicU32::new(u32::MAX),
            ..Shared::default()
        });
        let channels = u32::from(config.channels);
        // Room for the largest block the device may deliver, so the callback never allocates.
        let capacity_frames = buffer_range.map_or(16_384, |(_, max)| max.clamp(1, 65_536)) as usize;
        let capacity_frames = capacity_frames.max(match config.buffer_size {
            BufferSize::Fixed(n) => n as usize,
            BufferSize::Default => 0,
        });
        let user = UserData(user_data);
        let stream = match format {
            SampleFormat::F32 => build_f32(&device, config, render, user, Arc::clone(&shared))?,
            SampleFormat::I16 => build_converted::<i16>(
                &device,
                config,
                render,
                user,
                Arc::clone(&shared),
                capacity_frames,
            )?,
            SampleFormat::I32 => build_converted::<i32>(
                &device,
                config,
                render,
                user,
                Arc::clone(&shared),
                capacity_frames,
            )?,
            SampleFormat::U16 => build_converted::<u16>(
                &device,
                config,
                render,
                user,
                Arc::clone(&shared),
                capacity_frames,
            )?,
            other => return Err(format!("Unsupported sample format {other:?}.")),
        };
        Ok(OutputStream {
            stream,
            shared,
            sample_rate: config.sample_rate,
            channels,
            requested_frames: match config.buffer_size {
                BufferSize::Fixed(n) => n,
                BufferSize::Default => 0,
            },
            format: match format {
                SampleFormat::F32 => 0,
                SampleFormat::I16 => 1,
                SampleFormat::I32 => 2,
                _ => 3,
            },
        })
    }

    pub fn start(&self) -> Result<(), String> {
        self.stream.play().map_err(|e| e.to_string())?;
        // A failure reported by the error callback stays visible; only a stopped stream becomes running.
        let _ = self.shared.state.compare_exchange(
            STATE_STOPPED,
            STATE_RUNNING,
            Ordering::AcqRel,
            Ordering::Acquire,
        );
        Ok(())
    }

    pub fn stop(&self) -> Result<(), String> {
        self.stream.pause().map_err(|e| e.to_string())?;
        let _ = self.shared.state.compare_exchange(
            STATE_RUNNING,
            STATE_STOPPED,
            Ordering::AcqRel,
            Ordering::Acquire,
        );
        Ok(())
    }

    pub fn status(&self) -> StreamStatus {
        let s = &self.shared;
        let min = s.min_frames.load(Ordering::Relaxed);
        StreamStatus {
            struct_size: size_of::<StreamStatus>() as u32,
            state: s.state.load(Ordering::Acquire),
            sample_rate: self.sample_rate,
            channels: self.channels,
            requested_buffer_frames: self.requested_frames,
            last_buffer_frames: s.last_frames.load(Ordering::Relaxed),
            min_buffer_frames: if min == u32::MAX { 0 } else { min },
            max_buffer_frames: s.max_frames.load(Ordering::Relaxed),
            callbacks: s.callbacks.load(Ordering::Relaxed),
            frames: s.frames.load(Ordering::Relaxed),
            xruns: s.xruns.load(Ordering::Relaxed),
            discontinuities: s.discontinuities.load(Ordering::Relaxed),
            oversized_blocks: s.oversized.load(Ordering::Relaxed),
            output_latency_ns: s.latency_ns.load(Ordering::Relaxed),
            last_error: s.last_error.load(Ordering::Relaxed),
            errors: s.errors.load(Ordering::Relaxed),
            sample_format: self.format,
            reserved: 0,
        }
    }
}

fn error_callback(shared: Arc<Shared>) -> impl FnMut(cpal::Error) + Send + 'static {
    move |error| {
        let code = match error.kind() {
            ErrorKind::Xrun => {
                shared.xruns.fetch_add(1, Ordering::Relaxed);
                return;
            }
            ErrorKind::DeviceNotAvailable => ERROR_DEVICE_NOT_AVAILABLE,
            ErrorKind::StreamInvalidated => ERROR_STREAM_INVALIDATED,
            ErrorKind::DeviceChanged => ERROR_DEVICE_CHANGED,
            ErrorKind::BackendError => ERROR_BACKEND,
            _ => ERROR_OTHER,
        };
        shared.errors.fetch_add(1, Ordering::Relaxed);
        shared.last_error.store(code, Ordering::Relaxed);
        if code == ERROR_DEVICE_NOT_AVAILABLE || code == ERROR_STREAM_INVALIDATED {
            shared.state.store(STATE_FAILED, Ordering::Release);
        }
    }
}

fn build_f32(
    device: &Device,
    config: StreamConfig,
    render: RenderFn,
    user: UserData,
    shared: Arc<Shared>,
) -> Result<cpal::Stream, String> {
    let channels = u32::from(config.channels);
    let mut clock = Clock {
        sample_rate: config.sample_rate,
        position: 0,
        index: 0,
        previous: None,
    };
    let errors = error_callback(Arc::clone(&shared));
    device
        .build_output_stream::<f32, _, _>(
            config,
            move |data, info| {
                let user = &user;
                let frames = (data.len() / channels as usize) as u32;
                let block = clock.block(&shared, info, frames);
                data.fill(0.0);
                // SAFETY: `data` holds frames * channels samples for the duration of the call; the function
                // pointer and user data were registered by .NET and stay valid until the stream is closed.
                unsafe { render(user.0, data.as_mut_ptr(), frames, channels, &block) };
            },
            errors,
            None,
        )
        .map_err(|e| e.to_string())
}

fn build_converted<T>(
    device: &Device,
    config: StreamConfig,
    render: RenderFn,
    user: UserData,
    shared: Arc<Shared>,
    capacity_frames: usize,
) -> Result<cpal::Stream, String>
where
    T: SizedSample + FromSample<f32> + Send + 'static,
{
    let channels = u32::from(config.channels);
    let mut scratch = vec![0.0f32; capacity_frames * channels as usize];
    let mut clock = Clock {
        sample_rate: config.sample_rate,
        position: 0,
        index: 0,
        previous: None,
    };
    let errors = error_callback(Arc::clone(&shared));
    device
        .build_output_stream::<T, _, _>(
            config,
            move |data: &mut [T], info| {
                let user = &user;
                let frames = (data.len() / channels as usize) as u32;
                let block = clock.block(&shared, info, frames);
                if data.len() > scratch.len() {
                    shared.oversized.fetch_add(1, Ordering::Relaxed);
                    data.fill(T::EQUILIBRIUM);
                    return;
                }
                let buffer = &mut scratch[..data.len()];
                buffer.fill(0.0);
                // SAFETY: as for the f32 path; the buffer is ours and holds frames * channels samples.
                unsafe { render(user.0, buffer.as_mut_ptr(), frames, channels, &block) };
                for (out, &sample) in data.iter_mut().zip(buffer.iter()) {
                    *out = T::from_sample(sample.clamp(-1.0, 1.0));
                }
            },
            errors,
            None,
        )
        .map_err(|e| e.to_string())
}

/// A configuration, its sample format, and the device's buffer size range when known.
type Chosen = (StreamConfig, SampleFormat, Option<(u32, u32)>);

/// Picks a configuration for the request: the device default where the request says 0, f32 when the device offers it.
fn choose_config(device: &Device, request: &StreamRequest) -> Result<Chosen, String> {
    let default = device
        .default_output_config()
        .map_err(|e| format!("The device has no default output configuration: {e}"))?;
    let rate = if request.sample_rate == 0 {
        default.sample_rate()
    } else {
        request.sample_rate
    };
    let channels = if request.channels == 0 {
        u32::from(default.channels())
    } else {
        request.channels
    };
    let ranges: Vec<_> = device
        .supported_output_configs()
        .map_err(|e| format!("The device's configurations cannot be read: {e}"))?
        .filter(|r| {
            u32::from(r.channels()) == channels
                && r.min_sample_rate() <= rate
                && rate <= r.max_sample_rate()
        })
        .collect();
    let preference = [
        SampleFormat::F32,
        SampleFormat::I32,
        SampleFormat::I16,
        SampleFormat::U16,
    ];
    let range = preference
        .iter()
        .find_map(|format| ranges.iter().find(|r| r.sample_format() == *format))
        .ok_or_else(|| {
            format!("The device does not support {channels} channels at {rate} Hz in a usable sample format.")
        })?;
    let buffer_range = match range.buffer_size() {
        SupportedBufferSize::Range { min, max } => Some((*min, *max)),
        SupportedBufferSize::Unknown => None,
    };
    let buffer_size = if request.buffer_frames == 0 {
        BufferSize::Default
    } else {
        let frames = buffer_range.map_or(request.buffer_frames, |(min, max)| {
            request.buffer_frames.clamp(min, max)
        });
        BufferSize::Fixed(frames)
    };
    let channel_count = u16::try_from(channels).map_err(|_| "Too many channels.".to_string())?;
    Ok((
        StreamConfig {
            channels: channel_count,
            sample_rate: rate,
            buffer_size,
        },
        range.sample_format(),
        buffer_range,
    ))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_callback_on_time_is_not_a_discontinuity() {
        // 128 frames at 48 kHz last 2.67 ms.
        assert!(!is_discontinuity(2_666_666, 128, 48_000));
        assert!(!is_discontinuity(4_900_000, 128, 48_000));
    }

    #[test]
    fn a_callback_two_blocks_late_is_a_discontinuity() {
        assert!(is_discontinuity(6_000_000, 128, 48_000));
    }

    #[test]
    fn an_unknown_rate_never_reports_a_discontinuity() {
        assert!(!is_discontinuity(u128::MAX, 128, 0));
    }

    #[test]
    fn the_status_struct_has_a_stable_size() {
        // The .NET side checks this size; change both together and bump the ABI version.
        assert_eq!(size_of::<StreamStatus>(), 96);
        assert_eq!(size_of::<BlockInfo>(), 32);
        assert_eq!(size_of::<StreamRequest>(), 16);
    }
}
