# Bluestone project format

A Bluestone project (`.bluestone`) is a single UTF-8 JSON document. It is meant to be readable, to diff
cleanly in version control, and to stay portable: MIDI ports are stored by stable identifiers and
identity hints, never by index, and audio files by a path relative to the project file where possible.
The format is read and written by `ProjectSerializer` in `Bluestone.Infrastructure/Projects`.

```json
{
  "format": "bluestone-project",
  "formatVersion": 6,
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
          "audioStrip": { "gain": 0, "pan": 0, "muted": false, "soloed": false },
          "clips": [] },
        { "id": "…", "name": "Bass", "muted": false, "soloed": false, "role": "instrument", "group": "…",
          "clips": [
            { "id": "…", "type": "note", "start": 3840, "length": 7680, "offset": 0, "name": "Verse",
              "loop": { "start": 0, "length": 3840, "enabled": true },
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
          "audioStrip": { "gain": -3, "pan": 0, "muted": false, "soloed": false },
          "clips": [
            { "id": "…", "type": "audio", "start": 0, "length": 15360, "offset": 0,
              "source": { "location": "audio/vocals.wav", "sampleRate": 48000, "frames": 384000, "channels": 2 },
              "gain": -3, "fadeIn": 240, "fadeOut": 960, "fadeShape": "equalPower" }
          ] },
        { "id": "…", "name": "Room", "muted": false, "soloed": false, "role": "effect",
          "audioStrip": { "gain": 0, "pan": 0, "muted": false, "soloed": false }, "clips": [] }
      ]
    },
    "instruments": [
      { "id": "…", "name": "Synth Module", "profile": { "id": "bluestone.generic.gm1", "name": "General MIDI (Level 1)" },
        "operatingMode": "GM",
        "ports": [ { "id": "A", "name": "Port A",
                     "endpoint": { "provider": "coremidi", "key": "-12345", "name": "Synth Module" } } ] }
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
        "purpose": "output",
        "source": { "type": "track", "id": "…" }, "destination": { "type": "master" } },
      { "id": "…", "kind": "audio",
        "purpose": "send", "sendLevel": -12, "preFader": false,
        "source": { "type": "track", "id": "…" }, "destination": { "type": "track", "id": "…" } }
    ],
    "parkedConnections": [
      { "id": "…", "kind": "events",
        "source": { "type": "track", "id": "…" },
        "destination": { "type": "instrument", "id": "…", "port": "A" },
        "mapping": { "remap": [ { "from": 1, "to": 2 } ] } }
    ],
    "masterStrip": { "gain": 0, "pan": 0, "muted": false, "soloed": false },
    "masterAutomation": [
      { "id": "…", "target": { "type": "masterGain" },
        "points": [ { "tick": 0, "value": 2147483648, "curve": "linear" } ] }
    ]
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
- A clip's `loop` (optional, on note and audio clips) repeats part of its content (ADR 0039): `start` (content
  ticks), `length` (at least 1), and `enabled` (default false; the loop is kept while off). Content from `offset`
  plays up to the loop's end, then the loop's region repeats until the clip ends; content before `start` therefore
  plays once. `offset` is never wrapped into the loop. A note clip whose loop would play more than 262,144 events is
  refused, as is a file whose looping clips would together play more than 1,048,576.
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
- `chains` are device chains (ADR 0022). Each has one `owner`: a `track` (at most one chain per track),
  the project `master`, or a free-standing `rack`, and an optional `name`. Devices are in processing
  order, each with a stable
  `id`, a `definition` reference (ID plus the name and version last seen, so a missing plugin can still
  be named), an optional `name`, normalized parameter `value`s, `bypassed` (written only when true), and
  an opaque plugin `state` (base64 `data`, with its `format`).
- `connections` are the one routing model: `events` or `audio`, from a `source` (`track` or `rack`, the end of its
  chain; `chainInput`, a chain by its `id`, before its first device; or a `device` tap, after that device) to a
  `destination` (`track`, `rack`, `chainInput` for a chain's input, `deviceInput` with the device's `id` and the input's `port` (at most 64
  characters), `instrument` with a `port`, or `master`, which has no `id`). Each audio connection has a `purpose`:
  `output`, `send`, or `internal`. An output routes a strip to Master or its parent Group; a send copies signal to
  an Effect track and carries `sendLevel` (dB) and `preFader` (before gain and pan when true); internal routes
  express advanced junction, receiver, or sidechain connections. An optional channel `mapping` has `only` (the channels that pass), then
  `force` (one channel) or `remap` (`from` and `to` pairs), not both; `voice` selects a profile bank by ID and a
  `program`. Connections that do not validate are kept and reported, never dropped on load.
- `parkedConnections` (optional; omitted when empty) are a grouped track's own outputs, set aside when it
  joined its group and put back when it leaves (ADR 0030). They have the shape of `connections` but are not
  routing: nothing plays or validates them. An ID may not appear in both lists.
- An audio-capable track may have an `audioStrip` with `gain` (dB, -120 to 24), `pan` (-1 to 1),
  `muted`, and `soloed`. MIDI-only tracks have no audio strip. `masterStrip` is mandatory and has the
  same controls. `masterAutomation` is optional project-level automation for the Master fader, pan,
  or chain devices; track `automation` may also target its strip fader and pan. Meter display settings
  are application preferences and are not project data.
- `extensions` and any other unrecognised top-level properties are preserved on save. Unrecognised
  properties inside `project` are ignored and not saved.
- Reading is strict: any invalid value fails with the JSON path of the problem.

## Versions and migrations

`formatVersion` increases whenever the structure changes. The current version is 6. Older files are
upgraded in memory by a chain of migrations (`Format2To3Migration`, `Format3To4Migration`,
`Format4To5Migration`, `Format5To6Migration`) before they are read; saving always writes the current
version. A file from a newer Bluestone is refused with an explanation and never overwritten. Versions
before 6 do not understand format 6 and must not be used to open and save it.

Format 6 replaces format 5's independent mixer channels with track-owned audio strips, explicit
`output`/`send`/`internal` audio-route purposes, and the mandatory `masterStrip`. The Master may own
a device chain and project-level fader/pan/device automation. The Master chain processes its devices
in order before the Master fader and pan.

Format 5 projects migrate in memory. The old channel graph becomes track-owned Group buses and Effect
returns: ordinary incoming outputs identify buses, incoming sends identify returns, and a channel
that served both roles becomes a Group plus a neutral Effect return. Existing channel strip settings,
send levels, pre/post-fader taps, and representable topology are carried into the new graph; Master
gain becomes the Master strip gain. Legacy Device, Rack, and chain-input audio routes directly to
Master become deterministic neutral Effect returns: the junction route remains Internal and the
return has an ordinary Output to Master. Parked routes remain parked through that return. No Master
chain or Master automation is invented. IDs for generated tracks are deterministic. The conversion
is not lossless for every old graph: ambiguous multiple
ordinary outputs, conflicting group membership, unrepresentable cycles, and malformed/self sends fail
with a path-specific error instead of being silently flattened, dropped, or redirected. Supported
channel topology is preserved, but blanket byte-for-byte or sample-for-sample equivalence is not
promised. Meter mode and presentation remain app-local and are not changed by migration.

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

Format 5 has since gained, in the same way:

| Added | ADR | A format 5 reader that predates it |
| ----- | --- | ---------------------------------- |
| Connection nodes `chainInput` and `deviceInput` | 0023 | Refuses the file, with the path of the node's `type`, and never overwrites it |

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
