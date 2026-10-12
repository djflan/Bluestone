# Manual hardware test plan

The automated suite covers everything that can be checked without instruments, speakers, or a
person watching: virtual CoreMIDI ports, the built-in monitor, (when enabled) the IAC bus, audio through
a test backend, and plugins through Bluestone's test plugins. This checklist covers what needs real
hardware or a person. The MIDI target is a Yamaha QY100 or another XG module on a USB-MIDI interface.
Record the date, operating system and version, interface, audio device, plugins, and results for each run.

> Yamaha, XG, and QY100 are trademarks of Yamaha Corporation; Bluestone is not affiliated with Yamaha.

## Setup

1. Connect the interface's MIDI OUT to the instrument's MIDI IN (and MIDI OUT to MIDI IN for input tests).
2. Confirm the interface appears in **Audio MIDI Setup → MIDI Studio** (macOS) or the system's device list.
3. Start Bluestone: `dotnet run --project src/dotnet/Bluestone.Desktop` (needs the .NET SDK and a Rust toolchain).

## Checks

| # | Step | Expected |
| - | ---- | -------- |
| 1 | Open the inspector's **External MIDI ▸ Output** list | The interface port is listed as *Hardware* with its name; Bluestone Monitor and Bluestone Out are also listed |
| 2 | Import `samples/bluestone-demo.mid`, route all tracks to the interface (**Use for all tracks**) | All tracks show **Ready** |
| 3 | Press Space | The instrument plays drums, bass, keys, and lead in time; the status bar shows 0 late, 0 dropped |
| 4 | Select *Bass*, choose profile **Generic XG**, voice bank *Normal voices (MSB 0)*, any program; play from the start | The bass voice changes at the start of playback |
| 5 | Select a track with an XG profile and press **Initialize Instrument…** | A confirmation names *XG System On*; **Cancel** sends nothing; **Send** resets the instrument (check its display) |
| 6 | While playing, press Ctrl/Cmd + . (Panic) | All sound stops immediately; playback continues |
| 7 | Hold a long note (loop four bars with L, play), then press Stop | No hanging notes |
| 8 | Unplug the interface during playback | The track shows **Offline** with "not connected"; nothing crashes |
| 9 | Plug it back in | The track returns to **Ready**; playback resumes sending to it |
| 10 | Save, quit, unplug, reopen the project | The route is kept and explained as disconnected; reconnecting restores it |
| 11 | Edit while playing (mute a track, change transpose) | Changes are heard within a beat; no stuck notes |

## Recording and thru

These need an instrument whose MIDI OUT is connected to the interface's MIDI IN. Set the inspector's
**Recording ▸ Input** to the interface (or leave *All MIDI Inputs*).

| # | Step | Expected |
| - | ---- | -------- |
| 12 | Select a track routed to the instrument and play its keys, with **MIDI thru** on | You hear what you play through the track's output, on the track's channel; the LCD's **IN** light blinks |
| 13 | Turn **MIDI thru** off and play | Nothing is echoed; the IN light still blinks |
| 14 | Arm the track (red button), press R | One bar of count-in clicks (channel 10 wood blocks), then recording; the playhead turns red |
| 15 | Play a phrase, press Space | The take appears in the track and the piano roll; one **Undo** removes it all |
| 16 | Play the take back | Notes sound where they were played (compare against the click); note the apparent latency in milliseconds |
| 17 | Set a two-bar cycle (drag in the ruler's top strip), record over several passes | Every pass is merged into the track; a note held across the wrap ends at the cycle's end |
| 18 | Choose **Takes ▸ Replace**, record over part of the phrase | Only the notes that start inside the recorded range are replaced |
| 19 | While playing, press R, play, press R again | Recording punches in and out; playback continues |
| 20 | Hold a key, change the selected track, release the key | The note releases on the instrument it started on; nothing hangs |
| 21 | Move the sustain pedal and pitch wheel while recording | Sustain (CC 64) and pitch bend appear in the event list and the controller lanes |
| 22 | Open a MIDI file with program changes, select a track, and with thru on play the computer keyboard (`) without pressing play | You hear the track's instrument on its channel; the readout's **CH** matches the track |
| 23 | Hold a computer-keyboard key, select a track on another channel, release | The note releases; nothing hangs |

## Audio output

Run these on each operating system: macOS (CoreAudio), Windows (WASAPI), and Linux (ALSA). Windows
and Linux output is built and tested in CI but has not been heard. Audio settings are in
**Settings ▸ Audio** (⌘/Ctrl + ,; also **Window ▸ Audio Settings…** on macOS and **File ▸ Audio Settings…** on Windows and Linux).

| # | Step | Expected |
| - | ---- | -------- |
| 24 | In Audio Settings choose the device, press **Start audio**, then **Test tone** | A clean tone; the status shows the device, rate, buffer, and the render thread priority granted |
| 25 | Open a project with WAVE and AIFF audio clips (add them by hand as in [project-format.md](project-format.md); there is no audio import yet) with gain and fades, and play | Clips play in time with MIDI; fades and gain are audible; no clicks at clip edges or loop wraps |
| 26 | Unplug the chosen audio device (USB or Bluetooth) while playing | Audio moves to the default output with a message; nothing crashes |
| 27 | Plug it back in | Audio returns to the chosen device |
| 28 | Unplug the only output device, then plug it back in | Audio waits for a device, then opens it again without a restart |
| 29 | With a plugin instrument playing, switch to a device running at another rate (44.1 kHz versus 48 kHz) | The plugin is silent briefly, then plays with the same settings; an open editor closes |

## Plugins

Install at least one VST3 and one CLAP instrument and effect in the default plugin folders (listed in
**Plugins…**). The worker process is `Bluestone.PluginWorker`.

| # | Step | Expected |
| - | ---- | -------- |
| 30 | Open **Plugins…** (Window menu on macOS, File menu elsewhere) | Installed plugins are listed with their kind, vendor, version, and module; modules that could not be read say why |
| 31 | On a track, choose a VST3 instrument in **Add a device…**, press **Add**, play notes; repeat with CLAP | The instrument sounds through the master; the meters move |
| 32 | Add a plugin audio effect after the instrument and turn one of its parameters | The effect is heard; the change is heard within a block |
| 33 | Press **Open editor**; edit a control, resize the window, close it, open it again | The window appears at the plugin's size; edits change the sound; resizing and reopening work. Check Windows and Linux in particular: editors there have never been opened |
| 34 | While playing, end the worker process (Activity Monitor, Task Manager, or `kill`) | Bluestone keeps running; the device shows **Crashed** and names the devices that went down with it; plugins in other workers keep playing |
| 35 | Press **Restart** | The plugin plays again with its last saved settings |
| 36 | Save, quit, reopen the project | Plugin settings are restored |
| 37 | In **Plugins…**, change the hosting mode (for example from By plugin to Individually) while playing | Plugins move to their new workers and keep their settings |

## Hardware parameter automation

Route a track to the XG instrument with the **Generic XG** profile.

| # | Step | Expected |
| - | ---- | -------- |
| 38 | Add automation lanes for Filter Cutoff (NRPN), Pitch Bend Range (RPN), Part Volume (SysEx), and Part Detune (SysEx in nibbles); draw ramps; play | Each parameter audibly follows its lane; the MIDI monitor shows the NRPN, RPN, and SysEx messages |
| 39 | Stop in the middle of a ramp, seek, and loop across it | After each jump the instrument is at the lane's value; nothing is left at a stray value |
| 40 | Export a MIDI file and play it from another sequencer to the same instrument | The same parameter changes are heard |

## Live input latency

| # | Step | Expected |
| - | ---- | -------- |
| 41 | Play a plugin instrument from a MIDI keyboard with **MIDI thru** on, with audio running | Notes sound without hanging; Audio Settings shows "Live notes heard after … ms": record it per operating system, device, and buffer size |
| 42 | Put a plugin effect that reports latency (a look-ahead limiter) on another track, then play live with **Low-latency monitoring** off and on | Off: live notes are later by about the effect's latency; on: they are as quick as before the effect was added |
| 43 | Put an Arpeggiator before the plugin instrument and hold a chord | The arpeggio follows the tempo; releasing the keys stops it |

## Timing measurement (optional)

Loop the demo for one minute and note the status bar's *p95* and *late* values. For a deeper check,
connect the interface's OUT to its IN and compare send and receive timestamps with a MIDI monitor.
Report results in an issue so they can inform the scheduler benchmark ADR.
