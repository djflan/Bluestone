# 0040. Floating plugin editors: follow the selection, pin, and send keys to Bluestone

- Status: Accepted; amends 0025 and 0033
- Date: 2026-10-12

## Context

Plugin editors are top-level windows owned by worker processes (ADR 0025, 0033). Every open editor stayed on screen
until closed, however far the selection moved, and an editor that had focus took every key, so Bluestone's shortcuts
and computer-keyboard MIDI stopped working while the mouse was in a plugin. Workflow references (Bitwig Studio among
them) let floating plugin windows follow the selected track, pin individual windows, and let the host keep the keyboard.
Editors must stay out of Bluestone's process.

## Decision

- **Visibility is native, per window.** `SetEditorVisible` hides or shows the worker's window without destroying the
  view, so the plugin instance, its editor, and its state are untouched. Shown windows do not take focus; a window whose
  monitor is gone is moved back on screen (macOS, Windows).
- **Follow, pin, keyboard: workspace state.** `PluginEditorWindows` (Presentation, behind `IPluginEditorHost`) keeps
  per-device pin and keyboard choices for the session; nothing is saved in the project or in plugin state. "Floating
  plugin windows follow the selected track" is a plugin setting (on by default). Requests are serialized and coalesced
  per device.
- **Keys are taken in the worker's event pump.** The worker already pumps its own events on macOS and Windows; key events
  for a window whose keys go to the host are queued there and never delivered to the plugin, then sent to the host as
  `EditorKey` notifications using USB HID usage IDs (platform-neutral) with a small modifier mask. The host raises them
  as ordinary key events on its main window. Held forwarded keys are released on every path that could strand them.
- **Honest capability boundary.** Linux (X11) cannot reliably intercept a plugin view's input, so keyboard routing is
  reported unsupported (`SetEditorKeyboard` answers `NotSupported`; the toggle is disabled with an explanation). A
  floating CLAP editor (the plugin's own window) can be hidden through the plugin but cannot route keys.
- Protocol version 4.

## Consequences

- Rapid track switching with many editors is cheap (hide/show only) and never restarts plugins.
- Keyboard routing depends on the worker seeing the events first; a plugin that runs its own modal event loop could
  bypass it while that loop runs.
- Editor geometry is not remembered across sessions, and editors are not embedded in Bluestone's window (out of scope).
