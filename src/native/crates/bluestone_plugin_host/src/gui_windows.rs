//! Windows: a message pump on the main thread and plain Win32 windows for editors: a header child window painted from
//! the shared renderer above a child window for the plugin. Editor windows are topmost while Bluestone owns the
//! foreground window (followed with a WinEvent hook). See docs/plugin-hosting.md for what is verified on hardware.

use std::cell::{Cell, RefCell};
use std::ffi::c_void;
use std::rc::Rc;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Duration;

use windows_sys::Win32::Foundation::{
    CloseHandle, HWND, INVALID_HANDLE_VALUE, LPARAM, LRESULT, RECT, WPARAM,
};
use windows_sys::Win32::Graphics::Gdi::{
    BI_RGB, BITMAPINFO, BITMAPINFOHEADER, BLACK_BRUSH, BeginPaint, DIB_RGB_COLORS, EndPaint,
    GetStockObject, HBRUSH, InvalidateRect, MONITOR_DEFAULTTONULL, MonitorFromWindow, PAINTSTRUCT,
    SetDIBitsToDevice,
};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
};
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::System::Threading::GetCurrentThreadId;
use windows_sys::Win32::UI::Accessibility::{HWINEVENTHOOK, SetWinEventHook};
use windows_sys::Win32::UI::Controls::{
    ICC_WIN95_CLASSES, INITCOMMONCONTROLSEX, InitCommonControlsEx, TOOLTIPS_CLASSW, TTF_SUBCLASS,
    TTM_ADDTOOLW, TTM_DELTOOLW, TTS_ALWAYSTIP, TTTOOLINFOW,
};
use windows_sys::Win32::UI::HiDpi::GetDpiForWindow;
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
    GetKeyState, VK_CONTROL, VK_LWIN, VK_MENU, VK_RWIN, VK_SHIFT,
};
use windows_sys::Win32::UI::WindowsAndMessaging::*;

use super::{Button, WindowCallback, WindowEvent};
use crate::header::{self, Header};

static MAIN_THREAD_ID: AtomicU32 = AtomicU32::new(0);
static CLASS: OnceLock<Vec<u16>> = OnceLock::new();
static HEADER_CLASS: OnceLock<Vec<u16>> = OnceLock::new();
static AREA_CLASS: OnceLock<Vec<u16>> = OnceLock::new();

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
        let header = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(header_procedure),
            lpszClassName: HEADER_CLASS
                .get_or_init(|| wide("BluestonePluginEditorHeader"))
                .as_ptr(),
            ..class
        };
        RegisterClassW(&header);
        let area = WNDCLASSW {
            style: 0,
            lpfnWndProc: Some(DefWindowProcW),
            hbrBackground: GetStockObject(BLACK_BRUSH) as HBRUSH,
            lpszClassName: AREA_CLASS
                .get_or_init(|| wide("BluestonePluginEditorArea"))
                .as_ptr(),
            ..class
        };
        RegisterClassW(&area);
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
    /// The plugin area's last known size.
    last_size: Cell<(u32, u32)>,
    header: HWND,
    area: HWND,
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
            layout(
                window,
                state,
                (lparam as u32 & 0xFFFF).max(1),
                ((lparam as u32 >> 16) & 0xFFFF).max(1),
            );
            0
        }
        // SAFETY: default handling.
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

fn scale_of(window: HWND) -> f64 {
    // SAFETY: plain call; 0 for a window that is gone.
    let dpi = unsafe { GetDpiForWindow(window) };
    if dpi == 0 { 1.0 } else { dpi as f64 / 96.0 }
}

/// Puts the header across the top of a client area `width` × `height` and the plugin's area below it, and reports the
/// plugin area's new size.
fn layout(window: HWND, state: &WindowState, width: u32, height: u32) {
    let header_height = super::header_height(scale_of(window)).min(height.saturating_sub(1));
    let area_height = (height - header_height).max(1);
    // SAFETY: the window's own children.
    unsafe {
        MoveWindow(state.header, 0, 0, width as i32, header_height as i32, 1);
        MoveWindow(
            state.area,
            0,
            header_height as i32,
            width as i32,
            area_height as i32,
            1,
        );
    }
    let size = (width, area_height);
    if state.last_size.replace(size) != size {
        (state.callback)(WindowEvent::Resized {
            width: size.0,
            height: size.1,
        });
    }
}

unsafe extern "system" fn header_procedure(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    // SAFETY: the user data is the HeaderData box this module stored, or null.
    let data = unsafe { (GetWindowLongPtrW(window, GWLP_USERDATA) as *const HeaderData).as_ref() };
    match (message, data) {
        (WM_PAINT, Some(data)) => {
            paint_header(window, &data.header);
            0
        }
        (WM_ERASEBKGND, _) => 1,
        // Clicking a button never activates the editor, so Bluestone keeps the keyboard.
        (WM_MOUSEACTIVATE, _) => MA_NOACTIVATE as LRESULT,
        (WM_LBUTTONDOWN, Some(data)) => {
            let scale = scale_of(window);
            let x = (lparam as u32 & 0xFFFF) as i16 as f64 / scale;
            let y = ((lparam as u32 >> 16) & 0xFFFF) as i16 as f64 / scale;
            if data.header.click(x, y) {
                // SAFETY: our own window.
                unsafe { InvalidateRect(window, std::ptr::null(), 0) };
                update_tips(window, data);
            }
            0
        }
        // SAFETY: default handling.
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

/// What a header window keeps: its header, and its tooltip window (null when there is none).
struct HeaderData {
    header: Rc<Header>,
    tooltip: HWND,
}

fn tool(header_window: HWND, button: Button) -> TTTOOLINFOW {
    TTTOOLINFOW {
        // The size without lpReserved, which both comctl32 5 and 6 accept.
        cbSize: (std::mem::size_of::<TTTOOLINFOW>() - std::mem::size_of::<*mut c_void>()) as u32,
        hwnd: header_window,
        uId: button as usize,
        ..Default::default()
    }
}

/// Gives each shown button its tooltip, over where it is now.
fn update_tips(header_window: HWND, data: &HeaderData) {
    if data.tooltip.is_null() {
        return;
    }
    let state = data.header.state();
    let scale = scale_of(header_window);
    for button in Button::ALL {
        let mut info = tool(header_window, button);
        // SAFETY: messages to our own tooltip window; it copies the text during TTM_ADDTOOLW.
        unsafe {
            SendMessageW(data.tooltip, TTM_DELTOOLW, 0, &info as *const _ as LPARAM);
            let Some((left, top, width, height)) = state.frame(button) else {
                continue;
            };
            let mut text = wide(button.label(state.get(button)));
            info.uFlags = TTF_SUBCLASS;
            info.rect = RECT {
                left: (left * scale).round() as i32,
                top: (top * scale).round() as i32,
                right: ((left + width) * scale).round() as i32,
                bottom: ((top + height) * scale).round() as i32,
            };
            info.lpszText = text.as_mut_ptr();
            SendMessageW(data.tooltip, TTM_ADDTOOLW, 0, &info as *const _ as LPARAM);
        }
    }
}

fn paint_header(window: HWND, header: &Header) {
    // SAFETY: a paint cycle on our own window; the DIB describes `pixels` exactly.
    unsafe {
        let mut paint: PAINTSTRUCT = std::mem::zeroed();
        let dc = BeginPaint(window, &mut paint);
        let mut client: RECT = std::mem::zeroed();
        GetClientRect(window, &mut client);
        let width = (client.right - client.left).max(0) as usize;
        let height = (client.bottom - client.top).max(0) as usize;
        if width > 0 && height > 0 {
            let pixels = header::render(&header.state(), width, height, scale_of(window));
            let mut info: BITMAPINFO = std::mem::zeroed();
            info.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
            info.bmiHeader.biWidth = width as i32;
            // Negative: rows top to bottom, as rendered.
            info.bmiHeader.biHeight = -(height as i32);
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;
            SetDIBitsToDevice(
                dc,
                0,
                0,
                width as u32,
                height as u32,
                0,
                0,
                0,
                height as u32,
                pixels.as_ptr() as *const c_void,
                &info,
                DIB_RGB_COLORS,
            );
        }
        EndPaint(window, &paint);
    }
}

thread_local! {
    static EDITORS: RefCell<Vec<HWND>> = const { RefCell::new(Vec::new()) };
    static HOOK: Cell<HWINEVENTHOOK> = const { Cell::new(std::ptr::null_mut()) };
}

/// Starts following the foreground window, once. The hook's calls arrive through this thread's message pump.
fn watch_foreground() {
    if !HOOK.get().is_null() {
        return;
    }
    // SAFETY: an out-of-context hook on this thread, kept for the life of the process.
    unsafe {
        HOOK.set(SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            std::ptr::null_mut(),
            Some(foreground_changed),
            0,
            0,
            WINEVENT_OUTOFCONTEXT,
        ));
        super::set_bluestone_active(is_bluestone_window(GetForegroundWindow()));
    }
}

fn is_bluestone_window(window: HWND) -> bool {
    let mut pid = 0;
    // SAFETY: plain call; a window that is gone gives no process.
    unsafe { GetWindowThreadProcessId(window, &mut pid) };
    super::is_bluestone_process(pid)
}

unsafe extern "system" fn foreground_changed(
    _hook: HWINEVENTHOOK,
    _event: u32,
    window: HWND,
    _object: i32,
    _child: i32,
    _thread: u32,
    _time: u32,
) {
    if super::set_bluestone_active(is_bluestone_window(window)) {
        EDITORS.with_borrow(|editors| {
            for editor in editors {
                place(*editor, window);
            }
        });
    }
}

/// Floats an editor above other windows while Bluestone is active; otherwise puts it back among normal windows, just
/// below the application that took over.
fn place(editor: HWND, foreground: HWND) {
    let flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
    // SAFETY: z-order changes only; none activates or shows a window.
    unsafe {
        if super::bluestone_active() {
            SetWindowPos(editor, HWND_TOPMOST, 0, 0, 0, 0, flags);
            return;
        }
        SetWindowPos(editor, HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        // Below a topmost window it would become topmost itself.
        if !foreground.is_null()
            && GetWindowLongPtrW(foreground, GWL_EXSTYLE) as u32 & WS_EX_TOPMOST == 0
        {
            SetWindowPos(editor, foreground, 0, 0, 0, 0, flags);
        }
    }
}

/// Bluestone's process id is the parent of its workers.
pub fn parent_process(pid: u32) -> Option<u32> {
    // SAFETY: a process snapshot, walked and closed here.
    unsafe {
        let snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if snapshot == INVALID_HANDLE_VALUE {
            return None;
        }
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut parent = None;
        let mut more = Process32FirstW(snapshot, &mut entry) != 0;
        while more {
            if entry.th32ProcessID == pid {
                parent = Some(entry.th32ParentProcessID);
                break;
            }
            more = Process32NextW(snapshot, &mut entry) != 0;
        }
        CloseHandle(snapshot);
        parent
    }
}

pub struct Window {
    handle: HWND,
    header: HWND,
    area: HWND,
    state: *mut WindowState,
    header_data: *mut HeaderData,
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

fn child(class: &[u16], parent: HWND, style: u32) -> Result<HWND, String> {
    // SAFETY: plain Win32 child window creation on the main thread.
    let handle = unsafe {
        CreateWindowExW(
            0,
            class.as_ptr(),
            std::ptr::null(),
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | style,
            0,
            0,
            1,
            1,
            parent,
            std::ptr::null_mut(),
            GetModuleHandleW(std::ptr::null()),
            std::ptr::null(),
        )
    };
    if handle.is_null() {
        Err("Could not create the editor window.".into())
    } else {
        Ok(handle)
    }
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
        let class = CLASS.get().ok_or("The window class is not registered.")?;
        let header_class = HEADER_CLASS
            .get()
            .ok_or("The window class is not registered.")?;
        let area_class = AREA_CLASS
            .get()
            .ok_or("The window class is not registered.")?;
        let style = if resizable {
            WS_OVERLAPPEDWINDOW
        } else {
            WS_OVERLAPPEDWINDOW & !(WS_THICKFRAME | WS_MAXIMIZEBOX)
        } | WS_CLIPCHILDREN;
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
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                GetModuleHandleW(std::ptr::null()),
                std::ptr::null(),
            );
            if handle.is_null() {
                return Err("Could not create the editor window.".into());
            }
            let parts = child(header_class, handle, 0).and_then(|header_window| {
                child(area_class, handle, WS_CLIPCHILDREN).map(|area| (header_window, area))
            });
            let (header_window, area) = match parts {
                Ok(parts) => parts,
                Err(message) => {
                    DestroyWindow(handle);
                    return Err(message);
                }
            };
            let controls = INITCOMMONCONTROLSEX {
                dwSize: std::mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
                dwICC: ICC_WIN95_CLASSES,
            };
            InitCommonControlsEx(&controls);
            // Without a tooltip window the buttons simply have no tooltips.
            let tooltip = CreateWindowExW(
                WS_EX_TOPMOST,
                TOOLTIPS_CLASSW,
                std::ptr::null(),
                WS_POPUP | TTS_ALWAYSTIP,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                CW_USEDEFAULT,
                header_window,
                std::ptr::null_mut(),
                GetModuleHandleW(std::ptr::null()),
                std::ptr::null(),
            );
            let header_data = Box::into_raw(Box::new(HeaderData { header, tooltip }));
            SetWindowLongPtrW(header_window, GWLP_USERDATA, header_data as isize);
            let scale = scale_of(handle);
            let width = super::area_width(width, scale);
            let state = Box::into_raw(Box::new(WindowState {
                callback,
                last_size: Cell::new((width, height)),
                header: header_window,
                area,
            }));
            SetWindowLongPtrW(handle, GWLP_USERDATA, state as isize);
            let window = Window {
                handle,
                header: header_window,
                area,
                state,
                header_data,
                style: Cell::new(style),
            };
            window.set_content_size(width, height);
            watch_foreground();
            EDITORS.with_borrow_mut(|editors| editors.push(handle));
            place(handle, GetForegroundWindow());
            ShowWindow(handle, SW_SHOW);
            SetForegroundWindow(handle);
            Ok(window)
        }
    }

    pub fn parent_handle(&self) -> *mut c_void {
        self.area as *mut c_void
    }

    pub fn set_content_size(&self, width: u32, height: u32) {
        let scale = scale_of(self.handle);
        let width = super::area_width(width, scale);
        let (outer_width, outer_height) = outer_size(
            width,
            height + super::header_height(scale),
            self.style.get(),
        );
        // SAFETY: our own window and state.
        unsafe {
            (*self.state).last_size.set((width, height));
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
        scale_of(self.handle)
    }

    /// The window handle, which the root of any message for the editor's views resolves to.
    pub fn native_id(&self) -> usize {
        self.handle as usize
    }

    pub fn header_changed(&self) {
        // SAFETY: our own child window and the data stored in it.
        unsafe {
            InvalidateRect(self.header, std::ptr::null(), 0);
            update_tips(self.header, &*self.header_data);
        }
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
        EDITORS.with_borrow_mut(|editors| editors.retain(|h| *h != self.handle));
        // SAFETY: our own windows and the state stored in them; destroying the window destroys its children.
        unsafe {
            SetWindowLongPtrW(self.header, GWLP_USERDATA, 0);
            SetWindowLongPtrW(self.handle, GWLP_USERDATA, 0);
            let data = Box::from_raw(self.header_data);
            if !data.tooltip.is_null() {
                DestroyWindow(data.tooltip);
            }
            DestroyWindow(self.handle);
            drop(Box::from_raw(self.state));
            drop(data);
        }
    }
}
