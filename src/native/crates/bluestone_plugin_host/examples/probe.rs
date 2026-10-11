//! Loads a plugin module through the C ABI and exercises it: `cargo run --example probe -- vst3|clap PATH [editor]`.
//! A development aid for trying real plugins; the worker's tests are the real checks.

use std::ffi::{CStr, CString, c_char};

use bluestone_plugin_host::*;

fn check(result: i32, error: *mut c_char) {
    if result != 0 {
        let message = if error.is_null() {
            "?".into()
        } else {
            unsafe { CStr::from_ptr(error) }
                .to_string_lossy()
                .into_owned()
        };
        eprintln!("error: {message}");
        std::process::exit(1);
    }
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let format = if args[1] == "clap" {
        FORMAT_CLAP
    } else {
        FORMAT_VST3
    };
    let path = CString::new(args[2].clone()).unwrap();
    let mut error = std::ptr::null_mut();
    unsafe {
        check(bph_main_init(&mut error), error);
        let mut module = std::ptr::null_mut();
        check(
            bph_module_open(format, path.as_ptr(), &mut module, &mut error),
            error,
        );
        let description = bph_module_describe(module);
        let text = CStr::from_ptr(description).to_string_lossy().into_owned();
        println!("plugins: {text}");
        bph_string_free(description);
        let plugins: serde_json::Value = serde_json::from_str(&text).unwrap();
        for plugin_info in plugins.as_array().unwrap() {
            let id = CString::new(plugin_info["id"].as_str().unwrap()).unwrap();
            let mut plugin = std::ptr::null_mut();
            check(
                bph_plugin_create(module, id.as_ptr(), &mut plugin, &mut error),
                error,
            );
            let params = bph_plugin_parameters(plugin);
            let params_text = CStr::from_ptr(params).to_string_lossy().into_owned();
            let parsed: serde_json::Value = serde_json::from_str(&params_text).unwrap();
            println!(
                "{}: {} parameters; first: {}",
                plugin_info["name"],
                parsed.as_array().unwrap().len(),
                parsed
                    .as_array()
                    .unwrap()
                    .iter()
                    .take(3)
                    .map(|p| p.to_string())
                    .collect::<Vec<_>>()
                    .join(", ")
            );
            bph_string_free(params);
            check(
                bph_plugin_activate(plugin, 48000.0, 256, 2, 2, &mut error),
                error,
            );
            println!("latency {}", bph_plugin_latency(plugin));
            if let Ok(id) = std::env::var("PROBE_PARAMETER") {
                let id: u32 = id.parse().unwrap();
                let mut value = 0.0;
                bph_plugin_get_parameter(plugin, id, &mut value);
                println!(
                    "parameter {id} = {value}; set 0.8 -> {}",
                    bph_plugin_set_parameter(plugin, id, 0.8)
                );
                bph_plugin_get_parameter(plugin, id, &mut value);
                println!("after set: {value}");
                let (mut data, mut length) = (std::ptr::null_mut(), 0);
                check(
                    bph_plugin_save_state(plugin, &mut data, &mut length, &mut error),
                    error,
                );
                bph_bytes_free(data, length);
                bph_plugin_get_parameter(plugin, id, &mut value);
                println!("after save: {value}");
                let mut notifications = vec![BphNotification::default(); 16];
                let n = bph_plugin_idle(plugin, notifications.as_mut_ptr(), 16);
                bph_plugin_get_parameter(plugin, id, &mut value);
                println!("after idle ({n} notifications): {value}");
                let input = vec![0.0f32; 512];
                let mut output = vec![0f32; 512];
                let mut process = BphProcess {
                    frames: 256,
                    input_channels: 2,
                    output_channels: 2,
                    event_count: 0,
                    input: input.as_ptr(),
                    output: output.as_mut_ptr(),
                    events: std::ptr::null(),
                    changes: std::ptr::null(),
                    change_count: 0,
                    out_event_capacity: 0,
                    out_events: std::ptr::null_mut(),
                    out_event_count: 0,
                    out_events_dropped: 0,
                    position: 0,
                    tempo: 120.0,
                    playing: 1,
                    reserved: 0,
                };
                bph_plugin_process(plugin, &mut process);
                bph_plugin_get_parameter(plugin, id, &mut value);
                println!("after process: {value}");
                let n = bph_plugin_idle(plugin, notifications.as_mut_ptr(), 16);
                bph_plugin_get_parameter(plugin, id, &mut value);
                println!("after process+idle ({n}): {value}");
            }
            let input = vec![0.25f32; 512];
            let mut output = vec![0f32; 512];
            let events = [BphEvent {
                offset: 0,
                kind: 2,
                channel: 0,
                data1: 60,
                data2: 100,
            }];
            let mut out_events = vec![BphEvent::default(); 64];
            let mut peak = 0f32;
            let mut out_count = 0;
            for block in 0..40 {
                let mut process = BphProcess {
                    frames: 256,
                    input_channels: 2,
                    output_channels: 2,
                    event_count: if block == 0 { 1 } else { 0 },
                    input: input.as_ptr(),
                    output: output.as_mut_ptr(),
                    events: events.as_ptr(),
                    changes: std::ptr::null(),
                    change_count: 0,
                    out_event_capacity: 64,
                    out_events: out_events.as_mut_ptr(),
                    out_event_count: 0,
                    out_events_dropped: 0,
                    position: block * 256,
                    tempo: 120.0,
                    playing: 1,
                    reserved: 0,
                };
                bph_plugin_process(plugin, &mut process);
                out_count += process.out_event_count;
                peak = output.iter().fold(peak, |p, s| p.max(s.abs()));
            }
            println!("peak {peak}, output events {out_count}");
            let mut data = std::ptr::null_mut();
            let mut length = 0;
            check(
                bph_plugin_save_state(plugin, &mut data, &mut length, &mut error),
                error,
            );
            println!("state {length} bytes");
            check(
                bph_plugin_load_state(plugin, data, length, &mut error),
                error,
            );
            bph_bytes_free(data, length);
            println!("has editor {}", bph_plugin_has_editor(plugin));
            if args.get(3).is_some_and(|a| a == "editor") {
                let title = CString::new("probe").unwrap();
                check(
                    bph_plugin_open_editor(plugin, title.as_ptr(), &mut error),
                    error,
                );
                let mut notifications = vec![BphNotification::default(); 16];
                for _ in 0..300 {
                    bph_main_pump(10);
                    let n = bph_plugin_idle(plugin, notifications.as_mut_ptr(), 16);
                    for x in &notifications[..n as usize] {
                        println!("notification {x:?}");
                    }
                    if bph_plugin_is_editor_open(plugin) == 0 {
                        break;
                    }
                }
                bph_plugin_close_editor(plugin);
            }
            bph_plugin_destroy(plugin);
        }
        bph_module_close(module);
    }
}
