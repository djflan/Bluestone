# 0038. DAW-first tracks: hybrid by default, heard through explicit connections; Settings in the workspace's place

- Status: Accepted; amends 0021 and 0036
- Date: 2026-10-12

## Context

A new track was an `Instrument` track with no destination: it opened in a red "No output" state, the inspector led
with its role, MIDI output, instrument profile, channel, and transposition, and a plugin instrument inserted into it
was silent until an audio connection was built by hand in Connections. Bluestone is meant to feel like a
device-centric DAW first, with MIDI hardware a deliberate, fully supported step. Audio and plugin settings were two
separate dialogs. The milestone record is [docs/worklogs/daw-first-polish.md](../worklogs/daw-first-polish.md).

## Decision

### Tracks

- **New Track** creates a `Hybrid` track and, in the same undo step, an audio connection from its output to the
  master, or to its group track when it is in one (`TrackSetup.NewTrack`). The connection is ordinary project state
  (ADR 0023): it shows in Connections, is validated, saved, and undone like any other. The audio graph never routes
  anything implicitly.
- Inserting a device that produces audio into a track none of whose audio is sent anywhere (no connection from its
  output or from a device in its chain) adds the same default path in the same command
  (`DeviceCommands.InsertDevice`). An `Instrument` track becomes `Hybrid` then, because its output carries no audio
  of its own and the connection must stay valid whatever its chain holds later; ADR 0021 already only widens roles.
  Deliberate routing is never replaced, and removing the device whose tap was a track's only path leaves the track
  unrouted rather than re-routing it.
- A track's **primary audio output** is the first audio connection from its output, in project order; edits keep its
  position. The inspector's **Audio to** shows and edits it (`TrackSetup.SetAudioDestination`); choosing the
  destination of an existing send makes that send primary instead of duplicating it. Other audio connections from
  the output are sends; choosing no audio output removes them too. A track in a group sends its sound through the
  group (ADR 0030), so its destination is not chosen there. Presets and chains moved onto a track get the default
  path like a single inserted device.
- The role remains persisted and every role is still available (the inspector shows it for non-hybrid tracks; the
  track's context menu has **Track Type**). Imported MIDI files keep making instrument tracks for hardware.
- Track status describes the sound (`TrackSound`): the chain's instrument ("+N" for layers, "· Bypassed",
  "· Unavailable"), the hardware output, a note destination, or a neutral "No instrument" (`RouteHealth.Idle`).
  Warnings are kept for notes nothing plays, a failed instrument, a device that is not installed (its kind is not
  stored, so it may be the instrument), and missing or ambiguous outputs. Rows follow plugin status changes.

### Inspector

The inspector opens on a **Track** section (instrument, devices, audio destination, and the type when it says
something), then **External MIDI** (output, profile, channel, transposition, voice, initialization), which opens by
itself for tracks that use hardware and sums itself up in its header otherwise, then Connections, Racks, Mixer, and
Recording, each summarised when closed. It is a view of its own (`InspectorView`); the Track section's state is
`TrackSectionViewModel`.

### Settings

Settings (`SettingsViewModel`, `SettingsView`) is a two-pane surface (categories with search, and the chosen page)
shown in the main window in place of the workspace grid. The workspace is hidden, not changed: closing Settings
(Done, Escape) shows it exactly as it was, and nothing in the project or the workspace state is touched. Settings is
not a `WorkspaceViewKind`; it knows nothing of its host, so a window can show the same view later. Its pages are the
existing `AudioViewModel` and `PluginsViewModel`, so settings apply and save as before; the old Audio and Plugins
windows are removed, and their menu items open the matching page.

## Consequences

- Making music needs no routing knowledge: New Track, an instrument from the Browser, play.
- Tracks have no fader of their own until their audio is sent to a mixer channel; track-owned mixer channels would
  need a domain model of channel ownership (rename, delete, duplicate) and are left for later.
- Hybrid tracks' outputs may always carry audio, so validation is less precise about taps on them than about taps on
  instrument tracks (an over-approximation, as `SignalPresence` intends).
- Tests: `TrackSetupTests`, `TrackStatusTests`, `DawFirstTrackTests`, `SettingsTests`, `SettingsViewModelTests`, and
  the updated inspector, plugins, and audio settings tests.
