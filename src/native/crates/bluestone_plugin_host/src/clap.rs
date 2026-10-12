//! CLAP: modules (entry and plugin factory) and instances (params, state, latency, ports, gui).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char, c_void};
use std::path::Path;
use std::rc::Rc;
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};

use clap_sys::audio_buffer::clap_audio_buffer;
use clap_sys::entry::clap_plugin_entry;
use clap_sys::events::*;
use clap_sys::ext::audio_ports::*;
use clap_sys::ext::gui::*;
use clap_sys::ext::latency::*;
use clap_sys::ext::note_ports::*;
use clap_sys::ext::params::*;
use clap_sys::ext::state::*;
use clap_sys::factory::plugin_factory::*;
use clap_sys::fixedpoint::CLAP_BEATTIME_FACTOR;
use clap_sys::plugin::*;
use clap_sys::process::*;
use clap_sys::version::clap_version_is_compatible;

use crate::clap_host::*;
use crate::common::*;
use crate::gui;
use crate::loader::Library;

const STATE_MAGIC: &[u8; 4] = b"BSCL";
const STATE_VERSION: u16 = 1;
const MAX_EVENTS: usize = 2048;

pub struct ClapModule {
    entry: *const clap_plugin_entry,
    factory: *const clap_plugin_factory,
    _library: Option<Library>,
}

impl ClapModule {
    pub fn open(path: &Path) -> Result<Rc<ClapModule>, String> {
        let library = Library::open_clap(path)?;
        let entry = library
            .symbol("clap_entry")
            .ok_or("The module exports no clap_entry; it is not a CLAP module.")?
            as *const clap_plugin_entry;
        // SAFETY: clap_entry is a clap_plugin_entry in every CLAP module, and lives as long as its library.
        unsafe { Self::from_entry(entry, path, Some(library)) }
    }

    /// # Safety
    /// `entry` points at a clap_plugin_entry that lives as long as the module (inside `library` when one is given).
    unsafe fn from_entry(
        entry: *const clap_plugin_entry,
        path: &Path,
        library: Option<Library>,
    ) -> Result<Rc<ClapModule>, String> {
        // SAFETY: the entry's functions are called as the spec says.
        unsafe {
            let e = &*entry;
            if !clap_version_is_compatible(e.clap_version) {
                return Err(format!(
                    "The module uses CLAP {}.{}.{}, which this host does not support.",
                    e.clap_version.major, e.clap_version.minor, e.clap_version.revision
                ));
            }
            let path_text =
                CString::new(path.to_string_lossy().as_bytes()).map_err(|_| "Invalid path.")?;
            if !e.init.is_some_and(|init| init(path_text.as_ptr())) {
                return Err("The module's entry failed to initialize.".into());
            }
            let factory =
                e.get_factory
                    .map(|get| get(CLAP_PLUGIN_FACTORY_ID.as_ptr()))
                    .unwrap_or(std::ptr::null()) as *const clap_plugin_factory;
            if factory.is_null() {
                if let Some(deinit) = e.deinit {
                    deinit();
                }
                return Err("The module has no plugin factory.".into());
            }
            Ok(Rc::new(ClapModule {
                entry,
                factory,
                _library: library,
            }))
        }
    }

    pub fn describe(&self) -> Vec<PluginInfo> {
        let mut plugins = Vec::new();
        // SAFETY: plain factory queries; descriptor strings are the plugin's static C strings.
        unsafe {
            let factory = &*self.factory;
            let (Some(count), Some(get)) =
                (factory.get_plugin_count, factory.get_plugin_descriptor)
            else {
                return plugins;
            };
            for index in 0..count(self.factory) {
                let Some(descriptor) = get(self.factory, index).as_ref() else {
                    continue;
                };
                let features = features(descriptor.features);
                let kind = if features.iter().any(|f| f == "instrument") {
                    plugin_kind::INSTRUMENT
                } else if features.iter().any(|f| f == "note-effect") {
                    plugin_kind::MIDI_EFFECT
                } else {
                    plugin_kind::AUDIO_EFFECT
                };
                plugins.push(PluginInfo {
                    id: text(descriptor.id),
                    name: text(descriptor.name),
                    vendor: text(descriptor.vendor),
                    version: text(descriptor.version),
                    kind,
                    category: features.join("|"),
                });
            }
        }
        plugins.retain(|p| !p.id.is_empty());
        plugins
    }
}

impl Drop for ClapModule {
    fn drop(&mut self) {
        // SAFETY: init succeeded, so deinit is called once.
        unsafe {
            if let Some(deinit) = (*self.entry).deinit {
                deinit();
            }
        }
    }
}

unsafe fn text(pointer: *const c_char) -> String {
    // SAFETY: the plugin's NUL-terminated string, or null.
    if pointer.is_null() {
        String::new()
    } else {
        unsafe { CStr::from_ptr(pointer) }
            .to_string_lossy()
            .trim()
            .to_string()
    }
}

unsafe fn features(list: *const *const c_char) -> Vec<String> {
    let mut result = Vec::new();
    if list.is_null() {
        return result;
    }
    // SAFETY: a NULL-terminated array of C strings.
    unsafe {
        let mut p = list;
        while !(*p).is_null() && result.len() < 64 {
            result.push(text(*p));
            p = p.add(1);
        }
    }
    result
}

#[derive(Clone, Copy)]
struct ParamRange {
    id: u32,
    min: f64,
    max: f64,
    cookie: *mut c_void,
}

impl ParamRange {
    fn plain(&self, normalized: f64) -> f64 {
        self.min + normalized.clamp(0.0, 1.0) * (self.max - self.min)
    }

    fn normalized(&self, plain: f64) -> f64 {
        if self.max > self.min {
            unit_value((plain - self.min) / (self.max - self.min))
        } else {
            0.0
        }
    }
}

struct AudioState {
    adapter: ChannelAdapter,
    input_buffers: Vec<clap_audio_buffer>,
    output_buffers: Vec<clap_audio_buffer>,
    input_events: Box<InputEvents>,
    output_events: Box<OutputEvents>,
    transport: clap_event_transport,
    sample_rate: f64,
    started: bool,
    steady_time: i64,
    notes_as_midi: bool,
    accepts_midi: bool,
    /// The audio thread's copy of the parameter ranges, replaced under the processor lock when they change.
    ranges: HashMap<u32, ParamRange>,
}

struct Editor {
    window: Option<Rc<gui::Window>>,
}

struct Extensions {
    params: *const clap_plugin_params,
    state: *const clap_plugin_state,
    latency: *const clap_plugin_latency,
    audio_ports: *const clap_plugin_audio_ports,
    note_ports: *const clap_plugin_note_ports,
    gui: *const clap_plugin_gui,
}

pub struct ClapInstance {
    plugin: *const clap_plugin,
    ext: Extensions,
    host: Box<Host>,
    ranges: Mutex<HashMap<u32, ParamRange>>,
    audio: Mutex<Option<AudioState>>,
    known_values: RefCell<HashMap<u32, f64>>,
    settings: Cell<Option<(f64, u32, u32, u32)>>,
    editor: RefCell<Option<Editor>>,
    _module: Rc<ClapModule>,
}

impl ClapInstance {
    pub fn create(module: Rc<ClapModule>, id: &str) -> Result<ClapInstance, String> {
        let host = Host::new();
        let id_text = CString::new(id).map_err(|_| "Invalid plugin id.")?;
        // SAFETY: the CLAP creation sequence; every pointer is checked.
        unsafe {
            let factory = &*module.factory;
            let create = factory
                .create_plugin
                .ok_or("The factory cannot create plugins.")?;
            let plugin = create(module.factory, &host.clap, id_text.as_ptr());
            if plugin.is_null() {
                return Err(format!("The module has no plugin {id}."));
            }
            host.shared
                .plugin
                .store(plugin as *mut clap_plugin, Ordering::Release);
            if !(*plugin).init.is_some_and(|init| init(plugin)) {
                // Timers and file descriptors registered during init would otherwise call the destroyed plugin.
                host.shared.unregister_all();
                if let Some(destroy) = (*plugin).destroy {
                    destroy(plugin);
                }
                return Err("The plugin failed to initialize.".into());
            }
            let extension = |name: &CStr| {
                (*plugin)
                    .get_extension
                    .map(|get| get(plugin, name.as_ptr()))
                    .unwrap_or(std::ptr::null())
            };
            let ext = Extensions {
                params: extension(CLAP_EXT_PARAMS) as *const clap_plugin_params,
                state: extension(CLAP_EXT_STATE) as *const clap_plugin_state,
                latency: extension(CLAP_EXT_LATENCY) as *const clap_plugin_latency,
                audio_ports: extension(CLAP_EXT_AUDIO_PORTS) as *const clap_plugin_audio_ports,
                note_ports: extension(CLAP_EXT_NOTE_PORTS) as *const clap_plugin_note_ports,
                gui: extension(CLAP_EXT_GUI) as *const clap_plugin_gui,
            };
            let instance = ClapInstance {
                plugin,
                ext,
                host,
                ranges: Mutex::new(HashMap::new()),
                audio: Mutex::new(None),
                known_values: RefCell::new(HashMap::new()),
                settings: Cell::new(None),
                editor: RefCell::new(None),
                _module: module,
            };
            instance.refresh_ranges();
            Ok(instance)
        }
    }

    fn param_infos(&self) -> Vec<clap_param_info> {
        let mut infos = Vec::new();
        // SAFETY: the plugin's params extension, on the main thread.
        unsafe {
            let Some(params) = self.ext.params.as_ref() else {
                return infos;
            };
            let (Some(count), Some(get_info)) = (params.count, params.get_info) else {
                return infos;
            };
            for index in 0..count(self.plugin) {
                let mut info: clap_param_info = std::mem::zeroed();
                if get_info(self.plugin, index, &mut info) {
                    infos.push(info);
                }
            }
        }
        infos
    }

    fn refresh_ranges(&self) {
        let ranges = self
            .param_infos()
            .iter()
            .map(|i| {
                (
                    i.id,
                    ParamRange {
                        id: i.id,
                        min: i.min_value,
                        max: i.max_value,
                        cookie: i.cookie,
                    },
                )
            })
            .collect();
        if let Ok(mut audio) = self.audio.lock()
            && let Some(audio) = audio.as_mut()
        {
            audio.ranges.clone_from(&ranges);
        }
        if let Ok(mut current) = self.ranges.lock() {
            *current = ranges;
        }
    }

    fn range(&self, id: u32) -> Option<ParamRange> {
        self.ranges.lock().ok().and_then(|r| r.get(&id).copied())
    }

    fn port_channels(&self, input: bool) -> Vec<usize> {
        // SAFETY: the plugin's audio-ports extension, on the main thread.
        unsafe {
            let Some(ports) = self.ext.audio_ports.as_ref() else {
                return Vec::new();
            };
            let (Some(count), Some(get)) = (ports.count, ports.get) else {
                return Vec::new();
            };
            let mut infos: Vec<(bool, usize)> = Vec::new();
            for index in 0..count(self.plugin, input) {
                let mut info: clap_audio_port_info = std::mem::zeroed();
                if get(self.plugin, index, input, &mut info) {
                    infos.push((
                        info.flags & CLAP_AUDIO_PORT_IS_MAIN != 0,
                        info.channel_count as usize,
                    ));
                }
            }
            // The main port goes first: the channel adapter treats the first port as the main one.
            if let Some(main) = infos.iter().position(|p| p.0) {
                infos.swap(0, main);
            }
            infos.into_iter().map(|p| p.1).collect()
        }
    }

    /// The note dialect the plugin's first note input prefers (CLAP or MIDI) and whether it takes MIDI at all.
    fn note_dialects(&self) -> (bool, bool) {
        // SAFETY: the plugin's note-ports extension, on the main thread.
        unsafe {
            let Some(ports) = self.ext.note_ports.as_ref() else {
                return (false, false);
            };
            let (Some(count), Some(get)) = (ports.count, ports.get) else {
                return (false, false);
            };
            if count(self.plugin, true) == 0 {
                return (false, false);
            }
            let mut info: clap_note_port_info = std::mem::zeroed();
            if !get(self.plugin, 0, true, &mut info) {
                return (false, false);
            }
            let midi = info.supported_dialects & CLAP_NOTE_DIALECT_MIDI != 0;
            let clap = info.supported_dialects & CLAP_NOTE_DIALECT_CLAP != 0;
            (
                midi && (!clap || info.preferred_dialect == CLAP_NOTE_DIALECT_MIDI),
                midi,
            )
        }
    }

    /// Delivers parameter values to the plugin outside processing: `flush` in the audio role with the processor
    /// locked when active, on the main thread otherwise. Edits the plugin reports back are collected.
    fn flush(&self, values: &[(u32, f64)]) {
        let Some(params) = (unsafe { self.ext.params.as_ref() }) else {
            return;
        };
        let Some(flush) = params.flush else { return };
        let mut input = InputEvents::new(values.len().max(1));
        for &(id, normalized) in values {
            if let Some(range) = self.range(id) {
                input.push(param_event(0, &range, normalized));
            }
        }
        let mut output = OutputEvents::new(256);
        let guard = self.audio.lock();
        let _role = guard
            .as_ref()
            .ok()
            .and_then(|a| a.as_ref())
            .map(|_| AudioRole::enter());
        // SAFETY: the plugin's flush, with our own lists.
        unsafe { flush(self.plugin, &input.list, &output.list) };
        drop(guard);
        self.collect_edits(&mut output);
    }

    fn collect_edits(&self, output: &mut OutputEvents) {
        for event in output.events.drain(..) {
            // SAFETY: the slot's header says which member it is.
            unsafe {
                if event.header.type_ == CLAP_EVENT_PARAM_VALUE
                    && let Some(range) = self.range(event.param.param_id)
                {
                    self.host.shared.notify(
                        notification_kind::PARAMETER_EDITED,
                        range.id,
                        range.normalized(event.param.value),
                    );
                }
            }
        }
    }

    fn deactivate(&self) {
        let Ok(mut audio) = self.audio.lock() else {
            return;
        };
        if let Some(state) = audio.take() {
            // SAFETY: the CLAP teardown order: stop processing in the audio role, then deactivate on the main thread.
            unsafe {
                if state.started {
                    let _role = AudioRole::enter();
                    if let Some(stop) = (*self.plugin).stop_processing {
                        stop(self.plugin);
                    }
                }
                if let Some(deactivate) = (*self.plugin).deactivate {
                    deactivate(self.plugin);
                }
            }
        }
    }

    fn close_editor_now(&self, notify: bool) {
        let Some(editor) = self.editor.borrow_mut().take() else {
            return;
        };
        // SAFETY: the plugin's gui extension, on the main thread.
        unsafe {
            if let Some(gui) = self.ext.gui.as_ref() {
                if let Some(hide) = gui.hide {
                    hide(self.plugin);
                }
                if let Some(destroy) = gui.destroy {
                    destroy(self.plugin);
                }
            }
        }
        drop(editor);
        self.host.shared.gui_closed.store(false, Ordering::Release);
        if notify {
            self.host
                .shared
                .notify(notification_kind::EDITOR_CLOSED, 0, 0.0);
        }
    }

    fn gui_supports(&self, floating: bool) -> bool {
        let api = CString::new(gui::CLAP_WINDOW_API).unwrap_or_default();
        // SAFETY: the plugin's gui extension, on the main thread.
        unsafe {
            self.ext
                .gui
                .as_ref()
                .and_then(|g| g.is_api_supported)
                .is_some_and(|supported| supported(self.plugin, api.as_ptr(), floating))
        }
    }
}

fn param_event(time: u32, range: &ParamRange, normalized: f64) -> EventSlot {
    EventSlot {
        param: clap_event_param_value {
            header: clap_event_header {
                size: std::mem::size_of::<clap_event_param_value>() as u32,
                time,
                space_id: CLAP_CORE_EVENT_SPACE_ID,
                type_: CLAP_EVENT_PARAM_VALUE,
                flags: 0,
            },
            param_id: range.id,
            cookie: range.cookie,
            note_id: -1,
            port_index: -1,
            channel: -1,
            key: -1,
            value: range.plain(normalized),
        },
    }
}

fn note_event(time: u32, on: bool, channel: u8, key: u8, velocity: u8) -> EventSlot {
    EventSlot {
        note: clap_event_note {
            header: clap_event_header {
                size: std::mem::size_of::<clap_event_note>() as u32,
                time,
                space_id: CLAP_CORE_EVENT_SPACE_ID,
                type_: if on {
                    CLAP_EVENT_NOTE_ON
                } else {
                    CLAP_EVENT_NOTE_OFF
                },
                flags: 0,
            },
            note_id: -1,
            port_index: 0,
            channel: channel as i16,
            key: key as i16,
            velocity: velocity as f64 / 127.0,
        },
    }
}

fn midi_event(time: u32, data: [u8; 3]) -> EventSlot {
    EventSlot {
        midi: clap_event_midi {
            header: clap_event_header {
                size: std::mem::size_of::<clap_event_midi>() as u32,
                time,
                space_id: CLAP_CORE_EVENT_SPACE_ID,
                type_: CLAP_EVENT_MIDI,
                flags: 0,
            },
            port_index: 0,
            data,
        },
    }
}

fn midi_bytes(e: &BphEvent) -> Option<[u8; 3]> {
    let channel = e.channel & 0x0F;
    let status = match e.kind {
        event_kind::NOTE_OFF => 0x80,
        event_kind::NOTE_ON => 0x90,
        event_kind::POLY_PRESSURE => 0xA0,
        event_kind::CONTROL_CHANGE => 0xB0,
        event_kind::PROGRAM_CHANGE => 0xC0,
        event_kind::CHANNEL_PRESSURE => 0xD0,
        event_kind::PITCH_BEND => 0xE0,
        _ => return None,
    };
    Some([status | channel, e.data1 & 0x7F, e.data2 & 0x7F])
}

fn from_midi(offset: i32, data: [u8; 3]) -> Option<BphEvent> {
    let channel = data[0] & 0x0F;
    let (d1, d2) = (data[1] & 0x7F, data[2] & 0x7F);
    let kind = match data[0] & 0xF0 {
        0x80 => event_kind::NOTE_OFF,
        0x90 => event_kind::NOTE_ON,
        0xA0 => event_kind::POLY_PRESSURE,
        0xB0 => event_kind::CONTROL_CHANGE,
        0xC0 => event_kind::PROGRAM_CHANGE,
        0xD0 => event_kind::CHANNEL_PRESSURE,
        0xE0 => event_kind::PITCH_BEND,
        _ => return None,
    };
    Some(BphEvent {
        offset,
        kind,
        channel,
        data1: d1,
        data2: d2,
    })
}

impl Instance for ClapInstance {
    fn parameters(&self) -> Vec<ParamInfo> {
        self.param_infos()
            .iter()
            .map(|info| {
                let stepped = info.flags & CLAP_PARAM_IS_STEPPED != 0;
                let range = ParamRange {
                    id: info.id,
                    min: info.min_value,
                    max: info.max_value,
                    cookie: info.cookie,
                };
                let mut flags = 0;
                if info.flags & CLAP_PARAM_IS_AUTOMATABLE != 0 {
                    flags |= param_flags::AUTOMATABLE;
                }
                if info.flags & CLAP_PARAM_IS_READONLY != 0 {
                    flags |= param_flags::READ_ONLY;
                }
                if info.flags & CLAP_PARAM_IS_HIDDEN != 0 {
                    flags |= param_flags::HIDDEN;
                }
                if info.flags & CLAP_PARAM_IS_BYPASS != 0 {
                    flags |= param_flags::BYPASS;
                }
                if info.flags & CLAP_PARAM_IS_ENUM != 0 {
                    flags |= param_flags::LIST;
                }
                ParamInfo {
                    id: info.id,
                    name: c_chars_to_string(&info.name),
                    units: String::new(),
                    default_value: range.normalized(info.default_value),
                    step_count: if stepped {
                        (info.max_value - info.min_value)
                            .round()
                            .clamp(0.0, i32::MAX as f64) as i32
                    } else {
                        0
                    },
                    flags,
                }
            })
            .collect()
    }

    fn get_parameter(&self, id: u32) -> Option<f64> {
        let range = self.range(id)?;
        // SAFETY: the plugin's params extension, on the main thread.
        unsafe {
            let get = self.ext.params.as_ref()?.get_value?;
            let mut value = 0.0;
            (get(self.plugin, id, &mut value) && value.is_finite()).then(|| range.normalized(value))
        }
    }

    fn set_parameter(&self, id: u32, value: f64) -> bool {
        if !(0.0..=1.0).contains(&value) || self.range(id).is_none() {
            return false;
        }
        self.flush(&[(id, value)]);
        self.known_values.borrow_mut().insert(id, value);
        true
    }

    fn activate(
        &self,
        sample_rate: f64,
        max_frames: u32,
        input_channels: u32,
        output_channels: u32,
    ) -> Result<(), String> {
        self.deactivate();
        let inputs = self.port_channels(true);
        let outputs = self.port_channels(false);
        let (notes_as_midi, accepts_midi) = self.note_dialects();
        // SAFETY: activate on the main thread, with the processor not running.
        unsafe {
            let activate = (*self.plugin)
                .activate
                .ok_or("The plugin cannot be activated.")?;
            if !activate(self.plugin, sample_rate, 1, max_frames) {
                return Err("The plugin could not be activated.".into());
            }
        }
        let mut adapter = ChannelAdapter::new(max_frames as usize, inputs.clone(), outputs.clone());
        let buffers = |counts: &[usize], pointers: &mut Vec<*mut f32>| -> Vec<clap_audio_buffer> {
            let mut offset = 0;
            counts
                .iter()
                .map(|&count| {
                    // SAFETY: the pointer arrays live in the adapter, as long as these buffers.
                    let data32 = unsafe { pointers.as_mut_ptr().add(offset) };
                    offset += count;
                    clap_audio_buffer {
                        data32,
                        data64: std::ptr::null_mut(),
                        channel_count: count as u32,
                        latency: 0,
                        constant_mask: 0,
                    }
                })
                .collect()
        };
        let input_buffers = buffers(&inputs, &mut adapter.input_pointers);
        let output_buffers = buffers(&outputs, &mut adapter.output_pointers);
        // SAFETY: a zeroed transport is valid; the fields used are set below.
        let mut transport: clap_event_transport = unsafe { std::mem::zeroed() };
        transport.header = clap_event_header {
            size: std::mem::size_of::<clap_event_transport>() as u32,
            time: 0,
            space_id: CLAP_CORE_EVENT_SPACE_ID,
            type_: CLAP_EVENT_TRANSPORT,
            flags: 0,
        };
        transport.tsig_num = 4;
        transport.tsig_denom = 4;
        if let Ok(mut audio) = self.audio.lock() {
            *audio = Some(AudioState {
                adapter,
                input_buffers,
                output_buffers,
                input_events: InputEvents::new(MAX_EVENTS),
                output_events: OutputEvents::new(MAX_EVENTS),
                transport,
                sample_rate,
                started: false,
                steady_time: 0,
                notes_as_midi,
                accepts_midi,
                ranges: self.ranges.lock().map(|r| r.clone()).unwrap_or_default(),
            });
        }
        self.settings.set(Some((
            sample_rate,
            max_frames,
            input_channels,
            output_channels,
        )));
        Ok(())
    }

    fn process(&self, block: &mut BphProcess) -> bool {
        let Ok(mut guard) = self.audio.lock() else {
            return false;
        };
        let Some(audio) = guard.as_mut() else {
            return false;
        };
        let frames = block.frames as usize;
        if frames > audio.adapter.max_frames {
            return false;
        }
        let _role = AudioRole::enter();
        // SAFETY: CLAP processing calls in the audio role; every pointer refers to state owned by `audio`.
        unsafe {
            if !audio.started {
                audio.started = (*self.plugin)
                    .start_processing
                    .is_none_or(|start| start(self.plugin));
                if !audio.started {
                    return false;
                }
            }
        }
        audio.input_events.events.clear();
        audio.output_events.events.clear();
        let last = frames.saturating_sub(1) as u32;
        for change in block.changes() {
            if let Some(range) = audio.ranges.get(&change.id) {
                audio.input_events.push(param_event(
                    (change.offset.max(0) as u32).min(last),
                    range,
                    change.value,
                ));
            }
        }
        for e in block.events() {
            let time = (e.offset.max(0) as u32).min(last);
            let slot = match e.kind {
                event_kind::NOTE_ON | event_kind::NOTE_OFF if !audio.notes_as_midi => {
                    Some(note_event(
                        time,
                        e.kind == event_kind::NOTE_ON && e.data2 > 0,
                        e.channel & 15,
                        e.data1 & 127,
                        e.data2 & 127,
                    ))
                }
                _ if audio.accepts_midi => midi_bytes(e).map(|data| midi_event(time, data)),
                _ => None,
            };
            if let Some(slot) = slot {
                audio.input_events.push(slot);
            }
        }
        audio.input_events.sort();
        audio.adapter.prepare_inputs(block);
        audio.adapter.clear_outputs(frames);
        let beats = block.position as f64 / audio.sample_rate * block.tempo / 60.0;
        audio.transport.flags = CLAP_TRANSPORT_HAS_TEMPO
            | CLAP_TRANSPORT_HAS_BEATS_TIMELINE
            | CLAP_TRANSPORT_HAS_TIME_SIGNATURE
            | if block.playing != 0 {
                CLAP_TRANSPORT_IS_PLAYING
            } else {
                0
            };
        audio.transport.tempo = block.tempo;
        audio.transport.song_pos_beats = (beats * CLAP_BEATTIME_FACTOR as f64) as i64;
        audio.transport.bar_start =
            ((beats / 4.0).floor() * 4.0 * CLAP_BEATTIME_FACTOR as f64) as i64;
        audio.transport.bar_number = (beats / 4.0).floor() as i32;
        let process = clap_process {
            steady_time: audio.steady_time,
            frames_count: frames as u32,
            transport: &audio.transport,
            audio_inputs: if audio.input_buffers.is_empty() {
                std::ptr::null()
            } else {
                audio.input_buffers.as_ptr()
            },
            audio_outputs: if audio.output_buffers.is_empty() {
                std::ptr::null_mut()
            } else {
                audio.output_buffers.as_mut_ptr()
            },
            audio_inputs_count: audio.input_buffers.len() as u32,
            audio_outputs_count: audio.output_buffers.len() as u32,
            in_events: &audio.input_events.list,
            out_events: &audio.output_events.list,
        };
        // SAFETY: as above.
        let status = unsafe {
            (*self.plugin)
                .process
                .map(|p| p(self.plugin, &process))
                .unwrap_or(CLAP_PROCESS_ERROR)
        };
        audio.steady_time += frames as i64;
        if status == CLAP_PROCESS_ERROR {
            audio.adapter.clear_outputs(frames);
        }
        audio.adapter.deliver_outputs(block);
        for event in audio.output_events.events.iter() {
            let offset = (event.time().min(last)) as i32;
            // SAFETY: the slot's header says which member it is.
            unsafe {
                match event.header.type_ {
                    CLAP_EVENT_NOTE_ON | CLAP_EVENT_NOTE_OFF => {
                        let n = event.note;
                        if (0..16).contains(&n.channel) && (0..128).contains(&n.key) {
                            let on = n.header.type_ == CLAP_EVENT_NOTE_ON;
                            let velocity = (n.velocity.clamp(0.0, 1.0) * 127.0).round() as u8;
                            let kind = if on {
                                event_kind::NOTE_ON
                            } else {
                                event_kind::NOTE_OFF
                            };
                            block.push_output_event(BphEvent {
                                offset,
                                kind,
                                channel: n.channel as u8,
                                data1: n.key as u8,
                                data2: if on { velocity.max(1) } else { velocity },
                            });
                        }
                    }
                    CLAP_EVENT_MIDI => {
                        if let Some(e) = from_midi(offset, event.midi.data) {
                            block.push_output_event(e);
                        }
                    }
                    CLAP_EVENT_PARAM_VALUE => {
                        if let Some(range) = audio.ranges.get(&event.param.param_id) {
                            self.host.shared.notify(
                                notification_kind::PARAMETER_EDITED,
                                range.id,
                                range.normalized(event.param.value),
                            );
                        }
                    }
                    _ => {}
                }
            }
        }
        status != CLAP_PROCESS_ERROR
    }

    fn latency(&self) -> u32 {
        // SAFETY: the plugin's latency extension, on the main thread.
        unsafe {
            self.ext
                .latency
                .as_ref()
                .and_then(|l| l.get)
                .map(|get| get(self.plugin))
                .unwrap_or(0)
        }
    }

    fn save_state(&self) -> Result<Vec<u8>, String> {
        // SAFETY: the plugin's state extension, on the main thread, with the processor locked.
        let bytes = unsafe {
            let save = self.ext.state.as_ref().and_then(|s| s.save);
            match save {
                Some(save) => {
                    let mut stream = WriteStream::new();
                    let _guard = self.audio.lock();
                    if !save(self.plugin, &stream.stream) {
                        return Err("The plugin did not save its state.".into());
                    }
                    std::mem::take(&mut stream.data)
                }
                None => Vec::new(),
            }
        };
        let mut data = Vec::with_capacity(12 + bytes.len());
        data.extend_from_slice(STATE_MAGIC);
        data.extend_from_slice(&STATE_VERSION.to_le_bytes());
        data.extend_from_slice(&0u16.to_le_bytes());
        data.extend_from_slice(&(bytes.len() as u32).to_le_bytes());
        data.extend_from_slice(&bytes);
        Ok(data)
    }

    fn load_state(&self, data: &[u8]) -> Result<(), String> {
        if data.len() < 12 || &data[..4] != STATE_MAGIC {
            return Err("The CLAP state is malformed.".into());
        }
        let version = u16::from_le_bytes([data[4], data[5]]);
        if version != STATE_VERSION {
            return Err(format!("Unknown CLAP state version {version}."));
        }
        let length = u32::from_le_bytes([data[8], data[9], data[10], data[11]]) as usize;
        if data.len() != 12 + length {
            return Err("The CLAP state is malformed.".into());
        }
        // SAFETY: the plugin's state extension, on the main thread, with the processor locked.
        unsafe {
            let load = self
                .ext
                .state
                .as_ref()
                .and_then(|s| s.load)
                .ok_or("The plugin has no state to load.")?;
            let stream = ReadStream::new(&data[12..]);
            let _guard = self.audio.lock();
            if !load(self.plugin, &stream.stream) {
                return Err("The plugin rejected its saved state.".into());
            }
        }
        self.known_values.borrow_mut().clear();
        Ok(())
    }

    fn idle(&self, notifications: &mut Vec<BphNotification>) {
        if let Some(window) = self
            .editor
            .borrow()
            .as_ref()
            .and_then(|e| e.window.as_ref())
        {
            crate::common::push_editor_keys(window, notifications);
        }
        let shared = &self.host.shared;
        // SAFETY: main-thread plugin calls.
        unsafe {
            if shared.callback_requested.swap(false, Ordering::AcqRel)
                && let Some(on_main_thread) = (*self.plugin).on_main_thread
            {
                on_main_thread(self.plugin);
            }
        }
        if shared.restart_requested.swap(false, Ordering::AcqRel)
            && let Some((rate, frames, inputs, outputs)) = self.settings.get()
            && self.activate(rate, frames, inputs, outputs).is_err()
        {
            self.deactivate();
        }
        if shared.flush_requested.swap(false, Ordering::AcqRel) {
            self.flush(&[]);
        }
        if shared.latency_changed.swap(false, Ordering::AcqRel) {
            notifications.push(BphNotification {
                kind: notification_kind::LATENCY_CHANGED,
                id: 0,
                value: self.latency() as f64,
            });
        }
        let rescan = shared.rescan_flags.swap(0, Ordering::AcqRel);
        if rescan & (CLAP_PARAM_RESCAN_INFO | CLAP_PARAM_RESCAN_ALL) != 0 {
            self.refresh_ranges();
            notifications.push(BphNotification {
                kind: notification_kind::PARAMETERS_CHANGED,
                id: 0,
                value: 0.0,
            });
        }
        if rescan & (CLAP_PARAM_RESCAN_VALUES | CLAP_PARAM_RESCAN_ALL) != 0 {
            let ids: Vec<u32> = self
                .ranges
                .lock()
                .map(|r| r.keys().copied().collect())
                .unwrap_or_default();
            for id in ids {
                if let Some(value) = self.get_parameter(id)
                    && self
                        .known_values
                        .borrow_mut()
                        .insert(id, value)
                        .is_none_or(|old| (old - value).abs() > 1e-9)
                {
                    notifications.push(BphNotification {
                        kind: notification_kind::PARAMETER_EDITED,
                        id,
                        value,
                    });
                }
            }
        }
        let resize = shared.resize_request.lock().ok().and_then(|mut r| r.take());
        if let Some((width, height)) = resize
            && let Some(window) = self.editor.borrow().as_ref().and_then(|e| e.window.clone())
        {
            window.set_content_size(width, height);
            // SAFETY: the plugin's gui extension.
            unsafe {
                if let Some(set_size) = self.ext.gui.as_ref().and_then(|g| g.set_size) {
                    set_size(self.plugin, width, height);
                }
            }
        }
        if shared.gui_closed.load(Ordering::Acquire) && self.editor.borrow().is_some() {
            self.close_editor_now(true);
        }
        if let Ok(mut pending) = shared.notifications.lock() {
            for n in pending
                .iter()
                .filter(|n| n.kind == notification_kind::PARAMETER_EDITED)
            {
                self.known_values.borrow_mut().insert(n.id, n.value);
            }
            notifications.append(&mut pending);
        }
    }

    fn has_editor(&self) -> bool {
        self.gui_supports(false) || self.gui_supports(true)
    }

    fn open_editor(&self, title: &str) -> Result<(), String> {
        if self.editor.borrow().is_some() {
            return Ok(());
        }
        let gui_ext = unsafe { self.ext.gui.as_ref() }.ok_or("The plugin has no editor.")?;
        let api = CString::new(gui::CLAP_WINDOW_API).unwrap_or_default();
        let embedded = self.gui_supports(false);
        if !embedded && !self.gui_supports(true) {
            return Err(format!(
                "The plugin's editor does not support {}.",
                gui::CLAP_WINDOW_API
            ));
        }
        // SAFETY: the CLAP gui sequence on the main thread: create, scale, size, parent, show.
        unsafe {
            let create = gui_ext.create.ok_or("The plugin has no editor.")?;
            if !create(self.plugin, api.as_ptr(), !embedded) {
                return Err("The plugin could not create its editor.".into());
            }
            self.host.shared.gui_closed.store(false, Ordering::Release);
            if !embedded {
                let title_text = CString::new(title).unwrap_or_default();
                if let Some(suggest) = gui_ext.suggest_title {
                    suggest(self.plugin, title_text.as_ptr());
                }
                if let Some(show) = gui_ext.show {
                    show(self.plugin);
                }
                *self.editor.borrow_mut() = Some(Editor { window: None });
                return Ok(());
            }
            let (mut width, mut height) = (640u32, 480u32);
            if let Some(get_size) = gui_ext.get_size {
                get_size(self.plugin, &mut width, &mut height);
            }
            let resizable = gui_ext.can_resize.is_some_and(|can| can(self.plugin));
            let plugin = self.plugin;
            let gui_pointer = self.ext.gui;
            let closed = Arc::downgrade(&self.host.shared);
            let callback: gui::WindowCallback = Rc::new(move |event| match event {
                gui::WindowEvent::CloseRequested => {
                    if let Some(shared) = closed.upgrade() {
                        shared.gui_closed.store(true, Ordering::Release);
                    }
                }
                gui::WindowEvent::Resized { width, height } => {
                    // SAFETY: the plugin's gui extension, on the main thread, while the editor exists.
                    let gui = &*gui_pointer;
                    let (mut w, mut h) = (width, height);
                    if let Some(adjust) = gui.adjust_size {
                        adjust(plugin, &mut w, &mut h);
                    }
                    if let Some(set_size) = gui.set_size {
                        set_size(plugin, w, h);
                    }
                }
            });
            let window = Rc::new(gui::Window::create(
                title, width, height, resizable, callback,
            )?);
            if cfg!(not(target_os = "macos"))
                && let Some(set_scale) = gui_ext.set_scale
            {
                set_scale(self.plugin, window.content_scale());
            }
            let handle = window.parent_handle();
            let parent = clap_window {
                api: api.as_ptr(),
                specific: if cfg!(target_os = "linux") {
                    clap_window_handle {
                        x11: handle as usize as _,
                    }
                } else {
                    clap_window_handle { ptr: handle }
                },
            };
            if !gui_ext
                .set_parent
                .is_some_and(|set_parent| set_parent(self.plugin, &parent))
            {
                if let Some(destroy) = gui_ext.destroy {
                    destroy(self.plugin);
                }
                return Err("The plugin's editor could not be attached to a window.".into());
            }
            if let Some(show) = gui_ext.show {
                show(self.plugin);
            }
            *self.editor.borrow_mut() = Some(Editor {
                window: Some(window),
            });
        }
        Ok(())
    }

    fn close_editor(&self) {
        self.close_editor_now(false);
    }

    fn is_editor_open(&self) -> bool {
        self.editor.borrow().is_some()
    }

    fn set_editor_visible(&self, visible: bool) {
        let editor = self.editor.borrow();
        let Some(editor) = editor.as_ref() else {
            return;
        };
        match &editor.window {
            Some(window) => window.set_visible(visible),
            // A floating editor's window belongs to the plugin: ask it.
            // SAFETY: the plugin's gui extension, on the main thread, while the editor exists.
            None => unsafe {
                if let Some(gui) = self.ext.gui.as_ref() {
                    let call = if visible { gui.show } else { gui.hide };
                    if let Some(call) = call {
                        call(self.plugin);
                    }
                }
            },
        }
    }

    fn route_editor_keys_to_host(&self, to_host: bool) -> bool {
        self.editor
            .borrow()
            .as_ref()
            .and_then(|e| e.window.as_ref())
            .is_some_and(|window| window.route_keys_to_host(to_host))
    }
}

impl Drop for ClapInstance {
    fn drop(&mut self) {
        self.close_editor_now(false);
        self.deactivate();
        self.host.shared.unregister_all();
        // SAFETY: the plugin is destroyed once, after it was deactivated.
        unsafe {
            if let Some(destroy) = (*self.plugin).destroy {
                destroy(self.plugin);
            }
        }
    }
}

#[cfg(test)]
#[path = "clap_tests.rs"]
mod tests;
