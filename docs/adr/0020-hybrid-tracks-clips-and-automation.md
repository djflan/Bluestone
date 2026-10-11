# 0020. Hybrid tracks: typed clips and track automation lanes

- Status: Accepted; supersedes 0006's loop-wrap chase rule; amended by 0021 (track roles) and 0024 (device parameter automation)
- Date: 2026-10-10

## Context

A track is a name, mute and solo, and one list of events at absolute ticks. The arrangement shows one
"region" per track, computed when it is drawn from the bar of the first event to the bar of the last.
Moving a region moves every event on the track. There is nothing to split, trim, or arrange.
"Automation" means controller, pitch bend, and pressure events on that same list, so there is no way
to draw a curve over the arrangement without it being part of the notes.

Bluestone will eventually play audio and hosted instruments as well as MIDI. Many DAWs separate these
with track types (MIDI, instrument, audio), and converting between types later is awkward. Bitwig
Studio has a single hybrid track type instead: the clip's type decides what it holds (notes or audio),
any clip type can sit on any track, and automation lives on the track's timeline as well as inside
clips. Bluestone has no track types yet, so it can go hybrid from the start without merging anything.

## Decision

### Tracks hold clips, and the clip type decides the content

- **There is no track kind.** A `Track` holds `Clips`, `Automation` lanes, a name, and mute and
  solo. Routing stays outside the track (ADR 0008).
- **`Clip` is an abstract record** with `Id` (`ClipId`), `Start`, `Length` (at least one tick),
  `ContentOffset`, and `Name`. `NoteClip` holds an `EventList` of the existing `TrackEvent`s. An
  `AudioClip` will be added once there is an audio engine, under its own ADR. The format reserves the
  `"audio"` clip type for it.
- **Content positions are relative to the clip.** The content origin is `Start - ContentOffset`, so
  moving a clip touches no events.
- **Trimming is non-destructive.** A clip plays content ticks `[ContentOffset, ContentOffset +
  Length)`. Content outside that window is kept but neither played nor drawn. Extending a clip
  left of its content origin shifts the content and resets the offset to 0, so positions stay
  non-negative.
- **The clip's bounds are the whole truth.** Notes that cross the clip end are cut at the end. Events
  that start before the window do not play, and nothing is retriggered. A clip's length never
  changes on its own when its events change.
- **Clips on a track never overlap.** The `Track` constructor refuses overlaps. Placing a clip
  (moving or copying) lets the placed clip win: clips it covers are removed, trimmed, or split. Undo
  restores them. This keeps a track's arranged events a plain concatenation, with no merge rule.
- **Added events go to the clip where they start.** This covers editing, pasting, and recording. An
  event that starts between clips goes to the clip being edited (or where a take started) if that
  clip borders the same gap, or else to a new clip in the gap. Clips grow to bar lines to show what
  they receive, but never into a neighbour, so a note reaching into the next clip is kept whole but
  plays only to its clip's end. Recording therefore never joins, trims, or removes clips; a replacing
  take first removes the events that play starting in its range, except meta events.
- **Event IDs stay unique across all clips on a track.** Copying a clip gives the copy a new `ClipId`
  and new `EventId`s. Splitting divides events by timeline position; events keep their `EventId`s
  and the right half gets a new `ClipId`. A note held across the split stays in the left half and is
  cut at the split, like any note crossing a clip end.
- **Clip content is inline, not pooled.** Linked clips (several placements sharing one content) are
  future work: they would add a content pool to the sequence and make event identity `(ClipId,
  EventId)`. Keeping placement fields separate from content means clip placement, the arranger, and
  the compiler do not change when that happens.

### Track automation is separate from clip content

- **Controller data recorded or drawn in a clip stays in the clip** and moves with it.
- **Automation lanes belong to the track** and sit in arrangement time. They do not move with clips.
  A lane has an `AutomationTarget` and a list of `AutomationPoint`s.
- **Targets are MIDI parameters on a channel:** a controller number, pitch bend, or channel pressure.
  Bank select, the LSB controllers (32-63), data entry and increment/decrement, RPN/NRPN selectors,
  and channel mode messages are refused, because a lone value of these means something different
  from a curve. Controller lanes send 7-bit values; sending 14-bit MSB/LSB pairs for controllers 0-31
  is future work. Mixer and plugin parameters become targets when they exist.
- **A point is a position, a `ControlValue`, and a curve** (`Hold` or `Linear`) shaping the segment
  up to the next point. Values are 32-bit (ADR 0018), so a lane already has MIDI 2.0 resolution.
- **Lanes are rendered into channel events when the plan is compiled.** The first value is sent at
  tick 0, so chasing a position is deterministic. Linear segments are sampled every
  max(1, PPQN / 32) ticks (integer division), and a sample is sent only when its value differs at the
  target's MIDI 1.0 resolution (7 bits, or 14 for pitch bend). The plan compiler and the SMF exporter share this rendering, so exported files carry
  automation as controller events.
- **Automation wins over clip content.** Clip events aimed at the same target as a non-empty lane
  are left out and reported once per track. A controller lane also replaces its LSB partner
  (controller + 32), so stale fine values cannot skew the automated level. Playback compares targets after the route's channel
  override; SMF export has no routes, so it compares them as written. If a
  channel override sends two lanes to the same target, the first lane wins. At equal times, automation
  sorts after clip events.
- **Lanes follow the track's route.** The channel override applies to them; transposition does not.

### Format 3

The project format becomes version 3 (ADR 0009): each track writes `clips` (with event ticks
relative to the clip) and `automation`. Format 2 projects are migrated by wrapping each track's
events in one note clip. The clip starts on the bar of the first event. It ends at the latest end of
any event (note releases included) if that is a bar line, or else at the next bar line; if that is
not after every event's position (an event at the very end), the clip ends at the following bar line.
So nothing that played before is cut off. A track with no events gets no clip. A migrated project must send and export exactly the same MIDI 1.0 bytes; `PlanEquivalenceTests`
guards this with fixtures written by the format 2 serializer.

## Consequences

- Splitting, trimming, copying, and moving parts of a track become possible, and a later audio clip
  needs no new track type.
- Everything that read `Track.Events` changes: the plan compiler, SMF import and export, recording,
  the piano roll, and the event list. The piano roll edits one clip at a time and follows added
  events into the clip they land in. A take recorded into empty space becomes a new clip.
- **Loop wraps chase what changed.** Without it, a held automation value at the loop start would be
  resent only at its next point. The engine sends, at each wrap, the state at the loop start where it
  differs from the state at the loop end (`ChaseState.AtWrap`), computed when a plan is loaded or the
  loop is set rather than on the playback thread. A value the loop sets for the first time goes back
  to its Reset All Controllers value where it has one. This replaces ADR 0006's rule that controllers
  are not re-chased at the wrap.
- Splitting a clip through a held note silences the rest of the note, because the right half does not
  retrigger it. This matches the rule for clip ends; a split that keeps sounding notes would need
  notes that carry over between clips.
- When a channel override sends clip events and a lane to the same target only on playback, export
  keeps those clip events and playback drops them. The export is still what the track says; the
  difference comes from the route.
- Re-importing an exported file brings automation back as clip controller events, not lanes,
  because a MIDI file cannot tell them apart.
- Sampling interval and deduplication set how dense automation output is. A MIDI 2.0 encoder will
  dedupe at 32 bits instead and send more messages for the same curve.
