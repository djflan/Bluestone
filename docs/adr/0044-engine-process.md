# 0044. A separate C# engine process for real-time work

- Status: Accepted (after the ahead-of-time compilation spike, step 2 of the migration below); applies ADR 0043
- Date: 2026-11-03

## Context

ADR 0043 measured Bluestone's real-time paths against its timing targets. The C# code itself is fast: the audio render
uses 0.06–0.12 ms of a 1.3–2.7 ms buffer. The misses come from the process it runs in:

- **Garbage collection.** A collection suspends every managed thread in the process. The UI process always allocates:
  a plan compile is 8–100 MB and opening a project about 1 GB (ADR 0012). Under an editing load, pauses were 4.5 ms
  typical and up to 13.7 ms, and no GC setting kept them under 2 ms.
- **Tiered compilation.** Replacing code with optimized code briefly suspends threads, by up to 3.5 ms in the first
  minute of a run.

The same C# in a process of its own, with a small heap, met every target under the same load in the other process. So
the real-time work moves into a separate C# engine process. Rust is not needed (ADR 0026).

The owner decided:

- **Start-up:** the engine starts alongside the main window. Editing works at once; transport controls are enabled
  when the engine reports that it is ready.
- **Crash:** if the engine stops unexpectedly, Bluestone restarts it, sends All Notes Off, and leaves the transport
  stopped. Playback does not resume by itself.
- **Protocol:** the engine is architecturally similar to the plugin workers but has its own protocol, host, and
  supervisor. It does not reuse the plugin protocol.

## Decision

### Processes

| Process | Owns |
| ------- | ---- |
| **Bluestone** (UI) | The project, undo, compiling plans and graphs, plugin scanning and supervision, plugin editors, settings, all views, supervising the engine |
| **Bluestone.Engine** (one per session) | Everything with a deadline: the audio device stream (rendered in the device callback), the playback engine and thread, the metronome and transport, MIDI input and output endpoints, live routes and thru, the disk streamer and clip players, the data plane to plugin workers |
| **Plugin workers** (ADR 0025) | Unchanged |

**Plugins:** the UI process keeps the plugin control plane: loading, state, parameters, editors, and supervision.
It hands each instance's shared-memory block exchange to the engine, which runs the data plane (submitting and
collecting blocks) from the audio graph. If the engine crashes, plugin workers and their state survive, and the new
engine attaches to the same exchanges. Moving audio (step 6) must confirm this.

### What "crash" means

The engine has stopped when any of these happens:

- its process exits for any reason other than a requested shutdown (including a native fault in an audio or MIDI
  driver);
- it misses heartbeats for 1 s (sent every 250 ms on the control channel);
- the control channel breaks or the engine sends anything the protocol rejects;
- it reports a fault on a real-time thread, such as an exception in the playback thread.

A device that disappears is **not** a crash. The engine reports it, keeps running, and waits for the device as today.
A plugin worker that dies is not a crash either; ADR 0025 handles it.

### After a crash

1. The UI kills the engine process if it is still alive, and records a warning in Messages: "The engine stopped
   (reason) and was restarted; playback is stopped."
2. It starts a new engine, which sends **All Notes Off** before anything else: sustain off (CC 64), All Notes Off
   (CC 123), and All Sound Off (CC 120) on all 16 channels of every MIDI output. Hardware does not release notes when
   a port closes, so this stops hanging notes.
3. The UI sends the current plan, graph, devices, and settings; the engine opens the devices and reattaches plugin
   exchanges. The transport stays **stopped** at the position it had.
4. A recording in progress ends at the crash. Everything already streamed to the UI is kept as a take.
5. **Crash loops:** after 3 crashes within 60 s, the UI stops restarting. It shows the failure in the status line
   and Messages with a Restart command. Editing still works.

The status line (plan item 7) shows "Starting the engine…" and "Restarting the engine…" as activities.

### Protocol (`Bluestone.Engine.Protocol`)

Its own assembly, message set, version number, and limits. The design is similar to ADR 0025's:

- **Control channel:** a local stream (Unix domain socket; a named pipe on Windows) carrying length-prefixed binary
  frames. It starts with a versioned Hello and the per-launch token the UI passed on the command line, then
  heartbeats. Requests have ids; replies and events come back on the same channel. It carries what is not real-time:
  commands (play, stop, seek, loop, record, metronome, panic, audition), device lists and choices, settings, region
  handles, faults, and the Ready signal.
- **Shared-memory regions**, each with a fixed, versioned layout:

  | Region | Writer → reader | Contents |
  | ------ | --------------- | -------- |
  | Plan | UI → engine | The compiled playback plan in compact form, double-buffered and swapped whole |
  | Graph | UI → engine | The audio graph snapshot in compact form, with a string table for file paths |
  | Transport | engine → UI | Anchor and position under a sequence lock (as `PlaybackEngine` publishes today), state, loop wraps |
  | Telemetry | engine → UI | Meters, timing statistics, xruns, underruns, live latency, GC count and pause, JIT count |
  | Input | engine → UI | Single-producer ring of timestamped MIDI input, for recording, the monitor, and input activity; later, audio input |

- **Shared building blocks:** framing and the bounds-checked mapped region are generic. They move into a small
  neutral library that both protocols use, or are copied if extracting them costs more (decided in step 4). Neither
  protocol depends on the other.

Why not reuse the plugin protocol:

| | Plugin worker | Engine |
| --- | --- | --- |
| Trust | Runs untrusted third-party code | Bluestone's own code |
| Count | One per plugin, module, or shared group | One per session |
| Traffic | Audio blocks with a deadline, mostly one way | Plans, graphs, commands, transport, meters, input, and telemetry in both directions |
| Devices | Owns none | Owns audio and MIDI devices |
| On failure | Silence for that plugin; the session continues | Restart, All Notes Off, transport stopped |

Sharing one protocol would tie their versions together, and every engine message would widen what a plugin's process
can talk to.

### Compact plans and graphs

The engine never receives object graphs. The plan becomes arrays of blittable structs: events by time, with output
**slots** instead of endpoint objects, plus anchors and chase state. The graph becomes nodes, strips, connections, and
parameters as structs, with indices into a string table for file paths and plugin exchange names. The UI writes a new
buffer and publishes it with one handle swap over the control channel. The engine decodes nothing on a real-time
thread, and loading allocates only for small tables, nothing in proportion to the plan's size.

Equivalence: the regression fixtures that check the in-process plan must play identically from the compact form.

**Plans (step 3, done):** `PlanImage` writes a plan as one flat image. The header holds a magic number, version, and
event size, and the sections are the tempo and meter changes, events, payload ranges, payload bytes, and chased
parameter changes. `PlanImage.Read` returns a plan whose events and payload bytes are views of the image, not copies.
Only the small tables (tempo, meter, chased parameters) are decoded, so reading a 160,000-event plan allocates under
16 KB. The reader checks every count, offset, index, tick order, and MIDI byte, and refuses a damaged image or one
from another build with `InvalidDataException`. A randomized test corrupts single bytes to check that. The shipped
samples read from their images send the golden bytes, and a plan with SysEx, chased parameters, and tempo and meter
changes plays identically from mid-song and across loop wraps. The engine's pump is unchanged in cost (about 30 ns)
and allocates nothing.

**Graphs (step 3, done):** building the audio graph now has two halves. `ProjectAudioGraph.Describe` produces an
`AudioGraphDescription`: plain data with nodes by key (a track's clips, a device's plugin), strips by channel id, and
the builder calls in order. `AudioGraphRealizer` (in Bluestone.Audio, for the engine) turns a description into a
render graph. It keeps clip streams, plugin note tracking, strip meters, and compensation delay lines between graphs,
as `ProjectAudioGraph` did; `ProjectAudioGraph.Build` now does both halves in one process, and every existing graph and
plugin test passes unchanged. `AudioGraphImage` writes a description as a flat image (names, paths, and SysEx as UTF-8
and bytes; plugin events as compact 24-byte entries) and reads it back, replaying the steps through the description
builder so that whatever it accepts realizes. A rich project (fades, a looped clip, a bus, a pre-fader send to a
return, a muted track) renders identical samples whether its graph is built directly or described, written, read,
and realized separately. Unlike a plan, a description is decoded into objects. They are small, apart from plugin
schedules, and the realizer replaces a plugin's schedule only when its events change. A realizer refuses a
description whose connections form a loop, as the builder does; the describing side never produces one.

**The playback thread allocates nothing (done):** chases for play and seek were already computed by the caller. The
thread itself allocated in three places: the "stopped chase" it sends when a plan is loaded, outputs change, or the
playhead moves while stopped (live thru plays on this thread then); its per-output arrays; and a count-in's clicks.
Those are now computed by whoever gives the command. The thread only compares a stopped chase with the last one and
skips it if a later Seek or Play has moved the playhead. A test applies every command from another thread while the
playback thread pumps, and checks that the pumping thread allocates nothing; the previous engine allocated 9 KB per
round of commands.

### Rules inside the engine (ADR 0043)

- **No allocation in steady state** on the real-time threads (audio callback, playback thread, MIDI input callbacks,
  live thread), checked with allocation tests.
- **A small, reference-light heap.** Plans and graphs live in shared memory, not on the managed heap.
- **No JIT during playback:** the engine is published with NativeAOT. If AOT is blocked, tiered compilation is off
  and ReadyToRun is used.
- **Collections are reported:** the engine counts every collection and its pause. A probe fails on any pause longer
  than 1 ms during playback.
- Thread policies as today: the macOS real-time policy for the playback thread and render threads, and MMCSS, a 1 ms
  timer-resolution request, and a high-resolution final wait on Windows (ADRs 0012, 0013, 0043, 0047).

### Start-up

The UI starts the engine while the main window opens and shows "Starting the engine…" in the status line. Editing
works at once. Transport, record, audition, and live input are enabled when the engine sends Ready, after its devices
are open. If the engine cannot start, the window stays usable for editing and offers Restart.

## Migration

Each step lands separately and keeps the app working:

1. This ADR.
2. NativeAOT spike: publish a console host that references Playback, Audio, Midi, Signal, the host side of Plugins,
   and the platform adapters; fix trim and AOT warnings; measure start-up time.
3. Compact plan and graph format, with the allocation and equivalence tests.
4. Engine host skeleton: the process, handshake, heartbeat, supervisor, restart policy, telemetry, and kill tests. The
   shared-building-block decision is made here.
5. Move MIDI: playback, endpoints, live thru, and recording input. `PlaybackController` becomes a proxy.
6. Move audio: the device stream in direct mode, the disk streamer, meters, and the plugin data plane.
7. Live input rendered in the device callback. The live render-ahead queue defaults to 0 in the engine and stays
   available as a setting.
8. Probes against the real engine (`--engine`), with the load in the UI process. These must meet ADR 0043's targets.
9. CI: AOT publish on macOS, Windows, and Linux; allocation, equivalence, and crash-restart tests. No timing gates on
   hosted runners.
10. Make the engine the default where it meets the targets (done for macOS; see "The engine by default (step 10)"). The
    in-process path stays as the fallback and as the default on Windows and Linux until they are measured.

## MIDI in the engine (step 5)

With `BLUESTONE_ENGINE=1`, MIDI playback runs in the engine. It is still opt-in, because audio has not moved yet
(step 6). Without the variable, everything runs in the UI process as before, and every existing test uses that path.

**What moved:**

- **Playback.** The engine runs `PlaybackEngine` on its own playback thread, with the macOS real-time policy and
  precise sleep, or MMCSS, a 1 ms timer-resolution request, and a high-resolution final wait on Windows. It plays
  plans; it never compiles them.
- **Outputs.** The engine opens the operating system's endpoints itself (CoreMIDI, WinMM, or ALSA, chosen as the UI
  chooses them). Endpoints only the UI can reach are forwarded: the monitor and test buses, the computer keyboard, and
  the UI's own virtual ports such as "Bluestone Out". The engine plays to them through a forwarding output, which
  sends the messages back to the UI, and the UI delivers them. This is not real-time beyond the engine. It is fine for
  those endpoints, and it lets the integration tests run without MIDI hardware on every platform.
- **Inputs and thru.** The engine opens the operating system's inputs. A message is played on the thru track's live
  route in the input callback, then forwarded to the UI for recording, the monitor, and input activity. The engine runs
  live routes to external instruments whose stages are all built-in live devices (transpose, event filter,
  arpeggiator) or forced channels. The UI sends each route as its stages, parameter values, target output, and
  resolution.

**What stays in the UI:**

- Live routes with a plugin MIDI effect, and routes to a plugin instrument. They still run in the UI, and their thru
  to an engine output goes through the engine's playback thread. (Step 7 moved them into the engine; see below.)
- Input from endpoints only the UI can reach. The UI decides the thru route as before; a route the engine runs gets
  the message over the control channel.
- Recording. `MidiRecorder` places input on the timeline with the mirrored transport.
- Audio. `AudioPlayback` follows the mirrored transport, and its alignment nudges are sent to the engine. (Step 6
  moved audio into the engine; see below.)

**How it works.** `PlaybackController` talks to an `IPlaybackTransport`: the in-process `PlaybackEngine`, or
`EnginePlayback`, the engine's client. The UI still resolves slots to endpoints. It sends each output under a handle
(opened by the engine or forwarded), then the slots as handles.

- **Plans** cross as `PlanImage` files in a folder per session (`bluestone-engine-<pid>` in the temporary folder).
  This replaces the double-buffered shared region in the protocol table above; a file mapped read-only is shared
  memory and needs no buffer protocol. The engine maps the file and reads the plan over the mapping. It keeps the
  mapping while the playback thread can still read the plan, and releases the previous one once the thread has switched
  (`PlaybackEngine.LoadsApplied`). Then it acknowledges, and the UI deletes the older files.
- **Transport.** The engine reports its transport whenever it changes and every 10 ms while playing: state, position,
  loop wraps, count-in, and the current and previous anchors, each naming the plan whose tempo it uses. The UI mirrors
  it. `TryGetTickAt` and `TryGetSongTimeAt` share their code (`TransportAnchor`) with the engine, so a mirrored anchor
  maps a time to the same position; a test checks this across tempo changes, loop wraps, and seeks. Timing figures come
  every 250 ms. The transport and input travel as control messages, not the shared regions in the table above. That is
  simpler and fast enough for display and recording; nothing real-time waits on them.
- **Command order.** Play, stop, and seek carry a sequence number. The engine echoes the last one its playback thread
  has applied in each transport report, and the UI ignores a report older than its last command. A report already on
  its way when the UI seeks therefore cannot move the playhead back; the UI keeps its own position until the engine
  catches up. The numbers keep rising across restarts, and the resync's seek gets a new one, so a new engine's first
  reports are ignored until it has applied that seek.
- **Clocks.** Both processes use the system's monotonic clock, so input and output timestamps compare directly.
- **Restart.** After a restart the UI sends everything again: outputs, slots, the latest plan, loop, metronome, routes,
  thru, inputs, and the position. The transport stays stopped. A restarted engine sends sustain off, All Notes Off, and
  All Sound Off on all 16 channels of each output as it opens it, before anything else goes there. The outputs are only
  known after the UI resends them, so this happens just after Ready rather than before it. Forwarded outputs get the
  same messages.
- **Protocol** version 3 adds the MIDI messages. Each has bounded counts and lengths, and every handle, tick, and MIDI
  byte is checked when it is read. Random bodies of every type, and valid messages with one byte changed, decode or
  fail with a protocol error.

**Real-time rules.** The input callback, the live thread's route pumping, and the playback thread allocate nothing
in steady state. Input and forwarded output are copied into a fixed buffer under a short lock; a sender thread turns
them into messages. `Bluestone.Engine --check-allocations` plays notes from an input through a transposing route to
a forwarded output while the transport plays, and checks that the thread allocates nothing. An integration test runs
it, and it also passes in the NativeAOT build. The engine's sender thread still allocates: a record for each status
and MIDI batch it sends, about 100 a second while playing, plus the channel's writes. The `--engine` probe (step 8)
must measure what this costs through the heartbeat's collection count and pause time.

**Tests.** Integration tests run real engine processes: a plan plays in order to an output only the UI reaches; loop,
seek, and stop work; input from an endpoint only the UI reaches is recorded and played through a transpose that the
engine runs; and a killed engine is replaced, sends All Notes Off on its outputs, stays stopped, and plays again after
the resync.

**Still to do:**

- Make the engine the default, and enable the transport only when the engine is ready (after step 7).
- Live routes with plugin MIDI effects, and routes to plugin instruments (step 7).
- `--engine` in the probes, with the app load in the UI process, to measure MIDI lateness and thru against ADR
  0043's targets, and the engine's collections from its sender thread (step 8). Not measured yet.

## Audio in the engine (step 6)

With `BLUESTONE_ENGINE=1`, audio runs in the engine too: the output device, the render graph, clip playback and the
disk streamer, meters, and the data plane of every plugin in the graph. It is still opt-in: live input into plugin
instruments (step 7) does not reach the engine yet. Without the variable, `AudioPlayback` renders in the UI process
exactly as before, and every existing test uses that path.

**What moved:**

- **The device.** `AudioHost` (in `Bluestone.Engine`) opens the output Bluestone chooses through the same native layer,
  falls back to the system default output when the chosen one is missing, waits for a device when none opens, and
  checks every second for a lost device, as `AudioPlayback` does. It renders in the **device callback** by default:
  the engine's render-ahead queue is a setting of its own (`AudioSettings.EngineQueuedFrames`, 0 unless changed; the
  Audio window shows and edits it when the engine renders), forwarded with the device, buffer size, sample rate, and
  test tone in `SetAudio`. The engine reports the stream it opened (`AudioDeviceStatus`: state, rate, channels, block
  size, queue, render thread policy, and the problem in words when the chosen device is not the one playing). The UI
  thread never waits for it: `EngineAudio.Start` sends the settings and returns, the Audio window says "starting"
  (and the status line keeps "Opening …") until the report arrives, and the graph is described then. A new device
  problem (a stand-in playing, or nothing to open) also goes out as an `EngineProblem` of the audio area, which
  Bluestone records as a warning in the status line and Messages; it is never a crash.
- **Following the transport.** The audio engine follows the engine's own `PlaybackEngine` (an `IAudioTimeline` over
  its anchors), renders the graph's latency ahead of it, and every second moves the playback transport towards the
  audio clock by half the measured drift, as Bluestone did with nudges over the control channel.
- **The graph.** Bluestone still describes it (`ProjectAudioGraph.Describe`) but now for the stream the engine opened,
  and writes it as an `AudioGraphImage` file in the session's folder; `LoadAudioGraph` names the file, rate, and block
  size. The engine's audio thread (not the device callback) reads the file, realizes it with `AudioGraphRealizer`, and
  swaps it in with one reference write, so the callback never waits or allocates for a new graph. Clip streams, plugin
  note tracking, strip meters, and compensation delay lines carry over between graphs as before. Every graph is
  acknowledged (`AudioGraphLoaded`, with what it could not play and its latency), played or not, and Bluestone deletes
  older files then. A graph for another rate or block size is not played; Bluestone describes a new one when the
  engine reports a new stream, after every refresh of the playback controller, and whenever a plugin instance in the
  graph changes status or latency.
- **Clips.** File paths travel in the image; the engine opens the files, and its disk streamer reads ahead.
- **Meters and figures.** About 30 times a second while a device is open, `AudioStatus` carries every strip's meter
  and the master's, and the stream's figures: callbacks, xruns, discontinuities, late renders, render-ahead underruns,
  render faults, the longest render, the device's output latency, plugin blocks that followed the failure policy, and
  the graph's latency. `EngineAudio` mirrors them; the mixer's meters and the Audio window read them through
  `IAudioPlayback`, which `AudioPlayback` also implements (as step 5 did with `IPlaybackTransport`). Chain junction
  meters (ADR 0037) are measured only for the junctions Bluestone watches: `WatchJunctions` (protocol version 10,
  at most 512 keys) replaces the set, the engine applies it at once rather than queueing it behind a device that is
  opening, and sends it again to a restarted engine; `AudioStatus` carries the watched junctions' meters. The graph
  image is version 3: it adds the meter points and the nodes each sums, gain nodes (an Audio Receiver's level), sidechain
  connections (`ConnectSidechain` steps), and each plugin link's sidechain channels.
- **The plugin data plane.** Plugin workers stay Bluestone's: launch, control channel, scanning, editors, state,
  restart, and supervision. Each described plugin node now carries its instance's link: the exchange's shape, the
  pipeline depth, collect wait, failure policy, latency, and the running worker's exchange file and generation (none
  while it is not running). The engine keeps one `AttachedPluginInstance` per device and attaches it to the exchange
  named; `PluginInstance` and `AttachedPluginInstance` share the host's half of the pipeline (`PluginBlockPipeline`),
  so blocks, priming, and the failure policy behave identically, and `PluginNode` drives either through
  `IPluginProcessor`. When a worker dies Bluestone marks its exchange faulted, as before; a host handle now also
  checks that flag in the file's header, so the engine's handle stops at once and plays the failure policy rather than
  spinning its collect wait every block. The restarted worker's new exchange reaches the engine in the next graph.
- **Restart.** After an engine restart Bluestone sends the audio settings again; the new engine opens the device, and
  Bluestone describes the graph for that stream. Plugin workers and their state survive, and the new engine attaches
  to the same exchanges; it sends sustain off, All Notes Off, and All Sound Off on all 16 channels to each plugin before
  that plugin's first block, as it does for MIDI outputs. A test kills an engine and checks that the same audio comes
  back, sample for sample.

**Protocol** version 4 gives `EngineProblem` an area (MIDI or audio) and adds `SetAudio`, `AudioDeviceStatus`,
`LoadAudioGraph`, `AudioGraphLoaded`, `AudioStatus`, and the test-only `TestRender` and `TestRendered`. Counts, rates,
sizes, and levels are checked when read; random bodies of every audio type, and valid messages with one byte changed,
decode or fail with a protocol error. The graph image is version 2: it adds a plugin-link section (the exchange's
path, generation, and shape, checked as `ExchangeOptions` checks them; a path must be absolute). The supervisor now
takes several clients (MIDI and audio), each told about every engine before it is reported Ready.

**Real-time rules.** The device callback and the graph swap allocate nothing in steady state.
`Bluestone.Engine --check-allocations` now also renders as the callback does, through a looping clip streamed from disk
and a plugin whose worker is not running, following the playing transport, while another thread reads, realizes, and
swaps in a new graph; the rendering thread allocates nothing, in the JIT and NativeAOT builds. Reading and realizing
graphs, opening devices, and attaching exchanges happen on the engine's audio thread, never in the callback; the
control thread only queues audio commands for it, and the sender thread only sends what it left, so neither waits for a
device to open or a graph to be realized. The audio thread allocates each `AudioStatus` (about 30 a second, a few
hundred bytes plus 32 bytes a strip or watched junction), which adds to the collections the `--engine` probe must
measure (step 8). `--check-allocations` watches both junctions of its graph, so measuring them is covered.

**Tests.** Integration tests run real engine processes with a stand-in audio device (`--test-audio`, honoured only
with test commands) that never calls back; `TestRender` plays the current graph from the song's start through the
callback's render path. A project with a clip and the reference sine through a mixer channel renders exactly the same
samples in the engine (through a real worker the engine drove) as in one process; when the worker is killed the
engine plays silence for it and the clip as before; after a restart the engine attaches to the new exchange and the
samples match again. A killed engine is replaced and the same samples come back without any action. Unit tests cover
the messages, the image's plugin links, and Bluestone's client (settings resent to each engine, the device state,
meters and figures mirrored, graphs described for the open stream, and their files deleted).

**Deviations from the plan above:**

- Graphs cross as files, like plans, not a double-buffered shared region; meters and figures travel as a control
  message rather than a telemetry region. Nothing real-time waits on either.
- A described graph is decoded into objects in the engine (step 3 said so); only plugin schedules are large.
- Device handling in the engine is a port of `AudioPlayback`'s (fallback, waiting, checks), not shared code: that
  class also builds graphs and runs the live queue. They should become one when the in-process path goes (step 10).
- Automation reaches plugins from the schedules in the description, as before. In the engine, the values it sends are
  not remembered in Bluestone's parameter table, so a worker that restarts gets the last values set from Bluestone, not
  the last automated ones, until the next automation event.

**Not done, or risky:**

- Live input into plugin instruments and plugin MIDI effects: done in step 7, below.
- Recording audio input (not in the engine or the UI yet).
- Not measured: callback timing, xruns, and collections in the engine under the UI's load (step 8). The direct-mode
  default rests on ADR 0043's isolated-process measurements.
- Both processes map each exchange file. Only the engine submits and collects; Bluestone's handle must not be used for
  blocks in engine mode. Nothing enforces that beyond the engine path never rendering in the UI process.
- Make the engine the default and enable the transport only when it is ready: after the probes (step 8).

## Live input in the engine (step 7)

With `BLUESTONE_ENGINE=1`, every live route runs in the engine, including those step 5 left in Bluestone: routes through
plugin MIDI effects, and routes into plugin instruments, which are now rendered in the device callback. Without the
variable, `LiveInput` runs them in Bluestone exactly as before.

**What moved:**

- **Routes into plugin instruments.** Bluestone sends the route (`SetRoute` with the instrument's device instead of an
  output) and the engine's route (`EngineRoute`, a port of `LiveRunner`) becomes the live source of that instrument's
  node in the realized graph (`LiveSourceSet`, as in Bluestone); the audio thread tells the routes about the nodes of
  every graph it realizes. Input from the engine's own inputs (thru) and from inputs only Bluestone reaches (the
  computer keyboard, the on-screen keyboard, auditioning: `RouteInput`, as in step 5) waits in a fixed queue under a lock
  held only to copy an event, and goes into the next block the callback renders, at its first frame; generated events
  (arpeggiator steps) land on their own frame. That is what `LiveInput` does in Bluestone: the latency is the time to
  the next callback plus what follows, and the jitter is up to one device buffer. Placing input at a fixed delay after
  its timestamp would remove the jitter at the cost of a buffer of latency; it was not done.
- **Plugin MIDI effects.** A route's plugin effect stages (`PluginStageSpec`: the instance's exchange, as for graph
  nodes) use the engine's handle on that exchange from the same `PluginRegistry` as the graph, so one exchange is never
  driven through two handles. Bluestone re-sends the route when an effect's worker restarts. Effects play only live
  input, so the engine runs them at depth 1: their output comes in the same block (Bluestone ran them at depth 2). To an
  external output, a route with an effect runs fixed 64-frame blocks on the live thread, as in Bluestone.
- **Depth 1 for the instrument being played live** (ADR 0043 decision 6). The instrument the monitored track plays (the
  thru or recording track: `LiveInput.MonitoredInstrument`) is described with a depth-1 link
  (`ProjectAudioGraph.LivePlayed`, with its latency at that depth), whether or not low-latency monitoring is on; every
  other plugin stays at depth 2, which lets workers run in parallel during playback. The engine keeps the same
  `AttachedPluginInstance` (and so the node, its notes, and its live source) and changes its depth at the next block
  (`RequestPipelineDepth`); the pipeline submits and collects in the same call, with the bounded collect wait. The node
  reports the new latency, so the graph realized from that description compensates for it: monitoring on, the
  instrument and everything aligned with it move one block earlier; off, they move back. Going down drops the block in
  flight; going up gives one block of silence (`PluginBlockPipeline`). The monitored instrument was chosen rather than
  any instrument a route has played, so the depth does not flap as notes are auditioned on other tracks.
- **The callback's collect budget.** Every plugin collect in the device callback waits at most what is left of one budget
  for the block: 60% of the block's period (1.6 ms at 128 frames, 0.8 ms at 64, at 48 kHz; the share the
  `--plugin-pipeline` probe allowed), set by the audio engine as a deadline on its rendering thread
  (`CollectBudget`, `AudioEngine.CollectBudgetShare`) and shared by every plugin in the block: the instrument played
  live, the MIDI effects on its route, and the depth-2 plugins too. However many workers are late, the callback cannot
  wait past it; the instance's own collect wait (2 ms by default, longer than a 64-frame block) applies only where
  there is no budget (the live thread, Bluestone's process, and the tests' offline render, which is not paced like a
  device). `CollectBudgetTests` renders callbacks with three depth-1 plugins whose workers never answer, at 64 and 128
  frames, and checks that a callback waits no longer than the budget in all.
- **Missed depth-1 deadlines.** A block that is not back within the budget is silent (the failure policy), as any
  late block. After 3 misses within 1,000 blocks (2.7–5.3 s at 128–256 frames) the instance falls back to depth 2 by
  itself (`AttachedPluginInstance.FellBack`), and stays there until depth 1 is asked for again after another depth
  (monitoring stops and starts), so a worker that cannot keep up costs one block of latency instead of repeated
  dropouts. Its latency changes with it: the audio thread's once-a-second check sees the graph's latency is stale,
  realizes the last graph again, and reports the fallback as an audio `EngineProblem` (status line and Messages).
- **The live render-ahead queue.** The engine's live queue is a setting of its own (`AudioSettings.EngineLiveQueuedFrames`,
  forwarded in `SetAudio`), 0 by default, which keeps the playback queue, itself 0 by default: live input is rendered
  in the device callback. With a playback queue set, the engine switches to the live queue while live input reaches a
  plugin instrument and back after 10 s of quiet, as `AudioPlayback` does.
- **The live latency meter.** The route measures each note-on from its input timestamp to when its block is heard,
  plus the effects', the instrument's, and the way to the master's latency, as `LiveRunner` does, in the engine
  (`LiveMeter`). `AudioStatus` carries the count, average, shortest, and longest, the depth-1 misses, and whether the
  live queue is in use; `EngineAudio.LiveLatency` and `IsLiveQueue` read them, so the Audio window shows the engine's
  figures.

**Protocol** version 5: `SetRoute` takes an instrument or an output (exactly one), and stages that are plugin effects;
`SetAudio` the live queue; `AudioStatus` the live figures, the queue in use, and the depth-1 misses; and the test-only
`TestRender` live notes, posted on a track's route just before a chosen callback with a chosen lead, so tests know
exactly when a key was played relative to when its block is heard.

**Real-time rules.** `--check-allocations` now also plays keys into a plugin instrument at depth 1, from the input path
to the device callback, through an exchange a stand-in worker thread answers: the thread allocates nothing, in the JIT
and NativeAOT builds, and the notes are heard and measured.

**Tests.** In real engine processes with real workers: a key into the monitored reference sine is heard at the first
frame of the next block (no pipeline block), and the engine's meter reports exactly the key's lead; not monitored, it is
one block later and the meter says so; through the reference Transpose effect it is still heard in the same block,
transposed; the graph's compensation follows monitoring on and off (a clip moves with the instrument); a route through
a plugin effect to an external instrument runs in the engine; and after the engine is killed the new one plays the key
at depth 1 again. Unit tests drive the depth changes and the fallback through an exchange answered by a stand-in worker:
blocks at depth 1 come back in their own call, changing depth moves the output by one block, three misses within the
window fall back and stay back until depth 1 is asked for again, and misses further apart do not.

**Deviations:**

- `EngineRoute` and `EnginePluginStage` are ports of `LiveRunner` and `PluginLiveStage` (the engine cannot reference
  Bluestone.Application). The copies go when the in-process path does (step 10).
- Plugin MIDI effects in the engine always run at depth 1 (Bluestone runs them at depth 2).
- Depth 1 follows the monitored instrument, not every instrument a route reaches.

**Not done, or risky:**

- The fallback's re-realization and its `EngineProblem` are not covered by an engine-process test (provoking repeated
  misses from a real worker is not deterministic); the fallback itself is unit-tested.
- Two tracks whose routes go through the same plugin MIDI effect drive its exchange from both routes, as in Bluestone.
- Not measured: live input latency and depth-1 misses through a real device under the UI's load (step 8).

## Probes against the engine (step 8)

The engine meets every target these probes measure on macOS (results below); Windows and Linux are still to be
measured. The MIDI probe judges the engine's dispatch against Core MIDI's send-ahead deadline, not arrival at the
receiver; arrival, immediate output, and thru are measured by `--midi-loopback`, which does not take `--engine` yet.
`Bluestone.Benchmarks` takes `--engine` with `--jitter`, `--audio`, `--live`, and
`--audio-loopback` (`EngineProbes`): the probe process plays Bluestone's role, starting the engine with
`EngineSupervisor` and talking to it through `EnginePlayback` and `EngineAudio`, exactly as the app composes them, and
carries the `--pressure` load where the app's allocations would be. Reports use the same JSON format, `--repeat`, and
`--compare`; the commands are in the [hardware test plan](../hardware-test-plan.md).

- **Which engine:** `--engine-path`, else the NativeAOT build published to `src/dotnet/artifacts/engine-aot`, else the
  JIT build copied beside the probes, reported as not the shipping configuration (`engine` in the settings, and the
  engine's JIT count).
- **MIDI:** a dense plan (16 tracks) plays in the engine to a Core MIDI destination that a child process publishes and
  the engine opens itself (scheduled delivery); the engine's own timing figures give the lateness. The probe waits
  until the engine has the destination open (retrying, since a newly published endpoint can take a moment to appear),
  and misses if the engine dispatched fewer messages than the plan holds in the window (`midi.engine.missing`) or
  reported a problem. Without Core MIDI the engine plays to an output forwarded to the probe.
- **Audio:** the device in direct mode at `--buffer`, with four looping clips on channels through a bus and the
  reference sine in a real worker playing notes, while the transport loops: missing and late callbacks, xruns, gaps,
  render faults, failed plugin blocks, the longest render against the period, and the master's peak (to show it played).
- **Live:** keys into the monitored reference sine at depth 1. `--live-input midi` (default on macOS) sends them from a
  child process through a Core MIDI source the engine opens as an input, stamped at send, as a hardware keyboard's;
  `--live-input forwarded` posts them from the probe, as the computer keyboard's are, so a collection in the UI process
  delays them (the first quiet suite saw one key a block late under `--pressure app` this way). The engine's meter gives
  the count, average, shortest, and longest latency from each key's timestamp to when it is heard, and the added latency
  against ADR 0043's 2 ms: key to heard less the output latency and one period, so a key that adds nothing lands between
  minus one period and zero (about −1 ms on average at 128 frames). With `--audio-loopback` the keys are heard through
  BlackHole and captured here.
- **The probe's collections:** every engine report also has the probe process's collections and pauses (`probe.gc.*`),
  to attribute an outlier on a forwarded path.
- **The engine's collections:** heartbeats now carry the latest collection's pause (protocol version 6), and the
  supervisor raises every heartbeat (`TelemetryReceived`), so each engine report has its collections, every pause seen
  (judged against 1 ms), the allocation rate (mostly the sender thread's messages to Bluestone), and the JIT count.

**Results (2026-11-03).** Apple M5 Max, macOS, NativeAOT engine, BlackHole 2ch at 48 kHz and 128 frames (2.67 ms),
60 s per run, two runs of each probe at each load, quiet machine. `--pressure` is generated in the probe process, which
plays Bluestone: under `app` it collected about 375 times a run with pauses up to 8.7 ms.

| Probe | none | app | edit | Target |
| ----- | ---: | --: | ---: | -----: |
| MIDI dispatch after its send-ahead deadline, max (23,104 events; 0 of 15,232 messages missing) | 0 ms | 0 ms | 0 ms | 2 ms |
| Audio: missing or late callbacks, xruns, render faults | 0 | 0 | 0 | 0 |
| Audio: longest render | 0.02 ms | 0.06 ms | 0.26 ms | 2.67 ms |
| Live, Core MIDI keys: added latency, max | 0.03 ms | 0.04 ms | 0.04 ms | 2 ms |
| Live, forwarded keys (computer keyboard): added, max | 0.06 ms | 0.31 ms | – | 2 ms |
| Loopback through BlackHole: added, max (about 320 keys) | 0.38 ms | 0.38 ms | – | 2 ms |
| Engine collections / JIT-compiled methods | 0 / 0 | 0 / 0 | 0 / 0 | pause ≤ 1 ms |

The engine did not collect once in any run, so the sender thread's allocation (46–94 KB/s) is not a timing risk at
this rate: the first collection would come after many minutes, and its pause is bounded by the engine's small heap.
The first, uncorrected suite (keys forwarded from the probe, before the probe waited for the MIDI destination) had one
key a block late under `app` and one run that never opened its destination; both are fixed in the probes above.

**Not done:** `--midi-loopback --engine` (arrival, immediate output, and thru through the engine); Windows and Linux; a per-collection pause list from the engine (a
heartbeat sees the latest collection of its 250 ms interval; the total counts every one); the native layer's longest
render is kept from when the stream opened, so it includes the first callbacks.

## The engine by default (step 10)

On macOS the engine met every ADR 0043 target its probes measure (step 8): audio in the callback, scheduled MIDI
dispatched by its send-ahead deadline, live input from Core MIDI, and the loopback. So it is the default there. MIDI
arrival at the receiver, immediate output, and thru were measured in process (ADR 0043) but not yet through the
engine; that loopback probe does not take `--engine` yet. `App.UseEngineProcess` reads
`BLUESTONE_ENGINE`: `0` keeps the real-time work in Bluestone's process, `1` uses the engine on any platform, and
anything else gives the platform default, which is the engine on macOS and the in-process path on Windows and Linux.
Those platforms switch once the probes have run on native hardware (the Windows PC first, Linux later).

- **The shipped engine is NativeAOT.** The release workflow publishes `Bluestone.Engine` ahead of time into the
  application's folder, over the JIT engine copied there by the app's publish. The launcher prefers the native
  executable, and the app's own publish had not included one at all. CI publishes the engine with NativeAOT on every
  OS and fails on any trim or AOT warning (step 9).
- **A JIT engine (development builds) has tiered compilation off,** so it never replaces code while it plays. It
  compiles each method once, fully optimized. A quiet re-measurement on current code found no JIT pause over 0.14 ms
  with any setting (ADR 0043).
- **The in-process path is kept, not removed.** It is the fallback (`BLUESTONE_ENGINE=0`) and the default off macOS.
  Removing it, and the copies of `LiveRunner` and `PluginLiveStage` the engine carries (step 7), waits until the
  engine is the default everywhere.
- The probes report which engine ran from the engine's own JIT count (0 for NativeAOT), not from its path.

## The engine host and supervisor (step 4)

The skeleton is in place:

- **`Bluestone.Engine.Protocol`** (BCL only, AOT-safe, and not the plugin protocol: an architecture test checks it
  references nothing else) defines the messages: Hello, Welcome, Ready, Heartbeat (with the engine's collections,
  pause time, allocations, working set, and JIT count), Ping, Pong, Shutdown, Fault, and test commands. Frames are a
  length and a type, decoded strictly; random bytes decode or fail with a protocol error, nothing else.
- **`Bluestone.Engine`**, given a pipe and a token, connects, says Hello, reports Ready, sends a heartbeat every
  250 ms, answers pings, and stops on Shutdown or when Bluestone goes away. Test commands (crash, hang, fault) are
  obeyed only with `--allow-test-commands`. With `--spike` it still runs the step-2 smoke test.
- **`EngineSupervisor`** (Application) launches it on a pipe restricted to the current user, checks the handshake
  (version, token, process id), and watches for each kind of stop above: the process ending, 1 s without a message, a
  broken or invalid channel, and a reported fault. A stopped engine is killed if still running and replaced, with a
  warning in Messages ("The engine stopped (…) and was restarted; playback is stopped."). After 3 stops in 60 s it
  is left failed, with an error in Messages, until `RestartAsync`. Starting and restarting are activities on the
  status line. Integration tests do each of these with real engine processes: killed, hung, faulted, missing, a crash
  loop, and a clean shutdown that is not counted as a stop.
- The desktop app starts the engine alongside the main window when the engine is in use (step 10: by default on macOS).

Shared building blocks were copied, not extracted: the framing is about 300 lines and differs from the plugin
protocol's (no request ids, its own limits), and the launcher is short. Copying keeps the two protocols independent.

## Spike: the engine's code compiled ahead of time (step 2)

`src/dotnet/Bluestone.Engine` publishes a console host with NativeAOT. It references the ten assemblies the engine will
own: Audio, Domain, Midi, Playback, Plugins, Plugins.Protocol, Signal, and the three platform adapters. All ten are
rooted whole, so the trim and AOT analysis covers all of their code. The host compiles a 32,000-note plan, plays it on
the real playback thread, renders the probes' 32-strip graph offline, lists audio devices through the Rust layer, and
creates a Core MIDI client with a virtual port.

- **Analysis:** three problems, all in Bluestone.Plugins and both now fixed. The scan cache and quarantine files used
  reflection-based JSON; they now use source-generated JSON, which writes the same files (a test loads files written
  by the old code and saves them unchanged). The worker launcher read `Assembly.Location`, which is empty in an AOT
  binary; it already fell back to `AppContext.BaseDirectory`, so that warning is suppressed with the reason. Audio and
  Plugins are now marked `IsAotCompatible`, like the other eight, so ordinary builds keep all ten trim- and AOT-safe.
- **Result:** a 7.8 MB native binary, with `libbluestone_audio.dylib` beside it. It ran with **0 methods
  JIT-compiled**.

Apple M5 Max, macOS 27.2, .NET 10.0.300, 3 runs each (the first AOT run after publishing was slower from a cold disk
cache and is not counted):

| | NativeAOT | JIT (Release) |
| --- | ---: | ---: |
| Process start to `Main` | 6–12 ms | 25–34 ms |
| Compile the 32,000-note plan | 10–13 ms | 36–38 ms |
| First second of audio rendered offline | 23–26 ms | 72–74 ms |
| Steady-state render, per 128-frame block | 31.4 µs | 30.3 µs |
| Listing audio devices (Core Audio) | about 80 ms | — |
| Creating the Core MIDI client and a virtual port | about 14 ms | — |
| Methods JIT-compiled during the run | 0 | about 1,550 |

Ahead-of-time code is about 4% slower than fully optimized JIT code in steady state, because it has no dynamic
profile-guided optimization. That is about 1% of a 2.67 ms buffer. In return it never compiles while playing, which
ADR 0043 found causes pauses of up to 3.5 ms. The engine could report Ready about 150 ms after launch, most of it
opening devices.

Not covered by the spike: the code still in Bluestone.Application that will move into the engine (live routes,
`AudioPlayback`, parts of `PlaybackController`). It must stay AOT-safe as it moves; the analyzers check that once it
is in an `IsAotCompatible` assembly.

## Consequences

- Two processes to build, ship, sign, and supervise. The engine is AOT-compiled, so its code must stay trim- and
  AOT-safe: no reflection-based JSON or dynamic loading on its side.
- Edits reach the engine through a buffer swap and one control message. That is not on the real-time path, but it is
  measured: an edit should be audible within one block plus 5 ms.
- Debugging spans two processes. The engine writes its own log, and its faults are reported in Messages.
- A crash costs a restart of a few hundred milliseconds, plus a stopped transport. Plugin state survives it.
- Windows and Linux follow once macOS is complete. The engine runs the same code, and those platforms' thread
  policies and precise sleeps are measured then.
