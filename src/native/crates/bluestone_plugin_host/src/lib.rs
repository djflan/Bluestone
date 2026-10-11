//! Bluestone's native plugin host (ADR 0033): loads VST3 and CLAP plugins for `Bluestone.PluginWorker`, and only
//! for it; Bluestone's own process never loads this library.
//!
//! # C ABI (version [`ABI_VERSION`])
//!
//! - Handles are opaque pointers: a module (`bph_module_*`) and a plugin instance (`bph_plugin_*`). An instance
//!   keeps its module's code loaded, so a module may be closed while its instances live.
//! - Functions that can fail return 0 on success and a negative number on failure, with a message in `*error`
//!   (UTF-8, owned by the caller, freed with [`bph_string_free`]). JSON documents and state bytes are owned by the
//!   caller too ([`bph_string_free`], [`bph_bytes_free`]).
//! - Threads: [`bph_plugin_process`] runs in the audio role: one thread at a time per instance, never at the same time
//!   as [`bph_plugin_activate`] or state calls on that instance (the caller serializes them). [`bph_main_wake`] may be
//!   called from any thread. Everything else must be called on the thread that called [`bph_main_init`], which must
//!   be the process's main thread on macOS: plugins and their editors expect their "UI thread" to be the one running
//!   the event loop ([`bph_main_pump`]).
//! - Panics abort the worker process (the workspace builds with `panic = "abort"`), so none crosses the ABI; to
//!   Bluestone that is a plugin crash, which it already survives.

// The vst3 crate's enum constants have different integer types on different platforms, so casts that are no-ops
// on one platform are needed on another.
#![allow(clippy::unnecessary_cast)]

mod clap;
mod clap_host;
mod common;
mod gui;
mod loader;
mod vst3;
mod vst3_host;

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char};
use std::path::Path;
use std::rc::Rc;
use std::time::Duration;

use common::Instance;
pub use common::{BphEvent, BphNotification, BphParamChange, BphProcess};

pub const ABI_VERSION: u32 = 1;

pub const FORMAT_VST3: u32 = 1;
pub const FORMAT_CLAP: u32 = 2;

pub struct ModuleHandle {
    vst3: Option<Rc<vst3::Vst3Module>>,
    clap: Option<Rc<clap::ClapModule>>,
}

pub struct PluginHandle {
    instance: Box<dyn Instance>,
    pending: RefCell<Vec<BphNotification>>,
}

fn set_error(error: *mut *mut c_char, message: impl Into<String>) -> i32 {
    if !error.is_null() {
        let text = CString::new(message.into().replace('\0', " ")).unwrap_or_default();
        // SAFETY: the caller passes a valid out pointer or null.
        unsafe { *error = text.into_raw() };
    }
    -1
}

fn clear_error(error: *mut *mut c_char) {
    if !error.is_null() {
        // SAFETY: as above.
        unsafe { *error = std::ptr::null_mut() };
    }
}

fn json(text: String) -> *mut c_char {
    CString::new(text)
        .map(CString::into_raw)
        .unwrap_or(std::ptr::null_mut())
}

unsafe fn text<'a>(pointer: *const c_char) -> Option<&'a str> {
    // SAFETY: the caller passes a NUL-terminated UTF-8 string or null.
    (!pointer.is_null())
        .then(|| unsafe { CStr::from_ptr(pointer) }.to_str().ok())
        .flatten()
}

unsafe fn plugin<'a>(handle: *mut PluginHandle) -> Option<&'a PluginHandle> {
    // SAFETY: a handle from bph_plugin_create that has not been destroyed, or null.
    unsafe { handle.as_ref() }
}

#[unsafe(no_mangle)]
pub extern "C" fn bph_abi_version() -> u32 {
    ABI_VERSION
}

/// Makes the calling thread the main thread and prepares its event loop. Call once, before anything else.
#[unsafe(no_mangle)]
pub extern "C" fn bph_main_init(error: *mut *mut c_char) -> i32 {
    clear_error(error);
    match gui::init() {
        Ok(()) => 0,
        Err(message) => set_error(error, message),
    }
}

/// Handles window-system events and plugin timers, waiting at most `timeout_ms` for an event. Main thread.
#[unsafe(no_mangle)]
pub extern "C" fn bph_main_pump(timeout_ms: u32) {
    if gui::is_main_thread() {
        gui::pump(Duration::from_millis(timeout_ms as u64));
    }
}

/// Makes a waiting [`bph_main_pump`] return early. Any thread.
#[unsafe(no_mangle)]
pub extern "C" fn bph_main_wake() {
    gui::wake();
}

/// # Safety
/// `text` must come from this library and not have been freed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_string_free(text: *mut c_char) {
    if !text.is_null() {
        // SAFETY: created by CString::into_raw in this library.
        drop(unsafe { CString::from_raw(text) });
    }
}

/// # Safety
/// `data` and `length` must come from [`bph_plugin_save_state`] and not have been freed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_bytes_free(data: *mut u8, length: usize) {
    if !data.is_null() {
        // SAFETY: created from a boxed slice of this length in this library.
        drop(unsafe { Box::from_raw(std::ptr::slice_from_raw_parts_mut(data, length)) });
    }
}

/// Loads a module: `format` 1 is a VST3 bundle (or single-file module), 2 a CLAP file or bundle. Main thread.
///
/// # Safety
/// `path` must be a NUL-terminated UTF-8 string; `module` and `error` valid out pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_module_open(
    format: u32,
    path: *const c_char,
    module: *mut *mut ModuleHandle,
    error: *mut *mut c_char,
) -> i32 {
    clear_error(error);
    if module.is_null() {
        return set_error(error, "No out pointer.");
    }
    if !gui::is_main_thread() {
        return set_error(error, "Modules can only be loaded on the main thread.");
    }
    // SAFETY: per the contract above.
    let Some(path) = (unsafe { text(path) }) else {
        return set_error(error, "The path is not valid UTF-8.");
    };
    let opened = match format {
        FORMAT_VST3 => vst3::Vst3Module::open(Path::new(path)).map(|m| ModuleHandle {
            vst3: Some(m),
            clap: None,
        }),
        FORMAT_CLAP => clap::ClapModule::open(Path::new(path)).map(|m| ModuleHandle {
            vst3: None,
            clap: Some(m),
        }),
        _ => Err(format!("Unknown plugin format {format}.")),
    };
    match opened {
        Ok(handle) => {
            // SAFETY: checked non-null.
            unsafe { *module = Box::into_raw(Box::new(handle)) };
            0
        }
        Err(message) => set_error(error, message),
    }
}

/// The module's plugins as a JSON array of `{id, name, vendor, version, kind, category}`; `kind` is 1 instrument,
/// 2 MIDI effect, 3 audio effect. Main thread.
///
/// # Safety
/// `module` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_module_describe(module: *mut ModuleHandle) -> *mut c_char {
    // SAFETY: per the contract above.
    let Some(module) = (unsafe { module.as_ref() }) else {
        return std::ptr::null_mut();
    };
    let plugins = match (&module.vst3, &module.clap) {
        (Some(m), _) => m.describe(),
        (_, Some(m)) => m.describe(),
        _ => Vec::new(),
    };
    json(common::to_json_plugins(&plugins))
}

/// # Safety
/// `module` must be a live handle (or null); it is invalid afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_module_close(module: *mut ModuleHandle) {
    if !module.is_null() {
        // SAFETY: created by Box::into_raw in bph_module_open.
        drop(unsafe { Box::from_raw(module) });
    }
}

/// Creates and initializes an instance of the plugin with `id` (as described by [`bph_module_describe`]). Main thread.
///
/// # Safety
/// `module` must be a live handle, `id` a NUL-terminated UTF-8 string, `plugin` and `error` valid out pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_create(
    module: *mut ModuleHandle,
    id: *const c_char,
    plugin: *mut *mut PluginHandle,
    error: *mut *mut c_char,
) -> i32 {
    clear_error(error);
    // SAFETY: per the contract above.
    let (Some(module), Some(id)) = (unsafe { module.as_ref() }, unsafe { text(id) }) else {
        return set_error(error, "Invalid module or plugin id.");
    };
    if plugin.is_null() {
        return set_error(error, "No out pointer.");
    }
    let created: Result<Box<dyn Instance>, String> = match (&module.vst3, &module.clap) {
        (Some(m), _) => match m.class_id(id) {
            Some(cid) => {
                vst3::Vst3Instance::create(m.clone(), cid).map(|i| Box::new(i) as Box<dyn Instance>)
            }
            None => Err(format!("The module has no plugin {id}.")),
        },
        (_, Some(m)) => {
            clap::ClapInstance::create(m.clone(), id).map(|i| Box::new(i) as Box<dyn Instance>)
        }
        _ => Err("Empty module.".into()),
    };
    match created {
        Ok(instance) => {
            // SAFETY: checked non-null.
            unsafe {
                *plugin = Box::into_raw(Box::new(PluginHandle {
                    instance,
                    pending: RefCell::new(Vec::new()),
                }))
            };
            0
        }
        Err(message) => set_error(error, message),
    }
}

/// Stops, closes the editor of, and releases an instance. Main thread.
///
/// # Safety
/// `plugin` must be a live handle (or null) that no other thread is using; it is invalid afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_destroy(plugin: *mut PluginHandle) {
    if !plugin.is_null() {
        // SAFETY: created by Box::into_raw in bph_plugin_create.
        drop(unsafe { Box::from_raw(plugin) });
    }
}

/// The parameters as a JSON array of `{id, name, units, default, steps, flags}` with normalized defaults. Main thread.
///
/// # Safety
/// `plugin` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_parameters(plugin: *mut PluginHandle) -> *mut c_char {
    // SAFETY: per the contract above.
    match unsafe { self::plugin(plugin) } {
        Some(p) => json(common::to_json_params(&p.instance.parameters())),
        None => std::ptr::null_mut(),
    }
}

/// Reads a normalized value; 1 when found. Main thread.
///
/// # Safety
/// `plugin` must be a live handle and `value` a valid out pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_get_parameter(
    plugin: *mut PluginHandle,
    id: u32,
    value: *mut f64,
) -> i32 {
    // SAFETY: per the contract above.
    match (
        unsafe { self::plugin(plugin) }.and_then(|p| p.instance.get_parameter(id)),
        value.is_null(),
    ) {
        (Some(v), false) => {
            // SAFETY: checked non-null.
            unsafe { *value = v };
            1
        }
        _ => 0,
    }
}

/// Sets a normalized value; the processor hears about it with the next block. 1 when accepted. Main thread.
///
/// # Safety
/// `plugin` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_set_parameter(
    plugin: *mut PluginHandle,
    id: u32,
    value: f64,
) -> i32 {
    // SAFETY: per the contract above.
    unsafe { self::plugin(plugin) }.is_some_and(|p| p.instance.set_parameter(id, value)) as i32
}

/// Prepares processing. The host side always sees `input_channels` and `output_channels`; a plugin with another
/// layout is adapted (see `ChannelAdapter`). Main thread.
///
/// # Safety
/// `plugin` must be a live handle and `error` a valid out pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_activate(
    plugin: *mut PluginHandle,
    sample_rate: f64,
    max_frames: u32,
    input_channels: u32,
    output_channels: u32,
    error: *mut *mut c_char,
) -> i32 {
    clear_error(error);
    // SAFETY: per the contract above.
    let Some(p) = (unsafe { self::plugin(plugin) }) else {
        return set_error(error, "Invalid plugin.");
    };
    if !(1.0..=1_000_000.0).contains(&sample_rate)
        || max_frames == 0
        || max_frames > 65_536
        || input_channels > 64
        || output_channels > 64
    {
        return set_error(error, "Invalid processing setup.");
    }
    match p
        .instance
        .activate(sample_rate, max_frames, input_channels, output_channels)
    {
        Ok(()) => 0,
        Err(message) => set_error(error, message),
    }
}

/// Processes one block. 1 when the plugin processed it; otherwise the output is silence. Audio role.
///
/// # Safety
/// `plugin` must be a live handle; `block` must describe valid buffers of the sizes it states.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_process(
    plugin: *mut PluginHandle,
    block: *mut BphProcess,
) -> i32 {
    // SAFETY: per the contract above.
    let (Some(p), Some(block)) = (unsafe { self::plugin(plugin) }, unsafe { block.as_mut() })
    else {
        return 0;
    };
    block.out_event_count = 0;
    block.out_events_dropped = 0;
    let ok = p.instance.process(block);
    if !ok {
        for c in 0..block.output_channels {
            block.output_channel(c).fill(0.0);
        }
    }
    ok as i32
}

/// The plugin's own latency in frames. Main thread.
///
/// # Safety
/// `plugin` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_latency(plugin: *mut PluginHandle) -> u32 {
    // SAFETY: per the contract above.
    unsafe { self::plugin(plugin) }
        .map(|p| p.instance.latency())
        .unwrap_or(0)
}

/// Saves the plugin's opaque state (VST3: component and controller state in one versioned container). Main thread.
///
/// # Safety
/// `plugin` must be a live handle; `data`, `length`, and `error` valid out pointers.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_save_state(
    plugin: *mut PluginHandle,
    data: *mut *mut u8,
    length: *mut usize,
    error: *mut *mut c_char,
) -> i32 {
    clear_error(error);
    // SAFETY: per the contract above.
    let Some(p) = (unsafe { self::plugin(plugin) }) else {
        return set_error(error, "Invalid plugin.");
    };
    if data.is_null() || length.is_null() {
        return set_error(error, "No out pointer.");
    }
    match p.instance.save_state() {
        Ok(bytes) => {
            let boxed = bytes.into_boxed_slice();
            // SAFETY: checked non-null.
            unsafe {
                *length = boxed.len();
                *data = Box::into_raw(boxed) as *mut u8;
            }
            0
        }
        Err(message) => set_error(error, message),
    }
}

/// Loads state saved by [`bph_plugin_save_state`]. Main thread.
///
/// # Safety
/// `plugin` must be a live handle; `data` must point to `length` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_load_state(
    plugin: *mut PluginHandle,
    data: *const u8,
    length: usize,
    error: *mut *mut c_char,
) -> i32 {
    clear_error(error);
    // SAFETY: per the contract above.
    let Some(p) = (unsafe { self::plugin(plugin) }) else {
        return set_error(error, "Invalid plugin.");
    };
    let bytes = if data.is_null() || length == 0 {
        &[][..]
    } else {
        // SAFETY: per the contract above.
        unsafe { std::slice::from_raw_parts(data, length) }
    };
    match p.instance.load_state(bytes) {
        Ok(()) => 0,
        Err(message) => set_error(error, message),
    }
}

/// Does the plugin's main-thread work (requests it made, editor close, values its processor reported) and returns up
/// to `capacity` notifications; the rest come with the next call. Call regularly, after [`bph_main_pump`]. Main thread.
///
/// # Safety
/// `plugin` must be a live handle; `notifications` must have room for `capacity` entries.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_idle(
    plugin: *mut PluginHandle,
    notifications: *mut BphNotification,
    capacity: u32,
) -> u32 {
    // SAFETY: per the contract above.
    let Some(p) = (unsafe { self::plugin(plugin) }) else {
        return 0;
    };
    let mut pending = p.pending.borrow_mut();
    p.instance.idle(&mut pending);
    if notifications.is_null() {
        return 0;
    }
    let count = pending.len().min(capacity as usize);
    for (i, n) in pending.drain(..count).enumerate() {
        // SAFETY: below the caller's capacity.
        unsafe { *notifications.add(i) = n };
    }
    count as u32
}

/// 1 when the plugin offers an editor this platform can show. Main thread.
///
/// # Safety
/// `plugin` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_has_editor(plugin: *mut PluginHandle) -> i32 {
    // SAFETY: per the contract above.
    unsafe { self::plugin(plugin) }.is_some_and(|p| p.instance.has_editor()) as i32
}

/// Opens the plugin's editor in a top-level window titled `title` (a no-op when it is open). Main thread.
///
/// # Safety
/// `plugin` must be a live handle; `title` a NUL-terminated UTF-8 string; `error` a valid out pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_open_editor(
    plugin: *mut PluginHandle,
    title: *const c_char,
    error: *mut *mut c_char,
) -> i32 {
    clear_error(error);
    // SAFETY: per the contract above.
    let Some(p) = (unsafe { self::plugin(plugin) }) else {
        return set_error(error, "Invalid plugin.");
    };
    // SAFETY: per the contract above.
    let title = unsafe { text(title) }.unwrap_or("Plugin");
    match p.instance.open_editor(title) {
        Ok(()) => 0,
        Err(message) => set_error(error, message),
    }
}

/// Closes the editor if it is open. Main thread.
///
/// # Safety
/// `plugin` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_close_editor(plugin: *mut PluginHandle) {
    // SAFETY: per the contract above.
    if let Some(p) = unsafe { self::plugin(plugin) } {
        p.instance.close_editor();
    }
}

/// 1 while the editor window is open. Main thread.
///
/// # Safety
/// `plugin` must be a live handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bph_plugin_is_editor_open(plugin: *mut PluginHandle) -> i32 {
    // SAFETY: per the contract above.
    unsafe { self::plugin(plugin) }.is_some_and(|p| p.instance.is_editor_open()) as i32
}
