//! macOS: an NSApplication (accessory, so the worker has no Dock icon) whose events are pumped on the main thread,
//! and NSWindows for editors: a header view drawn from the shared renderer above the plugin's area. Editor windows sit
//! at the floating level while Bluestone is the active application (followed through NSWorkspace activations).

use std::cell::{Cell, RefCell};
use std::ffi::c_void;
use std::rc::Rc;
use std::sync::atomic::{AtomicPtr, Ordering};
use std::time::Duration;

use objc2::rc::Retained;
use objc2::runtime::{AnyObject, ProtocolObject};
use objc2::{
    AllocAnyThread, DefinedClass, MainThreadMarker, MainThreadOnly, define_class, msg_send, sel,
};
use objc2_app_kit::{
    NSApplication, NSApplicationActivationPolicy, NSAutoresizingMaskOptions, NSBackingStoreType,
    NSBitmapImageRep, NSColorSpace, NSDeviceRGBColorSpace, NSEvent, NSEventMask,
    NSEventModifierFlags, NSEventType, NSFloatingWindowLevel, NSNormalWindowLevel, NSResponder,
    NSRunningApplication, NSView, NSWindow, NSWindowDelegate, NSWindowLevel, NSWindowStyleMask,
    NSWorkspace, NSWorkspaceApplicationKey, NSWorkspaceDidActivateApplicationNotification,
};
use objc2_foundation::{
    NSDate, NSDefaultRunLoopMode, NSNotification, NSObject, NSObjectProtocol, NSPoint, NSRect,
    NSSize, NSString,
};

use super::{Button, WindowCallback, WindowEvent};
use crate::header::{self, Header};

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
    /// The plugin area's last known size.
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
                (size.height - header::HEIGHT).round().max(1.0) as u32,
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

pub struct HeaderViewState {
    header: Rc<Header>,
    // Tooltip owners: AppKit does not retain them.
    tips: RefCell<Vec<Retained<NSString>>>,
}

define_class!(
    #[unsafe(super(NSView, NSResponder, NSObject))]
    #[thread_kind = MainThreadOnly]
    #[name = "BluestonePluginHostEditorHeaderView"]
    #[ivars = HeaderViewState]
    struct HeaderView;

    impl HeaderView {
        #[unsafe(method(isOpaque))]
        fn is_opaque(&self) -> bool {
            true
        }

        // A click on a button works even while the window is not key, and never takes the plugin's keyboard focus (a
        // plain NSView does not accept first responder).
        #[unsafe(method(acceptsFirstMouse:))]
        fn accepts_first_mouse(&self, _event: Option<&NSEvent>) -> bool {
            true
        }

        #[unsafe(method(drawRect:))]
        fn draw_rect(&self, _dirty: NSRect) {
            self.draw();
        }

        #[unsafe(method(mouseDown:))]
        fn mouse_down(&self, event: &NSEvent) {
            let point = self.convertPoint_fromView(event.locationInWindow(), None);
            let height = self.bounds().size.height;
            if self.ivars().header.click(point.x, height - point.y) {
                self.refresh();
            }
        }
    }
);

impl HeaderView {
    fn new(mtm: MainThreadMarker, frame: NSRect, header: Rc<Header>) -> Retained<Self> {
        let this = Self::alloc(mtm).set_ivars(HeaderViewState {
            header,
            tips: RefCell::new(Vec::new()),
        });
        // SAFETY: NSView's designated initializer.
        unsafe { msg_send![super(this), initWithFrame: frame] }
    }

    fn refresh(&self) {
        self.setNeedsDisplay(true);
        self.update_tips();
    }

    fn update_tips(&self) {
        self.removeAllToolTips();
        let state = self.ivars().header.state();
        let height = self.bounds().size.height;
        let mut tips = self.ivars().tips.borrow_mut();
        tips.clear();
        for button in Button::ALL {
            let Some((left, top, width, button_height)) = state.frame(button) else {
                continue;
            };
            let tip = NSString::from_str(button.label(state.get(button)));
            let rect = NSRect::new(
                NSPoint::new(left, height - top - button_height),
                NSSize::new(width, button_height),
            );
            let owner: &AnyObject = tip.as_ref();
            // SAFETY: an NSString owner answers with its description (itself); it is kept alive in `tips`.
            unsafe { self.addToolTipRect_owner_userData(rect, owner, std::ptr::null_mut()) };
            tips.push(tip);
        }
    }

    fn draw(&self) {
        let bounds = self.bounds();
        let scale = self.window().map_or(1.0, |w| w.backingScaleFactor());
        let width = (bounds.size.width * scale).round() as usize;
        let height = (bounds.size.height * scale).round() as usize;
        if width == 0 || height == 0 {
            return;
        }
        let pixels = header::render(&self.ivars().header.state(), width, height, scale);
        // SAFETY: AppKit allocates the planes (null) for an 8-bit RGBA bitmap of this size.
        let rep = unsafe {
            NSBitmapImageRep::initWithBitmapDataPlanes_pixelsWide_pixelsHigh_bitsPerSample_samplesPerPixel_hasAlpha_isPlanar_colorSpaceName_bytesPerRow_bitsPerPixel(
                NSBitmapImageRep::alloc(),
                std::ptr::null_mut(),
                width as isize,
                height as isize,
                8,
                4,
                true,
                false,
                NSDeviceRGBColorSpace,
                (width * 4) as isize,
                32,
            )
        };
        let Some(rep) = rep else {
            return;
        };
        let data = rep.bitmapData();
        if data.is_null() {
            return;
        }
        // SAFETY: the bitmap holds width × height RGBA pixels, rows top to bottom.
        let bytes = unsafe { std::slice::from_raw_parts_mut(data, width * height * 4) };
        for (out, pixel) in bytes.as_chunks_mut::<4>().0.iter_mut().zip(&pixels) {
            out.copy_from_slice(&[(pixel >> 16) as u8, (pixel >> 8) as u8, *pixel as u8, 0xFF]);
        }
        // The theme's colours are sRGB, as in Bluestone's own window.
        let rep = rep
            .bitmapImageRepByRetaggingWithColorSpace(&NSColorSpace::sRGBColorSpace())
            .unwrap_or(rep);
        rep.drawInRect(bounds);
    }
}

pub struct ActivationState;

define_class!(
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[name = "BluestonePluginHostActivationObserver"]
    #[ivars = ActivationState]
    struct ActivationObserver;

    impl ActivationObserver {
        #[unsafe(method(applicationActivated:))]
        fn application_activated(&self, notification: &NSNotification) {
            // SAFETY: an immutable framework constant.
            let key: &AnyObject = unsafe { NSWorkspaceApplicationKey }.as_ref();
            let pid = notification
                .userInfo()
                .and_then(|info| info.objectForKey(key))
                .and_then(|app| app.downcast::<NSRunningApplication>().ok())
                .map(|app| app.processIdentifier());
            if let Some(pid) = pid
                && super::set_bluestone_active(super::is_bluestone_process(pid as u32))
            {
                apply_levels();
            }
        }
    }
);

thread_local! {
    static EDITORS: RefCell<Vec<Retained<NSWindow>>> = const { RefCell::new(Vec::new()) };
    static OBSERVER: RefCell<Option<Retained<ActivationObserver>>> = const { RefCell::new(None) };
}

/// Starts following which application is active, once.
fn watch_activation(mtm: MainThreadMarker) {
    OBSERVER.with_borrow_mut(|observer| {
        if observer.is_some() {
            return;
        }
        let this = ActivationObserver::alloc(mtm).set_ivars(ActivationState);
        // SAFETY: NSObject's designated initializer.
        let watcher: Retained<ActivationObserver> = unsafe { msg_send![super(this), init] };
        let workspace = NSWorkspace::sharedWorkspace();
        // SAFETY: the observer stays alive (in OBSERVER) for the life of the process, and its selector takes the
        // notification.
        unsafe {
            workspace
                .notificationCenter()
                .addObserver_selector_name_object(
                    &watcher,
                    sel!(applicationActivated:),
                    Some(NSWorkspaceDidActivateApplicationNotification),
                    None,
                );
        }
        if let Some(front) = workspace.frontmostApplication() {
            super::set_bluestone_active(super::is_bluestone_process(
                front.processIdentifier() as u32
            ));
        }
        *observer = Some(watcher);
    });
}

fn level() -> NSWindowLevel {
    if super::bluestone_active() {
        NSFloatingWindowLevel
    } else {
        NSNormalWindowLevel
    }
}

fn apply_levels() {
    let level = level();
    EDITORS.with_borrow(|editors| {
        for window in editors {
            window.setLevel(level);
        }
    });
}

/// Bluestone's process id is the parent of its workers.
pub fn parent_process(pid: u32) -> Option<u32> {
    // SAFETY: proc_pidinfo fills at most `size` bytes of `info`.
    unsafe {
        let mut info: libc::proc_bsdinfo = std::mem::zeroed();
        let size = std::mem::size_of::<libc::proc_bsdinfo>() as i32;
        let read = libc::proc_pidinfo(
            pid as i32,
            libc::PROC_PIDTBSDINFO,
            0,
            &mut info as *mut _ as *mut c_void,
            size,
        );
        (read == size).then_some(info.pbi_ppid)
    }
}

pub struct Window {
    window: Retained<NSWindow>,
    delegate: Retained<WindowDelegate>,
    area: Retained<NSView>,
    header_view: Retained<HeaderView>,
}

impl Window {
    pub fn create(
        title: &str,
        width: u32,
        height: u32,
        resizable: bool,
        callback: WindowCallback,
        header: Rc<Header>,
    ) -> Result<Window, String> {
        let mtm = MainThreadMarker::new().ok_or("Not on the main thread.")?;
        let mut style = NSWindowStyleMask::Titled
            | NSWindowStyleMask::Closable
            | NSWindowStyleMask::Miniaturizable;
        if resizable {
            style |= NSWindowStyleMask::Resizable;
        }
        let width = super::area_width(width, 1.0);
        let rect = NSRect::new(
            NSPoint::new(0.0, 0.0),
            NSSize::new(width as f64, height as f64 + header::HEIGHT),
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
        let content = window
            .contentView()
            .ok_or("The window has no content view.")?;
        // AppKit's y axis points up: the plugin's area at the bottom, the header above it.
        let area = NSView::initWithFrame(
            NSView::alloc(mtm),
            NSRect::new(
                NSPoint::new(0.0, 0.0),
                NSSize::new(width as f64, height as f64),
            ),
        );
        area.setAutoresizingMask(
            NSAutoresizingMaskOptions::ViewWidthSizable
                | NSAutoresizingMaskOptions::ViewHeightSizable,
        );
        let header_view = HeaderView::new(
            mtm,
            NSRect::new(
                NSPoint::new(0.0, height as f64),
                NSSize::new(width as f64, header::HEIGHT),
            ),
            header,
        );
        header_view.setAutoresizingMask(
            NSAutoresizingMaskOptions::ViewWidthSizable | NSAutoresizingMaskOptions::ViewMinYMargin,
        );
        content.addSubview(&area);
        content.addSubview(&header_view);
        if resizable {
            window.setContentMinSize(NSSize::new(header::MIN_WIDTH, header::HEIGHT + 1.0));
        }
        let delegate = WindowDelegate::new(
            mtm,
            DelegateState {
                callback,
                last_size: Cell::new((width, height)),
            },
        );
        window.setDelegate(Some(ProtocolObject::from_ref(&*delegate)));
        watch_activation(mtm);
        window.setLevel(level());
        EDITORS.with_borrow_mut(|editors| editors.push(window.clone()));
        window.center();
        window.makeKeyAndOrderFront(None);
        let app = NSApplication::sharedApplication(mtm);
        #[allow(deprecated)]
        app.activateIgnoringOtherApps(true);
        header_view.update_tips();
        Ok(Window {
            window,
            delegate,
            area,
            header_view,
        })
    }

    pub fn parent_handle(&self) -> *mut c_void {
        Retained::as_ptr(&self.area) as *mut c_void
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        let width = super::area_width(width, 1.0);
        self.delegate.ivars().last_size.set((width, height));
        self.window
            .setContentSize(NSSize::new(width as f64, height as f64 + header::HEIGHT));
    }

    pub fn content_scale(&self) -> f64 {
        1.0
    }

    /// The window number AppKit puts on the window's events.
    pub fn native_id(&self) -> usize {
        self.window.windowNumber() as usize
    }

    pub fn header_changed(&self) {
        self.header_view.refresh();
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
        let this = Retained::as_ptr(&self.window);
        EDITORS.with_borrow_mut(|editors| editors.retain(|w| Retained::as_ptr(w) != this));
        self.window.setDelegate(None);
        self.window.close();
    }
}
