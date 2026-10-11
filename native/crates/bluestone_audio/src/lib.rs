//! Bluestone's audio device layer: output devices and streams through cpal, behind a small, versioned C ABI.
//!
//! Ownership: `bluestone_audio_open_output` returns a stream handle that the caller owns and must pass to
//! `bluestone_audio_close` exactly once. The render function and user data must stay valid until `close`
//! returns; after that the function is never called again. Control functions may be called from any
//! thread, one at a time per stream. The render function is called on the device's audio thread.
//! Errors: functions return 0 on success and a negative value on failure, with a UTF-8 message written to
//! the caller's buffer when one is given. No Rust panic crosses the ABI (the workspace aborts on panic).

mod devices;
mod json;
mod stream;

use std::ffi::{CStr, c_char, c_void};

pub use stream::{
    BlockInfo, HISTOGRAM_BUCKET_NS, HISTOGRAM_BUCKETS, OutputStream, RenderFn, StreamRequest,
    StreamStatus,
};

/// Bump on any change to a function signature or a `#[repr(C)]` layout.
pub const ABI_VERSION: u32 = 1;

#[unsafe(no_mangle)]
pub extern "C" fn bluestone_audio_abi_version() -> u32 {
    ABI_VERSION
}

/// Writes the output devices as a UTF-8 JSON array into `buffer` (not NUL-terminated) and returns the
/// array's full length in bytes. When that exceeds `capacity`, nothing is written; call again with a larger
/// buffer. A null buffer with capacity 0 just returns the length.
///
/// # Safety
/// `buffer` must be valid for `capacity` bytes of writes, or null with `capacity` 0.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_devices_json(buffer: *mut u8, capacity: usize) -> i64 {
    let json = devices::devices_json();
    let bytes = json.as_bytes();
    if !buffer.is_null() && bytes.len() <= capacity {
        // SAFETY: the caller guarantees `buffer` holds `capacity` bytes, and `bytes.len() <= capacity`.
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buffer, bytes.len()) };
    }
    i64::try_from(bytes.len()).unwrap_or(i64::MAX)
}

/// Opens an output stream (stopped). `device_id` is a NUL-terminated UTF-8 id from the device list, or null
/// or empty for the default output device. Returns null on failure, with the reason in `error`.
///
/// # Safety
/// `request` must point to a valid `StreamRequest`; `device_id` must be null or a NUL-terminated string;
/// `error` must be valid for `error_capacity` bytes or null. `render` and `user_data` must stay valid until
/// the stream is closed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_open_output(
    device_id: *const c_char,
    request: *const StreamRequest,
    render: Option<RenderFn>,
    user_data: *mut c_void,
    error: *mut u8,
    error_capacity: usize,
) -> *mut OutputStream {
    // SAFETY: the caller guarantees `request` is null or valid.
    let Some(request) = (unsafe { request.as_ref() }) else {
        unsafe { write_error(error, error_capacity, "No stream request.") };
        return std::ptr::null_mut();
    };
    if request.struct_size as usize != size_of::<StreamRequest>() {
        unsafe {
            write_error(
                error,
                error_capacity,
                "The stream request has the wrong size.",
            )
        };
        return std::ptr::null_mut();
    }
    let Some(render) = render else {
        unsafe { write_error(error, error_capacity, "No render function.") };
        return std::ptr::null_mut();
    };
    let id = if device_id.is_null() {
        None
    } else {
        // SAFETY: the caller guarantees a NUL-terminated string.
        match unsafe { CStr::from_ptr(device_id) }.to_str() {
            Ok(id) => Some(id),
            Err(_) => {
                unsafe { write_error(error, error_capacity, "The device id is not UTF-8.") };
                return std::ptr::null_mut();
            }
        }
    };
    match OutputStream::open(id, request, render, user_data) {
        Ok(stream) => Box::into_raw(Box::new(stream)),
        Err(message) => {
            unsafe { write_error(error, error_capacity, &message) };
            std::ptr::null_mut()
        }
    }
}

/// Starts (or resumes) the stream.
///
/// # Safety
/// `stream` must be a live handle from `bluestone_audio_open_output` or null; see `write_error` for `error`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_start(
    stream: *mut OutputStream,
    error: *mut u8,
    error_capacity: usize,
) -> i32 {
    // SAFETY: the caller guarantees a live handle or null.
    let Some(stream) = (unsafe { stream.as_ref() }) else {
        return -1;
    };
    match stream.start() {
        Ok(()) => 0,
        Err(message) => {
            unsafe { write_error(error, error_capacity, &message) };
            -2
        }
    }
}

/// Pauses the stream; the render function is not called while paused.
///
/// # Safety
/// As for `bluestone_audio_start`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_stop(
    stream: *mut OutputStream,
    error: *mut u8,
    error_capacity: usize,
) -> i32 {
    // SAFETY: the caller guarantees a live handle or null.
    let Some(stream) = (unsafe { stream.as_ref() }) else {
        return -1;
    };
    match stream.stop() {
        Ok(()) => 0,
        Err(message) => {
            unsafe { write_error(error, error_capacity, &message) };
            -2
        }
    }
}

/// Fills `status` (whose `struct_size` must be set) with the stream's counters. Lock-free; any thread.
///
/// # Safety
/// `stream` must be a live handle or null; `status` must point to a writable `StreamStatus` or be null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_stream_status(
    stream: *const OutputStream,
    status: *mut StreamStatus,
) -> i32 {
    // SAFETY: the caller guarantees both pointers are null or valid.
    let (Some(stream), Some(status)) = (unsafe { stream.as_ref() }, unsafe { status.as_mut() })
    else {
        return -1;
    };
    if status.struct_size as usize != size_of::<StreamStatus>() {
        return -3;
    }
    *status = stream.status();
    0
}

/// Copies the render-duration histogram into `buckets` (bucket i counts render calls that took
/// [i, i + 1) × 10 µs; the last bucket counts everything longer) and returns how many buckets were written.
///
/// # Safety
/// `stream` must be a live handle or null; `buckets` must be valid for `count` writes or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_stream_histogram(
    stream: *const OutputStream,
    buckets: *mut u64,
    count: usize,
) -> i64 {
    // SAFETY: the caller guarantees a live handle or null.
    let Some(stream) = (unsafe { stream.as_ref() }) else {
        return -1;
    };
    if buckets.is_null() {
        return -1;
    }
    // SAFETY: the caller guarantees `count` writable elements.
    let out = unsafe { std::slice::from_raw_parts_mut(buckets, count) };
    stream.histogram(out) as i64
}

/// Stops and frees the stream. When this returns, the render function is never called again.
///
/// # Safety
/// `stream` must be a handle from `bluestone_audio_open_output` that has not been closed, or null.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bluestone_audio_close(stream: *mut OutputStream) {
    if !stream.is_null() {
        // SAFETY: the caller hands back ownership of a handle created by `Box::into_raw`.
        drop(unsafe { Box::from_raw(stream) });
    }
}

/// Writes `message` as UTF-8 plus a terminating NUL, truncated on a character boundary to fit.
///
/// # Safety
/// `error` must be valid for `capacity` bytes of writes, or null.
unsafe fn write_error(error: *mut u8, capacity: usize, message: &str) {
    if error.is_null() || capacity == 0 {
        return;
    }
    let mut end = message.len().min(capacity - 1);
    while !message.is_char_boundary(end) {
        end -= 1;
    }
    // SAFETY: `end + 1 <= capacity` bytes are written.
    unsafe {
        std::ptr::copy_nonoverlapping(message.as_ptr(), error, end);
        *error.add(end) = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};

    #[test]
    fn the_abi_version_is_one() {
        assert_eq!(bluestone_audio_abi_version(), 1);
    }

    #[test]
    fn write_error_truncates_on_a_character_boundary_and_terminates() {
        let mut buffer = [0xFFu8; 5];
        unsafe { write_error(buffer.as_mut_ptr(), buffer.len(), "aé€x") };
        // "a" (1) + "é" (2) fits in 4 bytes; "€" (3) would not.
        assert_eq!(&buffer[..4], &[b'a', 0xC3, 0xA9, 0]);
    }

    #[test]
    fn devices_json_reports_its_length_without_a_buffer() {
        let length = unsafe { bluestone_audio_devices_json(std::ptr::null_mut(), 0) };
        assert!(length >= 2);
        let mut buffer = vec![0u8; length as usize + 4096];
        let written = unsafe { bluestone_audio_devices_json(buffer.as_mut_ptr(), buffer.len()) };
        assert!(written >= 2);
        assert_eq!(buffer[0], b'[');
    }

    #[test]
    fn open_refuses_a_request_of_the_wrong_size() {
        let request = StreamRequest {
            struct_size: 3,
            sample_rate: 0,
            buffer_frames: 0,
            channels: 0,
        };
        let mut error = [0u8; 128];
        let stream = unsafe {
            bluestone_audio_open_output(
                std::ptr::null(),
                &request,
                Some(count),
                std::ptr::null_mut(),
                error.as_mut_ptr(),
                error.len(),
            )
        };
        assert!(stream.is_null());
        assert!(error.starts_with(b"The stream request has the wrong size."));
    }

    static CALLS: AtomicU64 = AtomicU64::new(0);

    unsafe extern "C" fn count(
        _user: *mut c_void,
        buffer: *mut f32,
        frames: u32,
        channels: u32,
        _info: *const BlockInfo,
    ) {
        CALLS.fetch_add(1, Ordering::Relaxed);
        // SAFETY: the stream hands us frames * channels samples.
        let data = unsafe { std::slice::from_raw_parts_mut(buffer, (frames * channels) as usize) };
        data.fill(0.0);
    }

    /// Opens the default device when there is one; hosted CI runners have none, and then this only checks
    /// that the failure is reported as an error, not a crash.
    #[test]
    fn the_default_device_plays_silence_and_counts_callbacks_when_present() {
        let request = StreamRequest {
            struct_size: size_of::<StreamRequest>() as u32,
            sample_rate: 0,
            buffer_frames: 0,
            channels: 0,
        };
        let mut error = [0u8; 256];
        let stream = unsafe {
            bluestone_audio_open_output(
                std::ptr::null(),
                &request,
                Some(count),
                std::ptr::null_mut(),
                error.as_mut_ptr(),
                error.len(),
            )
        };
        if stream.is_null() {
            assert_ne!(error[0], 0, "a failed open must say why");
            return;
        }
        unsafe {
            assert_eq!(
                bluestone_audio_start(stream, error.as_mut_ptr(), error.len()),
                0
            );
            std::thread::sleep(std::time::Duration::from_millis(300));
            let mut status = StreamStatus {
                struct_size: size_of::<StreamStatus>() as u32,
                ..StreamStatus::default()
            };
            assert_eq!(bluestone_audio_stream_status(stream, &mut status), 0);
            assert!(status.callbacks > 0);
            assert!(status.sample_rate > 0);
            assert!(status.render_total_ns > 0);
            let mut buckets = vec![0u64; HISTOGRAM_BUCKETS];
            assert_eq!(
                bluestone_audio_stream_histogram(stream, buckets.as_mut_ptr(), buckets.len()),
                HISTOGRAM_BUCKETS as i64
            );
            assert!(buckets.iter().sum::<u64>() > 0);
            assert_eq!(
                bluestone_audio_stop(stream, error.as_mut_ptr(), error.len()),
                0
            );
            bluestone_audio_close(stream);
        }
        assert!(CALLS.load(Ordering::Relaxed) > 0);
    }
}
