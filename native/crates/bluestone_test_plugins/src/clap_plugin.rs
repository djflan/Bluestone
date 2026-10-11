//! The CLAP side: one plugin per kind, with the params, state, audio-ports, note-ports, and latency extensions.

use std::ffi::{CStr, c_char, c_void};
use std::sync::Mutex;

use clap_sys::audio_buffer::clap_audio_buffer;
use clap_sys::entry::clap_plugin_entry;
use clap_sys::events::*;
use clap_sys::ext::audio_ports::*;
use clap_sys::ext::latency::*;
use clap_sys::ext::note_ports::*;
use clap_sys::ext::params::*;
use clap_sys::ext::state::*;
use clap_sys::factory::plugin_factory::*;
use clap_sys::host::clap_host;
use clap_sys::id::{CLAP_INVALID_ID, clap_id};
use clap_sys::plugin::*;
use clap_sys::process::*;
use clap_sys::stream::{clap_istream, clap_ostream};
use clap_sys::version::CLAP_VERSION;

use crate::{Core, Input, Kind, Output};

struct Pointers<const N: usize>([*const c_char; N]);

// SAFETY: the pointers refer to static, immutable C strings.
unsafe impl<const N: usize> Sync for Pointers<N> {}

static GAIN_FEATURES: Pointers<3> = Pointers([
    c"audio-effect".as_ptr(),
    c"stereo".as_ptr(),
    std::ptr::null(),
]);
static SINE_FEATURES: Pointers<3> = Pointers([
    c"instrument".as_ptr(),
    c"synthesizer".as_ptr(),
    std::ptr::null(),
]);
static TRANSPOSE_FEATURES: Pointers<2> = Pointers([c"note-effect".as_ptr(), std::ptr::null()]);

const fn descriptor(
    id: &'static CStr,
    name: &'static CStr,
    features: *const *const c_char,
) -> clap_plugin_descriptor {
    clap_plugin_descriptor {
        clap_version: CLAP_VERSION,
        id: id.as_ptr(),
        name: name.as_ptr(),
        vendor: c"Bluestone".as_ptr(),
        url: c"https://github.com/djflan/Bluestone".as_ptr(),
        manual_url: c"".as_ptr(),
        support_url: c"".as_ptr(),
        version: c"1.0.0".as_ptr(),
        description: c"A Bluestone test plugin".as_ptr(),
        features,
    }
}

static DESCRIPTORS: [clap_plugin_descriptor; 3] = [
    descriptor(
        c"com.bluestone.test.gain",
        c"Test Gain",
        GAIN_FEATURES.0.as_ptr(),
    ),
    descriptor(
        c"com.bluestone.test.sine",
        c"Test Sine",
        SINE_FEATURES.0.as_ptr(),
    ),
    descriptor(
        c"com.bluestone.test.transpose",
        c"Test Transpose",
        TRANSPOSE_FEATURES.0.as_ptr(),
    ),
];

#[repr(C)]
struct Plugin {
    clap: clap_plugin,
    kind: Kind,
    core: Mutex<Core>,
    events: Mutex<Vec<(u32, Input)>>,
}

unsafe fn this<'a>(plugin: *const clap_plugin) -> &'a Plugin {
    // SAFETY: `plugin` is the first field of a Plugin created by create_plugin.
    unsafe { &*(plugin as *const Plugin) }
}

/// The CLAP value range of a parameter: stepped ones count their steps (Semitones is 0..48), others are 0..1.
fn maximum(kind: Kind, id: u32) -> f64 {
    kind.parameters()
        .get(id as usize)
        .map(|p| if p.steps > 0 { p.steps as f64 } else { 1.0 })
        .unwrap_or(1.0)
}

unsafe extern "C" fn init(_plugin: *const clap_plugin) -> bool {
    true
}

unsafe extern "C" fn destroy(plugin: *const clap_plugin) {
    // SAFETY: created by Box::into_raw in create_plugin.
    drop(unsafe { Box::from_raw(plugin as *mut Plugin) });
}

unsafe extern "C" fn activate(
    plugin: *const clap_plugin,
    sample_rate: f64,
    _min: u32,
    _max: u32,
) -> bool {
    // SAFETY: as in `this`.
    if let Ok(mut core) = unsafe { this(plugin) }.core.lock() {
        core.set_sample_rate(sample_rate);
    }
    true
}

unsafe extern "C" fn deactivate(_plugin: *const clap_plugin) {}

unsafe extern "C" fn start_processing(_plugin: *const clap_plugin) -> bool {
    true
}

unsafe extern "C" fn stop_processing(_plugin: *const clap_plugin) {}

unsafe extern "C" fn reset(_plugin: *const clap_plugin) {}

unsafe fn read_events(
    plugin: &Plugin,
    list: *const clap_input_events,
    events: &mut Vec<(u32, Input)>,
) {
    // SAFETY: the host's list, valid for the call.
    let Some(list) = (unsafe { list.as_ref() }) else {
        return;
    };
    let (Some(size), Some(get)) = (list.size, list.get) else {
        return;
    };
    for index in 0..unsafe { size(list) } {
        // SAFETY: as above.
        let header = unsafe { get(list, index) };
        let Some(h) = (unsafe { header.as_ref() }) else {
            continue;
        };
        if h.space_id != CLAP_CORE_EVENT_SPACE_ID {
            continue;
        }
        // SAFETY: the event's type says which struct it is.
        let input = unsafe {
            match h.type_ {
                CLAP_EVENT_NOTE_ON | CLAP_EVENT_NOTE_OFF => {
                    let n = &*(header as *const clap_event_note);
                    Input::Note {
                        on: h.type_ == CLAP_EVENT_NOTE_ON,
                        channel: n.channel.max(0) as u8 & 15,
                        key: n.key.max(0) as u8 & 127,
                        velocity: n.velocity as f32,
                    }
                }
                CLAP_EVENT_MIDI => Input::Midi((*(header as *const clap_event_midi)).data),
                CLAP_EVENT_PARAM_VALUE => {
                    let p = &*(header as *const clap_event_param_value);
                    Input::Parameter {
                        id: p.param_id,
                        value: (p.value / maximum(plugin.kind, p.param_id)).clamp(0.0, 1.0),
                    }
                }
                _ => continue,
            }
        };
        events.push((h.time, input));
    }
}

unsafe extern "C" fn process(
    plugin: *const clap_plugin,
    process: *const clap_process,
) -> clap_process_status {
    // SAFETY: as in `this`; the host's process data, valid for the call.
    let (plugin, process) = unsafe { (this(plugin), &*process) };
    let (Ok(mut core), Ok(mut events)) = (plugin.core.lock(), plugin.events.lock()) else {
        return CLAP_PROCESS_ERROR;
    };
    events.clear();
    // SAFETY: as above.
    unsafe { read_events(plugin, process.in_events, &mut events) };
    events.sort_by_key(|e| e.0);
    let frames = process.frames_count as usize;
    let channels = |buffers: *const clap_audio_buffer, count: u32| -> Vec<*mut f32> {
        if buffers.is_null() || count == 0 {
            return Vec::new();
        }
        // SAFETY: the host's buffers.
        let buffer = unsafe { &*buffers };
        if buffer.data32.is_null() {
            return Vec::new();
        }
        (0..buffer.channel_count as usize)
            .map(|c| unsafe { *buffer.data32.add(c) })
            .collect()
    };
    let input_pointers = channels(process.audio_inputs, process.audio_inputs_count);
    let output_pointers = channels(process.audio_outputs, process.audio_outputs_count);
    // SAFETY: each channel holds `frames` samples for the call.
    let inputs: Vec<&[f32]> = input_pointers
        .iter()
        .map(|&p| unsafe { std::slice::from_raw_parts(p as *const f32, frames) })
        .collect();
    let mut outputs: Vec<&mut [f32]> = output_pointers
        .iter()
        .map(|&p| unsafe { std::slice::from_raw_parts_mut(p, frames) })
        .collect();
    let out = process.out_events;
    core.process(
        frames,
        &inputs,
        &mut outputs,
        &events,
        &mut |offset, output| unsafe { push(out, offset, output) },
    );
    CLAP_PROCESS_CONTINUE
}

unsafe fn push(list: *const clap_output_events, offset: u32, output: Output) {
    // SAFETY: the host's list.
    let Some(list) = (unsafe { list.as_ref() }) else {
        return;
    };
    let Some(try_push) = list.try_push else {
        return;
    };
    match output {
        Output::Note {
            on,
            channel,
            key,
            velocity,
        } => {
            let event = clap_event_note {
                header: clap_event_header {
                    size: std::mem::size_of::<clap_event_note>() as u32,
                    time: offset,
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
                velocity: velocity as f64,
            };
            // SAFETY: as above.
            unsafe { try_push(list, &event.header) };
        }
        Output::Midi(data) => {
            let event = clap_event_midi {
                header: clap_event_header {
                    size: std::mem::size_of::<clap_event_midi>() as u32,
                    time: offset,
                    space_id: CLAP_CORE_EVENT_SPACE_ID,
                    type_: CLAP_EVENT_MIDI,
                    flags: 0,
                },
                port_index: 0,
                data,
            };
            // SAFETY: as above.
            unsafe { try_push(list, &event.header) };
        }
    }
}

unsafe extern "C" fn on_main_thread(_plugin: *const clap_plugin) {}

unsafe extern "C" fn params_count(plugin: *const clap_plugin) -> u32 {
    // SAFETY: as in `this`.
    unsafe { this(plugin) }.kind.parameters().len() as u32
}

unsafe extern "C" fn params_get_info(
    plugin: *const clap_plugin,
    index: u32,
    info: *mut clap_param_info,
) -> bool {
    // SAFETY: as in `this`.
    let kind = unsafe { this(plugin) }.kind;
    let (Some(spec), false) = (kind.parameters().get(index as usize), info.is_null()) else {
        return false;
    };
    // SAFETY: checked non-null.
    let info = unsafe { &mut *info };
    info.id = index;
    info.flags = CLAP_PARAM_IS_AUTOMATABLE
        | if spec.steps > 0 {
            CLAP_PARAM_IS_STEPPED
        } else {
            0
        };
    info.cookie = std::ptr::null_mut();
    copy(spec.name, &mut info.name);
    copy("", &mut info.module);
    info.min_value = 0.0;
    info.max_value = maximum(kind, index);
    info.default_value = spec.default * info.max_value;
    true
}

unsafe extern "C" fn params_get_value(
    plugin: *const clap_plugin,
    id: clap_id,
    value: *mut f64,
) -> bool {
    // SAFETY: as in `this`.
    let plugin = unsafe { this(plugin) };
    match (
        plugin.core.lock().ok().and_then(|c| c.get(id)),
        value.is_null(),
    ) {
        (Some(v), false) => {
            // SAFETY: checked non-null.
            unsafe { *value = v * maximum(plugin.kind, id) };
            true
        }
        _ => false,
    }
}

unsafe extern "C" fn params_value_to_text(
    _plugin: *const clap_plugin,
    _id: clap_id,
    value: f64,
    buffer: *mut c_char,
    capacity: u32,
) -> bool {
    if buffer.is_null() || capacity == 0 {
        return false;
    }
    // SAFETY: the host's buffer of `capacity` characters.
    let target = unsafe { std::slice::from_raw_parts_mut(buffer, capacity as usize) };
    copy(&format!("{value:.3}"), target);
    true
}

unsafe extern "C" fn params_text_to_value(
    _plugin: *const clap_plugin,
    _id: clap_id,
    _text: *const c_char,
    _value: *mut f64,
) -> bool {
    false
}

unsafe extern "C" fn params_flush(
    plugin: *const clap_plugin,
    input: *const clap_input_events,
    _output: *const clap_output_events,
) {
    // SAFETY: as in `this`.
    let plugin = unsafe { this(plugin) };
    let mut events = Vec::new();
    // SAFETY: the host's list.
    unsafe { read_events(plugin, input, &mut events) };
    if let Ok(core) = plugin.core.lock() {
        for (_, event) in events {
            if let Input::Parameter { id, value } = event {
                core.set(id, value);
            }
        }
    }
}

unsafe extern "C" fn state_save(plugin: *const clap_plugin, stream: *const clap_ostream) -> bool {
    // SAFETY: as in `this`; the host's stream.
    let (plugin, Some(stream)) = (unsafe { this(plugin) }, unsafe { stream.as_ref() }) else {
        return false;
    };
    let Some(write) = stream.write else {
        return false;
    };
    let data = plugin.core.lock().map(|c| c.save()).unwrap_or_default();
    let mut written = 0;
    while written < data.len() {
        // SAFETY: as above.
        let n = unsafe {
            write(
                stream,
                data[written..].as_ptr() as *const c_void,
                (data.len() - written) as u64,
            )
        };
        if n <= 0 {
            return false;
        }
        written += n as usize;
    }
    true
}

unsafe extern "C" fn state_load(plugin: *const clap_plugin, stream: *const clap_istream) -> bool {
    // SAFETY: as in `this`; the host's stream.
    let (plugin, Some(stream)) = (unsafe { this(plugin) }, unsafe { stream.as_ref() }) else {
        return false;
    };
    let Some(read) = stream.read else {
        return false;
    };
    let mut data = Vec::new();
    let mut buffer = [0u8; 4096];
    loop {
        // SAFETY: as above.
        let n = unsafe {
            read(
                stream,
                buffer.as_mut_ptr() as *mut c_void,
                buffer.len() as u64,
            )
        };
        if n < 0 {
            return false;
        }
        if n == 0 {
            break;
        }
        data.extend_from_slice(&buffer[..n as usize]);
    }
    plugin.core.lock().map(|c| c.load(&data)).unwrap_or(false)
}

unsafe extern "C" fn audio_ports_count(plugin: *const clap_plugin, is_input: bool) -> u32 {
    // SAFETY: as in `this`.
    let kind = unsafe { this(plugin) }.kind;
    if is_input {
        kind.audio_inputs()
    } else {
        kind.audio_outputs()
    }
}

unsafe extern "C" fn audio_ports_get(
    plugin: *const clap_plugin,
    index: u32,
    is_input: bool,
    info: *mut clap_audio_port_info,
) -> bool {
    if index >= unsafe { audio_ports_count(plugin, is_input) } || info.is_null() {
        return false;
    }
    // SAFETY: checked non-null.
    let info = unsafe { &mut *info };
    info.id = 0;
    copy(if is_input { "Input" } else { "Output" }, &mut info.name);
    info.flags = CLAP_AUDIO_PORT_IS_MAIN;
    info.channel_count = 2;
    info.port_type = CLAP_PORT_STEREO.as_ptr();
    info.in_place_pair = CLAP_INVALID_ID;
    true
}

unsafe extern "C" fn note_ports_count(plugin: *const clap_plugin, is_input: bool) -> u32 {
    // SAFETY: as in `this`.
    let kind = unsafe { this(plugin) }.kind;
    if is_input {
        kind.event_inputs()
    } else {
        kind.event_outputs()
    }
}

unsafe extern "C" fn note_ports_get(
    plugin: *const clap_plugin,
    index: u32,
    is_input: bool,
    info: *mut clap_note_port_info,
) -> bool {
    if index >= unsafe { note_ports_count(plugin, is_input) } || info.is_null() {
        return false;
    }
    // SAFETY: checked non-null.
    let info = unsafe { &mut *info };
    info.id = 0;
    info.supported_dialects = CLAP_NOTE_DIALECT_CLAP | CLAP_NOTE_DIALECT_MIDI;
    info.preferred_dialect = CLAP_NOTE_DIALECT_CLAP;
    copy("Notes", &mut info.name);
    true
}

unsafe extern "C" fn latency_get(_plugin: *const clap_plugin) -> u32 {
    0
}

static PARAMS: clap_plugin_params = clap_plugin_params {
    count: Some(params_count),
    get_info: Some(params_get_info),
    get_value: Some(params_get_value),
    value_to_text: Some(params_value_to_text),
    text_to_value: Some(params_text_to_value),
    flush: Some(params_flush),
};
static STATE: clap_plugin_state = clap_plugin_state {
    save: Some(state_save),
    load: Some(state_load),
};
static AUDIO_PORTS: clap_plugin_audio_ports = clap_plugin_audio_ports {
    count: Some(audio_ports_count),
    get: Some(audio_ports_get),
};
static NOTE_PORTS: clap_plugin_note_ports = clap_plugin_note_ports {
    count: Some(note_ports_count),
    get: Some(note_ports_get),
};
static LATENCY: clap_plugin_latency = clap_plugin_latency {
    get: Some(latency_get),
};

unsafe extern "C" fn get_extension(
    _plugin: *const clap_plugin,
    id: *const c_char,
) -> *const c_void {
    if id.is_null() {
        return std::ptr::null();
    }
    // SAFETY: the host passes a NUL-terminated id.
    let id = unsafe { CStr::from_ptr(id) };
    if id == CLAP_EXT_PARAMS {
        &PARAMS as *const _ as *const c_void
    } else if id == CLAP_EXT_STATE {
        &STATE as *const _ as *const c_void
    } else if id == CLAP_EXT_AUDIO_PORTS {
        &AUDIO_PORTS as *const _ as *const c_void
    } else if id == CLAP_EXT_NOTE_PORTS {
        &NOTE_PORTS as *const _ as *const c_void
    } else if id == CLAP_EXT_LATENCY {
        &LATENCY as *const _ as *const c_void
    } else {
        std::ptr::null()
    }
}

fn copy(text: &str, target: &mut [c_char]) {
    let len = text.len().min(target.len() - 1);
    for (slot, byte) in target.iter_mut().zip(text.as_bytes()[..len].iter()) {
        *slot = *byte as c_char;
    }
    target[len] = 0;
}

unsafe extern "C" fn factory_count(_factory: *const clap_plugin_factory) -> u32 {
    DESCRIPTORS.len() as u32
}

unsafe extern "C" fn factory_descriptor(
    _factory: *const clap_plugin_factory,
    index: u32,
) -> *const clap_plugin_descriptor {
    DESCRIPTORS
        .get(index as usize)
        .map(|d| d as *const _)
        .unwrap_or(std::ptr::null())
}

unsafe extern "C" fn factory_create(
    _factory: *const clap_plugin_factory,
    host: *const clap_host,
    id: *const c_char,
) -> *const clap_plugin {
    if id.is_null() || host.is_null() {
        return std::ptr::null();
    }
    // SAFETY: the host passes a NUL-terminated id.
    let id = unsafe { CStr::from_ptr(id) };
    // SAFETY: descriptor ids are static C strings.
    let Some(index) = DESCRIPTORS
        .iter()
        .position(|d| unsafe { CStr::from_ptr(d.id) } == id)
    else {
        return std::ptr::null();
    };
    let kind = Kind::ALL[index];
    let plugin = Box::new(Plugin {
        clap: clap_plugin {
            desc: &DESCRIPTORS[index],
            plugin_data: std::ptr::null_mut(),
            init: Some(init),
            destroy: Some(destroy),
            activate: Some(activate),
            deactivate: Some(deactivate),
            start_processing: Some(start_processing),
            stop_processing: Some(stop_processing),
            reset: Some(reset),
            process: Some(process),
            get_extension: Some(get_extension),
            on_main_thread: Some(on_main_thread),
        },
        kind,
        core: Mutex::new(Core::new(kind)),
        events: Mutex::new(Vec::with_capacity(1024)),
    });
    Box::into_raw(plugin) as *const clap_plugin
}

static FACTORY: clap_plugin_factory = clap_plugin_factory {
    get_plugin_count: Some(factory_count),
    get_plugin_descriptor: Some(factory_descriptor),
    create_plugin: Some(factory_create),
};

unsafe extern "C" fn entry_init(_path: *const c_char) -> bool {
    true
}

unsafe extern "C" fn entry_deinit() {}

unsafe extern "C" fn entry_get_factory(id: *const c_char) -> *const c_void {
    // SAFETY: the host passes a NUL-terminated id.
    if !id.is_null() && unsafe { CStr::from_ptr(id) } == CLAP_PLUGIN_FACTORY_ID {
        &FACTORY as *const _ as *const c_void
    } else {
        std::ptr::null()
    }
}

#[allow(non_upper_case_globals)]
#[unsafe(no_mangle)]
pub static clap_entry: clap_plugin_entry = clap_plugin_entry {
    clap_version: CLAP_VERSION,
    init: Some(entry_init),
    deinit: Some(entry_deinit),
    get_factory: Some(entry_get_factory),
};
