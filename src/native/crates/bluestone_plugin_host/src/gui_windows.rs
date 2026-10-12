//! Windows: a message pump on the main thread and plain Win32 windows for editors. Compiled in CI; not verified on
//! hardware (see docs/plugin-hosting.md).

use std::cell::Cell;
use std::ffi::c_void;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Duration;

use windows_sys::Win32::Foundation::{HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{MONITOR_DEFAULTTONULL, MonitorFromWindow};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::GetCurrentThreadId;
use windows_sys::Win32::UI::HiDpi::GetDpiForWindow;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    GetKeyState, VK_CONTROL, VK_LWIN, VK_MENU, VK_RWIN, VK_SHIFT,
};
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
            if take_key(&message) {
                continue;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}

/// Takes a keyboard message for an editor window whose keys go to the host (the window or any child of it): key presses
/// and releases are queued, character messages dropped, and none reaches the plugin. True when taken.
fn take_key(message: &MSG) -> bool {
    let kind = message.message;
    let is_key = matches!(kind, WM_KEYDOWN | WM_KEYUP | WM_SYSKEYDOWN | WM_SYSKEYUP);
    if !is_key && !matches!(kind, WM_CHAR | WM_SYSCHAR | WM_DEADCHAR | WM_SYSDEADCHAR) {
        return false;
    }
    // SAFETY: GetAncestor accepts any window handle, including one that no longer exists.
    let root = unsafe { GetAncestor(message.hwnd, GA_ROOT) };
    let Some(queue) = super::routed_queue(root as usize) else {
        return false;
    };
    if is_key {
        let usage = crate::keys::from_windows(message.wParam as u32);
        if usage != 0 {
            // SAFETY: GetKeyState reads this thread's keyboard state.
            let held = |vk: u16| unsafe { GetKeyState(vk as i32) } < 0;
            let mut modifiers = 0;
            if held(VK_SHIFT) {
                modifiers |= crate::keys::modifier::SHIFT;
            }
            if held(VK_CONTROL) {
                modifiers |= crate::keys::modifier::CONTROL;
            }
            if held(VK_MENU) {
                modifiers |= crate::keys::modifier::ALT;
            }
            if held(VK_LWIN) || held(VK_RWIN) {
                modifiers |= crate::keys::modifier::META;
            }
            let down = kind == WM_KEYDOWN || kind == WM_SYSKEYDOWN;
            queue.borrow_mut().push(super::KeyEvent {
                usage,
                modifiers,
                down,
                repeat: down && (message.lParam >> 30) & 1 == 1,
            });
        }
    }
    true
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

    pub fn content_scale(&self) -> f64 {
        // SAFETY: our own window.
        let dpi = unsafe { GetDpiForWindow(self.handle) };
        if dpi == 0 { 1.0 } else { dpi as f64 / 96.0 }
    }

    /// The window handle, which the root of any message for the editor's views resolves to.
    pub fn native_id(&self) -> usize {
        self.handle as usize
    }

    pub fn set_visible(&self, visible: bool) {
        // SAFETY: our own window.
        unsafe {
            if !visible {
                ShowWindow(self.handle, SW_HIDE);
                return;
            }
            // A window left on a monitor that is no longer attached comes back near the primary monitor's corner.
            if MonitorFromWindow(self.handle, MONITOR_DEFAULTTONULL).is_null() {
                SetWindowPos(
                    self.handle,
                    std::ptr::null_mut(),
                    80,
                    80,
                    0,
                    0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE,
                );
            }
            ShowWindow(self.handle, SW_SHOWNOACTIVATE);
        }
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
