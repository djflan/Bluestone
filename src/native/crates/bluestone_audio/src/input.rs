//! One input stream: cpal owns the audio thread; each callback hands the captured buffer to a function registered by
//! .NET, with when it was captured. Like the output stream's data callback, this one does no allocation, locking, or
//! logging: it converts samples into a buffer allocated when the stream opens and calls the registered function.

use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
use cpal::{
    BufferSize, Device, FromSample, InputCallbackInfo, SampleFormat, SizedSample, StreamConfig,
    SupportedBufferSize,
};
use std::ffi::c_void;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU32, AtomicU64, Ordering};

use crate::stream::{STATE_FAILED, STATE_RUNNING, STATE_STOPPED, StreamRequest};

/// Called once per device callback with `frames * channels` interleaved captured samples.
pub type CaptureFn = unsafe extern "C" fn(
    user_data: *mut c_void,
    buffer: *const f32,
    frames: u32,
    channels: u32,
    info: *const CaptureInfo,
);

/// What the device says about one captured block. Passed by pointer; valid only during the call.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CaptureInfo {
    /// Frames captured before this block since the stream started.
    pub frame_position: u64,
    /// Time from when this block's first frame was captured until this callback, in nanoseconds (0 when unknown).
    pub input_latency_ns: i64,
    /// Zero-based callback count.
    pub callback_index: u64,
    pub sample_rate: u32,
    pub reserved: u32,
}

/// A snapshot of an input stream's state, filled by `bluestone_audio_input_status`.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct InputStatus {
    pub struct_size: u32,
    pub state: u32,
    pub sample_rate: u32,
    pub channels: u32,
    pub callbacks: u64,
    pub frames: u64,
    pub overruns: u64,
    pub input_latency_ns: i64,
    /// Callbacks whose buffer was larger than the conversion buffer; they were not delivered.
    pub oversized_blocks: u64,
}

#[derive(Default)]
struct Shared {
    state: AtomicU32,
    callbacks: AtomicU64,
    frames: AtomicU64,
    overruns: AtomicU64,
    oversized: AtomicU64,
    latency_ns: AtomicI64,
    /// Set when the callback thread still has to be promoted (see `priority`); cleared by the first callback.
    promote: AtomicBool,
}

struct Sink {
    capture: CaptureFn,
    user: *mut c_void,
}

// SAFETY: the pointer is opaque to Rust; the .NET side guarantees it stays valid and may be used from the audio
// thread until the stream is closed.
unsafe impl Send for Sink {}

impl Sink {
    fn deliver(&self, buffer: &[f32], frames: u32, channels: u32, info: &CaptureInfo) {
        // SAFETY: `buffer` holds frames * channels samples for the duration of the call; the function and user data
        // were registered by .NET and stay valid until the stream is closed.
        unsafe { (self.capture)(self.user, buffer.as_ptr(), frames, channels, info) };
    }
}

pub struct InputStream {
    stream: cpal::Stream,
    shared: Arc<Shared>,
    sample_rate: u32,
    channels: u32,
    _host_guard: crate::host::AsioStreamGuard,
}

impl InputStream {
    /// Opens a stopped input stream on `device_id` (an id from the input device list), or on the default input.
    pub fn open(
        host_name: Option<&str>,
        device_id: Option<&str>,
        request: &StreamRequest,
        capture: CaptureFn,
        user_data: *mut c_void,
    ) -> Result<InputStream, String> {
        let host = crate::host::select(host_name)?;
        let is_asio = host_name.is_some_and(|name| name.eq_ignore_ascii_case("ASIO"));
        let device = match device_id {
            Some(id) if !id.is_empty() => {
                let parsed = id
                    .parse()
                    .map_err(|e| format!("Not a device id: {id} ({e})"))?;
                (if is_asio {
                    crate::host::cached_asio_device(id, true)
                } else {
                    None
                })
                .or_else(|| host.device_by_id(&parsed))
                .ok_or_else(|| format!("The audio device {id} is not available."))?
            }
            _ => (if is_asio {
                crate::host::cached_asio_default(true)
            } else {
                None
            })
            .or_else(|| host.default_input_device())
            .ok_or_else(|| "There is no default audio input device.".to_string())?,
        };
        let (config, format, buffer_range) = choose_input_config(&device, request)?;
        let shared = Arc::new(Shared {
            promote: AtomicBool::new(crate::priority::promotes(host_name)),
            ..Shared::default()
        });
        let channels = u32::from(config.channels);
        let capacity_frames = buffer_range.map_or(16_384, |(_, max)| max.clamp(1, 65_536)) as usize;
        let capacity_frames = capacity_frames.max(match config.buffer_size {
            BufferSize::Fixed(n) => n as usize,
            BufferSize::Default => 0,
        });
        let sink = Sink {
            capture,
            user: user_data,
        };
        let stream = match format {
            SampleFormat::F32 => {
                build::<f32>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::F64 => {
                build::<f64>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::I8 => {
                build::<i8>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::I16 => {
                build::<i16>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::I24 => {
                build::<cpal::I24>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::I32 => {
                build::<i32>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::I64 => {
                build::<i64>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::U8 => {
                build::<u8>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::U16 => {
                build::<u16>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::U24 => {
                build::<cpal::U24>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::U32 => {
                build::<u32>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            SampleFormat::U64 => {
                build::<u64>(&device, config, sink, Arc::clone(&shared), capacity_frames)?
            }
            other => return Err(format!("Unsupported sample format {other:?}.")),
        };
        Ok(InputStream {
            stream,
            shared,
            sample_rate: config.sample_rate,
            channels,
            _host_guard: crate::host::AsioStreamGuard::new(host_name),
        })
    }

    pub fn start(&self) -> Result<(), String> {
        self.stream.play().map_err(|e| e.to_string())?;
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

    pub fn status(&self) -> InputStatus {
        let s = &self.shared;
        InputStatus {
            struct_size: size_of::<InputStatus>() as u32,
            state: s.state.load(Ordering::Acquire),
            sample_rate: self.sample_rate,
            channels: self.channels,
            callbacks: s.callbacks.load(Ordering::Relaxed),
            frames: s.frames.load(Ordering::Relaxed),
            overruns: s.overruns.load(Ordering::Relaxed),
            input_latency_ns: s.latency_ns.load(Ordering::Relaxed),
            oversized_blocks: s.oversized.load(Ordering::Relaxed),
        }
    }
}

fn build<T>(
    device: &Device,
    config: StreamConfig,
    sink: Sink,
    shared: Arc<Shared>,
    capacity_frames: usize,
) -> Result<cpal::Stream, String>
where
    T: SizedSample + Send + 'static,
    f32: FromSample<T>,
{
    let channels = u32::from(config.channels);
    let sample_rate = config.sample_rate;
    let mut scratch = vec![0.0f32; capacity_frames * channels as usize];
    let mut position = 0u64;
    let mut index = 0u64;
    let errors = {
        let shared = Arc::clone(&shared);
        move |error: cpal::Error| {
            if matches!(error.kind(), cpal::ErrorKind::Xrun) {
                shared.overruns.fetch_add(1, Ordering::Relaxed);
            } else {
                shared.state.store(STATE_FAILED, Ordering::Release);
            }
        }
    };
    device
        .build_input_stream::<T, _, _>(
            config,
            move |data: &[T], info: &InputCallbackInfo| {
                if shared.promote.load(Ordering::Relaxed) {
                    shared.promote.store(false, Ordering::Relaxed);
                    crate::priority::promote_current_thread();
                }
                let frames = (data.len() / channels as usize) as u32;
                let stamp = info.timestamp();
                let latency = stamp
                    .callback
                    .checked_duration_since(stamp.capture)
                    .map_or(0, |d| i64::try_from(d.as_nanos()).unwrap_or(i64::MAX));
                let block = CaptureInfo {
                    frame_position: position,
                    input_latency_ns: latency,
                    callback_index: index,
                    sample_rate,
                    reserved: 0,
                };
                position += u64::from(frames);
                index += 1;
                shared.callbacks.fetch_add(1, Ordering::Relaxed);
                shared
                    .frames
                    .fetch_add(u64::from(frames), Ordering::Relaxed);
                shared.latency_ns.store(latency, Ordering::Relaxed);
                if data.len() > scratch.len() {
                    shared.oversized.fetch_add(1, Ordering::Relaxed);
                    return;
                }
                let buffer = &mut scratch[..data.len()];
                for (out, &sample) in buffer.iter_mut().zip(data.iter()) {
                    *out = <f32 as FromSample<T>>::from_sample_(sample);
                }
                sink.deliver(buffer, frames, channels, &block);
            },
            errors,
            None,
        )
        .map_err(|e| e.to_string())
}

/// A configuration, its sample format, and the device's buffer size range when known.
type Chosen = (StreamConfig, SampleFormat, Option<(u32, u32)>);

/// Picks an input configuration for the request: the device default where the request says 0, f32 when offered.
fn choose_input_config(device: &Device, request: &StreamRequest) -> Result<Chosen, String> {
    let default = device
        .default_input_config()
        .map_err(|e| format!("The device has no default input configuration: {e}"))?;
    let channels = if request.channels == 0 {
        u32::from(default.channels())
    } else {
        request.channels
    };
    let supported: Vec<_> = device
        .supported_input_configs()
        .map_err(|e| format!("The device's configurations cannot be read: {e}"))?
        .collect();
    let rate_ranges: Vec<_> = supported
        .iter()
        .map(|r| {
            (
                r.min_sample_rate(),
                r.max_sample_rate(),
                u32::from(r.channels()),
            )
        })
        .collect();
    let rate = crate::host::resolve_sample_rate(
        request.sample_rate,
        default.sample_rate(),
        channels,
        &rate_ranges,
    )?;
    let ranges: Vec<_> = supported
        .into_iter()
        .filter(|r| {
            u32::from(r.channels()) == channels
                && r.min_sample_rate() <= rate
                && rate <= r.max_sample_rate()
        })
        .collect();
    let preference = [
        SampleFormat::F32,
        SampleFormat::I32,
        SampleFormat::F64,
        SampleFormat::I24,
        SampleFormat::I16,
        SampleFormat::U24,
        SampleFormat::U32,
        SampleFormat::U16,
        SampleFormat::I64,
        SampleFormat::U64,
        SampleFormat::I8,
        SampleFormat::U8,
    ];
    let range = preference
        .iter()
        .find_map(|format| ranges.iter().find(|r| r.sample_format() == *format))
        .ok_or_else(|| {
            format!("The device cannot record {channels} channels at {rate} Hz in a usable sample format.")
        })?;
    let buffer_range = match range.buffer_size() {
        SupportedBufferSize::Range { min, max } => Some((*min, *max)),
        SupportedBufferSize::Unknown => None,
    };
    let buffer_size = if request.buffer_frames == 0 {
        BufferSize::Default
    } else {
        BufferSize::Fixed(buffer_range.map_or(request.buffer_frames, |(min, max)| {
            request.buffer_frames.clamp(min, max)
        }))
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

    unsafe extern "C" fn ignore(
        _: *mut c_void,
        _: *const f32,
        _: u32,
        _: u32,
        _: *const CaptureInfo,
    ) {
    }

    #[test]
    fn an_unknown_device_is_refused_with_a_reason() {
        let request = StreamRequest {
            struct_size: size_of::<StreamRequest>() as u32,
            sample_rate: 48_000,
            buffer_frames: 128,
            channels: 0,
        };
        let result = InputStream::open(
            None,
            Some("no-such-device"),
            &request,
            ignore,
            std::ptr::null_mut(),
        );
        assert!(result.is_err());
    }
}
