# 0048. Plugin editor windows: a Bluestone header, and floating above Bluestone

- Status: Accepted; amends 0040
- Date: 2026-11-04

## Context

ADR 0040 added per-editor pinning and keyboard routing, but only as toggles on the device panel. The editor window
itself, a bare top-level window in the worker process, offered nothing: to pin an editor, or bypass its device, the
user had to find the device in the Device View. Because editors live in another process, clicking Bluestone's main
window also put every editor behind it, including the one for the track being worked on.

Reason draws its own row of controls at the top of every plugin window (Keep open, Automate, Remote, Screenshot), and
its plugin windows always stay above the song window. Bitwig has a pin in the corner of its floating device windows.

## Decision

- **A header strip on every editor window Bluestone owns.** The worker draws a strip of 28 logical units at the top of
  the window and attaches the plugin's view to an area below it. The strip has three buttons: **Bypass**, **Pin**, and
  **Keys to Bluestone**. The window is never narrower than its buttons. The strip and its icons (the device panel's)
  are drawn by one platform-neutral renderer in the native host; each platform only blits its pixels and passes
  clicks (macOS: an NSView; Windows: a child window with tooltips; Linux: an X11 child window, without tooltips).
- **The host stays the owner of the state.** `SetEditorHeader` tells a window what each button shows (hidden, off, on,
  or unavailable). A click flips the button at once and arrives as an `EditorButton` notification; the host applies it
  exactly as the device panel would (a bypass is an ordinary, undoable project command; pin and keys are the ADR 0040
  session choices) and sends back the state it settled on, so a refused click is undone in the window. Header updates
  are serialized and coalesced per device, like visibility.
- **Pin keeps its meaning**: the editor stays open when another track is selected. It is hidden when editors do not
  follow the selection. Keys is shown dimmed where routing is not possible.
- **Editors float above Bluestone while Bluestone is active.** An application counts as Bluestone if it is Bluestone's
  process, the worker itself, or another worker Bluestone started (its parent process is Bluestone's; the worker is
  given Bluestone's process id with `--host-pid`). While one is active, editor windows use the floating window level
  (macOS), are topmost (Windows), or ask the window manager for `_NET_WM_STATE_ABOVE` (Linux, best effort). When
  another application becomes active they drop back to the normal level, so they never float over other applications.
  Each worker follows activation itself (NSWorkspace notifications, a foreground WinEvent hook, `_NET_ACTIVE_WINDOW`);
  there is no polling and no cross-process window ownership, which on Windows would join the processes' input queues
  so a hung plugin could freeze Bluestone.
- A floating CLAP editor (the plugin's own window) has no header and does not float; `SetEditorHeader` answers
  `NotSupported` and is not asked again until the editor is reopened.
- Protocol version 5; native plugin host C ABI version 2.

## Consequences

- Bypass, pin, and keyboard routing are reachable where the user is looking, and stay in step with the device panel.
- Editors cover Bluestone's own dialogs while Bluestone is active, as in Reason.
- Every editor window is taller by the header, and very narrow plugins get side padding.
- A plugin popup that is a separate, unowned window of normal level can open behind its floating editor.
- On Linux, floating depends on the window manager honouring EWMH, and the header has no tooltips.
