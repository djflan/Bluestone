//! The CLAP instance against a fake plugin defined here: "test.fake" has one parameter (id 7, range 0..10), records
//! the parameter values it is sent, and reports an edit to 2.5 from every block; "test.fail-init" registers a timer in
//! `init` and then fails.

use std::cell::{Cell, RefCell};
use std::ffi::{CStr, c_char, c_void};
use std::path::Path;

use clap_sys::ext::timer_support::{CLAP_EXT_TIMER_SUPPORT, clap_host_timer_support};
use clap_sys::host::clap_host;
use clap_sys::id::clap_id;
use clap_sys::version::CLAP_VERSION;

use super::*;

const PARAMETER: clap_id = 7;

thread_local! {
    static RECEIVED: RefCell<Vec<f64>> = const { RefCell::new(Vec::new()) };
    static DESTROYED: Cell<bool> = const { Cell::new(false) };
}

static FAKE: clap_plugin_descriptor = descriptor(c"test.fake");
static FAIL_INIT: clap_plugin_descriptor = descriptor(c"test.fail-init");

const fn descriptor(id: &'static CStr) -> clap_plugin_descriptor {
    clap_plugin_descriptor {
        clap_version: CLAP_VERSION,
        id: id.as_ptr(),
        name: id.as_ptr(),
        vendor: c"Bluestone".as_ptr(),
        url: c"".as_ptr(),
        manual_url: c"".as_ptr(),
        support_url: c"".as_ptr(),
        version: c"1".as_ptr(),
        description: c"".as_ptr(),
        features: std::ptr::null(),
    }
}

static ENTRY: clap_plugin_entry = clap_plugin_entry {
    clap_version: CLAP_VERSION,
    init: Some(entry_init),
    deinit: Some(entry_deinit),
    get_factory: Some(get_factory),
};

static FACTORY: clap_plugin_factory = clap_plugin_factory {
    get_plugin_count: Some(plugin_count),
    get_plugin_descriptor: Some(plugin_descriptor),
    create_plugin: Some(create_plugin),
};

static PARAMS: clap_plugin_params = clap_plugin_params {
    count: Some(param_count),
    get_info: Some(param_info),
    get_value: Some(param_value),
    value_to_text: None,
    text_to_value: None,
    flush: None,
};

unsafe extern "C" fn entry_init(_path: *const c_char) -> bool {
    true
}

unsafe extern "C" fn entry_deinit() {}

unsafe extern "C" fn get_factory(_id: *const c_char) -> *const c_void {
    &FACTORY as *const clap_plugin_factory as *const c_void
}

unsafe extern "C" fn plugin_count(_factory: *const clap_plugin_factory) -> u32 {
    2
}

unsafe extern "C" fn plugin_descriptor(
    _factory: *const clap_plugin_factory,
    index: u32,
) -> *const clap_plugin_descriptor {
    if index == 0 { &FAKE } else { &FAIL_INIT }
}

unsafe extern "C" fn create_plugin(
    _factory: *const clap_plugin_factory,
    host: *const clap_host,
    id: *const c_char,
) -> *const clap_plugin {
    // SAFETY: the host passes a C string id.
    let fail = unsafe { CStr::from_ptr(id) } == c"test.fail-init";
    Box::into_raw(Box::new(clap_plugin {
        desc: if fail { &FAIL_INIT } else { &FAKE },
        plugin_data: host as *mut c_void,
        init: Some(if fail { init_and_fail } else { init }),
        destroy: Some(destroy),
        activate: Some(activate),
        deactivate: None,
        start_processing: None,
        stop_processing: None,
        reset: None,
        process: Some(process),
        get_extension: Some(get_extension),
        on_main_thread: None,
    }))
}

unsafe extern "C" fn init(_plugin: *const clap_plugin) -> bool {
    true
}

unsafe extern "C" fn init_and_fail(plugin: *const clap_plugin) -> bool {
    // SAFETY: plugin_data is the host this plugin was created with.
    unsafe {
        let host = (*plugin).plugin_data as *const clap_host;
        let get = (*host).get_extension.expect("host extensions");
        let timers = get(host, CLAP_EXT_TIMER_SUPPORT.as_ptr()) as *const clap_host_timer_support;
        let mut id: clap_id = 0;
        let register = (*timers).register_timer.expect("register_timer");
        assert!(register(host, 10, &mut id));
    }
    false
}

unsafe extern "C" fn destroy(plugin: *const clap_plugin) {
    DESTROYED.set(true);
    // SAFETY: created by Box::into_raw in create_plugin.
    drop(unsafe { Box::from_raw(plugin as *mut clap_plugin) });
}

unsafe extern "C" fn activate(
    _plugin: *const clap_plugin,
    _rate: f64,
    _min: u32,
    _max: u32,
) -> bool {
    true
}

unsafe extern "C" fn process(
    _plugin: *const clap_plugin,
    process: *const clap_process,
) -> clap_process_status {
    // SAFETY: the host's process structure and event lists, valid for the call.
    unsafe {
        let input = &*(*process).in_events;
        for index in 0..input.size.expect("size")(input) {
            let header = input.get.expect("get")(input, index);
            if (*header).type_ == CLAP_EVENT_PARAM_VALUE {
                let event = &*(header as *const clap_event_param_value);
                RECEIVED.with_borrow_mut(|r| r.push(event.value));
            }
        }
        let edit = clap_event_param_value {
            header: clap_event_header {
                size: std::mem::size_of::<clap_event_param_value>() as u32,
                time: 0,
                space_id: CLAP_CORE_EVENT_SPACE_ID,
                type_: CLAP_EVENT_PARAM_VALUE,
                flags: 0,
            },
            param_id: PARAMETER,
            cookie: std::ptr::null_mut(),
            note_id: -1,
            port_index: -1,
            channel: -1,
            key: -1,
            value: 2.5,
        };
        let output = &*(*process).out_events;
        output.try_push.expect("try_push")(output, &edit.header);
    }
    CLAP_PROCESS_CONTINUE
}

unsafe extern "C" fn get_extension(
    _plugin: *const clap_plugin,
    id: *const c_char,
) -> *const c_void {
    // SAFETY: the host passes a C string id.
    if unsafe { CStr::from_ptr(id) } == CLAP_EXT_PARAMS {
        &PARAMS as *const clap_plugin_params as *const c_void
    } else {
        std::ptr::null()
    }
}

unsafe extern "C" fn param_count(_plugin: *const clap_plugin) -> u32 {
    1
}

unsafe extern "C" fn param_info(
    _plugin: *const clap_plugin,
    index: u32,
    info: *mut clap_param_info,
) -> bool {
    if index != 0 {
        return false;
    }
    // SAFETY: the host passes a writable clap_param_info.
    unsafe {
        let info = &mut *info;
        info.id = PARAMETER;
        info.flags = CLAP_PARAM_IS_AUTOMATABLE;
        info.cookie = std::ptr::null_mut();
        info.min_value = 0.0;
        info.max_value = 10.0;
        info.default_value = 5.0;
    }
    true
}

unsafe extern "C" fn param_value(
    _plugin: *const clap_plugin,
    id: clap_id,
    value: *mut f64,
) -> bool {
    // SAFETY: the host passes a writable f64.
    unsafe { *value = 5.0 };
    id == PARAMETER
}

fn module() -> Rc<ClapModule> {
    // SAFETY: ENTRY is a static clap_plugin_entry.
    unsafe { ClapModule::from_entry(&ENTRY, Path::new("fake.clap"), None) }
        .expect("the fake module opens")
}

fn block(frames: u32, changes: &[BphParamChange]) -> BphProcess {
    BphProcess {
        frames,
        input_channels: 0,
        output_channels: 0,
        event_count: 0,
        input: std::ptr::null(),
        output: std::ptr::null_mut(),
        events: std::ptr::null(),
        changes: changes.as_ptr(),
        change_count: changes.len() as u32,
        out_event_capacity: 0,
        out_events: std::ptr::null_mut(),
        out_event_count: 0,
        out_events_dropped: 0,
        position: 0,
        tempo: 120.0,
        playing: 0,
        reserved: 0,
    }
}

#[test]
fn a_block_keeps_its_parameter_changes_and_edits_while_the_main_thread_holds_the_ranges() {
    let instance = ClapInstance::create(module(), "test.fake").expect("the fake plugin is created");
    instance
        .activate(48_000.0, 64, 0, 0)
        .expect("the fake plugin activates");
    let changes = [BphParamChange {
        offset: 0,
        id: PARAMETER,
        value: 0.5,
    }];
    let mut block = block(16, &changes);

    let held = instance.ranges.lock().expect("the ranges lock");
    assert!(instance.process(&mut block));
    drop(held);

    assert_eq!(RECEIVED.with_borrow(|r| r.clone()), [5.0]);
    let edits = instance
        .host
        .shared
        .notifications
        .lock()
        .expect("notifications")
        .clone();
    assert_eq!(
        edits,
        [BphNotification {
            kind: notification_kind::PARAMETER_EDITED,
            id: PARAMETER,
            value: 0.25,
        }]
    );
}

#[test]
fn a_plugin_that_fails_to_initialize_is_destroyed_without_leaving_its_timers() {
    gui::claim_main_thread_for_tests();

    assert!(ClapInstance::create(module(), "test.fail-init").is_err());

    assert!(DESTROYED.get());
    assert_eq!(gui::timer_count(), 0);
}
