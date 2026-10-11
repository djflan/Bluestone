//! What a CLAP plugin gets from its host: the `clap_host` with its callbacks and the host extensions Bluestone offers
//! (thread-check, log, params, state, latency, gui, audio-ports, note-ports, timer-support, and posix-fd-support on
//! Linux), plus the event lists and streams used when processing and saving state.

use std::cell::Cell;
use std::ffi::{CStr, c_char, c_void};
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU32, Ordering};
use std::sync::{Arc, Mutex};

use clap_sys::events::*;
use clap_sys::ext::audio_ports::*;
use clap_sys::ext::gui::*;
use clap_sys::ext::latency::*;
use clap_sys::ext::log::*;
use clap_sys::ext::note_ports::*;
use clap_sys::ext::params::*;
use clap_sys::ext::state::*;
use clap_sys::ext::thread_check::*;
use clap_sys::ext::timer_support::*;
use clap_sys::host::clap_host;
use clap_sys::id::clap_id;
use clap_sys::plugin::clap_plugin;
use clap_sys::stream::{clap_istream, clap_ostream};
use clap_sys::version::CLAP_VERSION;

use crate::common::{BphNotification, notification_kind};
use crate::gui;

thread_local! {
    /// Set while this thread acts as the plugin's audio thread (processing, or flushing with the processor locked).
    static AUDIO_ROLE: Cell<bool> = const { Cell::new(false) };
}

/// Marks the current thread as the audio thread until dropped.
pub struct AudioRole(bool);

impl AudioRole {
    pub fn enter() -> AudioRole {
        AudioRole(AUDIO_ROLE.replace(true))
    }
}

impl Drop for AudioRole {
    fn drop(&mut self) {
        AUDIO_ROLE.set(self.0);
    }
}

/// What the plugin asked for or did, shared between its callbacks (any thread) and the instance.
#[derive(Default)]
pub struct HostShared {
    pub notifications: Mutex<Vec<BphNotification>>,
    pub restart_requested: AtomicBool,
    pub callback_requested: AtomicBool,
    pub flush_requested: AtomicBool,
    pub latency_changed: AtomicBool,
    pub rescan_flags: AtomicU32,
    pub gui_closed: AtomicBool,
    pub resize_request: Mutex<Option<(u32, u32)>>,
    /// The plugin, for timer and file-descriptor callbacks; set once it is created.
    pub plugin: AtomicPtr<clap_plugin>,
    /// Timer ids the plugin registered, with the main loop's ids.
    pub timers: Mutex<Vec<(clap_id, u64)>>,
    #[cfg(target_os = "linux")]
    pub fds: Mutex<Vec<(i32, u64)>>,
}

impl HostShared {
    pub fn notify(&self, kind: u32, id: u32, value: f64) {
        if let Ok(mut list) = self.notifications.lock() {
            list.push(BphNotification { kind, id, value });
        }
    }

    /// Removes every timer and file descriptor the plugin registered. Main thread, before the plugin is destroyed.
    pub fn unregister_all(&self) {
        if let Ok(mut timers) = self.timers.lock() {
            for (_, id) in timers.drain(..) {
                gui::remove_timer(id);
            }
        }
        #[cfg(target_os = "linux")]
        if let Ok(mut fds) = self.fds.lock() {
            for (_, id) in fds.drain(..) {
                gui::remove_fd(id);
            }
        }
    }
}

/// The `clap_host` a plugin instance is created with. Boxed so its address stays fixed.
pub struct Host {
    pub clap: clap_host,
    pub shared: Arc<HostShared>,
}

impl Host {
    pub fn new() -> Box<Host> {
        let shared = Arc::new(HostShared::default());
        let mut host = Box::new(Host {
            clap: clap_host {
                clap_version: CLAP_VERSION,
                host_data: std::ptr::null_mut(),
                name: c"Bluestone".as_ptr(),
                vendor: c"Bluestone".as_ptr(),
                url: c"https://github.com/djflan/Bluestone".as_ptr(),
                version: c"0.1.0".as_ptr(),
                get_extension: Some(get_extension),
                request_restart: Some(request_restart),
                request_process: Some(request_process),
                request_callback: Some(request_callback),
            },
            shared,
        });
        host.clap.host_data = Arc::as_ptr(&host.shared) as *mut c_void;
        host
    }
}

unsafe fn shared<'a>(host: *const clap_host) -> Option<&'a HostShared> {
    // SAFETY: host_data points at the HostShared owned by the Host this clap_host belongs to.
    unsafe {
        host.as_ref()
            .and_then(|h| (h.host_data as *const HostShared).as_ref())
    }
}

unsafe extern "C" fn request_restart(host: *const clap_host) {
    if let Some(s) = unsafe { shared(host) } {
        s.restart_requested.store(true, Ordering::Release);
        gui::wake();
    }
}

unsafe extern "C" fn request_process(_host: *const clap_host) {
    // Bluestone processes every block it is given; there is nothing to start.
}

unsafe extern "C" fn request_callback(host: *const clap_host) {
    if let Some(s) = unsafe { shared(host) } {
        s.callback_requested.store(true, Ordering::Release);
        gui::wake();
    }
}

unsafe extern "C" fn is_main_thread(_host: *const clap_host) -> bool {
    gui::is_main_thread()
}

unsafe extern "C" fn is_audio_thread(_host: *const clap_host) -> bool {
    AUDIO_ROLE.get()
}

static THREAD_CHECK: clap_host_thread_check = clap_host_thread_check {
    is_main_thread: Some(is_main_thread),
    is_audio_thread: Some(is_audio_thread),
};

unsafe extern "C" fn log(
    _host: *const clap_host,
    severity: clap_log_severity,
    message: *const c_char,
) {
    if message.is_null() || severity < CLAP_LOG_WARNING {
        return;
    }
    // SAFETY: the plugin passes a NUL-terminated message. The worker's standard error is kept by Bluestone for diagnostics.
    eprintln!(
        "[clap] {}",
        unsafe { CStr::from_ptr(message) }.to_string_lossy()
    );
}

static LOG: clap_host_log = clap_host_log { log: Some(log) };

unsafe extern "C" fn params_rescan(host: *const clap_host, flags: clap_param_rescan_flags) {
    if let Some(s) = unsafe { shared(host) } {
        s.rescan_flags.fetch_or(flags, Ordering::AcqRel);
        gui::wake();
    }
}

unsafe extern "C" fn params_clear(
    _host: *const clap_host,
    _id: clap_id,
    _flags: clap_param_clear_flags,
) {
}

unsafe extern "C" fn params_request_flush(host: *const clap_host) {
    if let Some(s) = unsafe { shared(host) } {
        s.flush_requested.store(true, Ordering::Release);
        gui::wake();
    }
}

static PARAMS: clap_host_params = clap_host_params {
    rescan: Some(params_rescan),
    clear: Some(params_clear),
    request_flush: Some(params_request_flush),
};

unsafe extern "C" fn state_mark_dirty(host: *const clap_host) {
    if let Some(s) = unsafe { shared(host) } {
        s.notify(notification_kind::STATE_DIRTY, 0, 0.0);
    }
}

static STATE: clap_host_state = clap_host_state {
    mark_dirty: Some(state_mark_dirty),
};

unsafe extern "C" fn latency_changed(host: *const clap_host) {
    if let Some(s) = unsafe { shared(host) } {
        s.latency_changed.store(true, Ordering::Release);
    }
}

static LATENCY: clap_host_latency = clap_host_latency {
    changed: Some(latency_changed),
};

unsafe extern "C" fn gui_resize_hints_changed(_host: *const clap_host) {}

unsafe extern "C" fn gui_request_resize(host: *const clap_host, width: u32, height: u32) -> bool {
    match unsafe { shared(host) } {
        Some(s) => {
            if let Ok(mut request) = s.resize_request.lock() {
                *request = Some((width, height));
            }
            gui::wake();
            true
        }
        None => false,
    }
}

unsafe extern "C" fn gui_request_show(_host: *const clap_host) -> bool {
    true
}

unsafe extern "C" fn gui_request_hide(_host: *const clap_host) -> bool {
    true
}

unsafe extern "C" fn gui_closed(host: *const clap_host, _was_destroyed: bool) {
    if let Some(s) = unsafe { shared(host) } {
        s.gui_closed.store(true, Ordering::Release);
        gui::wake();
    }
}

static GUI: clap_host_gui = clap_host_gui {
    resize_hints_changed: Some(gui_resize_hints_changed),
    request_resize: Some(gui_request_resize),
    request_show: Some(gui_request_show),
    request_hide: Some(gui_request_hide),
    closed: Some(gui_closed),
};

unsafe extern "C" fn audio_ports_is_rescan_flag_supported(
    _host: *const clap_host,
    _flag: u32,
) -> bool {
    false
}

unsafe extern "C" fn audio_ports_rescan(_host: *const clap_host, _flags: u32) {}

static AUDIO_PORTS: clap_host_audio_ports = clap_host_audio_ports {
    is_rescan_flag_supported: Some(audio_ports_is_rescan_flag_supported),
    rescan: Some(audio_ports_rescan),
};

unsafe extern "C" fn note_ports_supported_dialects(_host: *const clap_host) -> clap_note_dialect {
    CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI
}

unsafe extern "C" fn note_ports_rescan(_host: *const clap_host, _flags: u32) {}

static NOTE_PORTS: clap_host_note_ports = clap_host_note_ports {
    supported_dialects: Some(note_ports_supported_dialects),
    rescan: Some(note_ports_rescan),
};

unsafe extern "C" fn timer_register(
    host: *const clap_host,
    period_ms: u32,
    timer_id: *mut clap_id,
) -> bool {
    let Some(s) = (unsafe { shared(host) }) else {
        return false;
    };
    if timer_id.is_null() || !gui::is_main_thread() {
        return false;
    }
    let Ok(mut timers) = s.timers.lock() else {
        return false;
    };
    let id = timers.iter().map(|t| t.0 + 1).max().unwrap_or(1);
    let plugin = s.plugin.load(Ordering::Acquire);
    let main_id = gui::add_timer(
        std::time::Duration::from_millis(period_ms.max(1) as u64),
        Box::new(move || {
            // SAFETY: the timer is removed before the plugin is destroyed.
            unsafe { on_timer(plugin, id) };
        }),
    );
    timers.push((id, main_id));
    // SAFETY: checked non-null.
    unsafe { *timer_id = id };
    true
}

unsafe fn on_timer(plugin: *const clap_plugin, id: clap_id) {
    if plugin.is_null() {
        return;
    }
    // SAFETY: the plugin's own extension, called on the main thread.
    unsafe {
        let Some(get_extension) = (*plugin).get_extension else {
            return;
        };
        let timer = get_extension(plugin, CLAP_EXT_TIMER_SUPPORT.as_ptr())
            as *const clap_plugin_timer_support;
        if let Some(on_timer) = timer.as_ref().and_then(|t| t.on_timer) {
            on_timer(plugin, id);
        }
    }
}

unsafe extern "C" fn timer_unregister(host: *const clap_host, timer_id: clap_id) -> bool {
    let Some(s) = (unsafe { shared(host) }) else {
        return false;
    };
    let Ok(mut timers) = s.timers.lock() else {
        return false;
    };
    let before = timers.len();
    timers.retain(|&(id, main_id)| {
        if id == timer_id {
            gui::remove_timer(main_id);
            false
        } else {
            true
        }
    });
    before != timers.len()
}

static TIMER_SUPPORT: clap_host_timer_support = clap_host_timer_support {
    register_timer: Some(timer_register),
    unregister_timer: Some(timer_unregister),
};

#[cfg(target_os = "linux")]
mod fd_support {
    use super::*;
    use clap_sys::ext::posix_fd_support::*;

    unsafe extern "C" fn register_fd(
        host: *const clap_host,
        fd: i32,
        flags: clap_posix_fd_flags,
    ) -> bool {
        let Some(s) = (unsafe { shared(host) }) else {
            return false;
        };
        let plugin = s.plugin.load(Ordering::Acquire);
        let id = gui::add_fd(
            fd,
            Box::new(move || {
                // SAFETY: the plugin's own extension, called on the main thread while the fd is registered.
                unsafe {
                    let Some(get_extension) = (*plugin).get_extension else {
                        return;
                    };
                    let ext = get_extension(plugin, CLAP_EXT_POSIX_FD_SUPPORT.as_ptr())
                        as *const clap_plugin_posix_fd_support;
                    if let Some(on_fd) = ext.as_ref().and_then(|e| e.on_fd) {
                        on_fd(plugin, fd, flags & CLAP_POSIX_FD_READ);
                    }
                }
            }),
        );
        s.fds.lock().map(|mut f| f.push((fd, id))).is_ok()
    }

    unsafe extern "C" fn modify_fd(
        _host: *const clap_host,
        _fd: i32,
        _flags: clap_posix_fd_flags,
    ) -> bool {
        true
    }

    unsafe extern "C" fn unregister_fd(host: *const clap_host, fd: i32) -> bool {
        let Some(s) = (unsafe { shared(host) }) else {
            return false;
        };
        let Ok(mut fds) = s.fds.lock() else {
            return false;
        };
        fds.retain(|&(f, id)| {
            if f == fd {
                gui::remove_fd(id);
                false
            } else {
                true
            }
        });
        true
    }

    pub static POSIX_FD_SUPPORT: clap_host_posix_fd_support = clap_host_posix_fd_support {
        register_fd: Some(register_fd),
        modify_fd: Some(modify_fd),
        unregister_fd: Some(unregister_fd),
    };

    pub fn matches(id: &CStr) -> bool {
        id == CLAP_EXT_POSIX_FD_SUPPORT
    }
}

unsafe extern "C" fn get_extension(_host: *const clap_host, id: *const c_char) -> *const c_void {
    if id.is_null() {
        return std::ptr::null();
    }
    // SAFETY: the plugin passes a NUL-terminated id.
    let id = unsafe { CStr::from_ptr(id) };
    #[cfg(target_os = "linux")]
    if fd_support::matches(id) {
        return &fd_support::POSIX_FD_SUPPORT as *const _ as *const c_void;
    }
    if id == CLAP_EXT_THREAD_CHECK {
        &THREAD_CHECK as *const _ as *const c_void
    } else if id == CLAP_EXT_LOG {
        &LOG as *const _ as *const c_void
    } else if id == CLAP_EXT_PARAMS {
        &PARAMS as *const _ as *const c_void
    } else if id == CLAP_EXT_STATE {
        &STATE as *const _ as *const c_void
    } else if id == CLAP_EXT_LATENCY {
        &LATENCY as *const _ as *const c_void
    } else if id == CLAP_EXT_GUI {
        &GUI as *const _ as *const c_void
    } else if id == CLAP_EXT_AUDIO_PORTS {
        &AUDIO_PORTS as *const _ as *const c_void
    } else if id == CLAP_EXT_NOTE_PORTS {
        &NOTE_PORTS as *const _ as *const c_void
    } else if id == CLAP_EXT_TIMER_SUPPORT {
        &TIMER_SUPPORT as *const _ as *const c_void
    } else {
        std::ptr::null()
    }
}

/// One event of the kinds Bluestone sends or receives, in a fixed-size slot.
#[repr(C)]
#[derive(Clone, Copy)]
pub union EventSlot {
    pub header: clap_event_header,
    pub note: clap_event_note,
    pub midi: clap_event_midi,
    pub param: clap_event_param_value,
}

impl EventSlot {
    pub fn time(&self) -> u32 {
        // SAFETY: every member starts with the header.
        unsafe { self.header.time }
    }
}

/// A preallocated input event list; used only in the audio role.
pub struct InputEvents {
    pub events: Vec<EventSlot>,
    pub list: clap_input_events,
}

impl InputEvents {
    pub fn new(capacity: usize) -> Box<InputEvents> {
        let mut events = Box::new(InputEvents {
            events: Vec::with_capacity(capacity),
            list: clap_input_events {
                ctx: std::ptr::null_mut(),
                size: Some(input_size),
                get: Some(input_get),
            },
        });
        events.list.ctx = &mut events.events as *mut Vec<EventSlot> as *mut c_void;
        events
    }

    pub fn push(&mut self, event: EventSlot) -> bool {
        if self.events.len() < self.events.capacity() {
            self.events.push(event);
            true
        } else {
            false
        }
    }

    /// Stable insertion sort by time: blocks hold few events, and this must not allocate.
    pub fn sort(&mut self) {
        for i in 1..self.events.len() {
            let current = self.events[i];
            let mut j = i;
            while j > 0 && self.events[j - 1].time() > current.time() {
                self.events[j] = self.events[j - 1];
                j -= 1;
            }
            self.events[j] = current;
        }
    }
}

unsafe extern "C" fn input_size(list: *const clap_input_events) -> u32 {
    // SAFETY: ctx points at the Vec of the InputEvents this list belongs to.
    unsafe { (*((*list).ctx as *const Vec<EventSlot>)).len() as u32 }
}

unsafe extern "C" fn input_get(
    list: *const clap_input_events,
    index: u32,
) -> *const clap_event_header {
    // SAFETY: as above.
    let events = unsafe { &*((*list).ctx as *const Vec<EventSlot>) };
    events
        .get(index as usize)
        .map(|e| unsafe { &e.header } as *const _)
        .unwrap_or(std::ptr::null())
}

/// A preallocated output event list: events the plugin pushes are copied in if Bluestone understands them.
pub struct OutputEvents {
    pub events: Vec<EventSlot>,
    pub list: clap_output_events,
}

impl OutputEvents {
    pub fn new(capacity: usize) -> Box<OutputEvents> {
        let mut events = Box::new(OutputEvents {
            events: Vec::with_capacity(capacity),
            list: clap_output_events {
                ctx: std::ptr::null_mut(),
                try_push: Some(output_try_push),
            },
        });
        events.list.ctx = &mut events.events as *mut Vec<EventSlot> as *mut c_void;
        events
    }
}

unsafe extern "C" fn output_try_push(
    list: *const clap_output_events,
    event: *const clap_event_header,
) -> bool {
    // SAFETY: ctx points at the Vec of the OutputEvents this list belongs to; the event is the plugin's.
    unsafe {
        let events = &mut *((*list).ctx as *mut Vec<EventSlot>);
        let Some(header) = event.as_ref() else {
            return false;
        };
        if header.space_id != CLAP_CORE_EVENT_SPACE_ID || events.len() >= events.capacity() {
            return false;
        }
        let slot = match header.type_ {
            CLAP_EVENT_NOTE_ON | CLAP_EVENT_NOTE_OFF
                if header.size as usize >= std::mem::size_of::<clap_event_note>() =>
            {
                EventSlot {
                    note: *(event as *const clap_event_note),
                }
            }
            CLAP_EVENT_MIDI if header.size as usize >= std::mem::size_of::<clap_event_midi>() => {
                EventSlot {
                    midi: *(event as *const clap_event_midi),
                }
            }
            CLAP_EVENT_PARAM_VALUE
                if header.size as usize >= std::mem::size_of::<clap_event_param_value>() =>
            {
                EventSlot {
                    param: *(event as *const clap_event_param_value),
                }
            }
            // Accepted and ignored: gestures, note ends, and anything else.
            _ => return true,
        };
        events.push(slot);
        true
    }
}

/// A stream the plugin writes its state into.
pub struct WriteStream {
    pub data: Vec<u8>,
    pub stream: clap_ostream,
}

impl WriteStream {
    pub fn new() -> Box<WriteStream> {
        let mut stream = Box::new(WriteStream {
            data: Vec::new(),
            stream: clap_ostream {
                ctx: std::ptr::null_mut(),
                write: Some(stream_write),
            },
        });
        stream.stream.ctx = &mut stream.data as *mut Vec<u8> as *mut c_void;
        stream
    }
}

unsafe extern "C" fn stream_write(
    stream: *const clap_ostream,
    buffer: *const c_void,
    size: u64,
) -> i64 {
    if buffer.is_null() {
        return -1;
    }
    // SAFETY: ctx points at our Vec; the plugin passes `size` readable bytes.
    unsafe {
        let data = &mut *((*stream).ctx as *mut Vec<u8>);
        data.extend_from_slice(std::slice::from_raw_parts(
            buffer as *const u8,
            size as usize,
        ));
    }
    size as i64
}

/// A stream the plugin reads its state from.
pub struct ReadStream<'a> {
    pub data: &'a [u8],
    pub position: usize,
    pub stream: clap_istream,
}

impl<'a> ReadStream<'a> {
    pub fn new(data: &'a [u8]) -> Box<ReadStream<'a>> {
        let mut stream = Box::new(ReadStream {
            data,
            position: 0,
            stream: clap_istream {
                ctx: std::ptr::null_mut(),
                read: Some(stream_read),
            },
        });
        stream.stream.ctx = &mut *stream as *mut ReadStream as *mut c_void;
        stream
    }
}

unsafe extern "C" fn stream_read(
    stream: *const clap_istream,
    buffer: *mut c_void,
    size: u64,
) -> i64 {
    if buffer.is_null() {
        return -1;
    }
    // SAFETY: ctx points at our ReadStream; the plugin's buffer holds `size` bytes.
    unsafe {
        let reader = &mut *((*stream).ctx as *mut ReadStream);
        let count = (size as usize).min(reader.data.len() - reader.position);
        std::ptr::copy_nonoverlapping(
            reader.data.as_ptr().add(reader.position),
            buffer as *mut u8,
            count,
        );
        reader.position += count;
        count as i64
    }
}
