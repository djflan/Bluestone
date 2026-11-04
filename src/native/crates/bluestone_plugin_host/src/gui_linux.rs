//! Linux: X11 windows for editors (through x11rb, without Xlib), a poll loop on the main thread over the X
//! connection, a wake pipe, and the file descriptors plugins register (VST3 IRunLoop, CLAP posix-fd-support). An
//! editor window is a header child window (the shared renderer's pixels, put as an image) above a child window for the
//! plugin. Editors ask the window manager to keep them above (`_NET_WM_STATE_ABOVE`) while a Bluestone window is
//! active (`_NET_ACTIVE_WINDOW`, `_NET_WM_PID`); that depends on the window manager.
//! Compiled in CI; not verified on hardware (see docs/plugin-hosting.md).

use std::cell::{Cell, RefCell};
use std::collections::HashMap;
use std::ffi::c_void;
use std::io::Read;
use std::os::fd::AsRawFd;
use std::os::unix::net::UnixStream;
use std::rc::Rc;
use std::sync::OnceLock;
use std::time::Duration;

use x11rb::connection::Connection;
use x11rb::protocol::Event;
use x11rb::protocol::xproto::*;
use x11rb::rust_connection::RustConnection;
use x11rb::wrapper::ConnectionExt as _;

use super::{WindowCallback, WindowEvent};
use crate::header::{self, Header};

static WAKE: OnceLock<(UnixStream, UnixStream)> = OnceLock::new();

struct Display {
    connection: RustConnection,
    root: Window_,
    black: u32,
    /// The root depth, when its images are 32 bits per pixel, least significant byte first (B, G, R, X): the format
    /// the header's pixels are in. Otherwise the header shows its background only.
    image_depth: Option<u8>,
    protocols: Atom,
    delete_window: Atom,
    net_wm_name: Atom,
    utf8_string: Atom,
    net_wm_pid: Atom,
    net_wm_state: Atom,
    net_wm_state_above: Atom,
    net_active_window: Atom,
    windows: HashMap<Window_, WindowEntry>,
    /// Each header window's top-level window.
    headers: HashMap<Window_, Window_>,
}

type Window_ = u32;

/// An editor window: its callback, the plugin area's last known size, its header, and its two child windows.
struct WindowEntry {
    callback: WindowCallback,
    size: Rc<Cell<(u32, u32)>>,
    header: Rc<Header>,
    header_window: Window_,
    area: Window_,
}

struct FdHandler {
    id: u64,
    fd: i32,
    callback: Option<Box<dyn FnMut()>>,
}

thread_local! {
    static DISPLAY: RefCell<Option<Display>> = const { RefCell::new(None) };
    static FDS: RefCell<(u64, Vec<FdHandler>)> = const { RefCell::new((0, Vec::new())) };
}

pub fn init() -> Result<(), String> {
    if WAKE.get().is_none() {
        let pair =
            UnixStream::pair().map_err(|e| format!("Could not create the wake pipe: {e}"))?;
        pair.0.set_nonblocking(true).map_err(|e| e.to_string())?;
        pair.1.set_nonblocking(true).map_err(|e| e.to_string())?;
        let _ = WAKE.set(pair);
    }
    Ok(())
}

fn display_open() -> Result<(), String> {
    DISPLAY.with_borrow_mut(|display| {
        if display.is_some() {
            return Ok(());
        }
        let (connection, screen) =
            x11rb::connect(None).map_err(|e| format!("No X11 display: {e}"))?;
        let setup = connection.setup();
        let screen_info = &setup.roots[screen];
        let (root, black) = (screen_info.root, screen_info.black_pixel);
        let depth = screen_info.root_depth;
        let image_depth = (setup.image_byte_order == ImageOrder::LSB_FIRST
            && setup
                .pixmap_formats
                .iter()
                .any(|f| f.depth == depth && f.bits_per_pixel == 32))
        .then_some(depth);
        let atom = |name: &str| -> Result<Atom, String> {
            Ok(connection
                .intern_atom(false, name.as_bytes())
                .map_err(|e| e.to_string())?
                .reply()
                .map_err(|e| e.to_string())?
                .atom)
        };
        let protocols = atom("WM_PROTOCOLS")?;
        let delete_window = atom("WM_DELETE_WINDOW")?;
        let net_wm_name = atom("_NET_WM_NAME")?;
        let utf8_string = atom("UTF8_STRING")?;
        let net_wm_pid = atom("_NET_WM_PID")?;
        let net_wm_state = atom("_NET_WM_STATE")?;
        let net_wm_state_above = atom("_NET_WM_STATE_ABOVE")?;
        let net_active_window = atom("_NET_ACTIVE_WINDOW")?;
        // Follow which window is active, to float editors while Bluestone's is.
        connection
            .change_window_attributes(
                root,
                &ChangeWindowAttributesAux::new().event_mask(EventMask::PROPERTY_CHANGE),
            )
            .map_err(|e| e.to_string())?;
        let display_state = Display {
            connection,
            root,
            black,
            image_depth,
            protocols,
            delete_window,
            net_wm_name,
            utf8_string,
            net_wm_pid,
            net_wm_state,
            net_wm_state_above,
            net_active_window,
            windows: HashMap::new(),
            headers: HashMap::new(),
        };
        super::set_bluestone_active(display_state.bluestone_active().unwrap_or(true));
        *display = Some(display_state);
        Ok(())
    })
}

/// Calls `callback` on the main thread whenever `fd` is readable. Returns an id for `remove_fd`.
pub fn add_fd(fd: i32, callback: Box<dyn FnMut()>) -> u64 {
    FDS.with_borrow_mut(|(next, handlers)| {
        *next += 1;
        handlers.push(FdHandler {
            id: *next,
            fd,
            callback: Some(callback),
        });
        *next
    })
}

pub fn remove_fd(id: u64) -> bool {
    FDS.with_borrow_mut(|(_, handlers)| {
        let before = handlers.len();
        handlers.retain(|h| h.id != id);
        before != handlers.len()
    })
}

pub fn pump(timeout: Duration) {
    let mut polled: Vec<libc::pollfd> = Vec::new();
    if let Some((reader, _)) = WAKE.get() {
        polled.push(libc::pollfd {
            fd: reader.as_raw_fd(),
            events: libc::POLLIN,
            revents: 0,
        });
    }
    let x_fd = DISPLAY.with_borrow(|d| d.as_ref().map(|d| d.connection.stream().as_raw_fd()));
    if let Some(fd) = x_fd {
        polled.push(libc::pollfd {
            fd,
            events: libc::POLLIN,
            revents: 0,
        });
    }
    let handlers: Vec<(u64, i32)> =
        FDS.with_borrow(|(_, h)| h.iter().map(|h| (h.id, h.fd)).collect());
    for &(_, fd) in &handlers {
        polled.push(libc::pollfd {
            fd,
            events: libc::POLLIN,
            revents: 0,
        });
    }
    // Events x11rb has already read sit in its buffer, where poll cannot see them.
    process_x_events();
    let wait = timeout.as_millis().min(i32::MAX as u128) as i32;
    // SAFETY: `polled` is a valid array of pollfd.
    unsafe { libc::poll(polled.as_mut_ptr(), polled.len() as libc::nfds_t, wait) };
    if let Some((reader, _)) = WAKE.get() {
        let mut buffer = [0u8; 64];
        while matches!((&*reader).read(&mut buffer), Ok(n) if n > 0) {}
    }
    process_x_events();
    let ready: Vec<u64> = handlers
        .iter()
        .filter(|(_, fd)| {
            polled
                .iter()
                .any(|p| p.fd == *fd && p.revents & libc::POLLIN != 0)
        })
        .map(|(id, _)| *id)
        .collect();
    for id in ready {
        let callback = FDS.with_borrow_mut(|(_, h)| {
            h.iter_mut()
                .find(|h| h.id == id)
                .and_then(|h| h.callback.take())
        });
        if let Some(mut callback) = callback {
            callback();
            FDS.with_borrow_mut(|(_, h)| {
                if let Some(handler) = h.iter_mut().find(|h| h.id == id) {
                    handler.callback = Some(callback);
                }
            });
        }
    }
}

fn process_x_events() {
    // Events are collected first and dispatched after the display is released, so callbacks may use it.
    let mut dispatch: Vec<(WindowCallback, WindowEvent)> = Vec::new();
    DISPLAY.with_borrow(|display| {
        let Some(display) = display else { return };
        while let Ok(Some(event)) = display.connection.poll_for_event() {
            match event {
                Event::ConfigureNotify(e) => {
                    if let Some(entry) = display.windows.get(&e.window) {
                        let size = display.layout(entry, e.width as u32, e.height as u32);
                        if entry.size.replace(size) != size {
                            dispatch.push((
                                entry.callback.clone(),
                                WindowEvent::Resized {
                                    width: size.0,
                                    height: size.1,
                                },
                            ));
                        }
                    }
                }
                Event::Expose(e) if e.count == 0 => {
                    if let Some(entry) = display.entry_of_header(e.window) {
                        display.draw_header(entry);
                    }
                }
                Event::ButtonPress(e) if e.detail == 1 => {
                    if let Some(entry) = display.entry_of_header(e.event)
                        && entry.header.click(e.event_x as f64, e.event_y as f64)
                    {
                        display.draw_header(entry);
                    }
                }
                Event::PropertyNotify(e)
                    if e.window == display.root && e.atom == display.net_active_window =>
                {
                    if let Some(active) = display.bluestone_active()
                        && super::set_bluestone_active(active)
                    {
                        for id in display.windows.keys() {
                            display.request_above(*id, active);
                        }
                        let _ = display.connection.flush();
                    }
                }
                Event::ClientMessage(e)
                    if e.type_ == display.protocols
                        && e.data.as_data32()[0] == display.delete_window =>
                {
                    if let Some(entry) = display.windows.get(&e.window) {
                        dispatch.push((entry.callback.clone(), WindowEvent::CloseRequested));
                    }
                }
                _ => {}
            }
        }
    });
    for (callback, event) in dispatch {
        callback(event);
    }
}

impl Display {
    fn entry_of_header(&self, header_window: Window_) -> Option<&WindowEntry> {
        self.headers
            .get(&header_window)
            .and_then(|id| self.windows.get(id))
    }

    /// Puts the header across the top of a window `width` × `height` and the plugin's area below; returns the area's
    /// size.
    fn layout(&self, entry: &WindowEntry, width: u32, height: u32) -> (u32, u32) {
        let width = width.max(1);
        let header_height = super::header_height(1.0).min(height.saturating_sub(1));
        let area_height = height.saturating_sub(header_height).max(1);
        let c = &self.connection;
        let _ = c.configure_window(
            entry.header_window,
            &ConfigureWindowAux::new()
                .x(0)
                .y(0)
                .width(width)
                .height(header_height.max(1)),
        );
        let _ = c.configure_window(
            entry.area,
            &ConfigureWindowAux::new()
                .x(0)
                .y(header_height as i32)
                .width(width)
                .height(area_height),
        );
        let _ = c.flush();
        (width, area_height)
    }

    fn draw_header(&self, entry: &WindowEntry) {
        let c = &self.connection;
        let _ = c.clear_area(false, entry.header_window, 0, 0, 0, 0);
        if let Some(depth) = self.image_depth {
            let state = entry.header.state();
            let width = header::drawn_width(&state).ceil() as usize;
            let height = super::header_height(1.0) as usize;
            let pixels = header::render(&state, width, height, 1.0);
            let data: Vec<u8> = pixels.iter().flat_map(|p| p.to_le_bytes()).collect();
            if let Ok(gc) = c.generate_id() {
                let _ = c.create_gc(gc, entry.header_window, &CreateGCAux::new());
                let _ = c.put_image(
                    ImageFormat::Z_PIXMAP,
                    entry.header_window,
                    gc,
                    width as u16,
                    height as u16,
                    0,
                    0,
                    0,
                    depth,
                    &data,
                );
                let _ = c.free_gc(gc);
            }
        }
        let _ = c.flush();
    }

    /// Whether the active window is Bluestone's, if the window manager says which is active.
    fn bluestone_active(&self) -> Option<bool> {
        let c = &self.connection;
        let active = c
            .get_property(
                false,
                self.root,
                self.net_active_window,
                AtomEnum::WINDOW,
                0,
                1,
            )
            .ok()?
            .reply()
            .ok()?
            .value32()?
            .next()?;
        if active == 0 {
            return Some(false);
        }
        let pid = c
            .get_property(false, active, self.net_wm_pid, AtomEnum::CARDINAL, 0, 1)
            .ok()?
            .reply()
            .ok()?
            .value32()
            .and_then(|mut v| v.next())
            .unwrap_or(0);
        Some(super::is_bluestone_process(pid))
    }

    /// Asks the window manager to keep a mapped window above others, or not.
    fn request_above(&self, window: Window_, above: bool) {
        let event = ClientMessageEvent::new(
            32,
            window,
            self.net_wm_state,
            [above as u32, self.net_wm_state_above, 0, 1, 0],
        );
        let _ = self.connection.send_event(
            false,
            self.root,
            EventMask::SUBSTRUCTURE_REDIRECT | EventMask::SUBSTRUCTURE_NOTIFY,
            event,
        );
    }

    /// Sets the state an unmapped window starts in when it is mapped: above others while Bluestone is active.
    fn initial_above(&self, window: Window_) {
        let states: &[u32] = if super::bluestone_active() {
            &[self.net_wm_state_above]
        } else {
            &[]
        };
        let _ = self.connection.change_property32(
            PropMode::REPLACE,
            window,
            self.net_wm_state,
            AtomEnum::ATOM,
            states,
        );
    }
}

/// Bluestone's process id is the parent of its workers.
pub fn parent_process(pid: u32) -> Option<u32> {
    let stat = std::fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    // pid (name) state ppid ...; the name may contain spaces and parentheses.
    let after_name = &stat[stat.rfind(')')? + 1..];
    after_name.split_whitespace().nth(1)?.parse().ok()
}

pub fn wake() {
    if let Some((_, writer)) = WAKE.get() {
        use std::io::Write;
        let _ = (&*writer).write(&[1]);
    }
}

pub struct Window {
    id: Window_,
    header_window: Window_,
    area: Window_,
    size: Rc<Cell<(u32, u32)>>,
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
        display_open()?;
        let width = super::area_width(width, 1.0);
        let header_height = super::header_height(1.0);
        let size = Rc::new(Cell::new((width, height)));
        let (id, header_window, area) =
            DISPLAY.with_borrow_mut(|display| -> Result<(Window_, Window_, Window_), String> {
                let display = display.as_mut().ok_or("No X11 display.")?;
                let c = &display.connection;
                let id = c.generate_id().map_err(|e| e.to_string())?;
                let header_window = c.generate_id().map_err(|e| e.to_string())?;
                let area = c.generate_id().map_err(|e| e.to_string())?;
                let aux = CreateWindowAux::new()
                    .background_pixel(display.black)
                    .event_mask(EventMask::STRUCTURE_NOTIFY);
                c.create_window(
                    x11rb::COPY_DEPTH_FROM_PARENT,
                    id,
                    display.root,
                    0,
                    0,
                    width as u16,
                    (height + header_height) as u16,
                    0,
                    WindowClass::INPUT_OUTPUT,
                    0,
                    &aux,
                )
                .map_err(|e| e.to_string())?;
                c.create_window(
                    x11rb::COPY_DEPTH_FROM_PARENT,
                    header_window,
                    id,
                    0,
                    0,
                    width as u16,
                    header_height as u16,
                    0,
                    WindowClass::INPUT_OUTPUT,
                    0,
                    &CreateWindowAux::new()
                        .background_pixel(header::BACKGROUND)
                        .event_mask(EventMask::EXPOSURE | EventMask::BUTTON_PRESS),
                )
                .map_err(|e| e.to_string())?;
                c.create_window(
                    x11rb::COPY_DEPTH_FROM_PARENT,
                    area,
                    id,
                    0,
                    header_height as i16,
                    width as u16,
                    height as u16,
                    0,
                    WindowClass::INPUT_OUTPUT,
                    0,
                    &CreateWindowAux::new().background_pixel(display.black),
                )
                .map_err(|e| e.to_string())?;
                c.change_property8(
                    PropMode::REPLACE,
                    id,
                    AtomEnum::WM_NAME,
                    AtomEnum::STRING,
                    title.as_bytes(),
                )
                .map_err(|e| e.to_string())?;
                c.change_property8(
                    PropMode::REPLACE,
                    id,
                    display.net_wm_name,
                    display.utf8_string,
                    title.as_bytes(),
                )
                .map_err(|e| e.to_string())?;
                c.change_property32(
                    PropMode::REPLACE,
                    id,
                    display.protocols,
                    AtomEnum::ATOM,
                    &[display.delete_window],
                )
                .map_err(|e| e.to_string())?;
                c.change_property32(
                    PropMode::REPLACE,
                    id,
                    display.net_wm_pid,
                    AtomEnum::CARDINAL,
                    &[std::process::id()],
                )
                .map_err(|e| e.to_string())?;
                if resizable {
                    // Never smaller than the header's buttons, or they would be cut off.
                    set_size_hints(c, id, (super::area_width(1, 1.0), header_height + 1), None)?;
                } else {
                    let size = (width, height + header_height);
                    set_size_hints(c, id, size, Some(size))?;
                }
                display.initial_above(id);
                let c = &display.connection;
                c.map_subwindows(id).map_err(|e| e.to_string())?;
                c.map_window(id).map_err(|e| e.to_string())?;
                c.flush().map_err(|e| e.to_string())?;
                display.windows.insert(
                    id,
                    WindowEntry {
                        callback,
                        size: size.clone(),
                        header,
                        header_window,
                        area,
                    },
                );
                display.headers.insert(header_window, id);
                Ok((id, header_window, area))
            })?;
        Ok(Window {
            id,
            header_window,
            area,
            size,
        })
    }

    pub fn parent_handle(&self) -> *mut c_void {
        self.area as usize as *mut c_void
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        let width = super::area_width(width, 1.0);
        self.size.set((width, height));
        DISPLAY.with_borrow(|display| {
            if let Some(display) = display {
                let _ = display.connection.configure_window(
                    self.id,
                    &ConfigureWindowAux::new()
                        .width(width)
                        .height(height + super::header_height(1.0)),
                );
                let _ = display.connection.flush();
            }
        });
    }

    pub fn content_scale(&self) -> f64 {
        1.0
    }

    pub fn native_id(&self) -> usize {
        self.id as usize
    }

    pub fn header_changed(&self) {
        DISPLAY.with_borrow(|display| {
            if let Some(display) = display
                && let Some(entry) = display.entry_of_header(self.header_window)
            {
                display.draw_header(entry);
            }
        });
    }

    /// Unmaps or maps the window; the window manager decides where it reappears.
    pub fn set_visible(&self, visible: bool) {
        DISPLAY.with_borrow(|display| {
            if let Some(display) = display {
                let c = &display.connection;
                let _ = if visible {
                    display.initial_above(self.id);
                    c.map_window(self.id)
                } else {
                    c.unmap_window(self.id)
                };
                let _ = c.flush();
            }
        });
    }
}

/// WM_NORMAL_HINTS: a minimum size (PMinSize, flag 16), and a maximum (PMaxSize, flag 32) when given.
fn set_size_hints(
    connection: &RustConnection,
    id: Window_,
    minimum: (u32, u32),
    maximum: Option<(u32, u32)>,
) -> Result<(), String> {
    let mut hints = [0u32; 18];
    hints[0] = 16;
    hints[5] = minimum.0;
    hints[6] = minimum.1;
    if let Some((width, height)) = maximum {
        hints[0] |= 32;
        hints[7] = width;
        hints[8] = height;
    }
    connection
        .change_property32(
            PropMode::REPLACE,
            id,
            AtomEnum::WM_NORMAL_HINTS,
            AtomEnum::WM_SIZE_HINTS,
            &hints,
        )
        .map_err(|e| e.to_string())?;
    Ok(())
}

impl Drop for Window {
    fn drop(&mut self) {
        DISPLAY.with_borrow_mut(|display| {
            if let Some(display) = display {
                display.windows.remove(&self.id);
                display.headers.remove(&self.header_window);
                let _ = display.connection.destroy_window(self.id);
                let _ = display.connection.flush();
            }
        });
    }
}
