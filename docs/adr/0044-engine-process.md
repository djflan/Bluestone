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

Still to do: graph descriptions (the audio graph's nodes, strips, and connections) follow the same pattern.
Separately, the chase sent when playback starts or seeks mid-song (`ChaseState.Compute`) runs on the playback thread
and allocates. Wrap chases are already computed when a plan is loaded. In the engine, the seek chase must use
preallocated state or be precomputed with the plan.

### Rules inside the engine (ADR 0043)

- **No allocation in steady state** on the real-time threads (audio callback, playback thread, MIDI input callbacks,
  live thread), checked with allocation tests.
- **A small, reference-light heap.** Plans and graphs live in shared memory, not on the managed heap.
- **No JIT during playback:** the engine is published with NativeAOT. If AOT is blocked, tiered compilation is off
  and ReadyToRun is used.
- **Collections are reported:** the engine counts every collection and its pause. A probe fails on any pause longer
  than 1 ms during playback.
- Thread policies as today: the macOS real-time policy for the playback thread and render threads, and MMCSS plus a
  1 ms timer on Windows (ADRs 0012, 0013, 0043).

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
10. Make the engine the only path; remove the in-process real-time path.

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
