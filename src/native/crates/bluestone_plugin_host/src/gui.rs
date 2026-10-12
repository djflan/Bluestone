//! The worker's main thread: the operating system's event loop (needed by plugin editors, and by plugins that
//! expect their "message thread" to run), timers and file-descriptor callbacks plugins register with the host, and
//! top-level editor windows. Everything here except `wake` runs on the main thread only.

use std::cell::RefCell;
use std::ffi::c_void;
use std::rc::Rc;
use std::sync::OnceLock;
use std::thread::ThreadId;
use std::time::{Duration, Instant};

static MAIN_THREAD: OnceLock<ThreadId> = OnceLock::new();

/// Something that happened to an editor window, delivered on the main thread while events are pumped.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WindowEvent {
    /// The content area now has this size (points on macOS, pixels elsewhere).
    Resized { width: u32, height: u32 },
    /// The user asked to close the window; the window stays until its owner drops it.
    CloseRequested,
}

pub type WindowCallback = Rc<dyn Fn(WindowEvent)>;

/// Keys a window has taken from its plugin for the host, oldest first.
pub type KeyQueue = Rc<RefCell<Vec<KeyEvent>>>;

pub use crate::keys::KeyEvent;

/// Whether this platform can give an editor window's keys to the host instead of the plugin. On macOS and Windows the
/// worker pumps its own events, so key events for a window can be taken before they are delivered. On Linux (X11) a
/// plugin's view usually reads input on its own X connection, which the host cannot intercept.
pub const KEY_ROUTING_SUPPORTED: bool = cfg!(any(target_os = "macos", windows));

thread_local! {
    // Windows whose keys go to the host, by their platform identity (window number, HWND).
    static ROUTED: RefCell<Vec<(usize, KeyQueue)>> = const { RefCell::new(Vec::new()) };
}

/// The queue of the routed window `native`, if its keys go to the host. Main thread (the pump).
#[cfg_attr(target_os = "linux", allow(dead_code))]
pub(crate) fn routed_queue(native: usize) -> Option<KeyQueue> {
    ROUTED.with_borrow(|routed| {
        routed
            .iter()
            .find(|(id, _)| *id == native)
            .map(|(_, q)| q.clone())
    })
}

/// Records the calling thread as the main thread and prepares the platform's event loop.
pub fn init() -> Result<(), String> {
    let current = std::thread::current().id();
    let main = *MAIN_THREAD.get_or_init(|| current);
    if main != current {
        return Err("The plugin host was initialized on another thread.".into());
    }
    platform::init()
}

pub fn is_main_thread() -> bool {
    MAIN_THREAD
        .get()
        .is_some_and(|id| *id == std::thread::current().id())
}

/// Handles pending events, waiting at most `timeout` for one, then runs due timers.
pub fn pump(timeout: Duration) {
    let timeout = timeout.min(Timers::time_to_next().unwrap_or(timeout));
    platform::pump(timeout);
    Timers::run_due();
}

/// Makes a waiting `pump` return early. Any thread.
pub fn wake() {
    platform::wake();
}

struct Timer {
    id: u64,
    period: Duration,
    due: Instant,
    callback: Option<Box<dyn FnMut()>>,
}

#[derive(Default)]
struct Timers {
    next_id: u64,
    timers: Vec<Timer>,
}

thread_local! {
    static TIMERS: RefCell<Timers> = RefCell::new(Timers::default());
}

impl Timers {
    fn time_to_next() -> Option<Duration> {
        TIMERS
            .with_borrow(|t| t.timers.iter().map(|t| t.due).min())
            .map(|due| due.saturating_duration_since(Instant::now()))
    }

    fn run_due() {
        let now = Instant::now();
        let due: Vec<u64> = TIMERS.with_borrow(|t| {
            t.timers
                .iter()
                .filter(|t| t.due <= now)
                .map(|t| t.id)
                .collect()
        });
        for id in due {
            // The callback is taken out while it runs, so it may add or remove timers (including itself).
            let callback = TIMERS.with_borrow_mut(|t| {
                t.timers
                    .iter_mut()
                    .find(|t| t.id == id)
                    .and_then(|t| t.callback.take())
            });
            if let Some(mut callback) = callback {
                callback();
                TIMERS.with_borrow_mut(|t| {
                    if let Some(timer) = t.timers.iter_mut().find(|t| t.id == id) {
                        timer.callback = Some(callback);
                        timer.due = Instant::now() + timer.period;
                    }
                });
            }
        }
    }
}

/// Calls `callback` on the main thread every `period` (at the pump's resolution). Returns an id for `remove_timer`.
pub fn add_timer(period: Duration, callback: Box<dyn FnMut()>) -> u64 {
    let period = period.max(Duration::from_millis(1));
    TIMERS.with_borrow_mut(|t| {
        t.next_id += 1;
        let id = t.next_id;
        t.timers.push(Timer {
            id,
            period,
            due: Instant::now() + period,
            callback: Some(callback),
        });
        id
    })
}

pub fn remove_timer(id: u64) -> bool {
    TIMERS.with_borrow_mut(|t| {
        let before = t.timers.len();
        t.timers.retain(|t| t.id != id);
        before != t.timers.len()
    })
}

/// Makes the calling test thread the main thread without starting the platform's event loop.
#[cfg(test)]
pub fn claim_main_thread_for_tests() {
    let current = std::thread::current().id();
    assert_eq!(*MAIN_THREAD.get_or_init(|| current), current);
}

/// Timers registered on the calling thread.
#[cfg(test)]
pub fn timer_count() -> usize {
    TIMERS.with_borrow(|t| t.timers.len())
}

/// A top-level window that hosts a plugin editor.
pub struct Window {
    inner: platform::Window,
    keys: KeyQueue,
}

impl Window {
    pub fn create(
        title: &str,
        width: u32,
        height: u32,
        resizable: bool,
        callback: WindowCallback,
    ) -> Result<Window, String> {
        if !is_main_thread() {
            return Err("Editor windows can only be created on the main thread.".into());
        }
        Ok(Window {
            inner: platform::Window::create(
                title,
                width.max(1),
                height.max(1),
                resizable,
                callback,
            )?,
            keys: Rc::new(RefCell::new(Vec::new())),
        })
    }

    /// Hides the window (keeping the editor and its state) or shows it again without taking focus, moved onto a screen
    /// if the one it was on is gone.
    pub fn set_visible(&self, visible: bool) {
        self.inner.set_visible(visible);
    }

    /// Sends the window's keys to the host (`true`) or to the plugin. False when this platform cannot.
    pub fn route_keys_to_host(&self, to_host: bool) -> bool {
        if !KEY_ROUTING_SUPPORTED {
            return false;
        }
        let native = self.inner.native_id();
        ROUTED.with_borrow_mut(|routed| {
            routed.retain(|(id, _)| *id != native);
            if to_host {
                routed.push((native, self.keys.clone()));
            }
        });
        true
    }

    /// The keys taken from the plugin since the last call.
    pub fn take_keys(&self) -> Vec<KeyEvent> {
        std::mem::take(&mut *self.keys.borrow_mut())
    }

    /// NSView* on macOS, HWND on Windows, the X11 window id on Linux.
    pub fn parent_handle(&self) -> *mut c_void {
        self.inner.parent_handle()
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        self.inner.set_content_size(width.max(1), height.max(1));
    }

    /// Display scale for plugins that size themselves in pixels (Windows, Linux); 1 on macOS, where sizes are points.
    pub fn content_scale(&self) -> f64 {
        self.inner.content_scale()
    }
}

impl Drop for Window {
    fn drop(&mut self) {
        let native = self.inner.native_id();
        ROUTED.with_borrow_mut(|routed| routed.retain(|(id, _)| *id != native));
    }
}

/// Platform names used by VST3 (`IPlugView::attached`) and CLAP (`clap_window.api`).
pub const VST3_PLATFORM_TYPE: &str = if cfg!(target_os = "macos") {
    "NSView"
} else if cfg!(windows) {
    "HWND"
} else {
    "X11EmbedWindowID"
};

pub const CLAP_WINDOW_API: &str = if cfg!(target_os = "macos") {
    "cocoa"
} else if cfg!(windows) {
    "win32"
} else {
    "x11"
};

#[cfg(target_os = "macos")]
#[path = "gui_macos.rs"]
mod platform;

#[cfg(windows)]
#[path = "gui_windows.rs"]
mod platform;

#[cfg(target_os = "linux")]
#[path = "gui_linux.rs"]
mod platform;

#[cfg(target_os = "linux")]
pub use platform::{add_fd, remove_fd};
