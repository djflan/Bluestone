//! The COM objects a VST3 plugin needs from its host: the host application (with message and attribute-list
//! creation, so component and controller can talk), the component handler (edits from the plugin), memory streams for
//! state, event lists and parameter-change queues for processing, and the editor's plug frame (with a run loop on
//! Linux).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_void};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, Mutex};

use vst3::Steinberg::Vst::*;
use vst3::Steinberg::*;
use vst3::{Class, ComWrapper, Interface};

use crate::common::{BphNotification, notification_kind};

/// What the plugin did on its own, shared between the host objects (main thread) and processing (audio role).
#[derive(Default)]
pub struct Shared {
    /// Reported to Bluestone at the next idle.
    pub notifications: Mutex<Vec<BphNotification>>,
    /// Edits from the controller side that the processor must hear about, delivered with the next block.
    pub to_processor: Mutex<Vec<(u32, f64)>>,
    /// Values the processor reported, to pass to the controller at the next idle.
    pub to_controller: Mutex<Vec<(u32, f64)>>,
    pub restart_flags: AtomicI32,
}

impl Shared {
    pub fn notify(&self, kind: u32, id: u32, value: f64) {
        if let Ok(mut list) = self.notifications.lock() {
            list.push(BphNotification { kind, id, value });
        }
    }
}

pub fn same_iid(tuid: *const TUID, iid: &[u8; 16]) -> bool {
    if tuid.is_null() {
        return false;
    }
    // SAFETY: a TUID is 16 bytes.
    let bytes = unsafe { &*(tuid as *const [u8; 16]) };
    bytes == iid
}

fn copy_to_string128(text: &str, target: &mut String128) {
    let mut len = 0;
    for (unit, slot) in text.encode_utf16().zip(target.iter_mut()) {
        *slot = unit as TChar;
        len += 1;
    }
    let end = len.min(target.len() - 1);
    target[end] = 0;
}

pub struct HostApplication;

impl Class for HostApplication {
    type Interfaces = (IHostApplication,);
}

impl IHostApplicationTrait for HostApplication {
    unsafe fn getName(&self, name: *mut String128) -> tresult {
        if name.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: the plugin passes a String128.
        copy_to_string128("Bluestone", unsafe { &mut *name });
        kResultOk
    }

    unsafe fn createInstance(
        &self,
        cid: *mut TUID,
        iid: *mut TUID,
        obj: *mut *mut c_void,
    ) -> tresult {
        if obj.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: `obj` is the plugin's out pointer.
        unsafe { *obj = std::ptr::null_mut() };
        let created = if same_iid(cid, &IMessage::IID) {
            ComWrapper::new(HostMessage::default()).to_com_ptr::<FUnknown>()
        } else if same_iid(cid, &IAttributeList::IID) {
            ComWrapper::new(AttributeList::default()).to_com_ptr::<FUnknown>()
        } else {
            None
        };
        match created {
            // SAFETY: standard queryInterface on our own object; it adds the reference the caller owns.
            Some(object) => unsafe {
                ((*(*object.as_ptr()).vtbl).queryInterface)(
                    object.as_ptr(),
                    iid as *const TUID,
                    obj,
                )
            },
            None => kResultFalse,
        }
    }
}

pub struct HostMessage {
    id: RefCell<Option<CString>>,
    attributes: ComWrapper<AttributeList>,
}

impl Default for HostMessage {
    fn default() -> Self {
        HostMessage {
            id: RefCell::new(None),
            attributes: ComWrapper::new(AttributeList::default()),
        }
    }
}

impl Class for HostMessage {
    type Interfaces = (IMessage,);
}

impl IMessageTrait for HostMessage {
    unsafe fn getMessageID(&self) -> FIDString {
        self.id
            .borrow()
            .as_ref()
            .map(|s| s.as_ptr())
            .unwrap_or(std::ptr::null())
    }

    unsafe fn setMessageID(&self, id: FIDString) {
        // SAFETY: the plugin passes a NUL-terminated string or null.
        *self.id.borrow_mut() = (!id.is_null()).then(|| unsafe { CStr::from_ptr(id) }.to_owned());
    }

    unsafe fn getAttributes(&self) -> *mut IAttributeList {
        self.attributes
            .as_com_ref::<IAttributeList>()
            .map(|r| r.as_ptr())
            .unwrap_or(std::ptr::null_mut())
    }
}

enum Attribute {
    Int(i64),
    Float(f64),
    String(Vec<TChar>),
    Binary(Vec<u8>),
}

#[derive(Default)]
pub struct AttributeList {
    values: RefCell<HashMap<Vec<u8>, Attribute>>,
}

impl Class for AttributeList {
    type Interfaces = (IAttributeList,);
}

fn attribute_key(id: IAttributeList_::AttrID) -> Option<Vec<u8>> {
    // SAFETY: the plugin passes a NUL-terminated id or null.
    (!id.is_null()).then(|| unsafe { CStr::from_ptr(id) }.to_bytes().to_vec())
}

impl IAttributeListTrait for AttributeList {
    unsafe fn setInt(&self, id: IAttributeList_::AttrID, value: int64) -> tresult {
        let Some(key) = attribute_key(id) else {
            return kInvalidArgument;
        };
        self.values.borrow_mut().insert(key, Attribute::Int(value));
        kResultOk
    }

    unsafe fn getInt(&self, id: IAttributeList_::AttrID, value: *mut int64) -> tresult {
        match (
            attribute_key(id).and_then(|k| match self.values.borrow().get(&k) {
                Some(Attribute::Int(v)) => Some(*v),
                _ => None,
            }),
            value.is_null(),
        ) {
            // SAFETY: checked non-null.
            (Some(v), false) => unsafe {
                *value = v;
                kResultOk
            },
            _ => kResultFalse,
        }
    }

    unsafe fn setFloat(&self, id: IAttributeList_::AttrID, value: f64) -> tresult {
        let Some(key) = attribute_key(id) else {
            return kInvalidArgument;
        };
        self.values
            .borrow_mut()
            .insert(key, Attribute::Float(value));
        kResultOk
    }

    unsafe fn getFloat(&self, id: IAttributeList_::AttrID, value: *mut f64) -> tresult {
        match (
            attribute_key(id).and_then(|k| match self.values.borrow().get(&k) {
                Some(Attribute::Float(v)) => Some(*v),
                _ => None,
            }),
            value.is_null(),
        ) {
            // SAFETY: checked non-null.
            (Some(v), false) => unsafe {
                *value = v;
                kResultOk
            },
            _ => kResultFalse,
        }
    }

    unsafe fn setString(&self, id: IAttributeList_::AttrID, string: *const TChar) -> tresult {
        let Some(key) = attribute_key(id) else {
            return kInvalidArgument;
        };
        if string.is_null() {
            return kInvalidArgument;
        }
        let mut chars = Vec::new();
        // SAFETY: the plugin passes a NUL-terminated UTF-16 string.
        unsafe {
            let mut p = string;
            while *p != 0 {
                chars.push(*p);
                p = p.add(1);
            }
        }
        chars.push(0);
        self.values
            .borrow_mut()
            .insert(key, Attribute::String(chars));
        kResultOk
    }

    unsafe fn getString(
        &self,
        id: IAttributeList_::AttrID,
        string: *mut TChar,
        size_in_bytes: uint32,
    ) -> tresult {
        let Some(key) = attribute_key(id) else {
            return kInvalidArgument;
        };
        let values = self.values.borrow();
        let Some(Attribute::String(chars)) = values.get(&key) else {
            return kResultFalse;
        };
        let capacity = size_in_bytes as usize / std::mem::size_of::<TChar>();
        if string.is_null() || capacity == 0 {
            return kInvalidArgument;
        }
        let count = chars.len().min(capacity);
        // SAFETY: the plugin's buffer holds `capacity` characters.
        unsafe {
            std::ptr::copy_nonoverlapping(chars.as_ptr(), string, count);
            *string.add(count - 1) = 0;
        }
        kResultOk
    }

    unsafe fn setBinary(
        &self,
        id: IAttributeList_::AttrID,
        data: *const c_void,
        size_in_bytes: uint32,
    ) -> tresult {
        let Some(key) = attribute_key(id) else {
            return kInvalidArgument;
        };
        let bytes = if data.is_null() || size_in_bytes == 0 {
            Vec::new()
        } else {
            // SAFETY: the plugin passes `size_in_bytes` readable bytes.
            unsafe { std::slice::from_raw_parts(data as *const u8, size_in_bytes as usize) }
                .to_vec()
        };
        self.values
            .borrow_mut()
            .insert(key, Attribute::Binary(bytes));
        kResultOk
    }

    unsafe fn getBinary(
        &self,
        id: IAttributeList_::AttrID,
        data: *mut *const c_void,
        size_in_bytes: *mut uint32,
    ) -> tresult {
        let Some(key) = attribute_key(id) else {
            return kInvalidArgument;
        };
        let values = self.values.borrow();
        let Some(Attribute::Binary(bytes)) = values.get(&key) else {
            return kResultFalse;
        };
        if data.is_null() || size_in_bytes.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: the stored bytes live as long as the attribute, which is what the API promises.
        unsafe {
            *data = bytes.as_ptr() as *const c_void;
            *size_in_bytes = bytes.len() as u32;
        }
        kResultOk
    }
}

/// Receives edits the plugin makes itself (usually from its editor), on the main thread.
pub struct ComponentHandler {
    pub shared: Arc<Shared>,
}

impl Class for ComponentHandler {
    type Interfaces = (IComponentHandler, IComponentHandler2);
}

impl IComponentHandlerTrait for ComponentHandler {
    unsafe fn beginEdit(&self, _id: ParamID) -> tresult {
        kResultOk
    }

    unsafe fn performEdit(&self, id: ParamID, value: ParamValue) -> tresult {
        if !(0.0..=1.0).contains(&value) {
            return kInvalidArgument;
        }
        if let Ok(mut pending) = self.shared.to_processor.lock() {
            pending.push((id, value));
        }
        self.shared
            .notify(notification_kind::PARAMETER_EDITED, id, value);
        kResultOk
    }

    unsafe fn endEdit(&self, _id: ParamID) -> tresult {
        kResultOk
    }

    unsafe fn restartComponent(&self, flags: int32) -> tresult {
        self.shared.restart_flags.fetch_or(flags, Ordering::AcqRel);
        kResultOk
    }
}

impl IComponentHandler2Trait for ComponentHandler {
    unsafe fn setDirty(&self, state: TBool) -> tresult {
        if state != 0 {
            self.shared.notify(notification_kind::STATE_DIRTY, 0, 0.0);
        }
        kResultOk
    }

    unsafe fn requestOpenEditor(&self, _name: FIDString) -> tresult {
        kNotImplemented
    }

    unsafe fn startGroupEdit(&self) -> tresult {
        kResultOk
    }

    unsafe fn finishGroupEdit(&self) -> tresult {
        kResultOk
    }
}

/// An in-memory IBStream for state.
#[derive(Default)]
pub struct MemoryStream {
    data: RefCell<Vec<u8>>,
    position: Cell<usize>,
}

impl MemoryStream {
    pub fn from_bytes(bytes: &[u8]) -> MemoryStream {
        MemoryStream {
            data: RefCell::new(bytes.to_vec()),
            position: Cell::new(0),
        }
    }
}

pub trait StreamBytes {
    fn bytes(&self) -> Vec<u8>;
}

impl StreamBytes for ComWrapper<MemoryStream> {
    fn bytes(&self) -> Vec<u8> {
        self.data.borrow().clone()
    }
}

impl Class for MemoryStream {
    type Interfaces = (IBStream,);
}

impl IBStreamTrait for MemoryStream {
    unsafe fn read(
        &self,
        buffer: *mut c_void,
        num_bytes: int32,
        num_bytes_read: *mut int32,
    ) -> tresult {
        if num_bytes < 0 || (buffer.is_null() && num_bytes > 0) {
            return kInvalidArgument;
        }
        let data = self.data.borrow();
        let position = self.position.get().min(data.len());
        let count = (num_bytes as usize).min(data.len() - position);
        // SAFETY: the plugin's buffer holds `num_bytes` bytes.
        unsafe {
            std::ptr::copy_nonoverlapping(data.as_ptr().add(position), buffer as *mut u8, count)
        };
        self.position.set(position + count);
        if !num_bytes_read.is_null() {
            // SAFETY: checked non-null.
            unsafe { *num_bytes_read = count as int32 };
        }
        kResultOk
    }

    unsafe fn write(
        &self,
        buffer: *mut c_void,
        num_bytes: int32,
        num_bytes_written: *mut int32,
    ) -> tresult {
        if num_bytes < 0 || (buffer.is_null() && num_bytes > 0) {
            return kInvalidArgument;
        }
        let mut data = self.data.borrow_mut();
        let position = self.position.get();
        let end = position + num_bytes as usize;
        if data.len() < end {
            data.resize(end, 0);
        }
        // SAFETY: the plugin passes `num_bytes` readable bytes.
        unsafe {
            std::ptr::copy_nonoverlapping(
                buffer as *const u8,
                data.as_mut_ptr().add(position),
                num_bytes as usize,
            )
        };
        self.position.set(end);
        if !num_bytes_written.is_null() {
            // SAFETY: checked non-null.
            unsafe { *num_bytes_written = num_bytes };
        }
        kResultOk
    }

    unsafe fn seek(&self, pos: int64, mode: int32, result: *mut int64) -> tresult {
        let length = self.data.borrow().len() as i64;
        const SET: i32 = IBStream_::IStreamSeekMode_::kIBSeekSet as i32;
        const CURRENT: i32 = IBStream_::IStreamSeekMode_::kIBSeekCur as i32;
        const END: i32 = IBStream_::IStreamSeekMode_::kIBSeekEnd as i32;
        let base = match mode {
            SET => 0,
            CURRENT => self.position.get() as i64,
            END => length,
            _ => return kInvalidArgument,
        };
        let target = base + pos;
        if target < 0 {
            return kInvalidArgument;
        }
        self.position.set(target as usize);
        if !result.is_null() {
            // SAFETY: checked non-null.
            unsafe { *result = target };
        }
        kResultOk
    }

    unsafe fn tell(&self, pos: *mut int64) -> tresult {
        if pos.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        unsafe { *pos = self.position.get() as int64 };
        kResultOk
    }
}

/// A preallocated event list; used only in the audio role, so it never allocates while processing.
pub struct EventList {
    pub events: RefCell<Vec<Event>>,
    capacity: usize,
}

impl EventList {
    pub fn new(capacity: usize) -> EventList {
        EventList {
            events: RefCell::new(Vec::with_capacity(capacity)),
            capacity,
        }
    }
}

impl Class for EventList {
    type Interfaces = (IEventList,);
}

impl IEventListTrait for EventList {
    unsafe fn getEventCount(&self) -> int32 {
        self.events.borrow().len() as int32
    }

    unsafe fn getEvent(&self, index: int32, e: *mut Event) -> tresult {
        let events = self.events.borrow();
        match (
            usize::try_from(index).ok().and_then(|i| events.get(i)),
            e.is_null(),
        ) {
            (Some(event), false) => {
                // SAFETY: checked non-null.
                unsafe { *e = *event };
                kResultOk
            }
            _ => kInvalidArgument,
        }
    }

    unsafe fn addEvent(&self, e: *mut Event) -> tresult {
        if e.is_null() {
            return kInvalidArgument;
        }
        let mut events = self.events.borrow_mut();
        if events.len() >= self.capacity {
            return kResultFalse;
        }
        // SAFETY: checked non-null.
        events.push(unsafe { *e });
        kResultOk
    }
}

pub struct ParamQueue {
    id: Cell<ParamID>,
    pub points: RefCell<Vec<(i32, f64)>>,
}

impl Class for ParamQueue {
    type Interfaces = (IParamValueQueue,);
}

impl IParamValueQueueTrait for ParamQueue {
    unsafe fn getParameterId(&self) -> ParamID {
        self.id.get()
    }

    unsafe fn getPointCount(&self) -> int32 {
        self.points.borrow().len() as int32
    }

    unsafe fn getPoint(
        &self,
        index: int32,
        sample_offset: *mut int32,
        value: *mut ParamValue,
    ) -> tresult {
        let points = self.points.borrow();
        match (
            usize::try_from(index).ok().and_then(|i| points.get(i)),
            sample_offset.is_null() || value.is_null(),
        ) {
            (Some(&(offset, v)), false) => {
                // SAFETY: checked non-null.
                unsafe {
                    *sample_offset = offset;
                    *value = v;
                }
                kResultOk
            }
            _ => kInvalidArgument,
        }
    }

    unsafe fn addPoint(
        &self,
        sample_offset: int32,
        value: ParamValue,
        index: *mut int32,
    ) -> tresult {
        let mut points = self.points.borrow_mut();
        if points.len() >= points.capacity() {
            return kResultFalse;
        }
        // Keep the points sorted by offset; a point at an offset already present replaces it.
        let position = points
            .iter()
            .position(|&(o, _)| o >= sample_offset)
            .unwrap_or(points.len());
        if points
            .get(position)
            .is_some_and(|&(o, _)| o == sample_offset)
        {
            points[position].1 = value;
        } else {
            points.insert(position, (sample_offset, value));
        }
        if !index.is_null() {
            // SAFETY: checked non-null.
            unsafe { *index = position as int32 };
        }
        kResultOk
    }
}

/// Preallocated parameter changes: a fixed pool of queues, one per parameter that changes in a block.
pub struct ParamChanges {
    queues: Vec<ComWrapper<ParamQueue>>,
    used: Cell<usize>,
}

impl ParamChanges {
    pub fn new(queue_count: usize, points_per_queue: usize) -> ParamChanges {
        let queues = (0..queue_count)
            .map(|_| {
                ComWrapper::new(ParamQueue {
                    id: Cell::new(0),
                    points: RefCell::new(Vec::with_capacity(points_per_queue)),
                })
            })
            .collect();
        ParamChanges {
            queues,
            used: Cell::new(0),
        }
    }

    pub fn clear(&self) {
        for queue in &self.queues[..self.used.get()] {
            queue.points.borrow_mut().clear();
        }
        self.used.set(0);
    }

    pub fn is_empty(&self) -> bool {
        self.used.get() == 0
    }

    /// Adds a point; false when the pool or the queue is full.
    pub fn add(&self, id: u32, offset: i32, value: f64) -> bool {
        match self.queue_for(id) {
            Some(index) => {
                let mut ignored = 0;
                // SAFETY: our own queue object.
                unsafe { self.queues[index].addPoint(offset, value, &mut ignored) == kResultOk }
            }
            None => false,
        }
    }

    fn queue_for(&self, id: u32) -> Option<usize> {
        let used = self.used.get();
        if let Some(index) = self.queues[..used].iter().position(|q| q.id.get() == id) {
            return Some(index);
        }
        if used == self.queues.len() {
            return None;
        }
        self.queues[used].id.set(id);
        self.used.set(used + 1);
        Some(used)
    }

    /// The last value of each changed parameter.
    pub fn last_values(&self, mut visit: impl FnMut(u32, f64)) {
        for queue in &self.queues[..self.used.get()] {
            if let Some(&(_, value)) = queue.points.borrow().last() {
                visit(queue.id.get(), value);
            }
        }
    }
}

impl Class for ParamChanges {
    type Interfaces = (IParameterChanges,);
}

impl IParameterChangesTrait for ParamChanges {
    unsafe fn getParameterCount(&self) -> int32 {
        self.used.get() as int32
    }

    unsafe fn getParameterData(&self, index: int32) -> *mut IParamValueQueue {
        usize::try_from(index)
            .ok()
            .filter(|&i| i < self.used.get())
            .and_then(|i| self.queues[i].as_com_ref::<IParamValueQueue>())
            .map(|r| r.as_ptr())
            .unwrap_or(std::ptr::null_mut())
    }

    unsafe fn addParameterData(
        &self,
        id: *const ParamID,
        index: *mut int32,
    ) -> *mut IParamValueQueue {
        if id.is_null() {
            return std::ptr::null_mut();
        }
        // SAFETY: checked non-null.
        let Some(i) = self.queue_for(unsafe { *id }) else {
            return std::ptr::null_mut();
        };
        if !index.is_null() {
            // SAFETY: checked non-null.
            unsafe { *index = i as int32 };
        }
        self.queues[i]
            .as_com_ref::<IParamValueQueue>()
            .map(|r| r.as_ptr())
            .unwrap_or(std::ptr::null_mut())
    }
}

/// Resizes the editor's window to a content size; false when the window is gone.
pub type ResizeWindow = Box<dyn Fn(u32, u32) -> bool>;

/// Lets an editor ask to be resized; on Linux also the run loop plugins register their timers and sockets with.
pub struct PlugFrame {
    pub resize: RefCell<Option<ResizeWindow>>,
    #[cfg(target_os = "linux")]
    pub registrations: RefCell<Vec<(*mut c_void, u64, bool)>>,
}

impl PlugFrame {
    pub fn new() -> PlugFrame {
        PlugFrame {
            resize: RefCell::new(None),
            #[cfg(target_os = "linux")]
            registrations: RefCell::new(Vec::new()),
        }
    }
}

#[cfg(not(target_os = "linux"))]
impl Class for PlugFrame {
    type Interfaces = (IPlugFrame,);
}

#[cfg(target_os = "linux")]
impl Class for PlugFrame {
    type Interfaces = (IPlugFrame, Linux::IRunLoop);
}

impl IPlugFrameTrait for PlugFrame {
    unsafe fn resizeView(&self, view: *mut IPlugView, new_size: *mut ViewRect) -> tresult {
        if view.is_null() || new_size.is_null() {
            return kInvalidArgument;
        }
        // SAFETY: checked non-null.
        let rect = unsafe { *new_size };
        let width = (rect.right - rect.left).max(1) as u32;
        let height = (rect.bottom - rect.top).max(1) as u32;
        let resized = self
            .resize
            .borrow()
            .as_ref()
            .is_some_and(|resize| resize(width, height));
        if !resized {
            return kResultFalse;
        }
        // The SDK's hosts tell the view its new size from inside resizeView.
        // SAFETY: the view is the caller's live view.
        unsafe { ((*(*view).vtbl).onSize)(view, new_size) };
        kResultTrue
    }
}

#[cfg(target_os = "linux")]
impl Linux::IRunLoopTrait for PlugFrame {
    unsafe fn registerEventHandler(
        &self,
        handler: *mut Linux::IEventHandler,
        fd: Linux::FileDescriptor,
    ) -> tresult {
        use vst3::ComPtr;
        // SAFETY: the plugin's handler; we hold a reference while it is registered.
        let Some(handler_ref) = (unsafe { vst3::ComRef::from_raw(handler) }) else {
            return kInvalidArgument;
        };
        let handler_ptr: ComPtr<Linux::IEventHandler> = handler_ref.to_com_ptr();
        let id = crate::gui::add_fd(
            fd,
            Box::new(move || {
                // SAFETY: the handler is alive while registered.
                unsafe { Linux::IEventHandlerTrait::onFDIsSet(&handler_ptr, fd) };
            }),
        );
        self.registrations
            .borrow_mut()
            .push((handler as *mut c_void, id, false));
        kResultTrue
    }

    unsafe fn unregisterEventHandler(&self, handler: *mut Linux::IEventHandler) -> tresult {
        self.unregister(handler as *mut c_void, false)
    }

    unsafe fn registerTimer(
        &self,
        handler: *mut Linux::ITimerHandler,
        milliseconds: Linux::TimerInterval,
    ) -> tresult {
        use vst3::ComPtr;
        // SAFETY: as above.
        let Some(handler_ref) = (unsafe { vst3::ComRef::from_raw(handler) }) else {
            return kInvalidArgument;
        };
        let handler_ptr: ComPtr<Linux::ITimerHandler> = handler_ref.to_com_ptr();
        let id = crate::gui::add_timer(
            std::time::Duration::from_millis(milliseconds),
            Box::new(move || {
                // SAFETY: the handler is alive while registered.
                unsafe { Linux::ITimerHandlerTrait::onTimer(&handler_ptr) };
            }),
        );
        self.registrations
            .borrow_mut()
            .push((handler as *mut c_void, id, true));
        kResultTrue
    }

    unsafe fn unregisterTimer(&self, handler: *mut Linux::ITimerHandler) -> tresult {
        self.unregister(handler as *mut c_void, true)
    }
}

#[cfg(target_os = "linux")]
impl PlugFrame {
    fn unregister(&self, handler: *mut c_void, timer: bool) -> tresult {
        let mut registrations = self.registrations.borrow_mut();
        let before = registrations.len();
        registrations.retain(|&(h, id, is_timer)| {
            if h == handler && is_timer == timer {
                if timer {
                    crate::gui::remove_timer(id);
                } else {
                    crate::gui::remove_fd(id);
                }
                false
            } else {
                true
            }
        });
        if registrations.len() != before {
            kResultTrue
        } else {
            kResultFalse
        }
    }

    pub fn unregister_all(&self) {
        for (_, id, timer) in self.registrations.borrow_mut().drain(..) {
            if timer {
                crate::gui::remove_timer(id);
            } else {
                crate::gui::remove_fd(id);
            }
        }
    }
}
