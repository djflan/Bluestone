# 0037. A device chain carries a parallel signal bundle

- Status: Accepted; amends 0022
- Date: 2026-10-11

## Context

ADR 0022 runs a chain's events device by device: each device gets the event classes its definition handles, and
everything else goes around it. An instrument (events in, no events out) **took** the classes it handled out of the
chain. A chain therefore behaved as "MIDI until the first instrument, audio after it": a second instrument after the
first never received notes, and a note effect after an instrument had nothing to transform.

Bluestone is meant to work like Bitwig's device chains: notes, non-note control and protocol events, and audio travel
through a chain side by side, with timing and transport as shared context. Each device works on the signals it
understands and passes the others on. The audio side already did this: `ProjectAudioGraph` puts an instrument's
output beside the audio already in the chain (so instruments layer), an audio effect replaces the audio with its
output, and a note effect leaves audio alone. Only the event side was exclusive.

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
- The internal note model stays independent of raw MIDI; MIDI 1.0, MPE, MIDI 2.0, and plugin formats remain
  boundary concerns (ADR 0018). Nothing here ties the bundle to stereo or to MIDI channel semantics.

The UI shows this model rather than hiding another: Device View panels are coloured by role (note, instrument, audio,
other), the chain runs between fixed input and output nodes, and nothing in the chain suggests a MIDI-to-audio
conversion point.

## Consequences

- Chains that put an instrument before note effects now send notes on to those effects; previously they received
  nothing. There is one user and no compatibility requirement, so existing projects simply change behaviour.
- `ChainRunnerTests` and `SignalGraphTests` cover pass-through, layering, note effects after instruments, explicit
  thru, and the diagnostic change.
- **Live input still ends at the first plugin instrument.** `LiveRoutePlan` (ADR 0035) stops its route at the first
  instrument that takes the notes, so playing live sounds the first instrument of a layered chain only. Extending
  the live route to fan out to every instrument on the chain is follow-up work.
- Plugins that put out MIDI are still not routed on (ADR 0033); a future note-producing instrument would add its
  output to the bundle as a note effect does.
- Per-note expression, audio-rate modulation, and multichannel (beyond mono and stereo) buses are not implemented,
  and the domain's note events do not yet carry a stable per-note ID. Nothing in the bundle prevents adding them:
  pass-through is decided by event class, not by MIDI channel and key, and channel counts are not part of the device
  contracts this ADR touches.
