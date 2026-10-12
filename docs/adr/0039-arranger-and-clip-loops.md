# 0039. Arranger and clip loops: ranges kept apart from looping, repeats produced at plan time

- Status: Accepted; amends 0009 (project format 5), 0020, and 0032
- Date: 2026-10-12

## Context

The arranger loop was a nullable range: turning looping off deleted it, a click on it in the ruler deleted it, and
its range could not be adjusted while it was off. Clips had no loops: repeating a figure meant copying clips. The
workflow references (Bitwig Studio's arranger and clip loops, and DAWs in general) keep a loop's range independent of
whether it loops, and let a clip repeat part of its content, after a one-time pickup, for as long as the clip lasts.
The milestone record is [docs/worklogs/bitwig-interaction-polish.md](../worklogs/bitwig-interaction-polish.md).

## Decision

### Arranger loop

- `Project.Loop` is an `ArrangerLoop`: an optional range and `IsEnabled` (an enabled loop has a range). Only
  `ArrangerLoop.Active` reaches playback, the audio transport, cycle recording, and the content shading; a disabled
  range has no effect on what plays.
- Turning looping on reuses the range; with none, it loops four bars from the bar containing the playhead.
- A loop engages only while the playhead is before its end: playback that starts after it plays on (unchanged).
- Loop edits are commands (`LoopCommands`); a ruler drag commits once, on release.

### Clip loop

- `Clip.Loop` is an optional `ClipLoop` (start and length in content ticks, and `IsEnabled`), on note and audio clips
  alike. `ContentOffset` stays the absolute trimmed position; looping only changes how clip time maps to content
  (`Clip.ContentAt`): content plays from `ContentOffset`, and once past the loop's end wraps into the loop. Content
  before the loop's start therefore plays once, as a pickup. `Clip.Passes` enumerates the passes without allocating.
- Turning a clip's loop on for the first time loops what the clip plays now (`[ContentOffset, + Length)`, an audio
  clip's loop stopping at the end of its file), so nothing audible changes until the clip is made longer.
- **Note clips** produce their repeats in `NoteClip.Arrange()`, which `Track.ArrangedEvents`, `TrackRendering`, the
  signal graph, plugin rendering, and MIDI export all use, so playback and export agree. Repeats are produced when a
  plan is compiled, never on a real-time thread. Each pass's notes are cut at the pass's end (a note starting in the
  loop sounds again on every pass; one crossing the loop's end stops there): conventional truncation, no retrigger of
  the tail. Each repetition starts by restoring the controller state of the loop's start, with the engine's own wrap
  rule (`ClipLoopChase`, mirroring `ChaseState.AtWrap`): values that differ are sent; values first set inside the loop
  return to their RP-015 reset where they have one; programs are restored with their bank; nothing set exactly at the
  loop start is repeated. Repeats keep the IDs of the content events they come from: an arranged event names its
  source. Track automation is not part of clips and never loops with them.
- **Bounded expansion**: a clip may play at most `NoteClip.MaxArrangedEvents` (262,144) events. The count is computed
  arithmetically (`ArrangedEventCount`) whenever a track is built, so an edit that would exceed it, by any path, is
  refused with an explanation (`ClipLoopLimitException` → `CommandRefusedException`), and a file that exceeds it is
  refused when read. Nothing is ever silently cut short.
- **Audio clips** are not time-stretched, so an audio loop is a fixed stretch of the file: its start and length are
  converted to source frames where the clip's content starts (`ProjectAudioGraph`), and stay whole frames
  (`AudioClipRegion.LoopStartSeconds`/`LoopLengthSeconds`). `ClipSource` resamples over the played ("virtual") stream,
  mapped frame by frame, so interpolation across a wrap reads the loop's start, any number of wraps can fall in one
  block, and the output depends only on song position (block-size independent). The loop's start is preloaded at open
  time; at each wrap the disk ring is sent ahead to where that preload ends. Fades belong to the clip's outer edges. No
  crossfade is added at the wrap; an explicit loop crossfade is later work.
- **Editing**: moving and copying keep the loop and its phase; trimming never wraps `ContentOffset` and never drops
  content; content rebased by an edit moves the loop with it; splitting a clip that has a loop keeps all content in
  both parts (the right part with new event IDs, starting at the content heard at the cut), so both still sound the
  same; Consolidate writes out what plays: a note clip's arranged events, or for an audio clip a new WAVE file of the
  played frames at the file's rate (`AudioConsolidation`, copied, never resampled).
- Project format 5 stores `loop` on clips (`start`, `length`, `enabled`) and `loop.enabled` on the project; format 4
  files migrate as enabled arranger loops with no clip loops.

## Consequences

- Turning loops off is safe: nothing is lost, and re-enabling restores the same loop.
- The plan's size grows with repetitions; the per-clip limit bounds it, at the cost of refusing extreme edits (a very
  short loop with content over a very long clip). Lazy expansion inside the engine was rejected: it would duplicate
  the chase and ordering machinery on the real-time thread.
- Under tempo changes an audio loop keeps its length in seconds while note-clip loops keep theirs in ticks; drawing
  uses the tick model, so an audio clip's repeat markers can drift from what plays where the tempo changes inside it.
- Recording with Replace over a looping note clip removes the source events that play in the range, which affects
  every repetition of them.
