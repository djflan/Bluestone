# 0037. A device chain carries a parallel signal bundle

- Status: Accepted; amends 0022 and 0023
- Date: 2026-10-11

## Context

ADR 0022 runs a chain's events device by device: each device gets the event classes its definition handles, and
everything else goes around it. An instrument (events in, no events out) **took** the classes it handled out of the
chain. A chain therefore behaved as "MIDI until the first instrument, audio after it": a second instrument after the
first never received notes, and a note effect after an instrument had nothing to transform.

Bluestone's device chains are inspired by the workflow of Bitwig Studio's: notes, non-note control and protocol
events, and audio travel through a chain side by side, with timing and transport as shared context. Each device works
on the signals it understands and passes the others on. The audio side already did this: `ProjectAudioGraph` puts an
instrument's output beside the audio already in the chain (so instruments layer), an audio effect replaces the audio
with its output, and a note effect leaves audio alone. Only the event side was exclusive.

## Decision

- A chain carries a **bundle**: expressive note events, other control and protocol events (controllers, programs,
  pressure, pitch bend, SysEx), and audio buses, against shared musical time and transport.
- Devices transform what they handle and **pass through** what they do not. This was already true for unhandled
  classes; it is now also true for the classes an **instrument** handles: the instrument plays them, and they continue
  down the chain. Two instruments in a row both play the same notes and their audio layers; a note effect after an
  instrument transforms the notes the instrument passed on; an audio effect between instruments passes notes on.
- `DeviceDefinition.EventThru` makes thru explicit for a definition that needs something else: `None` makes a
  specialized instrument end the note path; a set of classes makes a note effect mix its input with its output.
  `DeviceDefinition.PassesThrough` is the effective set: `EventThru` if given, otherwise the handled classes for an
  instrument and none for other devices, always limited to what the device handles.
- `ChainRunner` sends the passed-through classes around the device and merges them back in canonical order, exactly
  as for unhandled classes, with no new allocation once its buffers have grown. `SignalGraph` no longer reports
  notes that an instrument played and passed on as going nowhere; only classes no instrument handled are unheard.
- Routing validation follows the same rule. `SignalPresence.Through` (used by `SignalRoutingValidator` and when a
  track joins a group) removes after a device only the handled classes it does not pass through, read from the same
  `DeviceDefinition.PassesThrough`. An event connection from an instrument's device tap or from its track's output
  therefore carries the notes the instrument passes on; ADR 0023's "an event tap after an instrument that consumed
  everything carries nothing" now holds only for an instrument whose `EventThru` ends the note path.
- The internal note model stays independent of raw MIDI; MIDI 1.0, MPE, MIDI 2.0, and plugin formats remain
  boundary concerns (ADR 0018). Nothing here ties the bundle to stereo or to MIDI channel semantics.

- **Junctions.** A chain of n devices has n + 1 junctions, as in Bitwig's device panel: its input (before the first
  device), the output of each device, and the last one, which is the chain's output. Each carries the whole bundle.
  Every junction is a routing source (ADR 0023), and a device can declare input ports that bring signal from elsewhere
  into the chain at that device (receivers, sidechains). The **Note Receiver** and **Audio Receiver** are such
  devices: they take a source from anywhere (another track or rack, its input, or the point after any device, including
  an earlier point of their own chain) and mix it with the chain's signal or replace it; the Audio Receiver has a gain.
  Live input from a receiver's source is not brought in (only the track's own live notes pass or stop). Automation of
  an Audio Receiver's Gain or Replace is not yet applied to its audio during playback: the stored values are (the Note
  Receiver's Replace follows automation, as any built-in note effect's parameters do).
  A plugin audio effect's `sidechain` input feeds its first auxiliary input bus (ADR 0033): connected audio is summed
  apart from the chain's, aligned by delay compensation, and ignored by a plugin that has no such bus. Each junction's audio can be metered (ADR 0032): a chain's
  input by the chain's ID, the rest by the device before it, measured only while a view watches it
  (`IAudioPlayback.WatchJunctions`), so a project's many junctions cost nothing until shown. A meter point sums its
  sources as they are, without delay compensation, so it never changes what is heard; sources with different latencies
  at one junction can read lower there than where compensation aligns them. Each junction also has a
  **note lamp**, lit while notes are held there: during playback from the compiled signal graph
  (`SignalGraphResult.NoteActivity`, the held tick ranges at every junction, read at the playhead, so nothing is added
  to the real-time path), and for live input while keys are held on the chain's track (`LiveInput.HasHeldNotes`) at
  every junction notes can reach. The live lamp is therefore approximate: it does not follow what a device does to live
  notes (an arpeggiator's steps, a filter that blocks them), is not shown for racks, and counts a key by channel and
  pitch, so two inputs holding the same note on one track share it. The playback lamp reads each note's held range, so
  after starting or seeking into the middle of a note (which playback does not restart) its lamp is lit until that note
  ends. The Device View keeps the
  indicators in `JunctionSignal`s, which the UI draws as Bitwig-style indicators between devices, not as meters.

The Device View draws each junction as a **seam** in the gap between panels (`JunctionSeam`): the hairline the chain
reads along; a slim stereo level of thin LED segments (sky blue through indigo and magenta to red at full scale,
`Meter.*` tokens) that by default follows the average level with the peak held as one segment, or the peak alone; and
under it a small MIDI connector icon that glows (`Junction.LampColor`, derived from `Meter.Low`) while notes are held,
fading over `Motion.LampRelease`. An indicator that cannot apply at a junction (the level after a device that only puts out notes) is drawn flat dark grey
(`Junction.DisabledColor`) and never lit, and an idle seam is quiet. Only junctions audio can reach are measured, and a
meter point with no audio sources reads silence without summing anything. Settings ▸ Display
chooses, for every view, where the indicators sit (top, upper half, lower half, bottom) and what the levels show; the
choice is an application preference (`display-settings.json`), not part of the project. A seam is also where a
junction is used: hovering shows a **+** that aims the Browser's next insert at that gap (marked like a drop's
insertion marker, and named in the Browser's target line); its context menu offers *Insert Device Here* and *Route from
Here…*; and a junction a connection takes signal from shows a small tap on the wire that opens the connections from
there. A receiver's panel shows its source as a pill (the source track's colour and point, "Pad · Output") and a
*Mix | Replace* switch; an effect plugin whose worker reports a sidechain bus, or whose sidechain is connected, shows
an **SC** chip in its header, outlined until something keys it. Both open one `SourcePickerViewModel`: every track and
rack with its input, each device's output, and its output, filtered by typing, offering only points the routing
validator accepts for that input. Choosing replaces the input's source as one undo step.

The UI shows this model rather than hiding another: Device View panels are coloured by role (note, instrument, audio,
other), the chain runs between fixed input and output nodes, and nothing in the chain suggests a MIDI-to-audio
conversion point.

## Consequences

- Chains that put an instrument before note effects now send notes on to those effects; previously they received
  nothing. There is one user and no compatibility requirement, so existing projects simply change behaviour.
- `ChainRunnerTests` and `SignalGraphTests` cover pass-through, layering, note effects after instruments, explicit
  thru, and the diagnostic change; `SignalRoutingValidatorTests` cover routing from after an instrument, with the
  default thru and with `EventThru = None`.
- **Live input fans out like playback.** `LiveRoutePlan.FindAll` follows the whole chain (every plugin instrument plays,
  note effects after an instrument get the notes it passes on, an instrument whose `EventThru` ends the note path
  stops it) and every event connection, from the chain's output or any point inside it, to every usable external port,
  each target with the stages in front of it; a plugin instrument is played once. `LiveInput` keeps one runner per
  target, and the engine plays thru on every route of the track. Every plugin instrument the monitored track plays is
  monitored and live-played (ADR 0043 decision 6). Each target runs its own copy of the stages before it, so an
  arpeggiator in front of two instruments runs twice, in step. A plugin MIDI effect is one running instance, so only
  the first route through it is played live; later routes that would share it are left out of live input (playback
  still plays them). A track or rack reached along two routes is followed along the first only, so a second route with
  different stages in front of it (a dry and a transposed path into one rack) is not played live. When a track's live routes
  change while notes are held, the old route is released before the new one starts, and its plugin note effects stay
  loaded for 250 ms so the releases reach them; a stateful effect shared by both generations can, rarely, see the new
  route's first block before the old route's last release.
- Plugins that put out MIDI are still not routed on (ADR 0033); a future note-producing instrument would add its
  output to the bundle as a note effect does.
- Per-note expression, audio-rate modulation, and multichannel (beyond mono and stereo) buses are not implemented,
  and the domain's note events do not yet carry a stable per-note ID. Nothing in the bundle prevents adding them:
  pass-through is decided by event class, not by MIDI channel and key, and channel counts are not part of the device
  contracts this ADR touches.
