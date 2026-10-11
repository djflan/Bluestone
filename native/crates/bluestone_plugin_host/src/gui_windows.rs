//! Windows: a message pump on the main thread and plain Win32 windows for editors. Compiled in CI; not verified on
//! hardware (see docs/plugin-hosting.md).

use std::cell::Cell;
use std::ffi::c_void;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Duration;

use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::GetCurrentThreadId;
use windows_sys::Win32::UI::HiDpi::GetDpiForWindow;
use windows_sys::Win32::UI::WindowsAndMessaging::*;

use super::{WindowCallback, WindowEvent};

static MAIN_THREAD_ID: AtomicU32 = AtomicU32::new(0);
static CLASS: OnceLock<Vec<u16>> = OnceLock::new();

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn init() -> Result<(), String> {
    // SAFETY: plain Win32 calls.
    unsafe {
        MAIN_THREAD_ID.store(GetCurrentThreadId(), Ordering::Release);
        let name = CLASS.get_or_init(|| wide("BluestonePluginEditor"));
        let class = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(window_procedure),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: GetModuleHandleW(std::ptr::null()),
            hIcon: std::ptr::null_mut(),
            hCursor: LoadCursorW(std::ptr::null_mut(), IDC_ARROW),
            hbrBackground: std::ptr::null_mut(),
            lpszMenuName: std::ptr::null(),
            lpszClassName: name.as_ptr(),
        };
        RegisterClassW(&class);
    }
    Ok(())
}

pub fn pump(timeout: Duration) {
    // SAFETY: plain Win32 message loop on the thread that owns the windows.
    unsafe {
        MsgWaitForMultipleObjects(
            0,
            std::ptr::null(),
            0,
            timeout.as_millis().min(u32::MAX as u128) as u32,
            QS_ALLINPUT,
        );
        let mut message: MSG = std::mem::zeroed();
        while PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_REMOVE) != 0 {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

pub fn wake() {
    let thread = MAIN_THREAD_ID.load(Ordering::Acquire);
    if thread != 0 {
        // SAFETY: posting a no-op message to the main thread's queue.
        unsafe { PostThreadMessageW(thread, WM_NULL, 0, 0) };
    }
}

struct WindowState {
    callback: WindowCallback,
    last_size: Cell<(u32, u32)>,
}

unsafe extern "system" fn window_procedure(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    // SAFETY: the user data is the WindowState box this module stored, or null.
    let state =
        unsafe { (GetWindowLongPtrW(window, GWLP_USERDATA) as *const WindowState).as_ref() };
    match (message, state) {
        (WM_CLOSE, Some(state)) => {
            (state.callback)(WindowEvent::CloseRequested);
            0
        }
        (WM_SIZE, Some(state)) => {
            let size = (
                (lparam as u32 & 0xFFFF).max(1),
                ((lparam as u32 >> 16) & 0xFFFF).max(1),
            );
            if state.last_size.replace(size) != size {
                (state.callback)(WindowEvent::Resized {
                    width: size.0,
                    height: size.1,
                });
            }
            0
        }
        // SAFETY: default handling.
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

pub struct Window {
    handle: HWND,
    state: *mut WindowState,
    style: Cell<u32>,
}

fn outer_size(width: u32, height: u32, style: u32) -> (i32, i32) {
    let mut rect = RECT {
        left: 0,
        top: 0,
        right: width as i32,
        bottom: height as i32,
    };
    // SAFETY: plain call.
    unsafe { AdjustWindowRectEx(&mut rect, style, 0, 0) };
    (rect.right - rect.left, rect.bottom - rect.top)
}

impl Window {
    pub fn create(
        title: &str,
        width: u32,
        height: u32,
        resizable: bool,
        callback: WindowCallback,
    ) -> Result<Window, String> {
        let class = CLASS.get().ok_or("The window class is not registered.")?;
        let style = if resizable {
            WS_OVERLAPPEDWINDOW
        } else {
            WS_OVERLAPPEDWINDOW & !(WS_THICKFRAME | WS_MAXIMIZEBOX)
        };
        let (outer_width, outer_height) = outer_size(width, height, style);
        let title = wide(title);
        // SAFETY: plain Win32 window creation on the main thread.
        unsafe {
            let handle = CreateWindowExW(
                0,
                class.as_ptr(),
                title.as_ptr(),
                style,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                outer_width,
                outer_height,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                GetModuleHandleW(std::ptr::null()),
                std::ptr::null(),
            );
            if handle.is_null() {
                return Err("Could not create the editor window.".into());
            }
            let state = Box::into_raw(Box::new(WindowState {
                callback,
                last_size: Cell::new((width, height)),
            }));
            SetWindowLongPtrW(handle, GWLP_USERDATA, state as isize);
            ShowWindow(handle, SW_SHOW);
            SetForegroundWindow(handle);
            Ok(Window {
                handle,
                state,
                style: Cell::new(style),
            })
        }
    }

    pub fn parent_handle(&self) -> *mut c_void {
        self.handle as *mut c_void
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        // SAFETY: our own window and state.
        unsafe {
            (*self.state).last_size.set((width, height));
            let (outer_width, outer_height) = outer_size(width, height, self.style.get());
            SetWindowPos(
                self.handle,
                std::ptr::null_mut(),
                0,
                0,
                outer_width,
                outer_height,
                SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE,
            );
        }
    }

    pub fn set_resizable(&self, resizable: bool) {
        let style = if resizable {
            WS_OVERLAPPEDWINDOW
        } else {
            WS_OVERLAPPEDWINDOW & !(WS_THICKFRAME | WS_MAXIMIZEBOX)
        };
        self.style.set(style);
        // SAFETY: our own window.
        unsafe { SetWindowLongPtrW(self.handle, GWL_STYLE, style as isize) };
    }

    pub fn content_scale(&self) -> f64 {
        // SAFETY: our own window.
        let dpi = unsafe { GetDpiForWindow(self.handle) };
        if dpi == 0 { 1.0 } else { dpi as f64 / 96.0 }
    }
}

impl Drop for Window {
    fn drop(&mut self) {
        // SAFETY: our own window and the state box stored in it.
        unsafe {
            SetWindowLongPtrW(self.handle, GWLP_USERDATA, 0);
            DestroyWindow(self.handle);
            drop(Box::from_raw(self.state));
        }
    }
}
