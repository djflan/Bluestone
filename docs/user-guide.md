# Using the Bluestone alpha

This guide describes the application as it works today. Bluestone is alpha software; see the [README](../README.md) for current limitations and [roadmap](roadmap.md) for planned work.

## Start Bluestone

Released builds, when available, are on the [Releases](https://github.com/djflan/Bluestone/releases) page. They are self-contained and unsigned. On macOS, clear download quarantine once with `xattr -dr com.apple.quarantine <unpacked folder>`; on Windows, choose **More info** and **Run anyway** when SmartScreen warns.

To run from source:

```sh
dotnet run --project src/dotnet/Bluestone.Desktop
```

On macOS, MIDI playback, MIDI input and thru, and audio run in a separate Bluestone engine process so that editing cannot delay them. On Windows and Linux they run inside the application unless you set `BLUESTONE_ENGINE=1`; `BLUESTONE_ENGINE=0` keeps them inside the application on macOS too. See the [README](../README.md) for why Windows and Linux are opt-in.

Without hardware, route tracks to **Bluestone Monitor** and open the MIDI monitor, or route them to **Bluestone Out** on macOS and Linux and select that virtual port in a software instrument. Drag a `.mid` file or a `.bluestone` project onto the window to open it.

## Essential shortcuts

| Action | Shortcut |
| ------ | -------- |
| Play / stop | Space |
| Record or punch / metronome | R / K |
| Return to start / go to end | Home / End |
| Previous / next bar | , / . |
| Loop on / off | L, or double-click the loop |
| Loop selected clips or notes | Ctrl/Cmd + L |
| Nudge the selected loop by the grid / a bar | ← → / Shift + ← → |
| Panic | Ctrl/Cmd + . |
| Select tracks | Click; Shift-click for a range; Ctrl/Cmd-click to toggle; ↑ / ↓, with Shift to extend |
| Rename / mute / solo / delete selected tracks | Return / M / S / Delete |
| Add / duplicate / group tracks | Ctrl/Cmd + T / Ctrl/Cmd + D / Ctrl/Cmd + G |
| Undo / redo | Ctrl/Cmd + Z / Ctrl/Cmd + Shift + Z |
| New / open / save / save as | Ctrl/Cmd + N / O / S / Shift + S |
| Import / export MIDI | Ctrl/Cmd + I / E |
| Zoom and scroll time | Drag in the beat ruler; Ctrl/Cmd + scroll; pinch; Ctrl/Cmd + = / − |
| Zoom to project / selection / loop | Ctrl/Cmd + 0 / Shift + Ctrl/Cmd + 0 / Alt + Ctrl/Cmd + 0 |
| Inspector / Browser / Device View / piano roll / event list | I / Shift + B / E / P / D |
| Settings / return to workspace | Ctrl/Cmd + , / Escape |
| Computer keyboard input | ` (backquote) |
| Shortcut reference | Ctrl/Cmd + / |

## Arrange and edit notes

Double-click a note clip or press P to open it in the piano roll. The piano roll edits one clip at a time and darkens time outside that clip.

### Arrangement

| Action | How |
| ------ | --- |
| Select clips | Click; Shift-click to add; Ctrl/Cmd-click to toggle |
| Move / copy | Drag; Alt/Option-drag copies; clips can move to another compatible track |
| Trim or extend | Drag a clip's left or right edge; trimmed notes are retained |
| Loop a clip | Drag the lower half of its right edge; drag the dashed line to change loop length |
| Create a clip | Double-click empty space, or add notes in the piano roll on a track without clips |
| Rename | Double-click the name strip, or select and press Return |
| Split / duplicate / delete | B / Ctrl/Cmd + D / Delete while the arrangement is focused |
| Automation lanes | Use + on a track; the arrow shows or hides its lanes |
| Automation points | Click to add, drag to move, Alt/Option-click to delete, double-click for hold/ramp |

Draw, move, or resize the arranger loop in the ruler's top strip. Ctrl/Cmd temporarily ignores the grid. Disabling looping keeps its range for later.

### Piano roll

| Action | How |
| ------ | --- |
| Arrow / pencil / eraser | 1 / 2 / 3 |
| Select | Click, Shift-click, marquee, or Ctrl/Cmd + A; Escape deselects |
| Add | Double-click, or drag with the pencil to set length |
| Move / copy / resize | Drag; Alt/Option-drag copies; drag an edge to resize; Ctrl/Cmd ignores the grid |
| Nudge / transpose | ← → by grid; ↑ ↓ by semitone; Shift increases the step |
| Velocity | Drag velocity stems, draw a ramp, or use Alt/Option + ↑ / ↓ |
| Controllers | Choose a lane and draw with the pencil or erase with the eraser |
| Quantize | Q; Shift + Q includes note ends |
| Copy / cut / paste / duplicate / delete | Ctrl/Cmd + C / X / V / D; Delete |
| Zoom time / key height | Ctrl/Cmd + scroll or drag in the ruler / Alt/Option + scroll |
| Exact values | Use the position, length, pitch, velocity, and channel fields above the notes |

The event list (D) shows every event on the track with editable position, channel, data, and length. It shares selection with the piano roll. Each edit is one undo step.

## Record MIDI

Arm a track with its red button, or record into the selected track, then press R. From a stop, Bluestone counts in and begins recording. During playback, R punches in and out. Space stops and adds the take as one undo step.

Takes can merge with or replace existing material from **Recording ▸ Takes** in the inspector. With looping enabled, every pass is merged. MIDI thru plays the armed or selected track's output. External controllers can record on macOS (Core MIDI) and Windows (WinMM); MIDI input on Linux is not implemented yet. The computer keyboard works on every platform.

### Computer keyboard

Turn on computer-keyboard input with backquote or the transport button. The middle row plays white keys (`A S D F G H J K L ; '`), and the row above plays black keys (`W E T Y U O P`). Z/X change octave and C/V change velocity.

The keyboard records, plays through devices, and appears in the MIDI monitor like another input. A note always releases on the channel and route where it began, even if selection changes while it is held.

## Tracks and the inspector

**New Track** creates a hybrid track whose audio already goes to the master. Add an instrument and play; no track type or route is required first. The track header names what plays it and warns about real problems such as notes with no instrument or a crashed plugin.

The inspector (I) edits the selection. With several tracks selected, compatible changes apply to all of them in one undo step and differing values show **Mixed**.

- **Track** shows the instrument, device count, audio destination, and a type only where it matters.
- **External MIDI** holds the current alpha's hardware output, profile, channel, transpose, program, and initialization workflow.
- **Connections** exposes the underlying event and audio routes for advanced work.
- **Racks** manages device chains that several tracks can share.
- **Mixer** manages mixer channels and master gain.
- **Recording** selects MIDI input, take mode, count-in, metronome output, and MIDI thru.

Group tracks show their members beneath them. Use the group's arrow to fold or unfold them.

## Device View and Browser

Open the Device View with E. A chain runs left to right between its input and output. Device panels provide parameters, bypass, plugin-editor and restart controls, and collapse buttons. Drag a panel by its name to reorder it; long chains scroll horizontally.

An unlocked Device View follows the selected track. Lock it to keep its current track or rack visible. Use + beside the detail tabs for another Device View. Each view can be docked, collapsed to a border-rail icon, or detached. Several views of one chain share project device state but keep their own selection, collapse, and scroll state.

Open the Browser with Shift + B. Search or filter devices, plugins, and project racks. Drag a device to an exact gap in the Device View, or double-click/press Return to insert it into the most recently active Device View. Drop a Browser item over a device panel to replace that device in one undo step.

Drag a device from the Browser into empty arrangement space to create a track named after it, with that device already in its chain and audio routed to the master. Drop it on an existing track's header or arrangement lane (including its automation lanes) to replace **the whole device chain**, not its clips, automation, name, or track routing. The drop preview names the target and action. Undo restores the previous chain and routing in one step; a plugin that fails to start leaves the project unchanged. To add a device without replacing the chain, drop it into a gap in the Device View instead.

The current alpha exposes hardware profiles under **Hardware** with **Use on Track**, then edits transport details under **External MIDI**. The product roadmap replaces this split workflow with a unified hardware device; that future behavior is not implemented yet.

Workspace layout, open views, detached windows, locks, collapsed panels, and Browser state are stored locally per project and as defaults for new projects. They do not mark the musical project as edited.

## Audio and plugins

Open Settings with Ctrl/Cmd + ,. In **Audio**, choose the output device, sample rate, buffer, and render-ahead sizes, then start audio. **Test tone** verifies the output. Bluestone remembers the settings and whether audio should start at launch.

Audio input and recording are not implemented in the current alpha. Audio clips already present in a project can play, loop, stream from disk, use gain and fades, and route through the mixer and effects.

**Window ▸ Mixer** on macOS, or **File ▸ Mixer…** on Windows and Linux, opens the mixer console, a separate window with a strip, sends, and meters for each channel.

Scanned VST 3 and CLAP plugins appear in the Browser. The system and per-user scan folders are listed in [plugin-hosting.md](plugin-hosting.md#the-plugin-library-and-the-plugins-view). **Settings ▸ Plugins** rescans, reports modules that failed, retries quarantined modules, chooses how plugins share worker processes, and enables automatic restart.

A plugin editor opens in a separate worker-owned window. Unpinned editors follow the selected track by hiding and showing without restarting; pinning keeps one visible. On macOS and Windows, the keyboard button can forward its keys to Bluestone. The strip across the top of the editor window has the same controls as the device panel: **Bypass**, **Pin**, and **Keys to Bluestone**. Editor windows stay above Bluestone's window while you work in Bluestone, and drop behind when you switch to another application. If a worker crashes, affected devices report it and can restart from their saved state while the main application remains open.

Plugins run with the user's full permissions. Worker processes isolate crashes, not file or network access; see [SECURITY.md](../SECURITY.md).

## Status line and messages

The status line at the bottom of the window says what Bluestone is doing: opening or saving a project, importing or exporting, scanning plugins, opening the audio device, or waiting for a lost one to return. A moving bar beside it means the work is still running; when several things run at once it shows the newest and how many more. When nothing is running it shows the latest message. Click it to open **Messages**, which records how each activity ended, how long it took, and any warnings or errors.

## Menus and accessibility

macOS uses the standard application menu bar. Windows and Linux show the same menus in a menu bar at the top of the window (with Settings and Exit in File); Alt plus the underlined letter opens a menu.

Primary controls have screen-reader names, and statuses use words and icons rather than color alone. Screen readers announce changes to the status line politely, at most a few times a second. Custom-drawn arrangement and editor surfaces describe their gestures as a whole rather than exposing each event as a separate accessibility element. Platform accessibility verification is ongoing.

## Hardware checks

The [manual hardware test plan](hardware-test-plan.md) records workflows that require a real instrument, interface, plugin editor, speakers, or a person observing behavior. Automated coverage must not be presented as physical verification.
