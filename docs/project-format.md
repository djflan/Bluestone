# Bluestone project format

A Bluestone project (`.bluestone`) is a single UTF-8 JSON document. It is meant to be readable, to diff
cleanly in version control, and to stay portable: MIDI ports are stored by stable identifiers and
identity hints, never by index, and audio files by a path relative to the project file where possible.
The format is read and written by `ProjectSerializer` in `Bluestone.Infrastructure/Projects`.

```json
{
  "format": "bluestone-project",
  "formatVersion": 5,
  "project": {
    "id": "0199b0f2-…",
    "name": "Demo",
    "loop": { "start": 0, "end": 7680, "enabled": true },
    "sequence": {
      "ppqn": 960,
      "tempo":   [ { "tick": 0, "microsecondsPerQuarter": 500000 } ],
      "meter":   [ { "tick": 0, "numerator": 4, "denominator": 4 } ],
      "markers": [ { "tick": 3840, "name": "Verse" } ],
      "tracks": [
        { "id": "…", "name": "Rhythm", "muted": false, "soloed": false, "role": "group", "folded": true,
          "clips": [] },
        { "id": "…", "name": "Bass", "muted": false, "soloed": false, "role": "instrument", "group": "…",
          "clips": [
            { "id": "…", "type": "note", "start": 3840, "length": 7680, "offset": 0, "name": "Verse",
              "events": [
                { "id": "…", "tick": 0, "type": "program", "channel": 2, "program": 34, "bankMsb": 0, "bankLsb": 0 },
                { "id": "…", "tick": 0, "type": "controller", "channel": 2, "controller": 7, "value": 3355443200 },
                { "id": "…", "tick": 0, "type": "note", "length": 480, "channel": 2, "note": 40, "velocity": 100, "release": 64 },
                { "id": "…", "tick": 0, "type": "sysex", "bytes": "F0 43 10 4C 00 00 7E 00 F7" }
              ] }
          ],
          "automation": [
            { "id": "…", "target": { "type": "controller", "channel": 2, "controller": 7 },
              "points": [ { "tick": 0, "value": 0, "curve": "linear" },
                          { "tick": 7680, "value": 3355443200, "curve": "hold" } ] },
            { "id": "…", "target": { "type": "device", "device": "…", "parameter": 1 },
              "points": [ { "tick": 0, "value": 2147483648, "curve": "hold" } ] },
            { "id": "…", "target": { "type": "instrument", "instrument": "…", "port": "A", "channel": 2,
                                     "parameter": "filter-cutoff" },
              "points": [ { "tick": 0, "value": 2147483648, "curve": "linear" } ] }
          ] },
        { "id": "…", "name": "Vocals", "muted": false, "soloed": false, "role": "audio",
          "clips": [
            { "id": "…", "type": "audio", "start": 0, "length": 15360, "offset": 0,
              "source": { "location": "audio/vocals.wav", "sampleRate": 48000, "frames": 384000, "channels": 2 },
              "gain": -3, "fadeIn": 240, "fadeOut": 960, "fadeShape": "equalPower" }
          ] }
      ]
    },
    "instruments": [
      { "id": "…", "name": "QY Out", "profile": { "id": "bluestone.generic.xg", "name": "Generic XG" },
        "operatingMode": "XG",
        "ports": [ { "id": "A", "name": "Port A",
                     "endpoint": { "provider": "coremidi", "key": "-12345", "name": "QY Out",
                                   "manufacturer": "Yamaha", "model": "QY100" } } ] }
    ],
    "chains": [
      { "id": "…", "owner": { "type": "track", "track": "…" },
        "devices": [
          { "id": "…", "definition": { "id": "bluestone.midi.transpose", "name": "Transpose", "version": "1" },
            "parameters": [ { "id": 1, "value": 1610612735 } ] } ] },
      { "id": "…", "owner": { "type": "rack" }, "name": "Shared synth",
        "devices": [
          { "id": "…", "definition": { "id": "vst3:…", "name": "Vital" }, "name": "Pad", "bypassed": true,
            "parameters": [], "state": { "format": "vst3-component-v1", "data": "AAEC/w==" } } ] }
    ],
    "connections": [
      { "id": "…", "kind": "events",
        "source": { "type": "track", "id": "…" },
        "destination": { "type": "track", "id": "…" } },
      { "id": "…", "kind": "events",
        "source": { "type": "track", "id": "…" },
        "destination": { "type": "instrument", "id": "…", "port": "A" },
        "mapping": { "only": [ 1, 2 ], "force": 3 },
        "voice": { "bank": "normal", "program": 34 } },
      { "id": "…", "kind": "audio",
        "source": { "type": "rack", "id": "…" }, "destination": { "type": "mixer", "id": "…" } }
    ],
    "parkedConnections": [
      { "id": "…", "kind": "events",
        "source": { "type": "track", "id": "…" },
        "destination": { "type": "instrument", "id": "…", "port": "A" },
        "mapping": { "remap": [ { "from": 1, "to": 2 } ] } }
    ],
    "mixer": { "masterGain": 0,
      "channels": [ { "id": "…", "name": "Synth bus", "gain": -3.5, "pan": 0, "muted": false, "soloed": false,
                      "sends": [ { "target": "…", "level": -12, "preFader": false } ] },
                    { "id": "…", "name": "Reverb", "gain": 0, "pan": 0, "muted": false, "soloed": false,
                      "output": "…" } ] }
  },
  "extensions": { "org.example.feature": { } }
}
```

## Rules

- Channels and program numbers are one-based (1–16, 1–128), as musicians see them.
- `loop` (optional) is the arranger loop: its range in ticks (`start`, and `end` after it) and whether
  playback loops over it (`enabled`, default false). The range is kept while looping is off, so turning
  looping on again reuses it. Without `loop`, no range has been defined.
- A track holds clips (ADR 0020), in any order on disk but never overlapping. A clip's `start` and
  `length` (at least 1) are timeline ticks. A note clip's events' `tick`s are relative to the clip's
  content, and the clip shows content ticks from `offset` to `offset + length`. Content outside that
  window is kept but not played, so trimming a clip loses nothing. `name` is optional; without it the clip
  shows the track's name.
- A track's `role` is `instrument`, `audio`, `hybrid`, `effect`, or `group` (ADR 0021). Roles are
  stored, and on reading a role that cannot show the track's content is widened (an instrument track
  with audio clips becomes hybrid). `group` (optional) is the ID of the group track it belongs to.
  `folded` (optional, written only when true) says a group track's members are hidden under it in the
  track list and the arrangement (ADR 0030). `muted` and `soloed` default to false.
- A clip's `type` is `note`, holding `events`, or `audio`, holding a `source`:
  - `location` is the audio file: a path relative to the project file's folder, or an absolute path
    (at most 4096 characters). `sampleRate`, `frames`, and `channels` are informational; the file's own
    header is used for playback (ADR 0032).
  - `gain` (optional, omitted at 0) is in dB, -120 to 24.
  - `fadeIn` and `fadeOut` (optional, omitted at 0) are lengths in ticks from the clip's start and
    before its end. Fades that add up to more than the clip are kept and played shortened, the fade-in
    first.
  - `fadeShape` (optional) is `linear` (the default, omitted) or `equalPower`.
- Event IDs are unique across all clips of a track.
- `automation` (optional; omitted when empty) holds a track's automation lanes, in display order. A
  lane's `target` has a `type`:
  - `controller` (with a `controller` number; not bank select, 32–63, data entry or increment and
    decrement, RPN/NRPN selectors, or channel mode), `pitchBend`, or `channelPressure`, on a `channel`;
  - `device`: a `device` ID and a `parameter` ID, delivered to that device and never sent as MIDI
    (ADR 0024);
  - `instrument`: an external `instrument` ID, a `port` ID, a `channel`, and the `parameter`'s key in
    the instrument's profile's parameter map (port and key at most 64 characters), translated into the
    MIDI the profile maps it to (ADR 0031, [profiles.md](profiles.md#parameter-map)).

  No two lanes on a track share a target. Its `points` have timeline `tick`s, at most one per tick,
  32-bit `value`s (normalized for device and instrument parameters), and a `curve` (`hold` or
  `linear`) for the way to the next point. A lane whose device, instrument, port, profile, or parameter
  is missing is kept.
- Channel events describe what is played, not MIDI 1.0 bytes:
  - `note`: a note with its `length` (at least 1), attack `velocity` (1–127), and `release` velocity (0–127).
  - `noteOff`: a release with no matching note, kept from an import.
  - `controller`: a controller number (0–127) and its value.
  - `program`: a program, with optional `bankMsb` and `bankLsb` (0–127).
  - `pitchBend`, `channelPressure`, and `polyPressure` (which also has a `note`): a value.
- Controller, pitch bend, and pressure `value`s are 32-bit (0–4294967295), the MIDI 2.0 resolution.
  A MIDI 1.0 value is scaled up so that it scales back down exactly: 0 is 0, the centre (64, or
  8192 for pitch bend) is 2147483648, and 127 (or 16383) is 4294967295.
- The other event types are `sysex` (a complete `F0 … F7` message), `raw` (bytes kept verbatim from
  an import), and `meta` (a preserved meta event with its `metaType`), all as hex `bytes`.
- `instruments` are external MIDI instruments (ADR 0023): a name, an optional device `profile` (`id`
  and the `name` last seen), an optional `operatingMode`, and `ports`, each with an `id`, a `name`, and
  an optional `endpoint`: the MIDI `provider` and its `key`, with the `name`, `manufacturer`, and
  `model` last seen as optional identity hints. Instruments are stored whether or not their profile or
  endpoints are currently available.
- `chains` are device chains (ADR 0022). Each has one `owner`: a `track` (at most one chain per track)
  or a free-standing `rack`, and an optional `name`. Devices are in processing order, each with a stable
  `id`, a `definition` reference (ID plus the name and version last seen, so a missing plugin can still
  be named), an optional `name`, normalized parameter `value`s, `bypassed` (written only when true), and
  an opaque plugin `state` (base64 `data`, with its `format`).
- `connections` are the one routing model: `events` or `audio`, from a `source` (`track`, `rack`, or a
  `device` tap) to a `destination` (`track`, `rack`, `instrument` with a `port`, `mixer` channel, or
  `master`, which has no `id`). An optional channel `mapping` has `only` (the channels that pass), then
  `force` (one channel) or `remap` (`from` and `to` pairs), not both; `voice` selects a profile bank by ID and a
  `program`. Connections that do not validate are kept and reported, never dropped on load.
- `parkedConnections` (optional; omitted when empty) are a grouped track's own outputs, set aside when it
  joined its group and put back when it leaves (ADR 0030). They have the shape of `connections` but are not
  routing: nothing plays or validates them. An ID may not appear in both lists.
- `mixer` holds `masterGain` and the mixer channels, which are independent of tracks. Gains and send
  levels are in dB, -120 to 24; `pan` is -1 to 1. A channel's `output` (optional) is the channel it feeds
  instead of the master. Its `sends` (optional; omitted when empty, at most 64) copy its audio to other
  channels: a `target` channel, a `level`, and `preFader` (taken before the fader and pan). A channel
  sends to a target at most once and never to itself (a format error); a send to a missing channel or
  one that closes a loop is kept and reported.
- `extensions` and any other unrecognised top-level properties are preserved on save. Unrecognised
  properties inside `project` are ignored and not saved.
- Reading is strict: any invalid value fails with the JSON path of the problem.

## Versions and migrations

`formatVersion` increases whenever the structure changes. Older files are upgraded in memory by a
chain of migrations (`Format2To3Migration`, `Format3To4Migration`, `Format4To5Migration`) before they are read; saving always
writes the current version. A file from a newer Bluestone is refused with an explanation and never
overwritten.

Format 5 separated the arranger loop's range from whether it loops (`loop.enabled`) and added clip loops.
A format 4 file had a `loop` only while looping was on, so it becomes an enabled loop; its clips have no
loops and play as before. A format 4 reader refuses a format 5 file and never overwrites it.

Format 4 replaced the per-track `routing` with `instruments`, `chains`, `connections`, and `mixer`,
and added track `role` and `group`, audio clips, and device automation targets (ADR 0027). Each format 3
route becomes one external instrument per distinct endpoint and profile (named after the endpoint, or
"Unassigned" for a route with settings but no endpoint), a connection from the track forcing the
route's channel and selecting its voice, and, for a non-zero transposition, a Transpose device in the
track's chain. A route for a track that no longer exists is kept as a connection, which validation
reports. Every track gets the `instrument` role and the mixer starts empty. Migrated projects send
byte-identical MIDI (`Format3EquivalenceTests`).

These were added to format 4 later, without a new version. All are optional and written only when
used, so files without them are unchanged:

| Added | ADR | A format 4 reader that predates it |
| ----- | --- | ---------------------------------- |
| Track `folded`, `parkedConnections` | 0030 | Ignores them, and drops them if it saves the file |
| Instrument automation targets (`"type": "instrument"`) | 0031 | Refuses the file, with the path of the target's `type`, and never overwrites it |
| Mixer channel `sends`; audio clip `gain`, `fadeIn`, `fadeOut`, `fadeShape` | 0032 | Ignores them (no sends, clip gain, or fades), and drops them if it saves the file |

Format 3 put each track's events in clips. A format 2 track's events move into one note clip, with
the track's ID, from the bar of its first event to the bar line after its latest end, so it plays
exactly as before; a track with no events gets no clip.

Format 2 replaced format 1's `channel` events (raw MIDI 1.0 bytes) with the channel event types
above. Format 1 files are not migrated; import the original MIDI file again instead.

## Saving and recovery

1. The whole file is written to a hidden temporary file beside the project, flushed to disk, and
   read back to verify it.
2. The previous project file is copied to `<name>.bluestone.bak`.
3. The temporary file is renamed over the project (an atomic replace on the same volume).

If the project file is damaged or missing, Bluestone opens the backup and says so. Autosaves go to
`<name>.bluestone.recovery`; when that file is newer than the project, Bluestone offers to restore it.
