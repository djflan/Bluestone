# 0017. MIDI recording and note editing

- Status: Accepted
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
  thru, auditioning, and the metronome all need to send from or alongside it.

## Decision
- **Input timestamps map to song positions in the engine.** The playback thread publishes the
  immediate cursor's tick-to-time anchor (and the previous one) through a seqlock;
  `PlaybackEngine.TryGetTickAt` maps any clock time to a tick from any thread, without locks or
  allocation. Input stamped just before a loop wrap or seek uses the previous anchor, so it stays in
  the pass it was played in; input stamped after a wrap the playback thread has not reached yet is
  folded back into the loop. Times before a count-in's start extrapolate into the pickup bar, and
  times before the start of the song are not recorded.
- **Count-in and metronome live in the engine.** `Play` takes an optional count-in: the cursors are
  anchored at "now + count-in", and its clicks are scheduled like notes. Beat clicks come from the
  plan's meter map, follow loops, and go through the active-note table so they always release.
  Clicks use GM percussion on channel 10 by default and go to a chosen output or the record track's
  output. The metronome can click while recording only (the default), or always.
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

## Consequences
- Recording works wherever an adapter implements `IMidiInput`: Core MIDI today. WinMM and ALSA
  inputs still need implementing (and testing on their platforms) before recording works on
  Windows and Linux. The computer keyboard input works on every platform.
- Record-path latency is not compensated. A measured offset per input can be added later as a
  constant applied before `TryGetTickAt`.
- Arming is view state, not saved. Metronome, count-in, and take-mode settings are not saved either;
  if they should travel with projects, that needs a project format change and a migration.
- Overlapping notes of the same pitch, which piano roll moves can create, still cannot round-trip
  through MIDI files (warning SMF202).
- Hardware checks for recording and thru are in `docs/hardware-test-plan.md`; unattended tests only
  use loopback ports.
