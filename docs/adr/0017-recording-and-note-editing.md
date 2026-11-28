# 0017. MIDI recording and note editing

- Status: Accepted; audio-metronome amendment implemented 2026-11-27
- Date: 2026-10-04

## Context
Bluestone could play and route, but not record or edit notes. A sequencer needs both. Three forces
shape the design:

- **Timing.** A recorded note must land where it was played, not where the UI happened to notice it.
  The UI thread runs at about 30 Hz, and input arrives on adapter threads (Core MIDI's high-priority
  thread on macOS). Notes played just before a loop wrap, or during a count-in, need a position too.
- **Undo.** The edit history stores whole immutable projects (ADR 0009). An edit that creates one
  history entry per mouse move, or per recorded note, would make undo useless and the history large.
- **Real-time safety.** The playback thread must not lock or allocate per message (ADR 0006), but
  thru and auditioning must be coordinated with it, and the metronome needs its timing.

## Decision
- **Input timestamps map to song positions in the engine.** The playback thread publishes the
  immediate cursor's tick-to-time anchor (and the previous one) through a seqlock;
  `PlaybackEngine.TryGetTickAt` maps any clock time to a tick from any thread, without locks or
  allocation. Input stamped just before a loop wrap or seek uses the previous anchor, so it stays in
  the pass it was played in; input stamped after a wrap the playback thread has not reached yet is
  folded back into the loop. Times before a count-in's start extrapolate into the pickup bar, and
  times before the start of the song are not recorded.
- **Playback owns count-in and metronome timing.** `Play` takes an optional count-in: the cursors are
  anchored at "now + count-in", with clicks scheduled at the count-in's actual offsets. Beat
  positions come from the plan's tempo and meter maps and follow loops. The audio metronome is
  rendered on the Master as described in the amendment below; it does not require a MIDI endpoint.
  The mode controls ordinary beat clicks, while count-in clicks remain separately scheduled.
- **Thru and auditioning go through the playback thread.** `PlaybackEngine.SendNow` enqueues a
  message that the playback thread sends on its next pass, so outputs are only ever used from one
  thread. The recorder remembers where each echoed note went, so its release follows it even if the
  thru target changes. Input is never echoed to an output with the same provider and name (for
  example an IAC bus used both ways), which would loop forever.
- **The computer keyboard is an input endpoint.** `ComputerKeyboardProvider` is a software
  `IMidiEndpointProvider` with one input, so recording, thru, and the monitor need no special case.
  `ComputerKeyboardViewModel` maps key presses to notes on the effective channel of the recording,
  armed, or selected track (`PlaybackController.ChannelFor`), and remembers each held note's channel
  so its release follows it. Auto-repeat is ignored and held notes release when the window loses
  focus or the keyboard is turned off.
- **Recording captures raw messages and pairs them at the end.**
  messages and their ticks under a short lock; pairing into notes happens when recording stops.
  Notes still held end at the stop position; a release that maps before its note (after a loop wrap)
  ends the note at the loop end. Notes are paired in the order they were played, not by position,
  because positions repeat across passes. Cycle recording merges every pass, and the loop is the
  take's range only if playback actually wrapped. By default, inputs that share a name with an
  output being played to are not listened to, so an IAC bus used both ways does not record Bluestone's
  own playback. A take is one `Record` command,
  merged by default or replacing what starts in the recorded range. A live preview is computed from
  the same capture for display. System messages are not recorded.
- **Editors preview, then commit once.** Edits are pure functions (`EventEdits`) returning events with
  their original IDs, applied by `ProjectCommands.EditEvents` as one undo step. Piano roll gestures
  (move, copy, resize, draw, erase, velocity drags, controller lines) are drawn as previews while the
  pointer moves and committed on release. Selections are sets of event IDs, so they survive edits and
  undo.
- **Grids restart at bar lines.** `MusicalGrid` snaps relative to each bar's start, so odd meters keep
  their downbeats; swing delays every second grid line.

### Amendment: audio metronome (2026-11-27; implemented)

The owner chose an audio click on the Master for the first-session audio build
([engineering handoff, decision 35](../engineering-handoff.md)). This replaces the former
MIDI-output choice above. MIDI-click port, note, and channel options are deferred.

- The application derives the effective ordinary-click enablement from the metronome mode and
  recording state. Playback owns beat and count-in timing; audio renders the click in both local and
  engine-hosted modes without requiring a MIDI endpoint. The count-in remains independently
  scheduled as before, including when ordinary beat clicks are disabled.
- Mix the click after the Master device chain and before the Master strip's gain, pan, mute, and
  meter. It is not the post-Master test tone. It follows the Master strip and is audible in a
  plugin-only project, subject to the Master and output being audible.
- Derive beat and downbeat positions from the playback plan's tempo and meter maps, and use the
  count-in's actual scheduled offsets. Instantaneous BPM alone is not a beat grid. Handle
  count-in before song time zero, tempo and meter changes, seeks, and loop wraps, including wraps
  within a block. Align the click with the position heard at the output, accounting for graph
  latency and render-ahead; do not silently lose the first count-in click.
- Playback publishes an immutable timing/click snapshot using a version check. The audio reader
  makes one attempt and falls back to its last complete snapshot if publication overlaps; it does
  not spin; a read barrier keeps the copied snapshot from being accepted torn. The count-in end is
  set when Play is applied, not when it is requested, and beat clicks never fall before the
  transport's start. The audio-side click renderer keeps state across blocks: a click already past
  when playback starts (up to 200 ms, covering render-ahead and output latency) sounds whole at the
  first block instead of being lost, and overlapping 40 ms clicks are coalesced the same way at any
  block size. Candidate checks are bounded by the finite block window, so dense valid tempos cannot
  starve later clicks. A warmed callback test confirms zero managed allocations. The engine command and restart
  contract are amended in [ADR 0044](0044-engine-process.md).

The owner also chose app-wide recording preferences rather than project-owned settings. Their
persistence remains unimplemented; this amendment does not change the project format.

## Consequences
- Recording works wherever an adapter implements `IMidiInput`: Core MIDI on macOS and WinMM on
  Windows (ADR 0045); ALSA input remains to be implemented and tested. The computer keyboard input
  works on every platform.
- Record-path latency is not compensated. A measured offset per input can be added later as a
  constant applied before `TryGetTickAt`.
- Arming is view state, not saved. Metronome, count-in, take-mode, input, and thru preferences are
  not yet persisted; the accepted direction is app-wide settings, not project data. No project
  format change is part of that decision.
- Overlapping notes of the same pitch, which piano roll moves can create, still cannot round-trip
  through MIDI files (warning SMF202).
- Hardware checks for recording and thru are in `docs/hardware-test-plan.md`; unattended tests only
  use loopback ports.
