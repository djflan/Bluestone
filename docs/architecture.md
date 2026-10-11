# Bluestone architecture

Bluestone models music and musical intent. Tracks organize music, clips contain it, devices process
signals in chains, automation targets device parameters, and one list of connections routes signals to
other tracks, shared racks, external instruments, and mixer channels. MIDI 1.0, MIDI 2.0/UMP, SysEx
dialects, and operating-system MIDI APIs sit at the edges. This page shows where things live and why.
The decisions behind it are in [docs/adr/](adr/), in particular ADRs
[0018](adr/0018-midi-protocol-layering.md), [0019](adr/0019-sysex-preservation-and-interpretation.md),
and [0021](adr/0021-track-roles.md) to [0028](adr/0028-plugin-midi-effects-at-plan-time.md).

## Layers

```text
UI / Application         Bluestone.Desktop, Bluestone.Presentation, Bluestone.Application
       ↓
Sequencer / Domain       Bluestone.Domain, Bluestone.Profiles
       ↓
Signal processing        Bluestone.Signal: device chains, the routing graph
       ↓
Playback                 Bluestone.Playback: prepared plans, the real-time engine
Audio                    Bluestone.Audio: the render graph, mixer, clips, plugin nodes; device output in Rust (cpal)
       ↓
MIDI semantics           channel events (NoteEvent, ControllerEvent, ProgramEvent, …) and ControlValue
       ↓
Protocol encoding        Bluestone.Midi: Wire (MIDI 1.0 bytes), Files (SMF), SysEx (dialects)
       ↓
Platform transport       Bluestone.Midi.Endpoints contracts; Bluestone.Platform.CoreMidi / Windows / Alsa
```

These are conceptual boundaries, not one assembly each. A layer gets its own project only when that
buys something concrete: a dependency rule that must be enforced, an operating-system-specific
build, or a framework that must stay out of testable code. Project persistence lives in
`Bluestone.Infrastructure` for the same reason: it may depend on the domain and nothing else.

### Projects and their references

```text
Bluestone.Domain            (nothing)          sequence, tracks, roles, clips, automation, events, time, MIDI values,
                                             devices and chains, external instruments, connections, mixer
Bluestone.Midi              Domain             MIDI 1.0 encoding, SMF, SysEx dialects, endpoint contracts
Bluestone.Profiles          Domain             device profiles: what an instrument understands
Bluestone.Signal            Domain             chain runner, built-in processors, device catalog, signal graph
Bluestone.Infrastructure    Domain             project files, chain preset files, migrations
Bluestone.Playback          Domain, Midi, Signal   real-time engine, prepared plans
Bluestone.Audio             Plugins, Plugins.Protocol   audio device output (the bluestone_audio Rust crate), render graph,
                                             mixer strips and meters, audio file decoding, sample-rate conversion,
                                             disk streaming, plugin nodes
Bluestone.Plugins.Protocol  (nothing)          plugin control-plane frames and the shared-memory block exchange
Bluestone.Plugins           Plugins.Protocol   worker supervision, crash recovery, scanning (main process)
Bluestone.PluginWorker      Plugins.Protocol   the worker executable that hosts plugins (separate process)
Bluestone.Application       all of the above   sessions, editing commands, recording, routing resolution, plugin bridge,
                                             the project's audio graph and the audio playback controller
Bluestone.Presentation      Application, Audio view models, no UI framework
Bluestone.Desktop           Presentation, Platform.*   Avalonia views, platform selection
Bluestone.Platform.*        Midi               CoreMIDI, WinMM, ALSA adapters
```

`DependencyDirectionTests` enforces this. `Bluestone.Midi` and `Bluestone.Profiles` never reference
each other, and the domain references only the base class library.

Tests: `Bluestone.Tests.Unit` (fast, deterministic), `Bluestone.Tests.Integration` (real threads, files,
operating-system MIDI, and plugin worker processes), and `Bluestone.Tests.Ui` (Bluestone's own app and
main window on Avalonia's headless platform: real views, bindings, and UI thread, with no display).

The UI tests send mouse and keyboard input through the headless platform and check the project and view
models afterwards. They cover the inspector's Track, Routing, Voice, Devices, Connections, Racks, and Mixer
sections (including a refused connection and its message), dragging, resizing, and renaming clips, drawing,
selecting, and moving notes in the piano roll, the keyboard shortcuts (undo and redo, delete, play and stop,
mute and solo, copy and paste), and the Plugins view. Frames are saved to `bin/.../frames/` for looking at.
`Avalonia.Headless.XUnit` targets an older xUnit than the repository's, so the tests drive
`HeadlessUnitTestSession` directly. The headless platform has no macOS menu bar, so the window under test
uses its own menu and key bindings, as on Windows and Linux; one test checks the macOS menu's items and
gestures directly.

### Where does this belong?

| You are adding… | Put it in |
| --------------- | --------- |
| A musical concept or edit (a new event kind, a quantize rule) | `Bluestone.Domain`, with commands in `Bluestone.Application/Editing` |
| How a channel event becomes MIDI 1.0 messages (bank and program, value resolution) | `Bluestone.Midi/Wire/Midi1Encoder.cs` |
| How MIDI 1.0 messages from files or input become channel events | `Bluestone.Midi/Wire/Midi1Decoder.cs` |
| Reading or writing `.mid` files | `Bluestone.Midi/Files` |
| Recognizing a SysEx family (Universal, XG, GS, another manufacturer) | `Bluestone.Midi/SysEx` |
| Facts about one instrument (banks, voices, drum names, setup SysEx, parameter maps) | a JSON profile in `/profiles`; the loader is `Bluestone.Profiles` |
| How automation of an instrument's parameter becomes CC, RPN, NRPN, or SysEx | the profile's `parameterMap`; translation in `Bluestone.Application/Routing/HardwareAutomation.cs` |
| An operating-system MIDI API | `Bluestone.Platform.<OS>`, implementing `IMidiEndpointProvider` |
| The project file format or a migration | `Bluestone.Infrastructure/Projects` |
| A built-in device | its definition in `Bluestone.Domain/Devices/BuiltInDevices.cs`, its processor in `Bluestone.Signal/BuiltIn`, registered in `DeviceCatalog.BuiltIn` |
| A routing rule (what may connect to what) | `Bluestone.Domain/Routing/SignalRoutingValidator.cs` |
| How processed events reach the plan | `Bluestone.Signal/SignalGraph.cs`, then `Bluestone.Application/Routing/PlaybackRouting.cs` |
| An edit to chains, connections, instruments, the mixer, or roles | `Bluestone.Application/Editing` (`DeviceCommands`, `RoutingCommands`, `TrackRoleCommands`) |
| Audio processing on the real-time path (a node, the mixer, metering, decoding, resampling) | `Bluestone.Audio` |
| How a project becomes audio (clips, chains, connections, mixer) | `Bluestone.Application/Audio/ProjectAudioGraph.cs` |
| Audio device access (a backend, a stream setting) | `native/crates/bluestone_audio` (Rust), its C ABI in `Bluestone.Audio/Devices/NativeMethods.cs` |
| How something is shown or edited | `Bluestone.Presentation` (logic), `Bluestone.Desktop` (views) |

### Abstractions

Interfaces exist only where something is actually substituted:

- `IMidiEndpointProvider`, `IMidiOutput`, `IMidiInput`: each platform adapter, plus the loopback test double.
- `IMonotonicClock`: the real clock, and a virtual clock for deterministic tests.
- `IProjectCommand`, `IProjectMigration`: undoable edits and format upgrades.
- `ISignalProcessor`: each built-in device's processor (and test processors).
- `IChainObserver`: what the signal graph needs from inside a chain (taps, instrument inputs).
- `IUserInteraction`, `IUiDispatcher`: keep the UI framework out of the view models.
- `IAudioRenderer`: what a device stream calls (the audio engine, a test renderer); `IAudioTimeline`: the transport the
  audio engine follows (the MIDI playback engine, a test timeline).

Everything else is concrete. SysEx dialects, for example, are static classes chosen by a `switch`
on the manufacturer ID, because nothing needs to swap them at run time.

## MIDI strategy

```text
                        BLUESTONE
                            │
                    Sequencer domain
                            │
                     MIDI semantics
                            │
               ┌────────────┴────────────┐
               │                         │
            MIDI 1.0                  MIDI 2.0  (planned)
               │                         │
         byte encoding                  UMP
               │                         │
               └────────────┬────────────┘
                            │
                     MIDI transport
                            │
                         Devices


       SysEx interpretation is orthogonal:

                       Raw SysEx
                           │
               ┌───────────┼───────────┐
               │           │           │
           Universal      XG          GS
                        Yamaha       Roland
               │           │           │
               └───────────┴───────────┘
                           │
                  Semantic metadata
```

### MIDI 1.0: first-class

MIDI 1.0 is fully supported and is not a compatibility layer. Hardware, Standard MIDI Files, and
XG-era instruments all depend on it. Its specific concerns stay in `Bluestone.Midi`:

- The domain stores channel events, not messages: `NoteEvent`, `NoteOffEvent`, `ControllerEvent`,
  `ProgramEvent`, `PitchBendEvent`, `ChannelPressureEvent`, and `PolyPressureEvent`. Controller,
  pitch bend, and pressure values are `ControlValue`s at MIDI 2.0 resolution (32 bits). MIDI 1.0
  values are scaled up with the MIDI 2.0 min-center-max rule, so they scale back down exactly.
- `Wire/Midi1Encoder` turns events into messages. For example, a `ProgramEvent` (bank MSB, bank
  LSB, program) becomes CC 0, CC 32, then a program change, a `NoteEvent` (a note with a duration)
  becomes a note-on and a note-off, and values are reduced to 7 bits, or 14 for pitch bend.
- `Wire/Midi1Decoder` is the inverse, used by SMF import and recording. Bank selects on the same
  channel and tick as a program change are folded into it. Anywhere else they stay controller
  events, so what is sent is unchanged. `MidiOneOutputGoldenTests` checks that the shipped samples
  play and export exactly the same MIDI 1.0 bytes.
- `Wire/ChannelMessage` is the compact MIDI 1.0 message (status byte plus data bytes). It is used
  in the playback plan and the real-time engine, which only send MIDI 1.0 today.
- `Wire/MidiWire` validates complete messages, and `Wire/MidiStreamParser` handles running status,
  realtime bytes inside other messages, and SysEx split across driver packets.
- `Files/` reads and writes SMF with running status, and pairs notes, including a note-on with
  velocity 0 counting as a release.

### MIDI 2.0 and UMP: planned

Bluestone does not send MIDI 2.0 yet. The design leaves room for it:

- MIDI 2.0 extends MIDI 1.0. UMP (Universal MIDI Packet) is a packet format that carries both MIDI
  1.0 and MIDI 2.0 protocol messages. It is a wire representation, not Bluestone's model.
- A MIDI 2.0 encoder will sit beside `Midi1Encoder` (in `Bluestone.Midi/Ump/`), producing UMP from the
  same semantic operations. In MIDI 2.0 some operations become atomic: a `ProgramSelection` is one
  program change with a bank, not three messages.
- Transport gets a separate UMP send path with its own capability flag (ADR 0005). Platform
  adapters for Windows MIDI Services and CoreMIDI's event lists are natural UMP endpoints.
- The playback plan compiler encodes every event today with `Midi1Encoder`. It will pick the
  encoder for each output from its capabilities.
- The domain already holds controller, pitch bend, and pressure values at MIDI 2.0 resolution, and
  a program selection as one event. MIDI 2.0 also offers 16-bit velocity, per-note controllers, and
  per-note pitch bend, which Bluestone does not model yet. Some of this has no lossless MIDI 1.0 form.
  The MIDI 1.0 encoder already reduces values to 7 or 14 bits, and should report what was lost, in
  the same way SMF export reports today, once such data can be created.

### MIDI-CI: future

MIDI Capability Inquiry (Discovery, Profiles, Property Exchange) could let Bluestone ask a modern
device for its capabilities, program lists, and controller names, instead of relying only on
shipped profiles. It needs a paired input and output on one device, so it will sit above the
endpoint contracts, and what it learns will feed device knowledge, not transport. Legacy
instruments do not answer MIDI-CI, so data profiles and the XG and GS interpreters stay essential.

### SysEx: always preserved, optionally understood

SysEx is handled at two levels:

1. **Lossless opaque transport.** A complete message is a `SysExMessage` (`F0`…`F7`, validated
   7-bit data, at most 1 MiB) in a `SysExEvent`. Bytes that are not one complete message (split
   packets, `F7` escapes, malformed SysEx) are kept as a `RawMidiEvent`. SMF import and export,
   project files, and playback carry both byte for byte. Unknown SysEx is not invalid SysEx.
2. **Semantic interpretation.** `SysExInterpreter.Interpret` recognizes Universal SysEx (`7E`,
   `7F`), Yamaha XG (`43`, model `4C`), and Roland GS (`41`, model `42`). It returns `null` for
   anything else. Interpretations are worked out when needed and never stored, so they can never
   replace or corrupt the original bytes. A message with a bad checksum is reported as such, not
   repaired. Today the MIDI monitor and the event list use them to name messages.

**Universal SysEx** (GM System On/Off, GM2 On, Identity Request/Reply, master volume, balance,
and tuning) is kept separate from manufacturer SysEx and does not depend on Yamaha or Roland.

**XG** (`XgDialect`) recognizes parameter changes, including XG System On and All Parameter Reset.
It sorts them into system, reverb, chorus, variation, insertion, multi part (with the part number),
and drum setup (with the note), and it recognizes bulk dumps with their checksum. Parameter names
within each block, model-specific extensions such as the QY100's, and requests are future work.

**GS** (`GsDialect`) recognizes data set (DT1) messages: GS Reset, system mode, system, reverb,
chorus, common, part (using GS's own block-to-part numbering), and drum map. It checks the Roland
checksum. `GsDialect.Checksum` will also be used when Bluestone renders GS messages.

XG and GS each have their own model. They share only the `SysExInterpretation` base and the
dispatcher. To add a dialect, add a file to `Bluestone.Midi/SysEx`, add a case to the dispatch, and
add tests in `Bluestone.Tests.Unit/Midi/SysEx`.

Dialect interpreters understand a protocol family in code. Device profiles describe one instrument
as data. A profile's `protocols: ["xg"]` declares compatibility, but the interpreter reads XG
messages whichever profile a track is routed to.

### Device knowledge is not transport

| Concern | Examples | Lives in |
| ------- | -------- | -------- |
| Transport | CoreMIDI, WinMM, ALSA, future Windows MIDI Services | `Bluestone.Platform.*` |
| Protocol | MIDI 1.0 bytes, SMF, future UMP | `Bluestone.Midi` |
| Dialect and device semantics | Universal, XG, GS, device profiles | `Bluestone.Midi/SysEx`, `Bluestone.Profiles`, `/profiles` |

An adapter never interprets what it carries, and an interpreter never cares where the bytes came
from (ADR 0005, 0007, 0008).

## Tracks, devices, and routing

```text
Arrangement
  Track (role: instrument, audio, hybrid, effect, group)
    ├── clips (NoteClip, AudioClip)        musical content; never owns an endpoint or a device
    ├── automation lanes                    MIDI targets, device parameters by DeviceId, or instrument part parameters
    └── device chain (optional, one)        Arpeggiator → Transpose → Synth → Filter

Project
  ├── racks             free-standing chains that several tracks route into (one instrument instance)
  ├── instruments       external MIDI instruments: profile, operating mode, ports → endpoints
  ├── connections       the one routing model: events or audio, source → destination, channel mapping
  └── mixer             channels and the master, independent of tracks
```

### Tracks and clips

A track has one type and a role (ADR 0021). The role says what the track is for; it is stored and is
widened, never narrowed, to fit content, and changing it never discards anything. Clips decide what a
track holds (ADR 0020): `NoteClip` holds events relative to its content origin and shows a window of
them, so trimming loses nothing; `AudioClip` holds a reference to an audio file; the audio engine plays it
from disk (ADR 0032). Clips on a track never overlap. `Track.ArrangedEvents` is what
the note clips play. `TrackRendering` adds MIDI automation lanes, sampled into channel events, and
returns device automation separately as parameter changes.

A track can be in a group track, and groups nest (ADR 0030). Putting a track in a group is one command
that also routes it: its own outgoing connections are parked in the project and replaced by a connection
into the group, and taking it out puts back those that still work. A group's mute and solo reach its
members (`GroupTracks.Playing`: the nearest explicit state wins, and without groups the old rule holds).
A group can be folded, which hides its members in the track list and the arrangement and is saved.

### Devices and chains

A `DeviceDefinition` is what a device is (data); a `DeviceInstance` is one configured occurrence with a
stable ID, parameters, bypass, and plugin state; a `DeviceChain` is an ordered list with one owner, a
track or a rack (ADR 0022). Processing is sequential, and passthrough is managed by the host: a device
is given only the event classes it handles, and everything else (SysEx, controllers it does not
declare) goes around it and is merged back in canonical order. Only an explicit filter removes events.
Built-in devices run in `Bluestone.Signal` when the plan is compiled; plugin devices run in worker
processes, never in Bluestone's process. Presets are templates without identities: loading one creates
new devices.

### Routing

Every route is a `SignalConnection` in the project's one list (ADR 0023). A connection starts at a
track's output (the end of its chain), a rack's output, or a tap after one device, and ends at a track
or rack, a part of an external instrument (port plus channel), a mixer channel, or the master. A
connection's `ChannelMapping` filters, forces, or remaps channels; clips are never changed to reach a
different part. Validation rejects connections that cannot work and every feedback loop. The track
inspector's Output, Instrument, Channel, Transpose, and Voice fields are a view of this model
(`TrackOutputs`), so the inspector, the device strip's routing indicators, and the routing inspector
always agree.

```text
Piano ───┐                                           ┌──► MU2000 port A, ch 1..16
Strings ─┼──► rack "XG" [XG synth → Delay] ──audio──► mixer "XG" ──► master
Bass ────┘
Lead ──► [Arpeggiator → Transpose] ──► MU2000 port B, ch 4
                 └── tap ──► track "Harmony"
```

### From project to plan

`PlaybackRouting.Prepare` resolves each external instrument's profile and ports against what is
available, then `SignalGraph.Evaluate` renders every track (mute and solo apply to a track's own
content, and a group's to its members' content), runs every chain in dependency order, follows every event connection, and collects:
external-instrument part feeds, software-instrument feeds (merged from every source, which drive plugin
instruments in the audio engine), parameter feeds for devices that do not run in Bluestone's process, and diagnostics. Each part
with an available endpoint becomes a `PlanPart`; parts sharing an endpoint share an output slot. The
compiler encodes MIDI 1.0 at the edge and orders the plan by tick, phase, origin track, and index,
exactly as before the routing model existed, so old projects send the same bytes. The real-time engine
is unchanged (ADR 0006).

Live notes (audition, MIDI thru, the on-screen keyboard, the metronome) follow the track's live route:
the nearest playable instrument through its connections, with the forced channel and the Transpose
devices on the way.

In the app, the inspector's Devices strip edits the selected track's chain, or a rack's chain after
"Edit" in the Racks section; Connections follows whichever chain the strip shows. The Mixer section
edits channels, their outputs, and the master gain; they are heard while audio runs (ADR 0032). Every edit is a command, and one that would add a routing error is refused with a message.
Consecutive edits with the same merge key within a second (a slider drag) are one undo step.

### Automation

Automation is separate from signal flow (ADR 0024). A MIDI lane targets a controller, pitch bend, or
pressure on a channel, and becomes events. A device lane targets a parameter of a device by ID: it does
not pass through the devices before it, it is never encoded as MIDI, and reordering or moving devices
does not retarget it. Built-in processors apply it at its tick; other devices receive it as a parameter
feed for their worker.

A hardware lane targets a parameter of an external instrument's part (ADR 0031): the instrument, its port, the
channel, and the parameter's key in the instrument's profile. The profile's parameter map says how the parameter
is set: a 7- or 14-bit controller, an RPN or NRPN (followed by the RPN null unless the profile says otherwise), or
a SysEx template with the value (whole or in bit slices such as nibbles), the channel, a device number byte, and a
Roland or Yamaha checksum. When playback is prepared, `HardwareAutomation` samples the lane, scales its normalized
values to the parameter's range, and sends a value only when it changes and at most once per 20 ms per parameter
(a change that comes sooner waits, so a ramp still ends on its last value). The MIDI goes to the part's endpoint as
a `ParameterPart`, ordered like everything else by tick, phase, the lane's track, and index, after that track's own
events at the same tick and phase. On play, seek, and loop wrap the latest RPN, NRPN, or SysEx change of each
parameter is resent whole; controller mappings are chased as controllers. MIDI file export writes the same MIDI on
the lane's track. A lane whose instrument, port, profile, parameter, or endpoint is missing is kept and reported.
There is no "external instrument device": the target names the part, as connections do.

### Plugin workers

Third-party plugins are hosted in separate worker processes (ADR 0025), never loaded into Bluestone's own
process. Process isolation is crash isolation, not a security sandbox. When the plan is compiled, a
plugin MIDI effect runs in its worker over the arrangement and its output joins the chain like a
built-in device's (ADR 0028). Modules are scanned in their own scanner processes from a list of plugin
folders; the Plugins view (Window ▸ Plugins… on macOS, File ▸ Plugins… elsewhere) rescans them and retries
quarantined modules, and plugins it finds join the device catalog without a restart. See
[docs/plugin-hosting.md](plugin-hosting.md).

## Known compromises

These are deliberate for now and are where MIDI 2.0 work will start:

- **Notes are MIDI 1.0-shaped.** Note velocity is 7-bit (MIDI 2.0 has 16 bits), and notes have no
  per-note attributes, controllers, or pitch bend. See [Planned work](#planned-work).
- **RPN and NRPN stay controller sequences** (CC 101/100 or 99/98, then 6 and 38). Folding them into
  single parameter events cannot always reproduce the original bytes. For example, a message that
  sends only CC 6 leaves the device's previous LSB in place. A MIDI 2.0 encoder can translate the
  sequences as it sends them, as the MIDI 2.0 translation rules describe.
- **Sixteen channels.** `MidiChannel` is 1 to 16. UMP adds 16 groups. The group belongs to an external
  instrument's port and its endpoint, not to the music or to connections.
- **Audio engine limits (ADR 0032).** Audio clips, plugin instruments and effects, audio connections, and the
  mixer sound while an audio device is open, but: plugin latency is reported, not compensated; there are no sends,
  clip gain, or fades (not in the domain); only WAVE and AIFF files play; built-in devices make no sound; plugin
  instances run at 48 kHz, so another device rate leaves them silent; the default render-ahead queue adds 21 ms of
  output latency (measured need: .NET garbage collection pauses). Device output has been heard on macOS only.
- **Plugin MIDI effects are rendered, not played live.** When the plan is compiled, each plugin MIDI effect
  runs in its own worker over the arrangement (ADR 0028), so its output is routed downstream. A worker that
  is gone or hung leaves the events unchanged, with a diagnostic. Live input does not pass through plugins.
- **Live notes apply only Transpose devices** from the chains on a track's live route; other devices
  (an arpeggiator, say) shape played-back notes but not live input.
- **The playback plan is MIDI 1.0.** `PlaybackPlan` and `ChaseState` hold encoded `ChannelMessage`s,
  so chasing controller state on seek is worked out in MIDI 1.0 terms. A MIDI 2.0 output will need
  its plan entries in UMP form.
- **`EventPhase.BankSelect`** (ADR 0003) now applies only to bank selects that are not part of a
  `ProgramEvent`. A `ProgramEvent` sends its bank select messages in the program change phase,
  directly before its program change.

## Planned work

### 16-bit velocity

Note and release velocity move to MIDI 2.0's 16 bits, the same way controller values did:

- `Velocity` holds 16 bits, scaled from MIDI 1.0 with the min-center-max rule, so every 7-bit
  velocity converts up and back down exactly. Imported songs keep playing byte for byte, and
  `MidiOneOutputGoldenTests` checks it.
- `Velocity.Value` is removed rather than given a new meaning. In its place come `ToSevenBit()`
  and a 16-bit accessor, so every use has to be revisited. The MIDI 1.0 side (`ChannelMessage`,
  the playback plan, the metronome) takes a plain 7-bit value, and `Midi1Encoder` converts.
- A note-on is always sent to MIDI 1.0 with velocity 1 or more, because 0 there means note-off. A
  small 16-bit velocity that scales down to 0 is sent as 1.
- Bluestone notes keep requiring a velocity above zero. MIDI 2.0 allows a silent note-on at
  velocity 0, but nothing can create one yet.
- Editing shows and steps velocity in familiar 1–127 units (velocity lane, Alt + ↑ / ↓, event
  list, inspector, computer keyboard) and stores the exact scaled value. Nudging a
  higher-resolution velocity snaps it to the 7-bit grid.
- Velocity edits (scale, compress, humanize, ramps) keep calculating in 7 bits at first, so
  results match today exactly. Calculating in 16 bits is a separate, deliberate change, because
  results would then round differently.
- Release velocity gets the same treatment, for `NoteEvent` and `NoteOffEvent`.
- The project file stores 16-bit velocities. No migration is needed while there are no saved
  projects to preserve.

Not included: MIDI 2.0 note attributes (articulation, per-note pitch) and per-note controllers.
They follow once a MIDI 2.0 endpoint can play them.

### Controller ranges and steps

MIDI 2.0 messages always carry a controller's full range. Which part is meaningful is device
knowledge. MIDI-CI Property Exchange describes it per controller (the Controller Resources
specification) in one of two ways. `stepCount` quantizes the full range into N steps, and is the
preferred method. `minMax` marks a sub-range, and is intended for legacy products. An entry uses
only one of them.

Bluestone keeps events at full resolution and treats ranges and steps as profile data.
`ProfileController` (number, name, default) can gain optional `steps` or `min`/`max`. These would
be written by hand for legacy instruments or filled in from MIDI-CI later. Controller lanes would
then snap drawing to the steps and show values in the device's own units. Nothing supplies this
data yet, so it waits.

## Deferred

Built as boundaries and tested where cheap, but not finished:

- **Audio engine, remaining**: plugin delay compensation across the graph (latencies are reported per device),
  sends, audio input and recording, compressed audio formats, a mixer console view with meters (the meters exist),
  and recovery from a lost device beyond reporting it.
- **Third-party plugins beyond the first slice**: VST3 and CLAP load, process, keep state, recover, and show editors
  (ADR 0033, docs/plugin-hosting.md), but editors on Windows and Linux are not verified on hardware, editors are
  separate windows (not embedded), and there are no second-architecture workers, SysEx to native plugins, note
  expressions, or MPE.
- **MIDI 2.0** transport and UMP, **MPE**, and per-note controllers.
- **Visual node-graph editing** and **feedback routing** (loops are refused).
- **Operating-system sandboxing** of plugin workers.
- **Groups**: drag and drop into a group, and a folded group's lane summarizing its members' clips. Grouping,
  routing members into their group, group mute and solo, and folding work (ADR 0030).

## Decisions at a glance

| Decision | Recorded in |
| -------- | ----------- |
| The domain is protocol-independent; MIDI sits at the edges | ADR 0018 |
| MIDI 1.0 remains first-class | ADR 0018 |
| MIDI 2.0/UMP is a planned protocol target | ADR 0018, ADR 0005 |
| UMP is not the domain model | ADR 0018 |
| Raw SysEx is preserved losslessly | ADR 0019 |
| SysEx interpretation is optional, derived, and extensible | ADR 0019 |
| XG and GS are dialect interpreters, not core MIDI concepts | ADR 0019 |
| Device semantics are independent of platform transport | ADR 0005, ADR 0007, ADR 0008 |
| Simplicity over speculative abstraction | ADR 0018 |
| Track roles are stored and reconciled; conversions never discard | ADR 0021 |
| Device chains with stable identities; passthrough by event class | ADR 0022 |
| One routing model of connections; external instruments and mixer channels are entities | ADR 0023 (supersedes 0008) |
| Device automation targets parameters, separate from signal flow | ADR 0024 |
| Plugins run out of process; isolation is not a sandbox | ADR 0025 |
| C# first; Rust only for measured or native-interface needs | ADR 0026 |
| Project format 4 and its migration | ADR 0027 |
| Plugin MIDI effects run in their worker when the plan is compiled | ADR 0028 |
| A group's mute and solo reach its members; joining a group routes into it | ADR 0030 |
| Hardware parameters are automated through profile parameter maps, translated at plan compile | ADR 0031 |
| Audio: cpal output in Rust, the graph in C# rendered ahead into a ring, one transport | ADR 0032 |
| VST3 and CLAP through a Rust plugin host loaded only in the worker | ADR 0033 |

## Native technology

C# is the default for all code (ADR 0026). Rust is used for a component only when a measurement shows
managed code cannot meet a real-time or throughput requirement after tuning, or when a native interface
(such as plugin binaries) requires it; it replaces C or C++ for new Bluestone-owned native code. C and C++
appear only as third-party libraries, vendor SDK requirements, or thin ABI shims.

Rust code lives in a Cargo workspace, `native/`, one crate per component (`native/crates/*`), each a cdylib with a
small, versioned C ABI; `dotnet build` builds the crates a project names (`src/Native.targets`) and copies the
library beside the assemblies. The components:

- `bluestone_audio` (ADR 0032): audio device output through cpal, and the device callback, which only copies from a
  ring because the measurement showed a .NET collection holding a managed callback past its deadline. The render
  graph stays in C#.
- `bluestone_plugin_host` (ADR 0033): loads VST3 and CLAP plugin binaries, which is ADR 0026's native-interface
  reason. Only `Bluestone.PluginWorker` loads it, so no plugin code ever runs in Bluestone's process. The data plane,
  supervision, and everything else in plugin hosting stay C#.
- `bluestone_test_plugins`: small VST3 and CLAP plugins for the tests.

## Acceptance scenarios and their tests

| # | Scenario | Tests |
| - | -------- | ----- |
| 1 | Four tracks feed one shared XG synth with one audio output | `AcceptanceScenarioTests.Scenario1_*` |
| 2 | A track's MIDI effect transforms notes before a synth | `SignalGraphTests.Scenario2_*` |
| 3 | Events a MIDI effect generates reach another track | `SignalGraphTests.Scenario3_*` |
| 4 | Several tracks share one processing chain | `SignalGraphTests.Scenario4_*` |
| 5 | Automation targets MIDI FX, synth, and filter; reordering keeps it | `AcceptanceScenarioTests.Scenario5_*`, `DeviceAndRoutingCommandsTests.Scenario14_*` |
| 6 | A note processor keeps XG SysEx and controllers | `SignalGraphTests.Scenario6_*` |
| 7 | One preset on several tracks creates independent devices | `DeviceAndRoutingCommandsTests.Scenario7_*`, `ChainPresetSerializerTests` |
| 8 | A track plays an external Yamaha instrument on its port and channel | `AcceptanceScenarioTests.Scenario8_*` |
| 9 | Audio on an instrument track makes it hybrid without loss | `DeviceAndRoutingCommandsTests.Scenario9_*` |
| 10 | Invalid connections and feedback are detected | `SignalGraphTests.Scenario10_*`, `DeviceAndRoutingCommandsTests.Scenario10_*`, `SignalRoutingValidatorTests` |
| 11–13, 15 | Plugin crash isolation, state recovery, several workers, scanning failure | see [plugin-hosting.md](plugin-hosting.md) |
| 14 | A moved device keeps its identity and automation; routing is validated | `DeviceAndRoutingCommandsTests.Scenario14_*` |
