//! VST3: modules (factories) and instances (component, processor, controller, editor).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::ffi::{CString, c_char, c_void};
use std::path::Path;
use std::rc::{Rc, Weak};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use vst3::Steinberg::Vst::*;
use vst3::Steinberg::*;
use vst3::{ComPtr, ComRef, ComWrapper, Interface};

use crate::common::*;
use crate::gui;
use crate::loader::Library;
use crate::vst3_host::*;

const STATE_MAGIC: &[u8; 4] = b"BSV3";
const STATE_VERSION: u16 = 1;
const AUDIO_MODULE_CLASS: &str = "Audio Module Class";
const MAX_EVENTS: usize = 2048;
const MAX_CHANGED_PARAMETERS: usize = 512;
const MAX_POINTS: usize = 64;
const CONTROLLERS: usize = 131; // CC 0-127, aftertouch, pitch bend, program change
/// An instance that processed a block this recently is streaming: edits wait for its next block instead of a flush.
const STREAMING_WINDOW: Duration = Duration::from_millis(500);
const EVENT_NOTE_ON: u16 = Event_::EventTypes_::kNoteOnEvent as u16;
const EVENT_NOTE_OFF: u16 = Event_::EventTypes_::kNoteOffEvent as u16;
const EVENT_POLY_PRESSURE: u16 = Event_::EventTypes_::kPolyPressureEvent as u16;
const EVENT_LEGACY_CC: u16 = Event_::EventTypes_::kLegacyMIDICCOutEvent as u16;
const CTRL_AFTERTOUCH: u32 = ControllerNumbers_::kAfterTouch as u32;
const CTRL_PITCH_BEND: u32 = ControllerNumbers_::kPitchBend as u32;
const CTRL_PROGRAM_CHANGE: u32 = ControllerNumbers_::kCtrlProgramChange as u32;

pub struct Vst3Module {
    factory: Option<ComPtr<IPluginFactory>>,
    host: ComWrapper<HostApplication>,
    exit: Option<unsafe extern "C" fn() -> bool>,
    // Dropped last: the code above lives in it.
    _library: Library,
}

type EntryWithHandle = unsafe extern "C" fn(*mut c_void) -> bool;
type EntryNoArgs = unsafe extern "C" fn() -> bool;
type GetFactory = unsafe extern "C" fn() -> *mut IPluginFactory;

impl Vst3Module {
    pub fn open(path: &Path) -> Result<Rc<Vst3Module>, String> {
        let library = Library::open_vst3(path)?;
        // SAFETY: the module entry points have these signatures in the VST3 module ABI.
        unsafe {
            let (entry_ok, exit) = if cfg!(target_os = "macos") {
                let entry = library
                    .symbol("bundleEntry")
                    .or_else(|| library.symbol("BundleEntry"));
                let exit = library
                    .symbol("bundleExit")
                    .or_else(|| library.symbol("BundleExit"));
                (
                    entry.map(|e| {
                        std::mem::transmute::<*mut c_void, EntryWithHandle>(e)(library.raw_handle())
                    }),
                    exit,
                )
            } else if cfg!(windows) {
                (
                    library
                        .symbol("InitDll")
                        .map(|e| std::mem::transmute::<*mut c_void, EntryNoArgs>(e)()),
                    library.symbol("ExitDll"),
                )
            } else {
                (
                    library.symbol("ModuleEntry").map(|e| {
                        std::mem::transmute::<*mut c_void, EntryWithHandle>(e)(library.raw_handle())
                    }),
                    library.symbol("ModuleExit"),
                )
            };
            if entry_ok == Some(false) {
                return Err("The module's entry function failed.".into());
            }
            let get_factory = library
                .symbol("GetPluginFactory")
                .ok_or("The module exports no GetPluginFactory; it is not a VST3 module.")?;
            let factory = std::mem::transmute::<*mut c_void, GetFactory>(get_factory)();
            let factory =
                ComPtr::from_raw(factory).ok_or("The module returned no plugin factory.")?;
            let host = ComWrapper::new(HostApplication);
            if let (Some(factory3), Some(context)) = (
                factory.cast::<IPluginFactory3>(),
                host.to_com_ptr::<FUnknown>(),
            ) {
                factory3.setHostContext(context.as_ptr());
            }
            Ok(Rc::new(Vst3Module {
                factory: Some(factory),
                host,
                exit: exit.map(|e| std::mem::transmute::<*mut c_void, EntryNoArgs>(e)),
                _library: library,
            }))
        }
    }

    /// The module's audio processor classes. Each one is instantiated briefly to tell instruments, audio effects,
    /// and MIDI effects apart (a VST3 category alone cannot say "MIDI effect").
    pub fn describe(self: &Rc<Self>) -> Vec<PluginInfo> {
        let mut plugins = Vec::new();
        // SAFETY: plain factory queries with properly sized out structures.
        unsafe {
            let mut factory_info: PFactoryInfo = std::mem::zeroed();
            let factory_vendor = if self.factory().getFactoryInfo(&mut factory_info) == kResultOk {
                c_chars_to_string(&factory_info.vendor)
            } else {
                String::new()
            };
            let factory2 = self.factory().cast::<IPluginFactory2>();
            for index in 0..self.factory().countClasses() {
                let mut info: PClassInfo = std::mem::zeroed();
                if self.factory().getClassInfo(index, &mut info) != kResultOk
                    || c_chars_to_string(&info.category) != AUDIO_MODULE_CLASS
                {
                    continue;
                }
                let (mut vendor, mut version, mut sub_categories) =
                    (factory_vendor.clone(), String::new(), String::new());
                if let Some(factory2) = &factory2 {
                    let mut info2: PClassInfo2 = std::mem::zeroed();
                    if factory2.getClassInfo2(index, &mut info2) == kResultOk {
                        let class_vendor = c_chars_to_string(&info2.vendor);
                        if !class_vendor.is_empty() {
                            vendor = class_vendor;
                        }
                        version = c_chars_to_string(&info2.version);
                        sub_categories = c_chars_to_string(&info2.subCategories);
                    }
                }
                let mut kind = if sub_categories.split('|').any(|c| c == "Instrument") {
                    plugin_kind::INSTRUMENT
                } else {
                    plugin_kind::AUDIO_EFFECT
                };
                if let Ok(instance) = Vst3Instance::create(self.clone(), info.cid) {
                    let (audio_in, audio_out, event_in, event_out) = instance.bus_counts();
                    if audio_out == 0 && event_out > 0 && event_in > 0 {
                        kind = plugin_kind::MIDI_EFFECT;
                    } else if audio_in == 0
                        && audio_out > 0
                        && event_in > 0
                        && kind == plugin_kind::AUDIO_EFFECT
                    {
                        kind = plugin_kind::INSTRUMENT;
                    }
                }
                plugins.push(PluginInfo {
                    id: class_id_to_string(&info.cid),
                    name: c_chars_to_string(&info.name),
                    vendor,
                    version,
                    kind,
                    category: sub_categories,
                });
            }
        }
        plugins
    }

    fn factory(&self) -> &ComPtr<IPluginFactory> {
        self.factory
            .as_ref()
            .expect("the factory lives until the module is dropped")
    }

    pub fn class_id(&self, id: &str) -> Option<TUID> {
        let bytes = parse_class_id(id)?;
        // SAFETY: plain factory queries.
        unsafe {
            for index in 0..self.factory().countClasses() {
                let mut info: PClassInfo = std::mem::zeroed();
                if self.factory().getClassInfo(index, &mut info) == kResultOk
                    && info.cid.iter().map(|&c| c as u8).eq(bytes.iter().copied())
                {
                    return Some(info.cid);
                }
            }
        }
        None
    }
}

impl Drop for Vst3Module {
    fn drop(&mut self) {
        // The factory is released before the module's exit function runs.
        drop(self.factory.take());
        if let Some(exit) = self.exit {
            // SAFETY: the module's exit function, called once, after every object from it is gone.
            unsafe { exit() };
        }
    }
}

pub fn class_id_to_string(cid: &TUID) -> String {
    cid.iter().map(|&b| format!("{:02X}", b as u8)).collect()
}

fn parse_class_id(id: &str) -> Option<[u8; 16]> {
    if id.len() != 32 {
        return None;
    }
    let mut bytes = [0u8; 16];
    for (i, byte) in bytes.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&id[i * 2..i * 2 + 2], 16).ok()?;
    }
    Some(bytes)
}

struct AudioState {
    adapter: ChannelAdapter,
    input_buses: Vec<AudioBusBuffers>,
    output_buses: Vec<AudioBusBuffers>,
    input_events: ComWrapper<EventList>,
    output_events: ComWrapper<EventList>,
    input_changes: ComWrapper<ParamChanges>,
    output_changes: ComWrapper<ParamChanges>,
    context: ProcessContext,
    sample_rate: f64,
    has_event_input: bool,
    controller_map: Vec<Option<u32>>,
    last_block: Option<LastBlock>,
    /// Events a flush block produced, passed on at the start of the next real block.
    carried_events: Vec<BphEvent>,
}

/// The transport at the end of the last real block, and when it was processed.
#[derive(Clone, Copy)]
struct LastBlock {
    at: Instant,
    position: i64,
    tempo: f64,
    playing: u32,
}

struct Editor {
    view: ComPtr<IPlugView>,
    frame: ComWrapper<PlugFrame>,
    // Kept for its lifetime: dropping it closes the window.
    window: Rc<gui::Window>,
    close_requested: Rc<Cell<bool>>,
}

pub struct Vst3Instance {
    component: ComPtr<IComponent>,
    processor: ComPtr<IAudioProcessor>,
    controller: Option<ComPtr<IEditController>>,
    separate_controller: bool,
    connection: Option<(ComPtr<IConnectionPoint>, ComPtr<IConnectionPoint>)>,
    handler: ComWrapper<ComponentHandler>,
    shared: Arc<Shared>,
    audio: Mutex<Option<AudioState>>,
    known_values: RefCell<HashMap<u32, f64>>,
    editor: RefCell<Option<Editor>>,
    module: Rc<Vst3Module>,
}

impl Vst3Instance {
    pub fn create(module: Rc<Vst3Module>, cid: TUID) -> Result<Vst3Instance, String> {
        // SAFETY: standard VST3 instantiation sequence; every pointer is checked.
        unsafe {
            let mut object: *mut c_void = std::ptr::null_mut();
            let result = module.factory().createInstance(
                cid.as_ptr(),
                IComponent::IID.as_ptr() as *const c_char,
                &mut object,
            );
            if result != kResultOk || object.is_null() {
                return Err(format!(
                    "The factory could not create the component (result {result})."
                ));
            }
            let component = ComPtr::from_raw(object as *mut IComponent).ok_or("No component.")?;
            let context = module
                .host
                .to_com_ptr::<FUnknown>()
                .ok_or("No host context.")?;
            if component.initialize(context.as_ptr()) != kResultOk {
                return Err("The component failed to initialize.".into());
            }
            let processor = component
                .cast::<IAudioProcessor>()
                .ok_or("The component is not an audio processor.")?;
            let mut separate_controller = false;
            let controller = match component.cast::<IEditController>() {
                Some(controller) => Some(controller),
                None => {
                    let mut controller_cid: TUID = [0; 16];
                    if component.getControllerClassId(&mut controller_cid) == kResultOk
                        && controller_cid.iter().any(|&b| b != 0)
                    {
                        let mut object: *mut c_void = std::ptr::null_mut();
                        let created = module.factory().createInstance(
                            controller_cid.as_ptr(),
                            IEditController::IID.as_ptr() as *const c_char,
                            &mut object,
                        );
                        match (
                            created == kResultOk,
                            ComPtr::from_raw(object as *mut IEditController),
                        ) {
                            (true, Some(controller)) => {
                                if controller.initialize(context.as_ptr()) == kResultOk {
                                    separate_controller = true;
                                    Some(controller)
                                } else {
                                    None
                                }
                            }
                            _ => None,
                        }
                    } else {
                        None
                    }
                }
            };
            let shared = Arc::new(Shared::default());
            let handler = ComWrapper::new(ComponentHandler {
                shared: shared.clone(),
            });
            let mut connection = None;
            if let Some(controller) = &controller {
                if let Some(h) = handler.to_com_ptr::<IComponentHandler>() {
                    controller.setComponentHandler(h.as_ptr());
                }
                if separate_controller {
                    if let (Some(a), Some(b)) = (
                        component.cast::<IConnectionPoint>(),
                        controller.cast::<IConnectionPoint>(),
                    ) {
                        a.connect(b.as_ptr());
                        b.connect(a.as_ptr());
                        connection = Some((a, b));
                    }
                    // The controller starts from the component's state.
                    let stream = ComWrapper::new(MemoryStream::default());
                    if let Some(s) = stream.to_com_ptr::<IBStream>()
                        && component.getState(s.as_ptr()) == kResultOk
                    {
                        let read = ComWrapper::new(MemoryStream::from_bytes(&stream.bytes()));
                        if let Some(r) = read.to_com_ptr::<IBStream>() {
                            controller.setComponentState(r.as_ptr());
                        }
                    }
                }
            }
            Ok(Vst3Instance {
                component,
                processor,
                controller,
                separate_controller,
                connection,
                handler,
                shared,
                audio: Mutex::new(None),
                known_values: RefCell::new(HashMap::new()),
                editor: RefCell::new(None),
                module,
            })
        }
    }

    fn bus_counts(&self) -> (i32, i32, i32, i32) {
        // SAFETY: plain bus queries.
        unsafe {
            (
                self.component.getBusCount(
                    MediaTypes_::kAudio as MediaType,
                    BusDirections_::kInput as BusDirection,
                ),
                self.component.getBusCount(
                    MediaTypes_::kAudio as MediaType,
                    BusDirections_::kOutput as BusDirection,
                ),
                self.component.getBusCount(
                    MediaTypes_::kEvent as MediaType,
                    BusDirections_::kInput as BusDirection,
                ),
                self.component.getBusCount(
                    MediaTypes_::kEvent as MediaType,
                    BusDirections_::kOutput as BusDirection,
                ),
            )
        }
    }

    fn arrangement(&self, direction: BusDirections, index: i32) -> SpeakerArrangement {
        let mut arrangement: SpeakerArrangement = 0;
        // SAFETY: plain query.
        unsafe {
            if self
                .processor
                .getBusArrangement(direction as BusDirection, index, &mut arrangement)
                != kResultOk
            {
                let mut info: BusInfo = std::mem::zeroed();
                if self.component.getBusInfo(
                    MediaTypes_::kAudio as MediaType,
                    direction as BusDirection,
                    index,
                    &mut info,
                ) == kResultOk
                {
                    return if info.channelCount == 1 {
                        SpeakerArr::kMono
                    } else {
                        SpeakerArr::kStereo
                    };
                }
            }
        }
        arrangement
    }

    fn controller_map(&self) -> Vec<Option<u32>> {
        let mut map = vec![None; 16 * CONTROLLERS];
        let Some(mapping) = self
            .controller
            .as_ref()
            .and_then(|c| c.cast::<IMidiMapping>())
        else {
            return map;
        };
        for channel in 0..16 {
            for controller in 0..CONTROLLERS {
                let mut id: ParamID = 0;
                // SAFETY: plain query.
                if unsafe {
                    mapping.getMidiControllerAssignment(
                        0,
                        channel as i16,
                        controller as CtrlNumber,
                        &mut id,
                    )
                } == kResultTrue
                {
                    map[channel * CONTROLLERS + controller] = Some(id);
                }
            }
        }
        map
    }

    fn stop_processing(&self) {
        // SAFETY: plain calls on our own processor.
        unsafe {
            self.processor.setProcessing(0);
            self.component.setActive(0);
        }
    }

    /// Delivers pending controller-side edits to the processor. While blocks are streaming they stay queued for the
    /// next real block (returns true), so a flush never disturbs the stream. Otherwise they are delivered now with a
    /// one-frame block of silence at the last block's transport; its audio is dropped and its events are passed on
    /// with the next real block. The VST3 API also allows an empty block for this, but some plugins ignore one (The
    /// Legend HZ does), and their controller then keeps showing the old value.
    fn flush(&self) -> bool {
        let pending = self
            .shared
            .to_processor
            .lock()
            .map(|p| !p.is_empty())
            .unwrap_or(false);
        if !pending {
            return false;
        }
        let Ok(mut guard) = self.audio.lock() else {
            return false;
        };
        let Some(audio) = guard.as_mut() else {
            return false;
        };
        if audio
            .last_block
            .is_some_and(|b| b.at.elapsed() < STREAMING_WINDOW)
        {
            return true;
        }
        let (position, tempo, playing) = audio
            .last_block
            .map_or((0, 120.0, 0), |b| (b.position, b.tempo, b.playing));
        let mut events = vec![BphEvent::default(); MAX_EVENTS - audio.carried_events.len()];
        let mut block = BphProcess {
            frames: 1,
            input_channels: 0,
            output_channels: 0,
            event_count: 0,
            input: std::ptr::null(),
            output: std::ptr::null_mut(),
            events: std::ptr::null(),
            changes: std::ptr::null(),
            change_count: 0,
            out_event_capacity: events.len() as u32,
            out_events: events.as_mut_ptr(),
            out_event_count: 0,
            out_events_dropped: 0,
            position,
            tempo,
            playing,
            reserved: 0,
        };
        self.process_locked(audio, &mut block);
        let produced = block.out_event_count as usize;
        audio.carried_events.extend_from_slice(&events[..produced]);
        false
    }

    /// Delivers pending edits before the state is read: waits for the streaming blocks to take them, or flushes them
    /// once the stream stops.
    fn deliver_pending_edits(&self) {
        let deadline = Instant::now() + 2 * STREAMING_WINDOW;
        while self.flush() && Instant::now() < deadline {
            std::thread::sleep(Duration::from_millis(1));
        }
    }

    /// One block with the audio state already locked: the parameter changes and events in, the plugin's process call,
    /// and its audio, events, and parameter changes out.
    fn process_locked(&self, audio: &mut AudioState, block: &mut BphProcess) -> bool {
        let frames = block.frames as usize;
        audio.input_changes.clear();
        audio.output_changes.clear();
        audio.input_events.events.borrow_mut().clear();
        audio.output_events.events.borrow_mut().clear();
        if let Ok(mut pending) = self.shared.to_processor.try_lock() {
            for (id, value) in pending.drain(..) {
                audio.input_changes.add(id, 0, value);
            }
        }
        for change in block.changes() {
            audio.input_changes.add(
                change.id,
                change.offset.clamp(0, frames as i32 - 1),
                change.value,
            );
        }
        for e in block.events() {
            let offset = e.offset.clamp(0, frames.saturating_sub(1) as i32);
            let channel = (e.channel & 0x0F) as usize;
            let controller = |number: usize, value: f64| (number, value);
            let mapped = match e.kind {
                event_kind::CONTROL_CHANGE => {
                    Some(controller(e.data1 as usize & 0x7F, e.data2 as f64 / 127.0))
                }
                event_kind::CHANNEL_PRESSURE => Some(controller(
                    ControllerNumbers_::kAfterTouch as usize,
                    e.data1 as f64 / 127.0,
                )),
                event_kind::PITCH_BEND => Some(controller(
                    ControllerNumbers_::kPitchBend as usize,
                    ((e.data2 as u32) << 7 | e.data1 as u32) as f64 / 16383.0,
                )),
                event_kind::PROGRAM_CHANGE => Some(controller(
                    ControllerNumbers_::kCtrlProgramChange as usize,
                    e.data1 as f64 / 127.0,
                )),
                _ => None,
            };
            if let Some((number, value)) = mapped {
                if let Some(Some(id)) = audio.controller_map.get(channel * CONTROLLERS + number) {
                    audio.input_changes.add(*id, offset, value);
                }
                continue;
            }
            if !audio.has_event_input {
                continue;
            }
            // SAFETY: a zeroed Event is valid; the union member matching `type` is filled in.
            let mut event: Event = unsafe { std::mem::zeroed() };
            event.sampleOffset = offset;
            match e.kind {
                event_kind::NOTE_ON if e.data2 > 0 => {
                    event.r#type = EVENT_NOTE_ON;
                    event.__field0.noteOn = NoteOnEvent {
                        channel: channel as i16,
                        pitch: e.data1 as i16,
                        tuning: 0.0,
                        velocity: e.data2 as f32 / 127.0,
                        length: 0,
                        noteId: -1,
                    };
                }
                event_kind::NOTE_ON | event_kind::NOTE_OFF => {
                    event.r#type = EVENT_NOTE_OFF;
                    event.__field0.noteOff = NoteOffEvent {
                        channel: channel as i16,
                        pitch: e.data1 as i16,
                        velocity: e.data2 as f32 / 127.0,
                        noteId: -1,
                        tuning: 0.0,
                    };
                }
                event_kind::POLY_PRESSURE => {
                    event.r#type = EVENT_POLY_PRESSURE;
                    event.__field0.polyPressure = PolyPressureEvent {
                        channel: channel as i16,
                        pitch: e.data1 as i16,
                        pressure: e.data2 as f32 / 127.0,
                        noteId: -1,
                    };
                }
                _ => continue,
            }
            let mut events = audio.input_events.events.borrow_mut();
            if events.len() < MAX_EVENTS {
                events.push(event);
            }
        }
        audio.adapter.prepare_inputs(block);
        audio.adapter.clear_outputs(frames);
        let samples_per_beat = audio.sample_rate * 60.0 / block.tempo.max(1.0);
        audio.context.projectTimeSamples = block.position;
        audio.context.continousTimeSamples = block.position;
        audio.context.tempo = block.tempo;
        audio.context.projectTimeMusic = block.position as f64 / samples_per_beat;
        audio.context.barPositionMusic = (audio.context.projectTimeMusic / 4.0).floor() * 4.0;
        audio.context.state = ProcessContext_::StatesAndFlags_::kTempoValid as u32
            | ProcessContext_::StatesAndFlags_::kProjectTimeMusicValid as u32
            | ProcessContext_::StatesAndFlags_::kBarPositionValid as u32
            | ProcessContext_::StatesAndFlags_::kTimeSigValid as u32
            | if block.playing != 0 {
                ProcessContext_::StatesAndFlags_::kPlaying as u32
            } else {
                0
            };
        // SAFETY: every pointer in ProcessData refers to state owned by `audio`, alive for the call.
        let result = unsafe {
            let mut data: ProcessData = std::mem::zeroed();
            data.processMode = ProcessModes_::kRealtime as i32;
            data.symbolicSampleSize = SymbolicSampleSizes_::kSample32 as i32;
            data.numSamples = frames as i32;
            data.numInputs = audio.input_buses.len() as i32;
            data.numOutputs = audio.output_buses.len() as i32;
            data.inputs = if audio.input_buses.is_empty() {
                std::ptr::null_mut()
            } else {
                audio.input_buses.as_mut_ptr()
            };
            data.outputs = if audio.output_buses.is_empty() {
                std::ptr::null_mut()
            } else {
                audio.output_buses.as_mut_ptr()
            };
            for bus in audio.output_buses.iter_mut() {
                bus.silenceFlags = 0;
            }
            data.inputParameterChanges =
                com_ptr(audio.input_changes.as_com_ref::<IParameterChanges>());
            data.outputParameterChanges =
                com_ptr(audio.output_changes.as_com_ref::<IParameterChanges>());
            data.inputEvents = com_ptr(audio.input_events.as_com_ref::<IEventList>());
            data.outputEvents = com_ptr(audio.output_events.as_com_ref::<IEventList>());
            data.processContext = &mut audio.context;
            self.processor.process(&mut data)
        };
        if result != kResultOk {
            audio.adapter.clear_outputs(frames);
        }
        audio.adapter.deliver_outputs(block);
        for event in audio.output_events.events.borrow().iter() {
            let offset = event.sampleOffset.clamp(0, frames.saturating_sub(1) as i32);
            // SAFETY: the union member read matches the event's type.
            let converted = unsafe {
                match event.r#type {
                    EVENT_NOTE_ON => {
                        let n = event.__field0.noteOn;
                        Some((
                            if n.velocity > 0.0 {
                                event_kind::NOTE_ON
                            } else {
                                event_kind::NOTE_OFF
                            },
                            n.channel,
                            n.pitch as u8,
                            velocity_byte(n.velocity),
                        ))
                    }
                    EVENT_NOTE_OFF => {
                        let n = event.__field0.noteOff;
                        Some((
                            event_kind::NOTE_OFF,
                            n.channel,
                            n.pitch as u8,
                            velocity_byte(n.velocity),
                        ))
                    }
                    EVENT_POLY_PRESSURE => {
                        let p = event.__field0.polyPressure;
                        Some((
                            event_kind::POLY_PRESSURE,
                            p.channel,
                            p.pitch as u8,
                            velocity_byte(p.pressure),
                        ))
                    }
                    EVENT_LEGACY_CC => {
                        let cc = event.__field0.midiCCOut;
                        let (channel, value, value2) = (
                            cc.channel as i16,
                            cc.value as u8 & 0x7F,
                            cc.value2 as u8 & 0x7F,
                        );
                        match cc.controlNumber as u32 {
                            n if n < 128 => {
                                Some((event_kind::CONTROL_CHANGE, channel, n as u8, value))
                            }
                            CTRL_AFTERTOUCH => {
                                Some((event_kind::CHANNEL_PRESSURE, channel, value, 0))
                            }
                            CTRL_PITCH_BEND => {
                                Some((event_kind::PITCH_BEND, channel, value, value2))
                            }
                            CTRL_PROGRAM_CHANGE => {
                                Some((event_kind::PROGRAM_CHANGE, channel, value, 0))
                            }
                            _ => None,
                        }
                    }
                    _ => None,
                }
            };
            if let Some((kind, channel, data1, data2)) = converted
                && (0..16).contains(&channel)
                && data1 < 128
            {
                block.push_output_event(BphEvent {
                    offset,
                    kind,
                    channel: channel as u8,
                    data1,
                    data2,
                });
            }
        }
        if !audio.output_changes.is_empty()
            && let Ok(mut to_controller) = self.shared.to_controller.try_lock()
        {
            audio
                .output_changes
                .last_values(|id, value| to_controller.push((id, value)));
        }
        result == kResultOk
    }

    fn close_editor_now(&self, notify: bool) {
        let Some(editor) = self.editor.borrow_mut().take() else {
            return;
        };
        // SAFETY: the view was attached to our window.
        unsafe {
            editor.view.removed();
            editor.view.setFrame(std::ptr::null_mut());
        }
        *editor.frame.resize.borrow_mut() = None;
        #[cfg(target_os = "linux")]
        editor.frame.unregister_all();
        drop(editor);
        if notify {
            self.shared.notify(notification_kind::EDITOR_CLOSED, 0, 0.0);
        }
    }
}

fn channel_count(arrangement: SpeakerArrangement) -> usize {
    arrangement.count_ones() as usize
}

impl Instance for Vst3Instance {
    fn parameters(&self) -> Vec<ParamInfo> {
        let Some(controller) = &self.controller else {
            return Vec::new();
        };
        let midi_targets: std::collections::HashSet<u32> =
            self.controller_map().into_iter().flatten().collect();
        let mut list = Vec::new();
        // SAFETY: plain queries with sized out structures.
        unsafe {
            for index in 0..controller.getParameterCount() {
                let mut info: ParameterInfo = std::mem::zeroed();
                if controller.getParameterInfo(index, &mut info) != kResultOk {
                    continue;
                }
                let flags = info.flags as u32;
                let mut mapped = 0;
                if flags & ParameterInfo_::ParameterFlags_::kCanAutomate as u32 != 0 {
                    mapped |= param_flags::AUTOMATABLE;
                }
                if flags & ParameterInfo_::ParameterFlags_::kIsReadOnly as u32 != 0 {
                    mapped |= param_flags::READ_ONLY;
                }
                if flags & ParameterInfo_::ParameterFlags_::kIsHidden as u32 != 0 {
                    mapped |= param_flags::HIDDEN;
                }
                if flags & ParameterInfo_::ParameterFlags_::kIsBypass as u32 != 0 {
                    mapped |= param_flags::BYPASS;
                }
                if flags & ParameterInfo_::ParameterFlags_::kIsList as u32 != 0 {
                    mapped |= param_flags::LIST;
                }
                if midi_targets.contains(&info.id) {
                    mapped |= param_flags::MIDI_CONTROLLER;
                }
                list.push(ParamInfo {
                    id: info.id,
                    name: utf16_to_string(&info.title),
                    units: utf16_to_string(&info.units),
                    default_value: info.defaultNormalizedValue,
                    step_count: info.stepCount,
                    flags: mapped,
                });
            }
        }
        list
    }

    fn get_parameter(&self, id: u32) -> Option<f64> {
        let controller = self.controller.as_ref()?;
        // SAFETY: plain query.
        let value = unsafe { controller.getParamNormalized(id) };
        value.is_finite().then(|| value.clamp(0.0, 1.0))
    }

    fn set_parameter(&self, id: u32, value: f64) -> bool {
        if !(0.0..=1.0).contains(&value) {
            return false;
        }
        if let Some(controller) = &self.controller {
            // SAFETY: plain call.
            if unsafe { controller.setParamNormalized(id, value) } != kResultOk {
                return false;
            }
        }
        self.known_values.borrow_mut().insert(id, value);
        // Values the processor reported before this edit are older than it.
        if let Ok(mut stale) = self.shared.to_controller.lock() {
            stale.retain(|&(stale_id, _)| stale_id != id);
        }
        if let Ok(mut pending) = self.shared.to_processor.lock() {
            pending.push((id, value));
        }
        self.flush();
        true
    }

    fn activate(
        &self,
        sample_rate: f64,
        max_frames: u32,
        input_channels: u32,
        output_channels: u32,
    ) -> Result<(), String> {
        let mut audio = self
            .audio
            .lock()
            .map_err(|_| "The processor lock is poisoned.")?;
        if audio.is_some() {
            self.stop_processing();
            *audio = None;
        }
        // SAFETY: the VST3 activation sequence; arrays outlive the calls that read them.
        unsafe {
            if self
                .processor
                .canProcessSampleSize(SymbolicSampleSizes_::kSample32 as i32)
                != kResultTrue
            {
                return Err("The plugin cannot process 32-bit samples.".into());
            }
            let (audio_in, audio_out, event_in, event_out) = self.bus_counts();
            let wanted = |count: u32, current: SpeakerArrangement| match count {
                0 => current,
                1 => SpeakerArr::kMono,
                _ => SpeakerArr::kStereo,
            };
            let mut inputs: Vec<SpeakerArrangement> = (0..audio_in)
                .map(|i| self.arrangement(BusDirections_::kInput, i))
                .collect();
            let mut outputs: Vec<SpeakerArrangement> = (0..audio_out)
                .map(|i| self.arrangement(BusDirections_::kOutput, i))
                .collect();
            if let Some(main) = inputs.first_mut() {
                *main = wanted(input_channels, *main);
            }
            if let Some(main) = outputs.first_mut() {
                *main = wanted(output_channels, *main);
            }
            if self.processor.setBusArrangements(
                inputs.as_mut_ptr(),
                audio_in,
                outputs.as_mut_ptr(),
                audio_out,
            ) != kResultTrue
            {
                // The plugin keeps its own layout; the channel adapter maps it onto the host's channels.
            }
            let inputs: Vec<usize> = (0..audio_in)
                .map(|i| channel_count(self.arrangement(BusDirections_::kInput, i)))
                .collect();
            let outputs: Vec<usize> = (0..audio_out)
                .map(|i| channel_count(self.arrangement(BusDirections_::kOutput, i)))
                .collect();
            for i in 0..audio_in {
                self.component.activateBus(
                    MediaTypes_::kAudio as MediaType,
                    BusDirections_::kInput as BusDirection,
                    i,
                    1,
                );
            }
            for i in 0..audio_out {
                self.component.activateBus(
                    MediaTypes_::kAudio as MediaType,
                    BusDirections_::kOutput as BusDirection,
                    i,
                    1,
                );
            }
            if event_in > 0 {
                self.component.activateBus(
                    MediaTypes_::kEvent as MediaType,
                    BusDirections_::kInput as BusDirection,
                    0,
                    1,
                );
            }
            if event_out > 0 {
                self.component.activateBus(
                    MediaTypes_::kEvent as MediaType,
                    BusDirections_::kOutput as BusDirection,
                    0,
                    1,
                );
            }
            let mut setup = ProcessSetup {
                processMode: ProcessModes_::kRealtime as i32,
                symbolicSampleSize: SymbolicSampleSizes_::kSample32 as i32,
                maxSamplesPerBlock: max_frames as i32,
                sampleRate: sample_rate,
            };
            if self.processor.setupProcessing(&mut setup) != kResultOk {
                return Err("The plugin rejected the processing setup.".into());
            }
            if self.component.setActive(1) != kResultOk {
                return Err("The plugin could not be activated.".into());
            }
            self.processor.setProcessing(1);
            let mut adapter =
                ChannelAdapter::new(max_frames as usize, inputs.clone(), outputs.clone());
            let mut input_buses = Vec::with_capacity(inputs.len());
            let mut offset = 0;
            for &count in &inputs {
                let mut bus: AudioBusBuffers = std::mem::zeroed();
                bus.numChannels = count as i32;
                bus.__field0.channelBuffers32 = adapter.input_pointers.as_mut_ptr().add(offset);
                input_buses.push(bus);
                offset += count;
            }
            let mut output_buses = Vec::with_capacity(outputs.len());
            offset = 0;
            for &count in &outputs {
                let mut bus: AudioBusBuffers = std::mem::zeroed();
                bus.numChannels = count as i32;
                bus.__field0.channelBuffers32 = adapter.output_pointers.as_mut_ptr().add(offset);
                output_buses.push(bus);
                offset += count;
            }
            let mut context: ProcessContext = std::mem::zeroed();
            context.sampleRate = sample_rate;
            context.timeSigNumerator = 4;
            context.timeSigDenominator = 4;
            *audio = Some(AudioState {
                adapter,
                input_buses,
                output_buses,
                input_events: ComWrapper::new(EventList::new(MAX_EVENTS)),
                output_events: ComWrapper::new(EventList::new(MAX_EVENTS)),
                input_changes: ComWrapper::new(ParamChanges::new(
                    MAX_CHANGED_PARAMETERS,
                    MAX_POINTS,
                )),
                output_changes: ComWrapper::new(ParamChanges::new(
                    MAX_CHANGED_PARAMETERS,
                    MAX_POINTS,
                )),
                context,
                sample_rate,
                has_event_input: event_in > 0,
                controller_map: self.controller_map(),
                last_block: None,
                carried_events: Vec::with_capacity(MAX_EVENTS),
            });
        }
        Ok(())
    }

    fn process(&self, block: &mut BphProcess) -> bool {
        let Ok(mut guard) = self.audio.lock() else {
            return false;
        };
        let Some(audio) = guard.as_mut() else {
            return false;
        };
        if block.frames as usize > audio.adapter.max_frames {
            return false;
        }
        for event in audio.carried_events.drain(..) {
            block.push_output_event(event);
        }
        let processed = self.process_locked(audio, block);
        audio.last_block = Some(LastBlock {
            at: Instant::now(),
            position: block.position + block.frames as i64,
            tempo: block.tempo,
            playing: block.playing,
        });
        processed
    }

    fn latency(&self) -> u32 {
        // SAFETY: plain query.
        unsafe { self.processor.getLatencySamples() }
    }

    fn save_state(&self) -> Result<Vec<u8>, String> {
        self.deliver_pending_edits();
        let component_state = ComWrapper::new(MemoryStream::default());
        let controller_state = ComWrapper::new(MemoryStream::default());
        // SAFETY: plain state calls with our own streams.
        unsafe {
            let stream = component_state
                .to_com_ptr::<IBStream>()
                .ok_or("No stream.")?;
            let guard = self.audio.lock();
            if self.component.getState(stream.as_ptr()) != kResultOk {
                return Err("The plugin did not save its state.".into());
            }
            drop(guard);
            if let (Some(controller), true) = (&self.controller, self.separate_controller) {
                let stream = controller_state
                    .to_com_ptr::<IBStream>()
                    .ok_or("No stream.")?;
                controller.getState(stream.as_ptr());
            }
        }
        let (component, controller) = (component_state.bytes(), controller_state.bytes());
        let mut data = Vec::with_capacity(16 + component.len() + controller.len());
        data.extend_from_slice(STATE_MAGIC);
        data.extend_from_slice(&STATE_VERSION.to_le_bytes());
        data.extend_from_slice(&0u16.to_le_bytes());
        data.extend_from_slice(&(component.len() as u32).to_le_bytes());
        data.extend_from_slice(&component);
        data.extend_from_slice(&(controller.len() as u32).to_le_bytes());
        data.extend_from_slice(&controller);
        Ok(data)
    }

    fn load_state(&self, data: &[u8]) -> Result<(), String> {
        let (component, controller) = parse_state(data)?;
        // SAFETY: plain state calls with our own streams.
        unsafe {
            let stream = ComWrapper::new(MemoryStream::from_bytes(component));
            let pointer = stream.to_com_ptr::<IBStream>().ok_or("No stream.")?;
            {
                let _guard = self.audio.lock();
                if self.component.setState(pointer.as_ptr()) != kResultOk {
                    return Err("The plugin rejected its saved state.".into());
                }
            }
            if let Some(ctrl) = &self.controller {
                if self.separate_controller {
                    let stream = ComWrapper::new(MemoryStream::from_bytes(component));
                    if let Some(pointer) = stream.to_com_ptr::<IBStream>() {
                        ctrl.setComponentState(pointer.as_ptr());
                    }
                }
                if !controller.is_empty() {
                    let stream = ComWrapper::new(MemoryStream::from_bytes(controller));
                    if let Some(pointer) = stream.to_com_ptr::<IBStream>() {
                        ctrl.setState(pointer.as_ptr());
                    }
                }
            }
        }
        self.known_values.borrow_mut().clear();
        Ok(())
    }

    fn idle(&self, notifications: &mut Vec<BphNotification>) {
        if let Some(editor) = self.editor.borrow().as_ref() {
            crate::common::push_editor_keys(&editor.window, notifications);
        }
        // Edits left queued for a stream that has since stopped.
        self.flush();
        let from_processor: Vec<(u32, f64)> = self
            .shared
            .to_controller
            .lock()
            .map(|mut p| p.drain(..).collect())
            .unwrap_or_default();
        if let Some(controller) = &self.controller {
            for (id, value) in from_processor {
                // SAFETY: plain call on the main thread.
                unsafe { controller.setParamNormalized(id, value) };
            }
        }
        let flags = self.shared.restart_flags.swap(0, Ordering::AcqRel);
        if flags & RestartFlags_::kLatencyChanged as i32 != 0 {
            if let Ok(audio) = self.audio.lock()
                && audio.is_some()
            {
                // SAFETY: a deactivate/activate cycle so the plugin may change its latency, as the API asks.
                unsafe {
                    self.processor.setProcessing(0);
                    self.component.setActive(0);
                    self.component.setActive(1);
                    self.processor.setProcessing(1);
                }
            }
            notifications.push(BphNotification {
                kind: notification_kind::LATENCY_CHANGED,
                id: 0,
                value: self.latency() as f64,
            });
        }
        if flags & RestartFlags_::kMidiCCAssignmentChanged as i32 != 0 {
            let map = self.controller_map();
            if let Ok(mut audio) = self.audio.lock()
                && let Some(audio) = audio.as_mut()
            {
                audio.controller_map = map;
            }
        }
        if flags & RestartFlags_::kParamTitlesChanged as i32 != 0 {
            notifications.push(BphNotification {
                kind: notification_kind::PARAMETERS_CHANGED,
                id: 0,
                value: 0.0,
            });
        }
        if flags & RestartFlags_::kParamValuesChanged as i32 != 0 {
            // A preset change inside the plugin: report every parameter whose value moved.
            let mut known = self.known_values.borrow_mut();
            for parameter in self.parameters() {
                if let Some(value) = self.get_parameter(parameter.id)
                    && known
                        .insert(parameter.id, value)
                        .is_none_or(|old| (old - value).abs() > 1e-9)
                {
                    notifications.push(BphNotification {
                        kind: notification_kind::PARAMETER_EDITED,
                        id: parameter.id,
                        value,
                    });
                }
            }
            notifications.push(BphNotification {
                kind: notification_kind::STATE_DIRTY,
                id: 0,
                value: 0.0,
            });
        }
        let close = self
            .editor
            .borrow()
            .as_ref()
            .is_some_and(|e| e.close_requested.get());
        if close {
            self.close_editor_now(true);
        }
        if let Ok(mut pending) = self.shared.notifications.lock() {
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
        // Asking for a view is the only way to know; it is released at once.
        let Some(controller) = &self.controller else {
            return false;
        };
        if self.editor.borrow().is_some() {
            return true;
        }
        // SAFETY: plain calls.
        unsafe {
            let view = controller.createView(c"editor".as_ptr());
            match ComPtr::from_raw(view) {
                Some(view) => {
                    view.isPlatformTypeSupported(
                        CString::new(gui::VST3_PLATFORM_TYPE)
                            .unwrap_or_default()
                            .as_ptr(),
                    ) == kResultTrue
                }
                None => false,
            }
        }
    }

    fn open_editor(&self, title: &str) -> Result<(), String> {
        if self.editor.borrow().is_some() {
            return Ok(());
        }
        let controller = self
            .controller
            .as_ref()
            .ok_or("The plugin has no controller, so no editor.")?;
        let platform = CString::new(gui::VST3_PLATFORM_TYPE).unwrap_or_default();
        // SAFETY: the IPlugView sequence: create, check platform, size, attach to our window.
        unsafe {
            let view = ComPtr::from_raw(controller.createView(c"editor".as_ptr()))
                .ok_or("The plugin has no editor.")?;
            if view.isPlatformTypeSupported(platform.as_ptr()) != kResultTrue {
                return Err(format!(
                    "The plugin's editor does not support {}.",
                    gui::VST3_PLATFORM_TYPE
                ));
            }
            let mut rect: ViewRect = std::mem::zeroed();
            if view.getSize(&mut rect) != kResultOk
                || rect.right - rect.left <= 0
                || rect.bottom - rect.top <= 0
            {
                rect = ViewRect {
                    left: 0,
                    top: 0,
                    right: 640,
                    bottom: 480,
                };
            }
            let resizable = view.canResize() == kResultTrue;
            let close_requested = Rc::new(Cell::new(false));
            let callback_view = view.clone();
            let callback_close = close_requested.clone();
            let callback: gui::WindowCallback = Rc::new(move |event| match event {
                gui::WindowEvent::CloseRequested => callback_close.set(true),
                gui::WindowEvent::Resized { width, height } => {
                    let mut rect = ViewRect {
                        left: 0,
                        top: 0,
                        right: width as i32,
                        bottom: height as i32,
                    };
                    callback_view.onSize(&mut rect);
                }
            });
            let window = Rc::new(gui::Window::create(
                title,
                (rect.right - rect.left) as u32,
                (rect.bottom - rect.top) as u32,
                resizable,
                callback,
            )?);
            let frame = ComWrapper::new(PlugFrame::new());
            let weak: Weak<gui::Window> = Rc::downgrade(&window);
            *frame.resize.borrow_mut() =
                Some(Box::new(move |width, height| match weak.upgrade() {
                    Some(window) => {
                        window.set_content_size(width, height);
                        true
                    }
                    None => false,
                }));
            if let Some(frame_ptr) = frame.to_com_ptr::<IPlugFrame>() {
                view.setFrame(frame_ptr.as_ptr());
            }
            if let Some(scale) = view.cast::<IPlugViewContentScaleSupport>()
                && cfg!(not(target_os = "macos"))
            {
                scale.setContentScaleFactor(window.content_scale() as f32);
            }
            if view.attached(window.parent_handle(), platform.as_ptr()) != kResultOk {
                view.setFrame(std::ptr::null_mut());
                return Err("The plugin's editor could not be attached to a window.".into());
            }
            let mut attached: ViewRect = std::mem::zeroed();
            if view.getSize(&mut attached) == kResultOk
                && attached.right - attached.left > 0
                && attached.bottom - attached.top > 0
            {
                window.set_content_size(
                    (attached.right - attached.left) as u32,
                    (attached.bottom - attached.top) as u32,
                );
            }
            *self.editor.borrow_mut() = Some(Editor {
                view,
                frame,
                window,
                close_requested,
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

    fn set_editor_visible(&self, visible: bool) -> bool {
        match self.editor.borrow().as_ref() {
            Some(editor) => {
                editor.window.set_visible(visible);
                true
            }
            None => false,
        }
    }

    fn route_editor_keys_to_host(&self, to_host: bool) -> bool {
        self.editor
            .borrow()
            .as_ref()
            .is_some_and(|editor| editor.window.route_keys_to_host(to_host))
    }
}

fn velocity_byte(value: f32) -> u8 {
    (value.clamp(0.0, 1.0) * 127.0).round() as u8
}

fn com_ptr<I: Interface>(reference: Option<ComRef<'_, I>>) -> *mut I {
    reference
        .map(|r| r.as_ptr())
        .unwrap_or(std::ptr::null_mut())
}

fn parse_state(data: &[u8]) -> Result<(&[u8], &[u8]), String> {
    let invalid = || "The VST3 state is malformed.".to_string();
    if data.len() < 12 || &data[..4] != STATE_MAGIC {
        return Err(invalid());
    }
    let version = u16::from_le_bytes([data[4], data[5]]);
    if version != STATE_VERSION {
        return Err(format!("Unknown VST3 state version {version}."));
    }
    let mut position = 8;
    let mut take = |data: &'_ [u8]| -> Result<(usize, usize), String> {
        let length = data.get(position..position + 4).ok_or_else(invalid)?;
        let length = u32::from_le_bytes([length[0], length[1], length[2], length[3]]) as usize;
        let start = position + 4;
        let end = start
            .checked_add(length)
            .filter(|&e| e <= data.len())
            .ok_or_else(invalid)?;
        position = end;
        Ok((start, end))
    };
    let (a, b) = take(data)?;
    let (c, d) = take(data)?;
    if d != data.len() {
        return Err(invalid());
    }
    Ok((&data[a..b], &data[c..d]))
}

impl Drop for Vst3Instance {
    fn drop(&mut self) {
        self.close_editor_now(false);
        // SAFETY: the VST3 teardown sequence, in reverse of creation.
        unsafe {
            if let Ok(mut audio) = self.audio.lock()
                && audio.take().is_some()
            {
                self.stop_processing();
            }
            if let Some((a, b)) = self.connection.take() {
                a.disconnect(b.as_ptr());
                b.disconnect(a.as_ptr());
            }
            if let Some(controller) = self.controller.take() {
                controller.setComponentHandler(std::ptr::null_mut());
                if self.separate_controller {
                    controller.terminate();
                }
            }
            self.component.terminate();
        }
        let _ = &self.handler;
        let _ = &self.module;
    }
}
