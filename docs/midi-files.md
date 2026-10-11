# Standard MIDI File support

Bluestone reads and writes Standard MIDI Files (SMF) formats 0, 1, and 2. A MIDI file is an interchange
format, not a Bluestone project: importing turns it into a sequence, and exporting writes a sequence
back out. Both steps report anything that could not be carried over exactly.

## Reading

`SmfReader` never modifies its input and treats every file as untrusted. Limits are applied before
anything is allocated or parsed:

| Limit | Default |
| ----- | ------- |
| File size | 64 MiB |
| Tracks | 4096 |
| Events (whole file) | 10,000,000 |
| One meta or SysEx payload | 1 MiB |

A file that has no `MThd` header, has an unsupported format or timing division, or exceeds a limit
fails with `SmfFormatException`. Damage *inside* a track (truncation, invalid bytes) stops reading
that track only; the events read before the damage are kept, and a diagnostic says what happened.

## Importing

- **Format 0** files are split into one track per used MIDI channel, named `Channel 1` through
  `Channel 16` in channel order. Channel numbers, note timing, and channel messages are preserved.
  SysEx, raw packets, and retained meta events are kept once on a leading `MIDI Setup` track,
  which must also be routed to the playback output. Tempo, meter, and markers still become
  sequence data, and the original track name becomes the title. No unused channel tracks or
  empty setup track are added. Files without channel events retain a single track.
  Formats 1 and 2 retain their authored track structure.
- **Clips**: each imported track's events go into one clip, from the bar of its first event to the
  bar line after its last event ends, so nothing is cut short.
- **Notes** are paired first-in, first-out per channel and note number. Note-on with velocity 0
  counts as a release with release velocity 0.
- **Bank and program**: bank select MSB (CC 0) and LSB (CC 32) on the same channel and tick as a
  program change become part of that program selection. Bank selects anywhere else stay controller
  events. Either way the messages sent and exported are the same.
- **Controllers, pitch bend, and pressure** are stored at MIDI 2.0 resolution and written back at
  their original MIDI 1.0 values.
- **Tempo, time signature, and markers** from any track become the sequence's tempo map, meter map,
  and markers. Track names at tick 0 become track names. In a format 1 file, a first track that
  holds only conductor data is absorbed and its name becomes the title.
- **SMPTE-timed files** keep one tick per file tick; Bluestone picks a resolution and a constant tempo
  that reproduce the original timing (29.97 fps drop-frame included).
- **SysEx**: complete messages become SysEx events. Packets split across several events, `F7` escape
  events, and malformed SysEx are kept as raw bytes with their original timing.
- **Everything else** (text, lyrics, copyright, key signature, port, sequencer-specific, unknown
  meta types) is preserved as meta events and written back on export.
- **Text** is decoded as UTF-8 when valid, otherwise Latin-1.

## Exporting

Export always writes format 1 with running status: a conductor track (title, time signatures,
tempo changes, markers) followed by one track per sequence track. Simultaneous events are written in
Bluestone's canonical order (ADR 0003), so a release always precedes a retrigger on the same tick.
Text is written as UTF-8.

Each file track holds what its sequence track plays: the events its clips show (notes cut at clip
ends, trimmed content left out) and its automation lanes:

- MIDI lanes are rendered as controller, pitch bend, and pressure events (ADR 0020). Clip events a lane
  replaces are left out (SMF203).
- Lanes that automate an external instrument's parameter are written as the MIDI the instrument's
  profile maps them to (CC, RPN, NRPN, or SysEx), thinned exactly as playback sends them, after the
  track's own events at the same tick and phase (ADR 0031). A MIDI file has no ports, so they go on the
  part's channel. A lane whose instrument, port, profile, or parameter is missing, or whose parameter an
  earlier lane already controls, is not written; export reports it (SMF204).
- Lanes that automate a device parameter have no MIDI form and are not written; export reports them
  (SMF204, ADR 0024).

Export does not follow the routing model (device chains, connections), so it compares lanes and clip
events on their own channels; when a connection forces a channel, playback can replace more. A MIDI file
has no clips or lanes, so importing an exported file brings automation back as clip events.

## Diagnostics

Each diagnostic has a stable code. Repeats of a code in the same track are folded into one entry
with a count.

| Code | Severity | Meaning |
| ---- | -------- | ------- |
| SMF001 | Info | Header longer than six bytes; extra bytes ignored |
| SMF002 | Warning | Header track count differs from the tracks found |
| SMF003 | Warning | A chunk is shorter than its declared length |
| SMF004 | Warning | Unrecognized bytes after the last chunk were ignored |
| SMF005 | Info | A non-track chunk was preserved but not interpreted |
| SMF006 | Warning | A track has no end-of-track event |
| SMF007 | Warning | Data after end-of-track was ignored |
| SMF008 | Warning | A malformed event ended reading of its track |
| SMF009 | Info | Running status continued across a meta or SysEx event |
| SMF010 | Warning | A format 0 file contains more than one track |
| SMF100 | Info | SMPTE timing was converted to PPQN and a constant tempo |
| SMF101 | Info | A note-off with no matching note-on was kept as a separate release |
| SMF102 | Warning | A note with no release was ended at the end of its track |
| SMF103 | Warning | A zero-length note was lengthened to one tick |
| SMF104 | Warning | A malformed tempo event was kept raw and not applied |
| SMF105 | Warning | A malformed or unrepresentable time signature was kept raw and not applied |
| SMF106 | Info | Non-standard metronome settings in a time signature were not kept |
| SMF107 | Info | Split, escaped, or malformed SysEx was kept as raw bytes |
| SMF108 | Warning | Format 2 patterns were imported as simultaneous tracks |
| SMF109 | Info | Conductor events outside the first track were applied globally |
| SMF110 | Info | A MIDI port assignment was kept but does not affect routing |
| SMF111 | Warning | Tempo events in an SMPTE file were dropped |
| SMF112 | Warning | A track name was shortened to 256 characters |
| SMF200 | Info | Mute/solo state cannot be stored in a MIDI file |
| SMF201 | Info | Non-ASCII names were written as UTF-8 |
| SMF202 | Warning | Same-pitch notes overlap on one channel; their lengths may change when read back |
| SMF203 | Info | Clip events were left out because the track's automation replaces them |
| SMF204 | Warning | Automation lanes were left out: device parameter lanes (no MIDI form), or instrument parameter lanes that could not be translated to MIDI |

## Known limitations

- Byte-exact reproduction of an imported file is not a goal: export uses canonical event order,
  running status, and standard metronome settings.
- A track's end-of-track position is not kept; exported tracks end at their last event.
- Overlapping notes of the same pitch on the same channel cannot be stored unambiguously in a MIDI
  file: on import they are paired first-in, first-out, so their lengths may differ (SMF202).
- RIFF-wrapped (`.rmi`) files are not yet recognized.
