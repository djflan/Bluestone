# Bluestone architecture

Bluestone models music and musical intent. Tracks organize music, clips contain it (notes or audio),
devices process signals in chains, automation targets device and instrument parameters, and one list
of connections routes signals to other tracks, shared racks, external instruments, and mixer channels.
An audio engine plays audio clips, plugin instruments and effects, and the mixer; third-party plugins
run in separate worker processes. MIDI 1.0, MIDI 2.0/UMP, SysEx dialects, operating-system MIDI APIs,
audio devices, and plugin binaries sit at the edges. This page shows where things live and why. The
decisions behind it are in [docs/adr/](adr/), in particular ADRs
[0018](adr/0018-midi-protocol-layering.md), [0019](adr/0019-sysex-preservation-and-interpretation.md),
and [0020](adr/0020-hybrid-tracks-clips-and-automation.md) to
[0035](adr/0035-live-input-and-delay-compensation.md). What is left to do is in [roadmap.md](roadmap.md).

## Layers

```text
UI / Application         Bluestone.Desktop, Bluestone.Presentation, Bluestone.Application
       ↓
Sequencer / Domain       Bluestone.Domain, Bluestone.Profiles
       ↓
Signal processing        Bluestone.Signal: device chains, the routing graph, live processors
       ↓
Playback (MIDI)          Bluestone.Playback: prepared plans, the real-time engine
       ↓
MIDI semantics           channel events (NoteEvent, ControllerEvent, ProgramEvent, …) and ControlValue
       ↓
Protocol encoding        Bluestone.Midi: Wire (MIDI 1.0 bytes), Files (SMF), SysEx (dialects)
       ↓
Platform transport       Bluestone.Midi.Endpoints contracts; Bluestone.Platform.CoreMidi / Windows / Alsa

Audio                    Bluestone.Audio: render graph, mixer strips, clips, plugin nodes;
                         device output in Rust (bluestone_audio, cpal)
Plugin hosting           Bluestone.Plugins (main process: workers, supervision, scanning)
                         Bluestone.PluginWorker (worker process; VST3 and CLAP through bluestone_plugin_host, Rust)
                         Bluestone.Plugins.Protocol (what both sides speak)
```

The MIDI layers depend downward. Audio and plugin hosting are beside them and know nothing of the
domain: `Bluestone.Application` turns a project into an audio graph (`ProjectAudioGraph`) and into
plugin requests (`PluginDeviceHost`).

These are conceptual boundaries, not one assembly each. A layer gets its own project only when that
buys something concrete: a dependency rule that must be enforced, an operating-system-specific
build, a separate process, or a framework that must stay out of testable code. Project persistence
lives in `Bluestone.Infrastructure` for the same reason: it may depend on the domain and nothing else.

### Projects and their references

Every project in `src/dotnet/Bluestone.slnx`, with its project references:

```text
Bluestone.Domain            (none)             sequence, tracks, roles, groups, clips, automation, events, time,
                                               MIDI values, devices and chains, external instruments, connections, mixer
Bluestone.Midi              Domain             MIDI 1.0 encoding, SMF, SysEx dialects, endpoint contracts, clocks
Bluestone.Profiles          Domain             device profiles and parameter maps: what an instrument understands
Bluestone.Signal            Domain             chain runner, built-in processors and their live forms, device catalog,
                                               signal graph
Bluestone.Infrastructure    Domain             project files, chain preset files, migrations
Bluestone.Playback          Domain, Midi, Signal   real-time MIDI engine, prepared plans
Bluestone.Plugins.Protocol  (none)             plugin control-plane frames and the shared-memory block exchange
Bluestone.Plugins           Plugins.Protocol   worker launch and supervision, crash recovery, hosting modes, scanning
Bluestone.PluginWorker      Plugins.Protocol   the worker executable that hosts plugins (a separate process)
Bluestone.Audio             Plugins, Plugins.Protocol   render graph, mixer strips and meters, audio file decoding,
                                               sample-rate conversion, disk streaming, plugin nodes, device output
Bluestone.Application       Audio, Domain, Infrastructure, Midi, Playback, Plugins, Profiles, Signal
                                               sessions, editing commands, recording, routing resolution, hardware
                                               automation, live input, the plugin bridge, the project's audio graph
Bluestone.Presentation      Application        view models, no UI framework
Bluestone.Platform.CoreMidi Midi               CoreMIDI adapter (macOS)
Bluestone.Platform.Windows  Midi               WinMM adapter
Bluestone.Platform.Alsa     Midi               ALSA adapter (Linux)
Bluestone.Desktop           Presentation, Platform.Alsa, Platform.CoreMidi, Platform.Windows
                                               Avalonia views, platform selection; PluginWorker is built and copied
                                               beside it, never loaded into it
Bluestone.Benchmarks        Application, Audio, Infrastructure, Platform.CoreMidi, Platform.Windows, Playback, Profiles
                                               performance measurements and probes (ADR 0012)
Bluestone.Tests.Unit        Application, Audio, Domain, Infrastructure, Midi, Playback, PluginWorker, Plugins,
                            Plugins.Protocol, Presentation, Profiles, Signal
Bluestone.Tests.Integration every project except Desktop and Benchmarks
Bluestone.Tests.Ui          Desktop; PluginWorker copied beside it, never loaded
```

The Rust crates in `src/native/crates/` are built by `dotnet build` through `src/dotnet/Native.targets`:

```text
bluestone_audio             built by Bluestone.Audio, which loads it in Bluestone's process (Devices/NativeMethods.cs)
bluestone_plugin_host       built by Bluestone.PluginWorker and loaded only there, never in Bluestone's process
bluestone_test_plugins      built by Bluestone.Tests.Integration and packaged as a VST3 bundle and a CLAP module
                            (NativeTestPlugins.targets); loaded by worker processes in tests
```

`DependencyDirectionTests` enforces the direction. It checks compiled assemblies, so it also allows
what a project uses through a transitive reference (Presentation may use Audio, Domain, Infrastructure,
Midi, Playback, Plugins, Profiles, and Signal, but not Plugins.Protocol or a platform adapter). It also
checks that the domain and `Bluestone.Plugins.Protocol` reference only the base class library, that
Presentation does not reference Avalonia, that `Bluestone.Plugins` does not reference the worker, and
that only `Bluestone.PluginWorker` builds or imports `bluestone_plugin_host`. `Bluestone.Midi` and
`Bluestone.Profiles` never reference each other.

### Tests

`Bluestone.Tests.Unit` is fast and deterministic. `Bluestone.Tests.Integration` uses real threads,
files, operating-system MIDI, plugin worker and scanner processes, and the Rust test plugins.
`Bluestone.Tests.Ui` runs Bluestone's own app and main window on Avalonia's headless platform: real
views, bindings, and UI thread, with no display.

The UI tests send mouse and keyboard input through the headless platform and check the project and view
models afterwards. They cover the inspector's sections (track, external MIDI and voice, connections
including a refused one and its message, racks, the add-lane menu with instrument parameters), clips
(dragging, resizing, renaming), the piano roll (drawing, selecting, moving notes), grouping, folding, and
muting a group, the keyboard shortcuts, Settings (audio, plugins and hosting), the DAW-first track path (new track,
instrument, audio path), a plugin MIDI effect whose
worker dies and restarts, and the mixer console (a fader dragged and undone, a send added, meters fed from
a meter source). Frames are saved to `bin/.../frames/` for looking at. `Avalonia.Headless.XUnit` targets
an older xUnit than the repository's, so the tests drive `HeadlessUnitTestSession` directly. The headless
platform has no macOS menu bar, so the window under test uses its own menu and key bindings, as on Windows
and Linux; separate tests check the macOS menu's items, gestures, and Window menu directly.

### Where does this belong?

| You are adding… | Put it in |
| --------------- | --------- |
| A musical concept or edit (a new event kind, a quantize rule) | `Bluestone.Domain`, with commands in `Bluestone.Application/Editing` |
| How a channel event becomes MIDI 1.0 messages (bank and program, value resolution) | `Bluestone.Midi/Wire/Midi1Encoder.cs` |
| How MIDI 1.0 messages from files or input become channel events | `Bluestone.Midi/Wire/Midi1Decoder.cs` |
| Reading or writing `.mid` files | `Bluestone.Midi/Files` |
| Recognizing a SysEx family (Universal, XG, GS, another manufacturer) | `Bluestone.Midi/SysEx` |
| Facts about one instrument (banks, voices, drum names, setup SysEx) | a JSON profile in `/profiles`; the loader is `Bluestone.Profiles` |
| A hardware parameter automation can set (CC, RPN, NRPN, SysEx) | the profile's `parameterMap` ([profiles.md](profiles.md)); the loader is `Bluestone.Profiles/ProfileLoader.ParameterMap.cs`; translation is `Bluestone.Application/Routing/HardwareAutomation.cs` |
| An operating-system MIDI API | `Bluestone.Platform.<OS>`, implementing `IMidiEndpointProvider` |
| The project file format or a migration | `Bluestone.Infrastructure/Projects` |
| A built-in device | its definition in `Bluestone.Domain/Devices/BuiltInDevices.cs`, its processor in `Bluestone.Signal/BuiltIn`, its live processor in `Bluestone.Signal/Live`, both registered in `DeviceCatalog.BuiltIn` |
| A routing rule (what may connect to what) | `Bluestone.Domain/Routing/SignalRoutingValidator.cs` |
| How processed events reach the plan | `Bluestone.Signal/SignalGraph.cs`, then `Bluestone.Application/Routing/PlaybackRouting.cs` |
| An edit to chains, connections, instruments, the mixer, or roles | `Bluestone.Application/Editing` (`DeviceCommands`, `RoutingCommands`, `TrackRoleCommands`) |
| Group tracks: membership, mute and solo, folding | `Bluestone.Domain/Sequencing/GroupTracks.cs`; edits in `Bluestone.Application/Editing/GroupCommands.cs` |
| A mixer concept (channels, outputs, sends) | `Bluestone.Domain/Mixing/Mixer.cs`; edits in `RoutingCommands`; strips and meters in `Bluestone.Audio/MixerStrip.cs` |
| Audio processing on the real-time path (a node, metering, decoding, resampling, delay compensation) | `Bluestone.Audio` |
| How a project becomes audio (clips, chains, connections, mixer) | `Bluestone.Application/Audio/ProjectAudioGraph.cs`; the device and engine lifecycle in `AudioPlayback.cs` |
| Audio device access (a backend, a stream setting) | `src/native/crates/bluestone_audio` (Rust), its C ABI in `Bluestone.Audio/Devices/NativeMethods.cs` |
| How live input finds and plays its route | `Bluestone.Application/Live` (`LiveRoutePlan`, `LiveInput`) |
| How plugin devices are created, restarted, and moved between workers | `Bluestone.Application/Plugins/PluginDeviceHost.cs` |
| Hosting modes (how plugins share workers) | the user's settings in `Bluestone.Application/Plugins/PluginHostingSettings.cs`; worker keys in `Bluestone.Plugins` |
| Worker processes, supervision, scanning and quarantine | `Bluestone.Plugins` (`Workers`, `Scanning`); the plugin folders in `Bluestone.Application/Plugins/PluginLibrary.cs` |
| What a worker does with a plugin | `Bluestone.PluginWorker` (`IHostedPlugin`) |
| Loading VST 3 or CLAP binaries, plugin editor windows | `src/native/crates/bluestone_plugin_host` (Rust), its C ABI in `Bluestone.PluginWorker/Native` |
| How something is shown or edited | `Bluestone.Presentation` (logic), `Bluestone.Desktop` (views) |

### Abstractions

Interfaces exist where an implementation is substituted (per platform, or in tests) or where a layer
must call code it cannot reference:

- `IMidiEndpointProvider`, `IMidiOutput`, `IMidiInput`: each platform adapter, plus the loopback test double.
- `IMonotonicClock`: the system clock, and a virtual clock for deterministic tests.
- `IProjectCommand`, `IProjectMigration`: undoable edits and format upgrades.
- `ISignalProcessor`: each built-in device's processor (and test processors); `IReportingProcessor` is the optional
  report some of them give (notes the arpeggiator or transpose had to drop). `ILiveProcessor`: each built-in
  device's live form, a plugin MIDI effect on a live route, and a connection's forced channel.
- `IChainObserver`: what the signal graph needs from inside a chain (taps, instrument inputs).
- `IOutOfProcessRenderer`: runs plugin MIDI effects when the plan is compiled; the signal layer cannot reference
  the plugin bridge that implements it.
- `ILiveEventSource`: live input into a plugin instrument's node; the audio layer cannot reference the application
  that implements it.
- `IAudioRenderer`: what a device stream calls (the audio engine, a test renderer). `IAudioTimeline`: the transport
  the audio engine follows (the MIDI playback engine, a test timeline). `IAudioBackend` and `IAudioOutput`: where
  output streams come from (the operating system's devices through cpal, or a test backend whose devices come and
  go). `IStreamedSource` (internal): what the disk-streamer thread may call on a node that reads files.
- `IPluginScanner`: scanning in scanner processes, or a fake in view-model tests. `IHostedPlugin` (internal to
  the worker): a reference plugin or a VST 3 or CLAP plugin in the native host.
- `IUserInteraction`, `IUiDispatcher`: keep the UI framework out of the view models.

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
            MIDI 1.0                  MIDI 2.0  (not built)
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
  in the playback plan and the real-time engine, which send only MIDI 1.0.
- `Wire/MidiWire` validates complete messages, and `Wire/MidiStreamParser` handles running status,
  realtime bytes inside other messages, and SysEx split across driver packets.
- `Files/` reads and writes SMF with running status, and pairs notes, including a note-on with
  velocity 0 counting as a release ([midi-files.md](midi-files.md)).

### MIDI 2.0 and UMP: designed for, not built

Bluestone does not send MIDI 2.0. The design leaves room for it:

- MIDI 2.0 extends MIDI 1.0. UMP (Universal MIDI Packet) is a packet format that carries both MIDI
  1.0 and MIDI 2.0 protocol messages. It is a wire representation, not Bluestone's model.
- A MIDI 2.0 encoder would sit beside `Midi1Encoder` (in `Bluestone.Midi/Ump/`), producing UMP from the
  same semantic operations. In MIDI 2.0 some operations become atomic: a `ProgramSelection` is one
  program change with a bank, not three messages.
- Transport would get a separate UMP send path with its own capability flag (ADR 0005). Platform
  adapters for Windows MIDI Services and Core MIDI's event lists are natural UMP endpoints.
- The playback plan compiler encodes every event with `Midi1Encoder`. It would pick the encoder for
  each output from its capabilities.
- The domain already holds controller, pitch bend, and pressure values at MIDI 2.0 resolution, and
  a program selection as one event. MIDI 2.0 also offers 16-bit velocity, per-note controllers, and
  per-note pitch bend, which Bluestone does not model. Some of this has no lossless MIDI 1.0 form.
  The MIDI 1.0 encoder already reduces values to 7 or 14 bits, and should report what was lost, in
  the same way SMF export does, once such data can be created.

### MIDI-CI: future

MIDI Capability Inquiry (Discovery, Profiles, Property Exchange) could let Bluestone ask a modern
device for its capabilities, program lists, and controller names, instead of relying only on
shipped profiles. It needs a paired input and output on one device, so it would sit above the
endpoint contracts, and what it learns would feed device knowledge, not transport. Legacy
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
   repaired. The MIDI monitor and the event list use them to name messages.

**Universal SysEx** (GM System On/Off, GM2 On, Identity Request/Reply, master volume, balance,
and tuning) is kept separate from manufacturer SysEx and does not depend on Yamaha or Roland.

**XG** (`XgDialect`) recognizes parameter changes, including XG System On and All Parameter Reset.
It sorts them into system, reverb, chorus, variation, insertion, multi part (with the part number),
and drum setup (with the note), and it recognizes bulk dumps with their checksum. Parameter names
within each block, model-specific extensions such as the QY100's, and requests are future work.

**GS** (`GsDialect`) recognizes data set (DT1) messages: GS Reset, system mode, system, reverb,
chorus, common, part (using GS's own block-to-part numbering), and drum map. It checks the Roland
checksum (`SysExChecksum` in the domain), which profile parameter maps also use when they render
Roland or Yamaha parameter messages.

XG and GS each have their own model. They share only the `SysExInterpretation` base and the
dispatcher. To add a dialect, add a file to `Bluestone.Midi/SysEx`, add a case to the dispatch, and
add tests in `Bluestone.Tests.Unit/Midi/SysEx`.

Dialect interpreters understand a protocol family in code. Device profiles describe one instrument
as data. A profile's `protocols: ["xg"]` declares compatibility, but the interpreter reads XG
messages whichever profile a track is routed to.

### Device knowledge is not transport

| Concern | Examples | Lives in |
| ------- | -------- | -------- |
| Transport | Core MIDI, WinMM, ALSA, future Windows MIDI Services | `Bluestone.Platform.*` |
| Protocol | MIDI 1.0 bytes, SMF, future UMP | `Bluestone.Midi` |
| Dialect and device semantics | Universal, XG, GS, device profiles and their parameter maps | `Bluestone.Midi/SysEx`, `Bluestone.Profiles`, `/profiles` |

An adapter never interprets what it carries, and an interpreter never cares where the bytes came
from (ADR 0005, 0007, 0023).

## Tracks, devices, and routing

```text
Arrangement
  Track (role: instrument, audio, hybrid, effect, group; optionally in a group)
    ├── clips (NoteClip, AudioClip)        musical content; never owns an endpoint or a device
    ├── automation lanes                    MIDI targets, device parameters by DeviceId, or instrument part parameters
    └── device chain (optional, one)        Arpeggiator → Transpose → Synth → Filter

Project
  ├── racks             free-standing chains that several tracks route into (one instrument instance)
  ├── instruments       external MIDI instruments: profile, operating mode, ports → endpoints
  ├── connections       the one routing model: events or audio, source → destination, channel mapping
  └── mixer             channels with outputs and sends, and the master, independent of tracks
```

### Tracks and clips

A track has one type and a role (ADR 0021). The role says what the track is for; it is stored and is
widened, never narrowed, to fit content, and changing it never discards anything. Clips decide what a
track holds (ADR 0020), and clips on a track never overlap. A `NoteClip` holds events relative to its
content origin and shows a window of them, so trimming loses nothing. An `AudioClip` holds a reference
to an audio file, a gain (-120 to 24 dB), and a fade-in and fade-out (lengths in ticks from its edges,
linear or equal-power); the audio engine streams it from disk (ADR 0032). `Track.ArrangedEvents` is
what the note clips play. `TrackRendering` adds MIDI automation lanes, sampled into channel events, and
returns device automation separately as parameter changes.

Loops keep their range apart from whether they loop (ADR 0039). The arranger loop (`ArrangerLoop`) is an optional
range and an enabled flag; only `ArrangerLoop.Active` reaches playback, the audio transport, and cycle recording.
A clip's optional `ClipLoop` (start and length in content ticks) makes the clip play from `ContentOffset` once, as a
pickup, and then repeat the loop region for as long as the clip lasts. Note clips produce their repeats in
`NoteClip.Arrange()`, when the plan is compiled and never on a real-time thread, with each pass's notes cut at its
end and controller state restored at each repeat; the number of arranged events is bounded and an edit beyond it is
refused. Audio clips are not time-stretched: an audio loop is a fixed stretch of the file that `ClipSource` maps
frame by frame, so any number of wraps can fall in one block.

### Groups

A track can be in a group track, and groups nest (ADR 0030). Putting a track in a group is one command
that also routes it: its own outgoing connections are parked in the project and replaced by a connection
into the group, and taking it out puts back those that still work. A group's mute and solo reach its
members (`GroupTracks.Playing`: the nearest explicit state wins; without groups the usual rule holds).
A folded group hides its members in the track list and the arrangement; folding is saved.

### Devices and chains

A `DeviceDefinition` is what a device is (data); a `DeviceInstance` is one configured occurrence with a
stable ID, parameters, bypass, and plugin state; a `DeviceChain` is an ordered list with one owner, a
track or a rack (ADR 0022). Processing is sequential, and passthrough is managed by the host: a device
is given only the event classes it handles, and everything else (SysEx, controllers it does not
declare) goes around it and is merged back in canonical order. Only an explicit filter removes events.
A chain carries a parallel bundle of notes, other events, and audio (ADR 0037): an instrument plays the
notes it handles and passes them on, so instruments in a row layer and a note effect after an instrument
still has notes to work on; an audio effect replaces the audio and passes events on; a note effect leaves
audio alone. `DeviceDefinition.EventThru` lets a definition end the note path or mix its input with its output.
The built-in devices (Transpose, Event Filter, Arpeggiator) are MIDI effects; they run in
`Bluestone.Signal` when the plan is compiled, and in a real-time form on live input. Instruments and
audio effects are plugins, which run in worker processes, never in Bluestone's process. Presets are
templates without identities: loading one creates new devices.

### Routing

Every route is a `SignalConnection` in the project's one list (ADR 0023). A connection starts at a
track's output (the end of its chain), a rack's output, or a tap after one device, and ends at a track
or rack, a part of an external instrument (port plus channel), a mixer channel, or the master. A
connection's `ChannelMapping` filters, forces, or remaps channels; clips are never changed to reach a
different part. Validation rejects connections that cannot work and every feedback loop. The track
inspector's External MIDI fields (Output, Profile, Channel, Transpose, Voice) are a view of this model
(`TrackOutputs`), and so is its **Audio to** (a track's primary audio output, ADR 0038), so the inspector,
the Device View's routing indicators, and the routing inspector always agree. A new track, and a track that
gets an instrument while its sound goes nowhere, is given an explicit audio connection to the master
(`TrackSetup`, ADR 0038).

```text
Piano ───┐                                           ┌──► MU2000 port A, ch 1..16
Strings ─┼──► rack "XG" [XG synth → Delay] ──audio──► mixer "XG" ──► master
Bass ────┘
Lead ──► [Arpeggiator → Transpose] ──► MU2000 port B, ch 4
                 └── tap ──► track "Harmony"
```

In the app, the Device View (ADR 0036) edits the selected track's chain, or a rack's chain after
"Edit" in the Racks section; Connections follows whichever chain the active Device View shows. Every edit is a
command, and one that would add a routing error is refused with a message. Consecutive edits with the
same merge key within a second (a slider drag) are one undo step.

### Mixer

Mixer channels are audio paths, independent of tracks. A channel has gain, pan, mute, and solo, an
output (the master, or another channel as a bus), and sends: a copy of its audio to another channel at
a level, taken before or after the fader and pan (ADR 0032). A channel that receives sends is shown as a
return. An edit that would close a loop of outputs and sends is refused; one read from a file is reported
and left out of the audio graph. Channels are heard while audio runs.

The inspector's Mixer section edits channels, their outputs, and the master gain. The mixer console
(File ▸ Mixer…, or Window ▸ Mixer on macOS) shows the same `MixerViewModel` as one strip per channel and
the master: name, what feeds it (and whether it is a group's, a bus, or a return), output, sends (level,
pre or post fader), pan, fader, mute, solo, and a peak and RMS meter with a two-second peak hold and a
clip light. The meters are read from the engine's lock-free meters on a 30 Hz UI timer without
allocating. The inspector's Audio Clip section edits the selected audio clip's gain and fades; the
arrangement draws the fades.

### From project to plan

`PlaybackRouting.Prepare` resolves each external instrument's profile and ports against what is
available, then `SignalGraph.Evaluate` renders every track (mute and solo apply to a track's own
content, and a group's to its members' content), runs every chain in dependency order, follows every
event connection, and collects: external-instrument part feeds, software-instrument feeds (merged from
every source, which drive plugin instruments in the audio engine), parameter feeds for devices that do
not run in Bluestone's process, and diagnostics. Each part with an available endpoint becomes a
`PlanPart`; parts sharing an endpoint share an output slot. The compiler encodes MIDI 1.0 at the edge
and orders the plan by tick, phase, origin track, and index, so projects migrated from format 3 send the
same bytes (`Format3EquivalenceTests`). The real-time engine plays the plan (ADR 0006).

### From project to audio

`ProjectAudioGraph` turns the project into a render graph (ADR 0032): audio clips, plugin instruments and
effects in chain order, audio connections into mixer channels, buses, the master, and other chains, and
the mixer's gains, pans, mutes, solos, and sends. Node and strip state live across rebuilds, so an edit
while playing does not restart a clip's stream, lose a plugin's notes, or reset a meter. `AudioEngine`
renders the graph in fixed 256-frame blocks; in the default buffered mode a .NET thread renders ahead into
a ring that the Rust device callback only copies from. The MIDI playback engine is the one transport the
audio engine follows (`IAudioTimeline`), so audio and external MIDI stay in time. `AudioPlayback` opens
the device, falls back to the default output when the chosen one disappears, and waits for a device when
none can open.

### Delay compensation

Every path through the render graph is delay-compensated (ADR 0035). A node's output lags by its own
latency (for a plugin, its pipelining plus the plugin's reported latency; 0 for clips) plus its slowest
input's. At every summing point (a node's input, a mixer channel, the master, through outputs and sends)
faster arrivals are delayed to match, by delay lines kept across graph rebuilds. The engine renders the
master's latency ahead of the transport, so audio, plugin instruments, and external MIDI line up. A plugin
that reports a new latency gets the graph rebuilt. With low-latency monitoring (Audio Settings), the
instrument played live skips its compensation delay.

### Live input

Live notes (audition, MIDI thru, the on-screen and computer keyboards) follow the track's live route: the
nearest playable instrument through its connections, through every device on the way (ADR 0035).
Built-in devices run in a real-time form that puts out the same notes as at plan time
(`Bluestone.Signal/Live`); a plugin MIDI effect runs in its own instance; a plugin instrument is played
through the audio engine, an external one by MIDI out. `LiveInput` (`Bluestone.Application/Live`) runs
one route per track: on the audio thread for a plugin instrument, otherwise where the input arrives, and
on a live thread for what comes later (arpeggio steps). A route that changes first releases the notes it
sounds, so nothing hangs. While live input plays a plugin instrument, the engine switches to a shorter
render-ahead queue. Recording captures the input before any device. The metronome and instrument setup
go to the route's external endpoint.

### Automation

Automation is separate from signal flow (ADR 0024). A MIDI lane targets a controller, pitch bend, or
pressure on a channel, and becomes events. A device lane targets a parameter of a device by ID: it does
not pass through the devices before it, it is never encoded as MIDI, and reordering or moving devices
does not retarget it. Built-in processors apply it at its tick; other devices receive it as a parameter
feed for their worker.

A hardware lane targets a parameter of an external instrument's part (ADR 0031): the instrument, its
port, the channel, and the parameter's key in the instrument's profile. The profile's parameter map says
how the parameter is set: a 7- or 14-bit controller, an RPN or NRPN (followed by the RPN null unless the
profile says otherwise), or a SysEx template with the value (whole or in bit slices such as nibbles), the
channel, a device number byte, and a Roland or Yamaha checksum ([profiles.md](profiles.md#parameter-map)).
When playback is prepared, `HardwareAutomation` samples the lane, scales its normalized values to the
parameter's range, and sends a value only when it changes and at most once per 20 ms of song time per
parameter (a change that comes sooner waits, so a ramp still ends on its last value). The MIDI goes to
the part's endpoint as a `ParameterPart`, ordered like everything else by tick, phase, the lane's track,
and index, after that track's own events at the same tick and phase. It is sent whether the lane's track
is muted or not; when two lanes control the same parameter of one part, the first in track order is sent
and the other is reported. On play, seek, and loop wrap the latest RPN, NRPN, or SysEx change of each
parameter is resent whole; controller mappings are chased as controllers. MIDI file export writes the
same MIDI on the lane's track. A lane whose instrument, port, profile, parameter, or endpoint is missing
is kept and reported. There is no "external instrument device": the target names the part, as
connections do.

### Workspace and Device Views

The window is a semantic workspace (ADR 0036): fixed transport chrome and arranger, a detail region of tabs
below (Device Views, piano roll, event list, MIDI monitor, messages), a left and a right side region (inspector,
Browser), and border rails with icons for collapsed views. `WorkspaceViewModel` owns the views and their
placement (docked, collapsed, detached); the desktop layer reflects it and opens a `ViewWindow` per detached view.

View state and model state are kept apart. A `DeviceViewViewModel` holds only what it shows (following the
selection, or locked to a track or rack by ID) and its presentation (collapsed devices, selection, scroll); the
chain and its devices are the project's and change only through project commands, so several Device Views over
one chain share devices but not presentation. The Browser (`BrowserViewModel`) is a palette: dropping onto a
Device View picks the chain and gap, or, over the middle of a device's panel, the device to replace
(`DeviceCommands.ReplaceDevice`: one undo step, a new device identity, a plugin started in its worker before the
project changes); Return inserts into the most recently active Device View that shows a chain, or nowhere.

Time navigation is one pure model, `TimelineViewport` (zoom in pixels per quarter and scroll, anchored zoom, fit),
with one ruler gesture, `RulerNavigationGesture`, in the arrangement and the piano roll, each keeping its own state:
a click seeks, a vertical drag zooms around the position first pressed, and a horizontal drag scrolls. The
arranger's loop strip is reserved for loop editing (`LoopStripDrag`, committed once on release).

Layout is local state, never project state: `WorkspaceStore` keeps user defaults and per-project state as small
versioned JSON in the settings folder, with window bounds per display configuration. `LayoutRecovery` never
restores a window off-screen and shows a detached view whose monitor is missing as a rail icon while keeping its
saved window. Colours, type, spacing, and device-panel geometry are design tokens in `Theme/Tokens.axaml`.

### Plugin workers

Third-party plugins are hosted in separate worker processes (ADR 0025), never loaded into Bluestone's own
process. Process isolation is crash isolation, not a security sandbox. VST 3 and CLAP plugins are loaded by the
Rust plugin host inside the worker (ADR 0033). The user chooses how plugins share workers (ADR 0034): together,
by manufacturer, by plugin (the default: one worker per plugin module), or individually, with a mode per plugin
that overrides the global one. These are user settings (`PluginHostingSettings`), not project data;
`PluginDeviceHost` applies them and moves running plugins with their state, and a worker crash marks every
device in it Crashed and says which went down together. When the plan is compiled, a plugin MIDI effect runs in
its worker over the arrangement and its output joins the chain like a built-in device's (ADR 0028). A plugin's
editor opens in a separate window owned by its worker; `PluginEditorWindows` hides and shows editors as the
selection moves, keeps per-editor pins, and can ask the worker to forward an editor's keys to Bluestone (macOS
and Windows; not Linux X11) (ADR 0040). Modules are scanned in their own scanner processes from a list of
plugin folders; the Plugins view (Window ▸ Plugins… on macOS, File ▸ Plugins… elsewhere) rescans them and
retries quarantined modules, and plugins it finds join the device catalog without a restart. See
[plugin-hosting.md](plugin-hosting.md).

## Known compromises

Deliberate for now, each with a known cost:

- **Notes are MIDI 1.0-shaped.** Note velocity is 7-bit (MIDI 2.0 has 16 bits), and notes have no
  per-note attributes, controllers, or pitch bend. See [Planned work](#planned-work).
- **RPN and NRPN in clips stay controller sequences** (CC 101/100 or 99/98, then 6 and 38). Folding them
  into single parameter events cannot always reproduce the original bytes. For example, a message that
  sends only CC 6 leaves the device's previous LSB in place. A MIDI 2.0 encoder could translate the
  sequences as it sends them, as the MIDI 2.0 translation rules describe. (Automation of a mapped RPN or
  NRPN parameter always sends the whole sequence.)
- **Sixteen channels.** `MidiChannel` is 1 to 16. UMP adds 16 groups. The group belongs to an external
  instrument's port and its endpoint, not to the music or to connections.
- **The playback plan is MIDI 1.0.** `PlaybackPlan` and `ChaseState` hold encoded `ChannelMessage`s,
  so chasing controller state on seek is worked out in MIDI 1.0 terms. A MIDI 2.0 output would need
  its plan entries in UMP form.
- **`EventPhase.BankSelect`** (ADR 0003) applies only to bank selects that are not part of a
  `ProgramEvent`. A `ProgramEvent` sends its bank select messages in the program change phase,
  directly before its program change.
- **Render-ahead latency.** The default render-ahead queue (1024 frames, 21.3 ms at 48 kHz) adds that
  much output latency, because .NET garbage collection pauses were measured to need it (ADR 0032); while
  live input plays a plugin instrument the live queue (512 frames, 10.7 ms) is used. Both are settings.
- **Opening a device restarts plugins.** Plugins cannot change rate while running, so each time a device
  opens every plugin instance is created again at its rate from its live state; an open editor closes.
- **Plugin MIDI effects are rendered into the plan, and played live separately.** When the plan is
  compiled, each plugin MIDI effect runs a fresh copy in its worker over the arrangement (ADR 0028); live
  input goes through the device's running instance (ADR 0035). Each render is independent, so the plan
  cannot reflect what the live instance does.
- **Live input ignores device automation** of built-in devices: they run live at their stored values.
- **Live input sounds only the first plugin instrument of a chain.** At plan time instruments layer (ADR 0037),
  but the live route (ADR 0035) still ends at the first instrument that takes the notes.

### Not verified on hardware

Built and tested automatically where a test can reach, but not yet checked by a person on real devices (see
[hardware-test-plan.md](hardware-test-plan.md)): audio output on Windows (WASAPI) and Linux (ALSA), which
is built and tested in CI but has not been heard; unplugging an audio device (tested with a test backend);
plugin editors on Windows and Linux; live input latency outside macOS; third-party plugins through the
audio engine on a real device; the shipped XG parameter map on an XG instrument.

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
- The project file stores 16-bit velocities, which needs a new format version and a migration that
  scales the stored 7-bit values ([project-format.md](project-format.md)).

Not included: MIDI 2.0 note attributes (articulation, per-note pitch) and per-note controllers.
They follow once a MIDI 2.0 endpoint can play them.

### Controller ranges and steps

MIDI 2.0 messages always carry a controller's full range. Which part is meaningful is device
knowledge. MIDI-CI Property Exchange describes it per controller (the Controller Resources
specification) in one of two ways. `stepCount` quantizes the full range into N steps, and is the
preferred method. `minMax` marks a sub-range, and is intended for legacy products. An entry uses
only one of them.

Bluestone keeps events at full resolution and treats ranges and steps as profile data.
`ProfileController` (number, name, default) could gain optional `steps` or `min`/`max`, written by
hand for legacy instruments or filled in from MIDI-CI later. Controller lanes would then snap drawing
to the steps and show values in the device's own units. (Parameter map entries already have their own
`min` and `max`.) Nothing supplies this data yet, so it waits.

## Deferred

Not built; [roadmap.md](roadmap.md) keeps the full list:

- **Audio**: audio input and recording, compressed formats (FLAC, MP3, Ogg; only WAVE and AIFF play),
  send and fader automation, and fade curves other than linear and equal power.
- **Plugins**: editors embedded in Bluestone's window (they are separate windows), workers for a second
  architecture (Intel-only plugins on Apple silicon are refused), SysEx to native plugins, note
  expressions, and routing the MIDI that plugin instruments put out.
- **MIDI 2.0** transport and UMP, **MPE**, and per-note controllers.
- **Visual node-graph editing** and **feedback routing** (loops are refused).
- **Workspace**: vertical device chains, auto-collapse of device panels, nested and container devices in the
  Device View, modulators, windows holding several views, free docking, and a full-page Settings view.
- **Groups**: drag and drop into a group, and a folded group's lane summarizing its members' clips.
- **Operating-system sandboxing** of plugin workers is not planned: crash isolation with hosting modes
  instead (ADR 0034).

## Decisions at a glance

| Decision | Recorded in |
| -------- | ----------- |
| The domain is protocol-independent; MIDI sits at the edges | ADR 0018 |
| MIDI 1.0 remains first-class | ADR 0018 |
| MIDI 2.0/UMP is a future protocol target; UMP is not the domain model | ADR 0018, ADR 0005 |
| Simplicity over speculative abstraction | ADR 0018 |
| Raw SysEx is preserved losslessly | ADR 0019 |
| SysEx interpretation is optional, derived, and extensible; XG and GS are dialect interpreters | ADR 0019 |
| Device semantics are independent of platform transport | ADR 0005, ADR 0007, ADR 0023 |
| Tracks hold clips; note clips, audio clips, and automation lanes | ADR 0020 |
| Track roles are stored and reconciled; conversions never discard | ADR 0021 |
| Device chains with stable identities; passthrough by event class | ADR 0022 |
| One routing model of connections; external instruments and mixer channels are entities | ADR 0023 (supersedes 0008) |
| Device automation targets parameters, separate from signal flow | ADR 0024 |
| Plugins run out of process; isolation is not a sandbox | ADR 0025 |
| C# first; Rust only for measured or native-interface needs | ADR 0026 |
| Project format 4 and its migration | ADR 0027 |
| Plugin MIDI effects run in their worker when the plan is compiled | ADR 0028 |
| A group's mute and solo reach its members; joining a group routes into it; folding is saved | ADR 0030 |
| Hardware parameters are automated through profile parameter maps, translated when the plan is compiled | ADR 0031 |
| Audio: cpal output in Rust, the graph in C# rendered ahead into a ring, one transport | ADR 0032 |
| VST 3 and CLAP through a Rust plugin host loaded only in the worker | ADR 0033 |
| Users choose how plugins share worker processes; by plugin by default | ADR 0034 |
| Live input through every device on the live route; a live render-ahead queue; plugin delay compensation | ADR 0035 |
| A semantic workspace; Device Views own presentation, the project owns devices; layout is local state | ADR 0036 |
| A chain carries notes, events, and audio in parallel; instruments pass notes on and layer | ADR 0037 |
| New tracks are hybrid and heard through explicit connections; Settings takes the workspace's place | ADR 0038 |
| Loop ranges are kept apart from looping; clip loops repeat at plan time within a bound; project format 5 | ADR 0039 |
| Floating plugin editors follow the selection, can be pinned, and can send their keys to Bluestone | ADR 0040 |

## Native technology

C# is the default for all code (ADR 0026). Rust is used for a component only when a measurement shows
managed code cannot meet a real-time or throughput requirement after tuning, or when a native interface
(such as plugin binaries) requires it; it replaces C or C++ for new Bluestone-owned native code. C and C++
appear only as third-party libraries, vendor SDK requirements, or thin ABI shims.

Rust code lives in a Cargo workspace, `src/native/`, one crate per component (`src/native/crates/*`), each
a cdylib with a small, versioned C ABI. `dotnet build` builds the crates a project names
(`src/dotnet/Native.targets`) and copies the library beside the assemblies. The crates and why each is Rust:

- `bluestone_audio` (ADR 0032): audio device output through cpal (Core Audio, WASAPI, ALSA). The device
  callback only copies from a ring, because the measurement showed a .NET collection holding a managed
  callback past its deadline. The render graph stays in C#.
- `bluestone_plugin_host` (ADR 0033): loads VST 3 and CLAP plugin binaries and opens their editors, which
  is ADR 0026's native-interface reason. Only `Bluestone.PluginWorker` loads it, so no plugin code ever
  runs in Bluestone's process. The data plane, supervision, and everything else in plugin hosting stay C#.
- `bluestone_test_plugins`: small VST 3 and CLAP plugins for the tests; not shipped.

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
| 11–13, 15 | Plugin crash isolation, state recovery, several workers, scanning failure | see [plugin-hosting.md](plugin-hosting.md#acceptance-scenarios) |
| 14 | A moved device keeps its identity and automation; routing is validated | `DeviceAndRoutingCommandsTests.Scenario14_*` |
