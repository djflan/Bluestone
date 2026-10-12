//! macOS: an NSApplication (accessory, so the worker has no Dock icon) whose events are pumped on the main thread,
//! and NSWindows for editors.

use std::cell::Cell;
use std::ffi::c_void;
use std::sync::atomic::{AtomicPtr, Ordering};
use std::time::Duration;

use objc2::rc::Retained;
use objc2::runtime::ProtocolObject;
use objc2::{DefinedClass, MainThreadMarker, MainThreadOnly, define_class, msg_send};
use objc2_app_kit::{
    NSApplication, NSApplicationActivationPolicy, NSBackingStoreType, NSEvent, NSEventMask,
    NSEventModifierFlags, NSEventType, NSWindow, NSWindowDelegate, NSWindowStyleMask,
};
use objc2_foundation::{
    NSDate, NSDefaultRunLoopMode, NSNotification, NSObject, NSObjectProtocol, NSPoint, NSRect,
    NSSize, NSString,
};

use super::{WindowCallback, WindowEvent};

static APP: AtomicPtr<NSApplication> = AtomicPtr::new(std::ptr::null_mut());

pub fn init() -> Result<(), String> {
    let mtm = MainThreadMarker::new()
        .ok_or("The plugin host must be initialized on the process's main thread.")?;
    if !APP.load(Ordering::Acquire).is_null() {
        return Ok(());
    }
    let app = NSApplication::sharedApplication(mtm);
    app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);
    app.finishLaunching();
    APP.store(Retained::into_raw(app), Ordering::Release);
    Ok(())
}

pub fn pump(timeout: Duration) {
    let Some(mtm) = MainThreadMarker::new() else {
        return;
    };
    let app = NSApplication::sharedApplication(mtm);
    let mut until = NSDate::dateWithTimeIntervalSinceNow(timeout.as_secs_f64());
    // SAFETY: NSDefaultRunLoopMode is an immutable framework constant.
    let mode = unsafe { NSDefaultRunLoopMode };
    while let Some(event) = app.nextEventMatchingMask_untilDate_inMode_dequeue(
        NSEventMask::Any,
        Some(&until),
        mode,
        true,
    ) {
        until = NSDate::distantPast();
        let kind = event.r#type();
        if kind == NSEventType::ApplicationDefined || take_key(&event, kind) {
            continue;
        }
        app.sendEvent(&event);
    }
    app.updateWindows();
}

/// Takes a key event for a window whose keys go to the host: queued (modifier changes alone are just dropped), never
/// sent to the plugin. True when taken.
fn take_key(event: &NSEvent, kind: NSEventType) -> bool {
    if kind != NSEventType::KeyDown
        && kind != NSEventType::KeyUp
        && kind != NSEventType::FlagsChanged
    {
        return false;
    }
    let Some(queue) = super::routed_queue(event.windowNumber() as usize) else {
        return false;
    };
    if kind == NSEventType::FlagsChanged {
        return true;
    }
    let flags = event.modifierFlags();
    let mut modifiers = 0;
    for (flag, bit) in [
        (NSEventModifierFlags::Shift, crate::keys::modifier::SHIFT),
        (
            NSEventModifierFlags::Control,
            crate::keys::modifier::CONTROL,
        ),
        (NSEventModifierFlags::Option, crate::keys::modifier::ALT),
        (NSEventModifierFlags::Command, crate::keys::modifier::META),
    ] {
        if flags.contains(flag) {
            modifiers |= bit;
        }
    }
    let usage = crate::keys::from_macos(event.keyCode());
    if usage != 0 {
        queue.borrow_mut().push(super::KeyEvent {
            usage,
            modifiers,
            down: kind == NSEventType::KeyDown,
            repeat: kind == NSEventType::KeyDown && event.isARepeat(),
        });
    }
    true
}

pub fn wake() {
    let app = APP.load(Ordering::Acquire);
    if app.is_null() {
        return;
    }
    let event = NSEvent::otherEventWithType_location_modifierFlags_timestamp_windowNumber_context_subtype_data1_data2(
        NSEventType::ApplicationDefined,
        NSPoint::new(0.0, 0.0),
        NSEventModifierFlags::empty(),
        0.0,
        0,
        None,
        0,
        0,
        0,
    );
    if let Some(event) = event {
        // SAFETY: `app` is the retained shared application; postEvent:atStart: may be called from any thread.
        unsafe { (*app).postEvent_atStart(&event, false) };
    }
}

pub struct DelegateState {
    callback: WindowCallback,
    last_size: Cell<(u32, u32)>,
}

define_class!(
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[name = "BluestonePluginHostEditorWindowDelegate"]
    #[ivars = DelegateState]
    struct WindowDelegate;

    unsafe impl NSObjectProtocol for WindowDelegate {}

    unsafe impl NSWindowDelegate for WindowDelegate {
        #[unsafe(method(windowShouldClose:))]
        fn window_should_close(&self, _sender: &NSWindow) -> bool {
            (self.ivars().callback)(WindowEvent::CloseRequested);
            false
        }

        #[unsafe(method(windowDidResize:))]
        fn window_did_resize(&self, notification: &NSNotification) {
            let Some(object) = notification.object() else {
                return;
            };
            // SAFETY: the notification's object is the window this delegate belongs to.
            let window: &NSWindow = unsafe { &*(Retained::as_ptr(&object) as *const NSWindow) };
            let Some(view) = window.contentView() else {
                return;
            };
            let size = view.frame().size;
            let size = (
                size.width.round().max(1.0) as u32,
                size.height.round().max(1.0) as u32,
            );
            if self.ivars().last_size.replace(size) != size {
                (self.ivars().callback)(WindowEvent::Resized {
                    width: size.0,
                    height: size.1,
                });
            }
        }
    }
);

impl WindowDelegate {
    fn new(mtm: MainThreadMarker, state: DelegateState) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(state);
        // SAFETY: NSObject's designated initializer.
        unsafe { msg_send![super(this), init] }
    }
}

pub struct Window {
    window: Retained<NSWindow>,
    delegate: Retained<WindowDelegate>,
}

impl Window {
    pub fn create(
        title: &str,
        width: u32,
        height: u32,
        resizable: bool,
        callback: WindowCallback,
    ) -> Result<Window, String> {
        let mtm = MainThreadMarker::new().ok_or("Not on the main thread.")?;
        let mut style = NSWindowStyleMask::Titled
            | NSWindowStyleMask::Closable
            | NSWindowStyleMask::Miniaturizable;
        if resizable {
            style |= NSWindowStyleMask::Resizable;
        }
        let rect = NSRect::new(
            NSPoint::new(0.0, 0.0),
            NSSize::new(width as f64, height as f64),
        );
        // SAFETY: standard NSWindow initialization on the main thread.
        let window = unsafe {
            NSWindow::initWithContentRect_styleMask_backing_defer(
                NSWindow::alloc(mtm),
                rect,
                style,
                NSBackingStoreType::Buffered,
                false,
            )
        };
        // SAFETY: the window is owned by `Window` and closed explicitly; AppKit must not release it on close.
        unsafe { window.setReleasedWhenClosed(false) };
        window.setTitle(&NSString::from_str(title));
        let delegate = WindowDelegate::new(
            mtm,
            DelegateState {
                callback,
                last_size: Cell::new((width, height)),
            },
        );
        window.setDelegate(Some(ProtocolObject::from_ref(&*delegate)));
        window.center();
        window.makeKeyAndOrderFront(None);
        let app = NSApplication::sharedApplication(mtm);
        #[allow(deprecated)]
        app.activateIgnoringOtherApps(true);
        Ok(Window { window, delegate })
    }

    pub fn parent_handle(&self) -> *mut c_void {
        self.window
            .contentView()
            .map(|v| Retained::as_ptr(&v) as *mut c_void)
            .unwrap_or(std::ptr::null_mut())
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        self.delegate.ivars().last_size.set((width, height));
        self.window
            .setContentSize(NSSize::new(width as f64, height as f64));
    }

    pub fn content_scale(&self) -> f64 {
        1.0
    }

    /// The window number AppKit puts on the window's events.
    pub fn native_id(&self) -> usize {
        self.window.windowNumber() as usize
    }

    pub fn set_visible(&self, visible: bool) {
        if !visible {
            self.window.orderOut(None);
            return;
        }
        // A window left on a screen that is no longer attached comes back in the middle of the main screen.
        if self.window.screen().is_none() {
            self.window.center();
        }
        self.window.orderFrontRegardless();
    }
}

impl Drop for Window {
    fn drop(&mut self) {
        self.window.setDelegate(None);
        self.window.close();
    }
}
