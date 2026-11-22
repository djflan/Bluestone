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

## The window

The toolbar at the top shows or hides the inspector, Browser, Mixer, and editors, and holds undo, redo, Settings, and Panic. The transport bar runs along the bottom, above the status line: the loop start and length, the position and elapsed time, the transport buttons (back and forward one bar, go to start, stop, play, record, loop; Play, Loop, and the other toggles glow aqua while on, and Record is always a red dot), tempo and time signature, the metronome and **Pre** (count-in), the MIDI input light (**IN**), and the computer-keyboard button with its channel, octave, velocity, and last note. Each track header has square **M**, **S**, and record-arm keys.

Open **Settings** from the toolbar, the menu, or Ctrl/Cmd + ,. **Window ▸ Audio Settings…** and **Window ▸ Plugins…** open their pages directly.

Settings opens in a compact popover on the right, over the workspace, which stays visible ([ADR 0051](adr/0051-settings-popover.md)). **Done**, Escape, clicking outside it, or tabbing out of it closes it; Escape closes an open list or color picker first. Shortcuts that edit the project, such as Ctrl/Cmd + T, close Settings before they act; single-key shortcuts such as I wait until Settings is closed. Opening or closing Settings alone doesn't change your project or workspace.

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
| Open / close Settings | Ctrl/Cmd + , / Escape (when no picker is open) |
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

Arm a track with its record key, or record into the selected track, then press R. From a stop, Bluestone begins recording, after a count-in when **Pre** is lit in the transport bar. During playback, R punches in and out. Space stops and adds the take as one undo step.

Takes can merge with or replace existing material from **Recording ▸ Takes** in the inspector. With looping enabled, every pass is merged. MIDI thru plays the armed or selected track's output. External controllers can record on macOS (Core MIDI) and Windows (WinMM); MIDI input on Linux is not implemented yet. The computer keyboard works on every platform.

### Computer keyboard

Turn on computer-keyboard input with backquote or the transport button. The middle row plays white keys (`A S D F G H J K L ; '`), and the row above plays black keys (`W E T Y U O P`). Z/X change octave and C/V change velocity.

The keyboard records, plays through devices, and appears in the MIDI monitor like another input. A note always releases on the channel and route where it began, even if selection changes while it is held.

## Tracks and the inspector

**New Track** creates a hybrid track whose audio already goes to the master. Add an instrument and play; no track type or route is required first. The track header names what plays it and warns about real problems such as notes with no instrument or a crashed plugin.

The inspector (I) edits the selection. With several tracks selected, compatible changes apply to all of them in one undo step and differing values show **Mixed**.

Selecting a mixer track also selects that track in the arranger and Inspector. The **Master** is independently selectable: selecting it leaves no arrangement track selected and shows the Master gain, pan, mute, solo, and chain in the Inspector. Selecting a track again restores that track's Inspector scope.

- **Track** shows the instrument, device count, audio destination, and send count; sends are managed in Mixer. A type appears only where it matters.
- **External MIDI** holds the current alpha's hardware output, profile, channel, transpose, program, and initialization workflow.
- **Connections** exposes the underlying event and audio routes for advanced work. A route can start at the track's chain input (before its devices), after any device, or at its output.
- **Racks** manages device chains that several tracks can share.
- **Recording** selects MIDI input, take mode, count-in, metronome output, and MIDI thru.

Group tracks show their members beneath them. Use the group's arrow to fold or unfold them.

## Device View and Browser

Open the Device View with E. A chain runs left to right between its input and output. Device panels provide parameters, bypass, plugin-editor and restart controls, and collapse buttons. Drag a panel by its name to reorder it; long chains scroll horizontally.

Each device is a card; the selected one is lit. Between devices, each point of the chain shows what is there: a MIDI icon at the top that glows while notes are held, and under it a stereo LED ladder of the audio's level: by default the average level with the peak held above it, or the peak alone (see [Display settings](#display-settings)). Hover a point to read it in words. Hovering or focusing it shows a **+** that opens the Browser aimed at that gap, so the next device you insert lands there (Return or Space on a focused point does the same; click it again to cancel); right-click it for **Insert Device Here**, **Route from Here…**, and **Clear Clip**. A point that also sends its signal somewhere shows a small aqua tap; click it to see the connections. A ladder that passed 0 dBFS lights its clip lamp, and the point's description says the clip is latched, until you clear it: click the lamp, press Delete or Backspace on the focused point, or choose **Clear Clip** from its context menu (available only while a clip is latched). The **Out** card at the end of the chain shows what leaves it: a wider stereo meter with a dB scale and a readout of the held peak.

A **Note Receiver** or **Audio Receiver** brings signal from anywhere in the project into the chain at its place: click its source to choose a track or rack and the point to take (its input, after any of its devices, or its output), then choose **Mix** to add it to the chain's signal or **Replace** to use it instead. An effect plugin with a sidechain input shows an **SC** chip in its header; click it to choose what keys it. The source list only offers points that can be connected, and choosing one is a single undo step.

An unlocked Device View follows the selected track. Lock it to keep its current track or rack visible. Use + beside the detail tabs for another Device View. Each view can be docked, collapsed to a border-rail icon, or detached. Several views of one chain share project device state but keep their own selection, collapse, and scroll state.

Open the Browser with Shift + B. Search or filter devices, plugins, and project racks. Drag a device to an exact gap in the Device View, or double-click/press Return to insert it into the most recently active Device View. Drop a Browser item over a device panel to replace that device in one undo step.

Drag a device from the Browser into empty arrangement space to create a track named after it, with that device already in its chain and audio routed to the master. Drop it on an existing track's header or arrangement lane (including its automation lanes) to replace **the whole device chain**, not its clips, automation, name, or track routing. The drop preview names the target and action in the track's visible area, including tall expanded automation. Undo restores the previous chain and routing in one step; a plugin that fails to start leaves the project unchanged. To add a device without replacing the chain, drop it into a gap in the Device View instead.

Whole-chain replacement captures the outgoing plugins' live settings for Undo, including opaque configuration not exposed as parameters and the last recoverable snapshot of a plugin that has crashed. A pending drop is refused if its plugin stops running or disappears in a rescan, its target changes, or a document is opened/recovered again, even if it is the same saved project. A refused drop leaves surviving plugins' live settings intact.

The current alpha exposes hardware profiles under **Hardware** with **Use on Track**, then edits transport details under **External MIDI**. The product roadmap replaces this split workflow with a unified hardware device; that future behavior is not implemented yet.

Workspace layout, open views, detached windows, locks, collapsed panels, and Browser state are stored locally per project and as defaults for new projects. They do not mark the musical project as edited.

## Audio and plugins

Open Settings with Ctrl/Cmd + ,. In **Audio**, choose the output device, sample rate, buffer, and render-ahead sizes, then start audio. **Test tone** verifies the output. Bluestone remembers the settings and whether audio should start at launch.

Audio input and recording are not implemented in the current alpha. Audio clips already present in a project can play, loop, stream from disk, use gain and fades, and route through the mixer and effects.

Open **Mixer** from the workspace toolbar or menu to show the track-owned mixer console. It can stay docked in the main window, collapse to a workspace-rail icon, or detach to its own window. Each arrangement track has a strip identified by its track colour, number, name, and role. From top to bottom, an audio-capable strip shows its inserts and sends, a pan knob, square mute, solo, and record-arm keys, the gain (dB) and peak (dBFS) readouts, then the fader with a column of gain LEDs beside it and the level meter, and at the bottom a name plate with its output. MIDI-only hardware tracks show their MIDI destination and routing status without an audio fader or meter. The mandatory **Master** strip is pinned at the right and has gain, pan, mute/solo, meter, and optional device-chain controls. Track fader and pan automation, and project-level Master fader/pan/device automation, are saved with the project. Master-device automation is retained when a device is unavailable.

The insert well at the top of a strip lists its devices in order: up to three show, a free row's **+** adds a device, and **+N more** opens the rest. Each row's power glyph bypasses that device (Space on a focused device name does the same), and clicking the name opens the device. Under the well, a strip with sends shows how many; click it to edit them. A strip with none shows **+ Send** only while you hover over or focus it. Record arm is a view state only; it is not saved or undoable. On a short mixer the inserts collapse to a one-line summary. Clicking a Master insert selects the Master; there is no Master chain view in the mixer yet.

The pan knob reads, for example, **L41**, **C**, or **R35**. The fader shares the meter's dB scale from −60 to +6 dB, so unity sits level with 0 on the scale; the arrow and Page Up keys still reach +24 dB. The gain LEDs beside it light up to the gain. On a focused fader or pan knob, Up/Down adjusts (Shift for fine steps), PageUp/PageDown takes larger steps, Home goes to the minimum, End goes to +6 dB (hard right on pan), and Delete, Backspace, or a double-click resets to the default. A reset is its own undo step. The mouse wheel adjusts the control under the pointer; Shift+wheel or a horizontal wheel scrolls the strips instead.

Each strip has one LED-ladder meter. The clip lamp on top latches when the signal goes over 0 dBFS; while it is lit, the meter becomes a **Clipped – click to reset** button.

An ungrouped track's **Output** goes to Master; a grouped track's Output goes to its parent Group. **Send** routes copy audio to Effect tracks, before or after the source fader and pan. Effect tracks sum incoming sends at their chain input, process them through the chain once, then route through their strip to Master or a parent Group. Groups sum their direct members and process the sum through the Group chain and strip. Advanced **Internal** routes are available in Connections for device taps, receivers, and sidechains; they do not replace the ordinary Output or Send routes. Master devices run in chain order before the Master fader and pan.

Removing a track from a Group or deleting its Group restores the track's parked routes where they still validate. Routes that cannot be restored remain inactive and produce a warning; they are not silently retargeted. Changing a MIDI-only track to an audio-capable type adds its required active Output while preserving existing taps and parked routes.

The mixer meter selector offers **Peak + RMS** (default: post-fader peak hold falling at 20 dB/s plus RMS with a 300 ms time constant), **VU** (average-rectified level with 300 ms ballistics), **K-20**, **K-14**, **K-12**, and **LUFS-S**. VU's application-local reference ranges from −36 to 0 dBFS (default −18 dBFS); it is not an acoustic SPL calibration. K-System 0 references are −20, −14, and −12 dBFS, respectively, and are not room calibrations. LUFS-S is ungated short-term loudness with standard stereo channel weighting over a trailing 3 seconds of K-weighted audio. It shows a warming-up/not-ready state until three seconds of valid samples are available and after a discontinuity. The mode changes the meter's scale and ballistics, not how many meters a strip has. The meter has a column for each channel; in **Peak + RMS** a column's fill is that channel's RMS and the lightened cell above it is its held peak. Meter mode and VU reference are application-local preferences, not project data, and do not change the Device View's meters.

The strip fader remains authoritative for track gain. A hand-off from the strip fader to bx_console AMEK 9099 remains deferred: no verified VST 3 class ID, parameter ID, parameter range, or normalized-to-dB conversion has been established. No vendor-specific mapping is supplied or implied, and naming the plugin does not imply vendor endorsement.

Scanned VST 3 and CLAP plugins appear in the Browser. The system and per-user scan folders are listed in [plugin-hosting.md](plugin-hosting.md#the-plugin-library-and-the-plugins-view). **Settings ▸ Plugins** rescans, reports modules that failed, retries quarantined modules, chooses how plugins share worker processes, and enables automatic restart.

A plugin editor opens in a separate worker-owned window. Unpinned editors follow the selected track by hiding and showing without restarting; pinning keeps one visible. On macOS and Windows, the keyboard button can forward its keys to Bluestone. The strip across the top of the editor window has the same controls as the device panel: **Bypass**, **Pin**, and **Keys to Bluestone**. Editor windows stay above Bluestone's window while you work in Bluestone, and drop behind when you switch to another application. If a worker crashes, affected devices report it and can restart from their saved state while the main application remains open.

Plugins run with the user's full permissions. Worker processes isolate crashes, not file or network access; see [SECURITY.md](../SECURITY.md).

## Display settings

Under **Settings ▸ Display ▸ INDICATOR COLORS**, choose one of three independent color gradients: **Mixer meters**, **Device chain** (the meters between devices and on the Out card), and **Gain LEDs** beside the faders. Select an indicator tile or use the **Mixer / Chain / Gain** selector to edit that family's gradient, from the bottom of its scale to the top. Select a stop by its handle, then open its color chip to open the **Color picker**. It has a continuous hue/saturation wheel, a brightness fader, **NEW / WAS** color previews, a **Color (hex)** field, and solid preset color tiles. Hue runs around the wheel and saturation runs from the center to the rim; the brightness fader darkens the wheel.

Picker changes preview live while preference saves are held. **Apply** keeps the color; **Cancel**, Escape, or clicking outside the picker restores the original without saving the preview color. Leaving the page, selecting another indicator, or closing Settings also cancels. An incomplete or invalid hex value does not change the preview and disables Apply until six valid hex digits are entered.

Drag a handle, or focus it and press Left or Right (1%, or 5% with Shift), to change where its color begins; two stops at the same place make a hard edge. **Add stop** adds one in the widest gap (up to eight), **Remove stop** removes the selected one (two always remain), and **Reset to default** resets only that gradient: aqua-to-violet for mixer and chain meters, violet-to-rose for gain LEDs. **Device chain** also offers **MIDI light**, a single color whose chip opens the same picker; it is not a gradient. The selected stop and MIDI light also have hex fields and swatches on the page for direct edits. Clip lamps stay fixed red, outside the gradients. An existing saved signal gradient is copied into both mixer and chain settings on upgrade; gain LEDs start with their own default.

The **LED fade** and **LED warm-up** check boxes are both on by default and can be cleared independently. Warm-up softly lights meter cells and gain LEDs over about 20 ms; held peaks, clip lamps, MIDI indicators, and lit keys light immediately. Fade softens the return to dark: about 90 ms for meter cells and held peaks; 140 ms for gain LEDs, clip lamps, the Device chain MIDI light, and the transport's **IN** and computer-keyboard note lights; and 60 ms for lit keys, including the transport buttons. This is display motion only: it does not change the measured levels, peak hold, clip latch, or audio. It sits on top of the meters' existing fall.

Both effects behave as off when the operating system requests reduced motion: macOS uses Reduce motion, and Windows uses its client-area animation preference, read at startup and when the main window becomes active. After changing that system preference, return to Bluestone to refresh it. On Linux the two check boxes decide alone. If Bluestone can't read the system preference, it reduces motion. Under **Device View**, **Levels show** chooses whether the meters between devices and on the Out card follow the average level with the peak held above it (the default) or the peak alone. These settings are saved on this computer, not in the project. If they can't be saved (for example, a read-only settings folder), they still apply until you quit, and a warning appears in Messages.

## Status line and messages

The status line at the bottom of the window says what Bluestone is doing: opening or saving a project, importing or exporting, scanning plugins, opening the audio device, or waiting for a lost one to return. A moving bar beside it means the work is still running; when several things run at once it shows the newest and how many more. When nothing is running it shows the latest message. Click it to open **Messages**, which records how each activity ended, how long it took, and any warnings or errors.

## Menus and accessibility

macOS uses the standard application menu bar. Windows and Linux show the same menus in a menu bar at the top of the window (with Settings and Exit in File); Alt plus the underlined letter opens a menu.

Primary controls have screen-reader names, and statuses use words and icons rather than color alone. Screen readers announce changes to the status line politely, at most a few times a second. Custom-drawn arrangement and editor surfaces describe their gestures as a whole rather than exposing each event as a separate accessibility element. Platform accessibility verification is ongoing.

## Hardware checks

The [manual hardware test plan](hardware-test-plan.md) records workflows that require a real instrument, interface, plugin editor, speakers, or a person observing behavior. Automated coverage must not be presented as physical verification.
