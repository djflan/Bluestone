# Manual hardware test plan

The automated suite covers everything that can be checked without instruments: virtual CoreMIDI
ports, the built-in monitor, and (when enabled) the IAC bus. This checklist covers what needs a
real instrument. The first target is a Yamaha QY100 or another XG module on a USB-MIDI interface.
Record the date, macOS version, interface, and results for each run.

> Yamaha, XG, and QY100 are trademarks of Yamaha Corporation; Bluestone is not affiliated with Yamaha.

## Setup

1. Connect the interface's MIDI OUT to the instrument's MIDI IN (and MIDI OUT to MIDI IN for input tests).
2. Confirm the interface appears in **Audio MIDI Setup → MIDI Studio**.
3. Start Bluestone: `dotnet run --project src/dotnet/Bluestone.Desktop`.

## Checks

| # | Step | Expected |
| - | ---- | -------- |
| 1 | Open the inspector's **Output** list | The interface port is listed as *Hardware* with its name; Bluestone Monitor and Bluestone Out are also listed |
| 2 | Import `samples/bluestone-demo.mid`, route all tracks to the interface (**Use this output for all tracks**) | All tracks show **Ready** |
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

## Timing measurement (optional)

Loop the demo for one minute and note the status bar's *p95* and *late* values. For a deeper check,
connect the interface's OUT to its IN and compare send and receive timestamps with a MIDI monitor.
Report results in an issue so they can inform the scheduler benchmark ADR.
