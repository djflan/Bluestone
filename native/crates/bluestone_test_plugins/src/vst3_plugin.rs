//! The VST3 side: one single-component class per plugin (component, processor, and controller in one object).

use std::ffi::{c_char, c_void};
use std::sync::Mutex;

use vst3::Steinberg::Vst::*;
use vst3::Steinberg::*;
use vst3::{Class, ComRef, ComWrapper, uid};

use crate::{Core, Input, Kind, Output};

const CLASS_IDS: [TUID; 3] = [
    uid(0x42535447, 0x41494E00, 0x8C1A0001, 0x0B1E5701),
    uid(0x42535453, 0x494E4500, 0x8C1A0002, 0x0B1E5702),
    uid(0x42535454, 0x52414E00, 0x8C1A0003, 0x0B1E5703),
];

const EVENT_NOTE_ON: u16 = Event_::EventTypes_::kNoteOnEvent as u16;
const EVENT_NOTE_OFF: u16 = Event_::EventTypes_::kNoteOffEvent as u16;
const MAX_EVENTS: usize = 1024;

fn copy_c(text: &str, target: &mut [c_char]) {
    let len = text.len().min(target.len() - 1);
    for (slot, byte) in target.iter_mut().zip(text.as_bytes()[..len].iter()) {
        *slot = *byte as c_char;
    }
    target[len] = 0;
}

fn copy_w(text: &str, target: &mut [TChar]) {
    let mut len = 0;
    for (slot, unit) in target.iter_mut().zip(text.encode_utf16()) {
        *slot = unit as TChar;
        len += 1;
    }
    let end = len.min(target.len() - 1);
    target[end] = 0;
}

struct Plugin {
    kind: Kind,
    core: Mutex<Core>,
    channels: Mutex<(i32, i32)>,
    events: Mutex<Vec<(u32, Input)>>,
}

impl Plugin {
    fn new(kind: Kind) -> Plugin {
        Plugin {
            kind,
            core: Mutex::new(Core::new(kind)),
            channels: Mutex::new((2, 2)),
            events: Mutex::new(Vec::with_capacity(MAX_EVENTS)),
        }
    }

    fn get(&self, id: u32) -> f64 {
        self.core
            .lock()
            .map(|c| c.get(id).unwrap_or(0.0))
            .unwrap_or(0.0)
    }
}

impl Class for Plugin {
    type Interfaces = (IComponent, IAudioProcessor, IEditController);
}

impl IPluginBaseTrait for Plugin {
    unsafe fn initialize(&self, _context: *mut FUnknown) -> tresult {
        kResultOk
    }

    unsafe fn terminate(&self) -> tresult {
        kResultOk
    }
}

impl IComponentTrait for Plugin {
    unsafe fn getControllerClassId(&self, _class_id: *mut TUID) -> tresult {
        kResultFalse
    }

    unsafe fn setIoMode(&self, _mode: IoMode) -> tresult {
        kResultOk
    }

    unsafe fn getBusCount(&self, media_type: MediaType, dir: BusDirection) -> int32 {
        let input = dir == BusDirections_::kInput as BusDirection;
        let count = if media_type == MediaTypes_::kAudio as MediaType {
            if input {
                self.kind.audio_inputs()
            } else {
                self.kind.audio_outputs()
            }
        } else if input {
            self.kind.event_inputs()
        } else {
            self.kind.event_outputs()
        };
        count as int32
    }

    unsafe fn getBusInfo(
        &self,
        media_type: MediaType,
        dir: BusDirection,
        index: int32,
        bus: *mut BusInfo,
    ) -> tresult {
        if index != 0 || bus.is_null() || index >= unsafe { self.getBusCount(media_type, dir) } {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        let bus = unsafe { &mut *bus };
        bus.mediaType = media_type;
        bus.direction = dir;
        let channels = *self.channels.lock().unwrap_or_else(|e| e.into_inner());
        bus.channelCount = if media_type == MediaTypes_::kAudio as MediaType {
            if dir == BusDirections_::kInput as BusDirection {
                channels.0
            } else {
                channels.1
            }
        } else {
            16
        };
        copy_w(
            if media_type == MediaTypes_::kAudio as MediaType {
                "Audio"
            } else {
                "MIDI"
            },
            &mut bus.name,
        );
        bus.busType = BusTypes_::kMain as BusType;
        bus.flags = BusInfo_::BusFlags_::kDefaultActive as u32;
        kResultOk
    }

    unsafe fn getRoutingInfo(
        &self,
        _in_info: *mut RoutingInfo,
        _out_info: *mut RoutingInfo,
    ) -> tresult {
        kNotImplemented
    }

    unsafe fn activateBus(
        &self,
        _media_type: MediaType,
        _dir: BusDirection,
        _index: int32,
        _state: TBool,
    ) -> tresult {
        kResultOk
    }

    unsafe fn setActive(&self, _state: TBool) -> tresult {
        kResultOk
    }

    unsafe fn setState(&self, state: *mut IBStream) -> tresult {
        let data = unsafe { read_all(state) };
        match self.core.lock() {
            Ok(core) if core.load(&data) => kResultOk,
            _ => kResultFalse,
        }
    }

    unsafe fn getState(&self, state: *mut IBStream) -> tresult {
        let data = self.core.lock().map(|c| c.save()).unwrap_or_default();
        unsafe { write_all(state, &data) }
    }
}

unsafe fn read_all(stream: *mut IBStream) -> Vec<u8> {
    let mut data = Vec::new();
    // SAFETY: the host's stream.
    let Some(stream) = (unsafe { ComRef::from_raw(stream) }) else {
        return data;
    };
    let mut buffer = [0u8; 4096];
    loop {
        let mut read = 0;
        // SAFETY: as above.
        if unsafe {
            stream.read(
                buffer.as_mut_ptr() as *mut c_void,
                buffer.len() as i32,
                &mut read,
            )
        } != kResultOk
            || read <= 0
        {
            break;
        }
        data.extend_from_slice(&buffer[..read as usize]);
    }
    data
}

unsafe fn write_all(stream: *mut IBStream, data: &[u8]) -> tresult {
    // SAFETY: the host's stream.
    let Some(stream) = (unsafe { ComRef::from_raw(stream) }) else {
        return kInvalidArgument;
    };
    let mut written = 0;
    // SAFETY: as above.
    unsafe {
        stream.write(
            data.as_ptr() as *mut c_void,
            data.len() as i32,
            &mut written,
        )
    }
}

impl IAudioProcessorTrait for Plugin {
    unsafe fn setBusArrangements(
        &self,
        inputs: *mut SpeakerArrangement,
        num_ins: int32,
        outputs: *mut SpeakerArrangement,
        num_outs: int32,
    ) -> tresult {
        if num_ins != self.kind.audio_inputs() as int32
            || num_outs != self.kind.audio_outputs() as int32
        {
            return kResultFalse;
        }
        let count = |arrangement: SpeakerArrangement| arrangement.count_ones() as i32;
        let mut channels = self.channels.lock().unwrap_or_else(|e| e.into_inner());
        if num_ins > 0 {
            // SAFETY: the host passes num_ins arrangements.
            channels.0 = count(unsafe { *inputs }).clamp(1, 2);
        }
        if num_outs > 0 {
            // SAFETY: as above.
            channels.1 = count(unsafe { *outputs }).clamp(1, 2);
        }
        kResultTrue
    }

    unsafe fn getBusArrangement(
        &self,
        dir: BusDirection,
        index: int32,
        arrangement: *mut SpeakerArrangement,
    ) -> tresult {
        if index != 0 || arrangement.is_null() {
            return kInvalidArgument;
        }
        let channels = *self.channels.lock().unwrap_or_else(|e| e.into_inner());
        let count = if dir == BusDirections_::kInput as BusDirection {
            channels.0
        } else {
            channels.1
        };
        // SAFETY: checked non-null.
        unsafe {
            *arrangement = if count == 1 {
                SpeakerArr::kMono
            } else {
                SpeakerArr::kStereo
            }
        };
        kResultOk
    }

    unsafe fn canProcessSampleSize(&self, size: int32) -> tresult {
        if size == SymbolicSampleSizes_::kSample32 as int32 {
            kResultTrue
        } else {
            kResultFalse
        }
    }

    unsafe fn getLatencySamples(&self) -> uint32 {
        0
    }

    unsafe fn setupProcessing(&self, setup: *mut ProcessSetup) -> tresult {
        if let (false, Ok(mut core)) = (setup.is_null(), self.core.lock()) {
            // SAFETY: checked non-null.
            core.set_sample_rate(unsafe { (*setup).sampleRate });
        }
        kResultOk
    }

    unsafe fn setProcessing(&self, _state: TBool) -> tresult {
        kResultOk
    }

    unsafe fn process(&self, data: *mut ProcessData) -> tresult {
        // SAFETY: the host's process data, valid for the call.
        let data = unsafe { &mut *data };
        let (Ok(mut core), Ok(mut events)) = (self.core.lock(), self.events.lock()) else {
            return kResultFalse;
        };
        events.clear();
        // SAFETY: the host's parameter changes.
        if let Some(changes) = unsafe { ComRef::from_raw(data.inputParameterChanges) } {
            for index in 0..unsafe { changes.getParameterCount() } {
                // SAFETY: as above.
                let Some(queue) = (unsafe { ComRef::from_raw(changes.getParameterData(index)) })
                else {
                    continue;
                };
                let id = unsafe { queue.getParameterId() };
                for point in 0..unsafe { queue.getPointCount() } {
                    let (mut offset, mut value) = (0, 0.0);
                    // SAFETY: as above.
                    if unsafe { queue.getPoint(point, &mut offset, &mut value) } == kResultOk
                        && events.len() < MAX_EVENTS
                    {
                        events.push((offset.max(0) as u32, Input::Parameter { id, value }));
                    }
                }
            }
        }
        // SAFETY: the host's event list.
        if let Some(list) = unsafe { ComRef::from_raw(data.inputEvents) } {
            for index in 0..unsafe { list.getEventCount() } {
                // SAFETY: a zeroed Event is valid; the host fills it.
                let mut event: Event = unsafe { std::mem::zeroed() };
                if unsafe { list.getEvent(index, &mut event) } != kResultOk
                    || events.len() >= MAX_EVENTS
                {
                    continue;
                }
                // SAFETY: the union member read matches the type.
                let input = unsafe {
                    match event.r#type {
                        EVENT_NOTE_ON => {
                            let n = event.__field0.noteOn;
                            Input::Note {
                                on: n.velocity > 0.0,
                                channel: n.channel as u8 & 15,
                                key: n.pitch as u8 & 127,
                                velocity: n.velocity,
                            }
                        }
                        EVENT_NOTE_OFF => {
                            let n = event.__field0.noteOff;
                            Input::Note {
                                on: false,
                                channel: n.channel as u8 & 15,
                                key: n.pitch as u8 & 127,
                                velocity: n.velocity,
                            }
                        }
                        _ => continue,
                    }
                };
                events.push((event.sampleOffset.max(0) as u32, input));
            }
        }
        events.sort_by_key(|e| e.0);
        let frames = data.numSamples.max(0) as usize;
        let channels = |buses: *mut AudioBusBuffers, count: i32| -> Vec<*mut f32> {
            if buses.is_null() || count < 1 {
                return Vec::new();
            }
            // SAFETY: the host passes `count` buses with valid channel pointers.
            let bus = unsafe { &*buses };
            if bus.numChannels < 1 || unsafe { bus.__field0.channelBuffers32 }.is_null() {
                return Vec::new();
            }
            (0..bus.numChannels as usize)
                .map(|c| unsafe { *bus.__field0.channelBuffers32.add(c) })
                .collect()
        };
        let input_pointers = channels(data.inputs, data.numInputs);
        let output_pointers = channels(data.outputs, data.numOutputs);
        // SAFETY: each channel holds `frames` samples for the call.
        let inputs: Vec<&[f32]> = input_pointers
            .iter()
            .map(|&p| unsafe { std::slice::from_raw_parts(p as *const f32, frames) })
            .collect();
        let mut outputs: Vec<&mut [f32]> = output_pointers
            .iter()
            .map(|&p| unsafe { std::slice::from_raw_parts_mut(p, frames) })
            .collect();
        // SAFETY: the host's output event list.
        let out_list = unsafe { ComRef::from_raw(data.outputEvents) };
        core.process(
            frames,
            &inputs,
            &mut outputs,
            &events,
            &mut |offset, output| {
                let Some(list) = out_list else { return };
                if let Output::Note {
                    on,
                    channel,
                    key,
                    velocity,
                } = output
                {
                    // SAFETY: a zeroed Event is valid; the matching union member is set.
                    let mut event: Event = unsafe { std::mem::zeroed() };
                    event.sampleOffset = offset as i32;
                    if on {
                        event.r#type = EVENT_NOTE_ON;
                        event.__field0.noteOn = NoteOnEvent {
                            channel: channel as i16,
                            pitch: key as i16,
                            tuning: 0.0,
                            velocity,
                            length: 0,
                            noteId: -1,
                        };
                    } else {
                        event.r#type = EVENT_NOTE_OFF;
                        event.__field0.noteOff = NoteOffEvent {
                            channel: channel as i16,
                            pitch: key as i16,
                            velocity,
                            noteId: -1,
                            tuning: 0.0,
                        };
                    }
                    // SAFETY: the host's list.
                    unsafe { list.addEvent(&mut event) };
                }
            },
        );
        kResultOk
    }

    unsafe fn getTailSamples(&self) -> uint32 {
        0
    }
}

impl IEditControllerTrait for Plugin {
    unsafe fn setComponentState(&self, state: *mut IBStream) -> tresult {
        // A single component: the processor already holds the values.
        let _ = state;
        kResultOk
    }

    unsafe fn setState(&self, _state: *mut IBStream) -> tresult {
        kResultOk
    }

    unsafe fn getState(&self, _state: *mut IBStream) -> tresult {
        kResultOk
    }

    unsafe fn getParameterCount(&self) -> int32 {
        self.kind.parameters().len() as int32
    }

    unsafe fn getParameterInfo(&self, index: int32, info: *mut ParameterInfo) -> tresult {
        let Some(spec) = usize::try_from(index)
            .ok()
            .and_then(|i| self.kind.parameters().get(i))
        else {
            return kInvalidArgument;
        };
        if info.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        let info = unsafe { &mut *info };
        info.id = index as u32;
        copy_w(spec.name, &mut info.title);
        copy_w(spec.name, &mut info.shortTitle);
        copy_w("", &mut info.units);
        info.stepCount = spec.steps;
        info.defaultNormalizedValue = spec.default;
        info.unitId = 0;
        info.flags = ParameterInfo_::ParameterFlags_::kCanAutomate as int32;
        kResultOk
    }

    unsafe fn getParamStringByValue(
        &self,
        _id: ParamID,
        value: ParamValue,
        string: *mut String128,
    ) -> tresult {
        if string.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        copy_w(&format!("{value:.3}"), unsafe { &mut *string });
        kResultOk
    }

    unsafe fn getParamValueByString(
        &self,
        _id: ParamID,
        _string: *mut TChar,
        _value: *mut ParamValue,
    ) -> tresult {
        kNotImplemented
    }

    unsafe fn normalizedParamToPlain(&self, _id: ParamID, value: ParamValue) -> ParamValue {
        value
    }

    unsafe fn plainParamToNormalized(&self, _id: ParamID, value: ParamValue) -> ParamValue {
        value
    }

    unsafe fn getParamNormalized(&self, id: ParamID) -> ParamValue {
        self.get(id)
    }

    unsafe fn setParamNormalized(&self, id: ParamID, value: ParamValue) -> tresult {
        if self.core.lock().map(|c| c.set(id, value)).unwrap_or(false) {
            kResultOk
        } else {
            kInvalidArgument
        }
    }

    unsafe fn setComponentHandler(&self, _handler: *mut IComponentHandler) -> tresult {
        kResultOk
    }

    unsafe fn createView(&self, _name: FIDString) -> *mut IPlugView {
        std::ptr::null_mut()
    }
}

struct Factory;

impl Class for Factory {
    type Interfaces = (IPluginFactory, IPluginFactory2);
}

impl IPluginFactoryTrait for Factory {
    unsafe fn getFactoryInfo(&self, info: *mut PFactoryInfo) -> tresult {
        if info.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        let info = unsafe { &mut *info };
        copy_c("Bluestone", &mut info.vendor);
        copy_c("https://github.com/djflan/Bluestone", &mut info.url);
        copy_c("", &mut info.email);
        info.flags = PFactoryInfo_::FactoryFlags_::kUnicode as int32;
        kResultOk
    }

    unsafe fn countClasses(&self) -> int32 {
        Kind::ALL.len() as int32
    }

    unsafe fn getClassInfo(&self, index: int32, info: *mut PClassInfo) -> tresult {
        let Some(kind) = usize::try_from(index).ok().and_then(|i| Kind::ALL.get(i)) else {
            return kInvalidArgument;
        };
        if info.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        let info = unsafe { &mut *info };
        info.cid = CLASS_IDS[index as usize];
        info.cardinality = PClassInfo_::ClassCardinality_::kManyInstances as int32;
        copy_c("Audio Module Class", &mut info.category);
        copy_c(kind.name(), &mut info.name);
        kResultOk
    }

    unsafe fn createInstance(
        &self,
        cid: FIDString,
        iid: FIDString,
        object: *mut *mut c_void,
    ) -> tresult {
        if cid.is_null() || object.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: a class id is 16 bytes.
        let cid = unsafe { *(cid as *const TUID) };
        let Some(index) = CLASS_IDS.iter().position(|c| *c == cid) else {
            return kInvalidArgument;
        };
        let Some(instance) =
            ComWrapper::new(Plugin::new(Kind::ALL[index])).to_com_ptr::<FUnknown>()
        else {
            return kResultFalse;
        };
        let pointer = instance.as_ptr();
        // SAFETY: queryInterface on our own object adds the caller's reference.
        unsafe { ((*(*pointer).vtbl).queryInterface)(pointer, iid as *const TUID, object) }
    }
}

impl IPluginFactory2Trait for Factory {
    unsafe fn getClassInfo2(&self, index: int32, info: *mut PClassInfo2) -> tresult {
        let Some(kind) = usize::try_from(index).ok().and_then(|i| Kind::ALL.get(i)) else {
            return kInvalidArgument;
        };
        if info.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        let info = unsafe { &mut *info };
        info.cid = CLASS_IDS[index as usize];
        info.cardinality = PClassInfo_::ClassCardinality_::kManyInstances as int32;
        copy_c("Audio Module Class", &mut info.category);
        copy_c(kind.name(), &mut info.name);
        info.classFlags = 0;
        copy_c(
            if *kind == Kind::Gain {
                "Fx"
            } else {
                "Instrument"
            },
            &mut info.subCategories,
        );
        copy_c("Bluestone", &mut info.vendor);
        copy_c("1.0.0", &mut info.version);
        copy_c("VST 3.8.0", &mut info.sdkVersion);
        kResultOk
    }
}

#[unsafe(no_mangle)]
extern "system" fn GetPluginFactory() -> *mut IPluginFactory {
    ComWrapper::new(Factory)
        .to_com_ptr::<IPluginFactory>()
        .map(|p| p.into_raw())
        .unwrap_or(std::ptr::null_mut())
}

#[cfg(target_os = "macos")]
#[unsafe(no_mangle)]
extern "C" fn bundleEntry(_bundle: *mut c_void) -> bool {
    true
}

#[cfg(target_os = "macos")]
#[unsafe(no_mangle)]
extern "C" fn bundleExit() -> bool {
    true
}

#[cfg(windows)]
#[unsafe(no_mangle)]
extern "system" fn InitDll() -> bool {
    true
}

#[cfg(windows)]
#[unsafe(no_mangle)]
extern "system" fn ExitDll() -> bool {
    true
}

#[cfg(target_os = "linux")]
#[unsafe(no_mangle)]
extern "C" fn ModuleEntry(_handle: *mut c_void) -> bool {
    true
}

#[cfg(target_os = "linux")]
#[unsafe(no_mangle)]
extern "C" fn ModuleExit() -> bool {
    true
}
