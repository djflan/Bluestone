# DAW-first polish milestone: worklog and decision record

This is the durable record of the `feature/daw-first-polish` milestone: where it started, what was planned, every
product, architecture, and visual decision worth examining later, progress checkpoints with the independent review,
and the final assessment. Screenshots are in [../images/daw-first-polish/](../images/daw-first-polish/).

## Working arrangement

- **Implementation owner**: the lead agent. Edits code, tests, documentation, and screenshots on the feature branch,
  commits stable checkpoints.
- **Independent reviewer**: a separate, read-only agent context. It never edits files. It assesses product vision,
  visual direction, workflow, accessibility, architecture, and tests, and classifies findings as *Required now*,
  *Important follow-up*, *Optional refinement*, or *Rejected scope*. The implementation owner responds to each finding
  here; it does not grade its own work.
- **Reliability investigation**: the macOS `CoreMidiProviderTests` failure is investigated in a third, separate context
  confined to the CoreMIDI adapter, playback, and integration tests, and lands (if at all) in its own commit.

## Starting state

- **Branch**: `feature/daw-first-polish`, created from `main` at `dc61bda` ("Merge pull request #31 from
  djflan/chore/split-gitignore").
- **Working tree**: clean (only ignored `bin/`, `obj/`, `TestResults/`).
- **Toolchain**: .NET SDK 10.0.300, cargo 1.99.0, macOS (Apple silicon).
- **Relevant decisions**: ADR 0020 (hybrid tracks), 0021 (track roles), 0022 (device chains), 0023 (signal routing
  model), 0030 (group tracks), 0032 (audio engine), 0034 (plugin hosting modes), 0036 (semantic workspace and Device
  View), 0037 (parallel signal bundle). Also `docs/architecture.md`, `docs/plugin-hosting.md`, `docs/roadmap.md`.

### Baseline tests

| Suite | Command | Result |
| ----- | ------- | ------ |
| .NET | `dotnet test src/dotnet/Bluestone.slnx` | 1686 total, 1673 passed, 12 skipped, **1 failed**: `CoreMidiProviderTests.Playback_ThroughBluestoneVirtualPort_ArrivesInOrder` (expected note-ons 48–63, the last, 63, missing) |
| Rust | `cargo test` in `src/native` | all passed |

Skips are platform-specific (WinMM, ALSA) or need an enabled IAC bus.

### Baseline screenshots

The checked-in `docs/images/bluestone-main-window.png` (a real session with a VST3 instrument) is the visual
reference. Reproducible headless frames of the same workspace were added as `WorkspaceShowcaseTests`, which renders
fixed scenes (three tracks, a chain of Arpeggiator, a reference plugin instrument, and Transpose; a freshly added
track; a multiple selection) at 1100×720, 1280×800, 1440×900, and 1920×1080. The baseline frames are kept as
`docs/images/daw-first-polish/before-*.png`.

### Gaps observed at the start

Workflow:

- `Track.Create` and **New Track** make an `Instrument` track with no destination. A new track immediately shows a red
  *No output* status, an inspector warning ("No output is assigned; the track will not play"), and an inspector
  that opens on **Role**, **Output**, **Instrument** (profile), **Channel**, and **Transpose**.
- Inserting a plugin instrument does not make it audible: audio reaches the master only through an explicit audio
  connection, which the user has to build in the *Connections* section (source, destination, channel, Connect).
- Track status in the arranger headers is entirely about MIDI endpoints ("No output", "Synth · Check"), even for a
  track whose sound comes from a plugin in its chain.
- Inspector sections that are project-wide (Racks, Mixer, Recording) sit among the track's own settings; Recording is
  expanded by default.

Visual:

- The empty Device View shows the IN and OUT nodes squeezed together at the top left and the empty-state message
  centred elsewhere: two unrelated compositions.
- Device panels have a fixed height but their controls sit top-left with a large dead area below; plugin headers can
  truncate the device name to one letter when the format badge is long.
- The device header uses a gradient (`Gradient.DeviceHeader`); some inline sizes and raw colours remain in views.
- The Browser lists hardware profiles among devices in **All**, and offers **Insert** for them even though a profile is
  not a chain device.

Architecture:

- `MainViewModel` (1580 lines) and `MainWindow.axaml.cs` (1187 lines) are the largest UI files; new behaviour should
  not add to them without need.
- Audio and plugin settings are separate windows (`AudioWindow`, `PluginsWindow`) opened from the File menu; there is
  no settings surface.

### Reviewer's baseline gap matrix (summary)

The reviewer's matrix, condensed; severities are the reviewer's.

| Area | Gap | Severity |
| ---- | --- | -------- |
| Workflow | New tracks start in an error state ("No output") instead of ready to make music | Required now |
| Workflow | Inserting a plugin instrument does not make it audible without building a connection by hand | Required now |
| Workflow | Track status describes MIDI endpoints even when a chain instrument is the sound source | Required now |
| Workflow | Inspector leads with Role, Output, profile, Channel, Transpose, Voice; project-wide Recording is expanded | Required now |
| Workflow | Browser mixes devices, racks, and hardware profiles in **All** and enables Insert for any of them | Required now |
| Workflow | Audio and plugin-hosting settings are separate dialogs, not a settings workflow | Required now |
| Visual | Empty Device View is two disconnected compositions | Required now |
| Visual | Device panels waste vertical space; plugin names collapse beside the format badge | Required now |
| Visual | `Gradient.DeviceHeader` weakens the crisp tool-window language | Required now |
| Visual | Browser hierarchy is flat; category chips consume narrow width | Important follow-up |
| Visual | At 1100 px both side panes cramp the arranger and chain | Optional refinement |
| Architecture | Audible defaults must be explicit, undoable project state, not graph magic | Required now |
| Architecture | Settings must not add another `WorkspaceViewKind` and its switches | Required now |
| Architecture | `MainViewModel` and `MainWindow.axaml.cs` must not absorb the milestone | Required now |
| Architecture | Panel parameters are "the first N descriptors", not declarative grouping | Important follow-up |
| Architecture | Free docking would discard ADR 0036's foundation | Rejected scope |

Reviewer's top risks: invisible auto-routing; external hardware becoming undiscoverable; Settings growing the
coordinators; screenshot-only polish missing narrow/detached/failed/keyboard/drag states; generic cleanup flattening
the tool-window character. Traps: cards, oversized headings, pill-heavy filters, one giant "Advanced" bucket, hiding
"No output" while the track stays silent, profiles still insertable, a modal-looking Settings pasted into the grid.

## Milestone plan

### Selected vertical slice

**New Track → empty Device View → find and insert an instrument from the Browser → it is audible at the master → the
track header, inspector, and Device View all describe that path accurately**, with external MIDI hardware still one
deliberate step away, plus a working Settings surface for audio and plugin hosting.

### Acceptance criteria (milestone-specific)

1. **New Track** creates a hybrid track with an explicit, undoable audio connection to the master (or to its group),
   in one undo step. No role choice, no warning, no red status.
2. Inserting an audio-producing device into a track with no audio path gives it one in the same command (and undo
   step). Deliberate routing (taps, mixer channels, removed connections followed by other routing) is respected.
3. Track headers show the effective sound source: the chain's instrument, the external output, the group, or a
   neutral "No instrument"; red/warning states are reserved for real, actionable problems.
4. The inspector opens on the track's sound (instrument, devices, audio destination). External MIDI (output, profile,
   channel, transpose, voice) is a distinct section that opens itself when a track uses hardware and summarises its
   state when closed. The role is contextual. Routing (connections), racks, mixer, and recording remain sections.
5. The Device View's empty state reads as one chain (IN → insertion slot → OUT); panels use their height; plugin
   names stay legible; IN/OUT say where the chain's signal comes from and goes; no gradient header.
6. The Browser never presents a hardware profile as a device: profiles live in their own category with an
   assignment action, and the footer says exactly where Insert puts a device.
7. A two-pane Settings surface replaces the workspace content while open and restores it exactly on close; Audio and
   Plugins pages are fully working; it is a self-contained view that a window could host later.
8. Every changed workflow has tests; the full .NET and Rust suites pass; the reviewer's required findings are resolved.

### Phases

1. DAW-first track model (application commands, route status, track header).
2. Inspector refinement (extracted into its own view; MIDI hardware as a contextual section).
3. Device View refinement.
4. Browser refinement.
5. Settings foundation.
6. Visual pass, full suites, final review.

### Non-goals

Per-track mixer channels owned by tracks (a domain change), modulation, vertical or wrapped chains, free docking,
MIDI 2.0/MPE, multichannel buses, send/return devices, a new audio engine, a plugin-host rewrite, MIDI import
defaults (imported files keep describing hardware playback).

### Risks

- Default audio connections must not break group routing (members' outputs are parked while grouped) or validation.
- Status changes can ripple through existing presentation tests that assert "Offline" for unrouted imports.
- Extracting the inspector out of `MainWindow.axaml` must keep every named control the UI tests find.
- Settings must reuse `AudioViewModel` and `PluginsViewModel` without duplicating their logic.

### Visual elements to preserve

Control bar and LCD; border rails with rotated labels; inspector left, Browser right, arranger above the detail
region; dock headers in small caps; dark modules with coloured top accents, compact knobs, power buttons, and device
headers; IN/OUT boundary nodes; signal-role colours; hairline separators; the dense row rhythm.

## Decision journal

### D1. Working arrangement and review independence

- **Context**: the brief asks for two agents: an implementation owner and a read-only reviewer who never grades its own work.
- **Alternatives**: two concurrent implementation-capable agents; one agent switching roles.
- **Chosen**: the lead agent is the implementation owner; the reviewer is a separate agent context restricted to reading,
  briefed with the vision and constraints and asked for classified findings at each checkpoint. The CoreMIDI
  investigation ran in a third context confined to CoreMIDI, playback, and integration-test files.
- **Reasoning**: one writer avoids conflicting edits on one branch; separate contexts keep the review independent.
- **Reviewer response**: accepted (it produced the gap matrix and checkpoint reviews without edits).

### D2. A new track is hybrid and heard at the master through an explicit connection

- **Context**: new tracks were `Instrument` tracks with no destination, in a red "No output" state; a plugin instrument
  was silent until an audio connection was built by hand.
- **Alternatives**: (a) let the audio graph send any unconnected track's audio to the master implicitly; (b) give every
  new track its own mixer channel; (c) an explicit audio connection from the track's output to the master.
- **Chosen**: (c). `TrackSetup.NewTrack` adds a `Hybrid` track and a `Track → Master` audio connection (to its group if it
  is in one) in one undo step.
- **Reasoning and evidence**: ADR 0023 has one routing model of explicit connections; (a) would be invisible state the
  routing inspector, validation, and undo cannot see (the reviewer named this the top risk). (b) needs track-owned mixer
  channels: renaming, deleting, and duplicating a track would have to keep a channel in step, which the domain does not
  model (mixer channels are independent of tracks); that is a domain change, deferred.
- **Affected**: `Bluestone.Application/Editing/TrackSetup.cs`, `MainViewModel.AddTrack`.
- **Tradeoffs**: tracks have no fader of their own until given a mixer channel (choose one in "Audio to").
- **Reviewer response**: "the correct architecture"; rejected moving routing into `ProjectAudioGraph`.

### D3. Inserting something that makes sound gives a silent track an audio path

- **Context**: tracks made before D2, imported tracks, and tracks whose path was removed would still be silent after an
  instrument is inserted.
- **Alternatives**: do it in the Device View only; do it in `DeviceCommands.InsertDevice` for track chains.
- **Chosen**: in `DeviceCommands.InsertDevice(TrackId …)`: when the inserted definition produces audio and no audio of the
  track is sent anywhere (no connection from its output or from a device tap), add the default path in the same command.
  An `Instrument` track becomes `Hybrid` at the same time.
- **Reasoning**: every insertion path (Browser, drop, presets via the command) behaves the same, and undo removes the
  device and the path together. Widening is needed because an instrument track's output carries no audio of its own:
  without it, removing the instrument later would leave an invalid connection and be refused. ADR 0021 already allows
  widening (`Reconcile` only ever widens).
- **Tradeoffs**: one existing test's premise (an instrument track whose tap is broken by a move) had to construct its
  chain directly; the refusal rule itself is unchanged. Removing a device whose tap was the only path leaves the track
  unrouted rather than re-routing it behind the musician's back (tested).
- **Reviewer response**: accepted; asked for tests of multiple outputs, used destinations, cycles, and tap removal (added).

### D4. Track status describes the sound, not the MIDI port

- **Context**: the header said "No output" for a track played by a plugin.
- **Chosen**: `TrackSound` (presentation) reads the chain's instruments, the primary audio output, sends, and note
  destinations from the project. `TrackViewModel.StatusLine` names the instrument ("Surge XT", "Surge XT +1",
  "· Bypassed", "· Unavailable"), the hardware output, the note destination, or a neutral "No instrument".
  `RouteHealth.Idle` (a muted dot) is new: nothing sounds and nothing is wrong. Attention is reserved for notes with no
  instrument at all, failed instruments, and hardware problems; Offline for a missing output.
- **Alternatives**: hide the "No output" warning for hybrid tracks only (rejected: it would hide a silent track).
- **Affected**: `TrackSound.cs`, `TrackViewModel.cs`, `Options.cs` (`RouteHealth.Idle`), `RouteResolver` (an audio
  connection is a destination), the track row in `MainWindow.axaml`.
- **Reviewer response**: required consistent bypass semantics and layered instruments (done in `b4f9269`).

### D5. The primary audio output

- **Context**: "Audio to" shows one destination, but a track's output may have several audio connections.
- **Chosen**: the primary audio output is the first audio connection from the track's output in project order; edits keep
  its position. Choosing the destination of an existing send makes that send primary instead of duplicating it; further
  connections show as "+N sends in Connections".
- **Alternatives**: a stored "primary" flag on connections (a format change, not needed while order is preserved).
- **Reviewer response**: raised as *Required now*; resolved.

### D6. The inspector leads with the track's sound; external MIDI is a contextual section

- **Context**: Role, Output, profile, Channel, Transpose, and Voice led the inspector.
- **Chosen**: a **Track** section (Instrument, Devices, Audio to, Notes to, and Type only for non-hybrid tracks or mixed
  selections), then **External MIDI** (output, profile, channel, transpose, voice, initialize, use for all tracks) that
  opens itself when the selection uses hardware, keeps the musician's choice until the selection changes, and shows a
  summary ("Off", "Synth · Ch 2") when closed. Connections, Racks, Mixer, and Recording stay as sections with one-line
  summaries; Recording starts closed. The role is also in the track's context menu (**Track Type**). The inspector moved
  out of `MainWindow.axaml` into `InspectorView`; the Track section's state is `TrackSectionViewModel`.
- **Alternatives**: one "Advanced" expander (rejected by the brief); moving External MIDI to Settings (rejected: it is per
  track); keeping Transpose in the Track section (rejected: Device View has a Transpose device; the inspector's Transpose
  remains with the hardware settings it historically served).
- **Reviewer response**: "materially DAW-first without becoming sparse"; asked to keep `SelectionViewModel` from growing
  (done) and to prioritise output and channel in the summary (done).

### D7. Device View: one composition, labelled boundaries, flat headers

- **Chosen**: an empty chain shows a panel-sized dashed slot between IN and OUT (drop target, Browse Instruments); the
  centred message is only for "no chain to show". IN and OUT carry a caption (Track / N sources; Master / channel / "Not
  routed" in the warning colour). The device header is flat `Surface.Raised` (the `Gradient.DeviceHeader` token is
  removed); a plugin's format badge moves to the footer so the name keeps the header; controls are centred in the fixed
  panel height; a selected panel's header is tinted; a bypassed device's accent dims.
- **Kept**: the faint control-bar, section-header, and button gradients. They are part of the baseline's hardware-panel
  character and nearly imperceptible; flattening them is left to a deliberate visual decision, not this milestone.
- **Reviewer response**: pending (checkpoint 2).

### D8. Reproducible visual baselines

- **Chosen**: `WorkspaceShowcaseTests` renders fixed scenes headlessly (Skia) at 1100×720, 1280×800, 1440×900, and
  1920×1080, waiting for routing, plugin parameters, and transitions to settle. Frames are compared by eye; there is no
  pixel-diff assertion (fonts and Skia versions differ between machines).
- **Discovery**: frames saved during a full-suite run can show stale state; regenerate them with the showcase class alone.

### D9. CoreMIDI integration test

- **Finding**: a test-observation problem. Loading a plan while stopped chases bank and program for live input (ADR 0006)
  and Play chases them again, so a run emits 36 messages; the test stopped reading after 34 and missed note 63's on/off
  pair. Nothing was dropped. **Fix** (`8985d10`): wait for the last note's release (one ordered virtual source) and assert
  the exact stream. Not weakened: the assertion is stricter than before. The reviewer confirmed the arithmetic.
- **Separate observation**: launching many CoreMIDI test processes back to back occasionally makes `MIDIClientCreate`
  return -304; unrelated, not addressed.

### D10. Imported MIDI files keep describing hardware playback

- Imported tracks remain `Instrument` tracks without an audio path, so a file imported for a hardware rig still says "No
  output" until routed. Inserting an instrument into one makes it hybrid and audible (D3).

## Progress checkpoints

### Checkpoint 1: DAW-first track model and inspector (phases 1–2)

- **Commits**: `8985d10` (CoreMIDI test, separate), `b6349fe` (track model and status), `7ca71f7` (inspector), then
  `b4f9269` (review fixes).
- **Tests**: unit 1486/1486, UI 73/73 after the review fixes (new: `TrackSetupTests` ×11, `TrackStatusTests` ×5,
  `DawFirstTrackTests` ×3, `WorkspaceShowcaseTests` ×7; updated `TrackInspectorTests`, `InspectorTests`,
  `DeviceAndRoutingCommandsTests.Scenario14`).
- **Visual**: a new track reads "No instrument" with a neutral dot and no warning; the inspector opens on Instrument,
  Devices, and Audio to; External MIDI is one closed line ("Off") until used.
- **Reviewer findings and responses**:

  | # | Finding | Class | Response |
  | - | ------- | ----- | -------- |
  | 1 | "Audio to" edits the first audio connection with no defined primary | Required now | D5; tests for sends and used destinations |
  | 2 | Showcase frames captured before routing settled | Required now | Waits for status, parameters, transitions |
  | 3 | Bypassed instrument inconsistent (Attention vs Ready) | Important follow-up | Neutral `Idle` and "· Bypassed"; tested |
  | 4 | Only the first instrument summarised | Important follow-up | "+N" and aggregate failure; tested |
  | 5 | Detached Browser not brought forward by "Add an instrument…" | Important follow-up | Phase 4 |
  | 6 | `SelectionViewModel` accumulating responsibilities | Important follow-up | `TrackSectionViewModel` extracted |
  | 7 | Missing routing tests (multiple outputs, cycles, tap removal) | Important follow-up | Added |
  | 8 | External MIDI summary truncates | Optional | Output and channel only; profile in the tooltip |

### Checkpoint 2: Device View (phase 3)

- **Commit**: `1c138ba`.
- **Tests**: `DeviceViewTests` 14/14 (new: an empty chain is one composition with labelled boundaries).
## Deviations and discoveries

## Final assessment
