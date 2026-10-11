//! Linux: X11 windows for editors (through x11rb, without Xlib), a poll loop on the main thread over the X
//! connection, a wake pipe, and the file descriptors plugins register (VST3 IRunLoop, CLAP posix-fd-support).
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

static WAKE: OnceLock<(UnixStream, UnixStream)> = OnceLock::new();

struct Display {
    connection: RustConnection,
    root: Window_,
    black: u32,
    protocols: Atom,
    delete_window: Atom,
    net_wm_name: Atom,
    utf8_string: Atom,
    windows: HashMap<Window_, (WindowCallback, Rc<Cell<(u32, u32)>>)>,
}

type Window_ = u32;

struct FdHandler {
    id: u64,
    fd: i32,
    callback: Option<Box<dyn FnMut()>>,
}

thread_local! {
    static DISPLAY: RefCell<Option<Display>> = const { RefCell::new(None) };
    static FDS: RefCell<(u64, Vec<FdHandler>)> = RefCell::new((0, Vec::new()));
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
        let screen_info = &connection.setup().roots[screen];
        let (root, black) = (screen_info.root, screen_info.black_pixel);
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
        *display = Some(Display {
            connection,
            root,
            black,
            protocols,
            delete_window,
            net_wm_name,
            utf8_string,
            windows: HashMap::new(),
        });
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
                    if let Some((callback, size)) = display.windows.get(&e.window) {
                        let new_size = (e.width.max(1) as u32, e.height.max(1) as u32);
                        if size.replace(new_size) != new_size {
                            dispatch.push((
                                callback.clone(),
                                WindowEvent::Resized {
                                    width: new_size.0,
                                    height: new_size.1,
                                },
                            ));
                        }
                    }
                }
                Event::ClientMessage(e) => {
                    if e.type_ == display.protocols
                        && e.data.as_data32()[0] == display.delete_window
                    {
                        if let Some((callback, _)) = display.windows.get(&e.window) {
                            dispatch.push((callback.clone(), WindowEvent::CloseRequested));
                        }
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

pub fn wake() {
    if let Some((_, writer)) = WAKE.get() {
        use std::io::Write;
        let _ = (&*writer).write(&[1]);
    }
}

pub struct Window {
    id: Window_,
    size: Rc<Cell<(u32, u32)>>,
}

impl Window {
    pub fn create(
        title: &str,
        width: u32,
        height: u32,
        resizable: bool,
        callback: WindowCallback,
    ) -> Result<Window, String> {
        display_open()?;
        let size = Rc::new(Cell::new((width, height)));
        let id = DISPLAY.with_borrow_mut(|display| -> Result<Window_, String> {
            let display = display.as_mut().ok_or("No X11 display.")?;
            let c = &display.connection;
            let id = c.generate_id().map_err(|e| e.to_string())?;
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
                height as u16,
                0,
                WindowClass::INPUT_OUTPUT,
                0,
                &aux,
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
            if !resizable {
                set_fixed_size(c, id, width, height)?;
            }
            c.map_window(id).map_err(|e| e.to_string())?;
            c.flush().map_err(|e| e.to_string())?;
            display.windows.insert(id, (callback, size.clone()));
            Ok(id)
        })?;
        Ok(Window { id, size })
    }

    pub fn parent_handle(&self) -> *mut c_void {
        self.id as usize as *mut c_void
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        self.size.set((width, height));
        DISPLAY.with_borrow(|display| {
            if let Some(display) = display {
                let _ = display.connection.configure_window(
                    self.id,
                    &ConfigureWindowAux::new().width(width).height(height),
                );
                let _ = display.connection.flush();
            }
        });
    }

    pub fn content_scale(&self) -> f64 {
        1.0
    }
}

fn set_fixed_size(
    connection: &RustConnection,
    id: Window_,
    width: u32,
    height: u32,
) -> Result<(), String> {
    // WM_NORMAL_HINTS with PMinSize | PMaxSize (flags 16 | 32), min = max = the size.
    let mut hints = [0u32; 18];
    hints[0] = 16 | 32;
    hints[5] = width;
    hints[6] = height;
    hints[7] = width;
    hints[8] = height;
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
                let _ = display.connection.destroy_window(self.id);
                let _ = display.connection.flush();
            }
        });
    }
}
