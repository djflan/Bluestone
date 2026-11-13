//! Audio priority for device callback threads the backend leaves at normal priority.
//!
//! cpal raises its WASAPI threads only with its `realtime` feature, which Bluestone does not enable, so those
//! threads compete with the UI and everything else on the machine and miss deadlines when the CPU is busy. ASIO
//! drivers and Core Audio run their callbacks on their own high-priority threads, which are left alone.

/// No callback has run yet, so the thread has not been promoted.
pub const POLICY_UNREPORTED: u32 = 0;
/// The backend's own thread, left as the backend scheduled it.
pub const POLICY_BACKEND: u32 = 1;
/// Registered with the Windows Multimedia Class Scheduler Service as a "Pro Audio" task.
pub const POLICY_MMCSS_PRO_AUDIO: u32 = 2;
/// MMCSS refused; the thread runs at `THREAD_PRIORITY_TIME_CRITICAL`.
pub const POLICY_TIME_CRITICAL: u32 = 3;
/// Both were refused; the thread stays at normal priority.
pub const POLICY_NORMAL: u32 = 4;

/// Whether a stream on `host_name` has callback threads Bluestone promotes itself: Windows hosts other than ASIO.
pub(crate) fn promotes(host_name: Option<&str>) -> bool {
    cfg!(windows) && !host_name.is_some_and(|name| name.eq_ignore_ascii_case("ASIO"))
}

/// Promotes the calling thread to audio priority; returns the `POLICY_*` it got. Called once, from the first callback.
pub(crate) fn promote_current_thread() -> u32 {
    #[cfg(windows)]
    {
        windows::promote()
    }
    #[cfg(not(windows))]
    {
        POLICY_BACKEND
    }
}

#[cfg(windows)]
mod windows {
    use std::ffi::c_void;

    const THREAD_PRIORITY_TIME_CRITICAL: i32 = 15;

    #[link(name = "avrt")]
    unsafe extern "system" {
        fn AvSetMmThreadCharacteristicsW(
            task_name: *const u16,
            task_index: *mut u32,
        ) -> *mut c_void;
    }

    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GetCurrentThread() -> *mut c_void;
        fn SetThreadPriority(thread: *mut c_void, priority: i32) -> i32;
    }

    pub(super) fn promote() -> u32 {
        // "Pro Audio\0" as UTF-16, so the callback does not allocate.
        const TASK: [u16; 10] = [
            b'P' as u16,
            b'r' as u16,
            b'o' as u16,
            b' ' as u16,
            b'A' as u16,
            b'u' as u16,
            b'd' as u16,
            b'i' as u16,
            b'o' as u16,
            0,
        ];
        let mut index = 0u32;
        // SAFETY: TASK is NUL-terminated and `index` is a valid out pointer. The registration lasts until the thread
        // exits, which is when the stream closes.
        if !unsafe { AvSetMmThreadCharacteristicsW(TASK.as_ptr(), &mut index) }.is_null() {
            return super::POLICY_MMCSS_PRO_AUDIO;
        }
        // SAFETY: the pseudo-handle of the calling thread.
        if unsafe { SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL) } != 0 {
            return super::POLICY_TIME_CRITICAL;
        }
        super::POLICY_NORMAL
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn asio_keeps_its_drivers_thread() {
        assert!(!promotes(Some("ASIO")));
        assert!(!promotes(Some("asio")));
    }

    #[test]
    fn other_hosts_are_promoted_only_on_windows() {
        assert_eq!(promotes(None), cfg!(windows));
        assert_eq!(promotes(Some("WASAPI")), cfg!(windows));
        assert_eq!(promotes(Some("CoreAudio")), cfg!(windows));
    }

    #[test]
    fn a_thread_gets_audio_priority_where_bluestone_promotes() {
        let policy = std::thread::spawn(promote_current_thread).join().unwrap();
        if cfg!(windows) {
            assert!(
                matches!(policy, POLICY_MMCSS_PRO_AUDIO | POLICY_TIME_CRITICAL),
                "policy {policy}"
            );
        } else {
            assert_eq!(policy, POLICY_BACKEND);
        }
    }
}
