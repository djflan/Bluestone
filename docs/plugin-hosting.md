# Plugin hosting

Bluestone hosts plugins **out of process** (ADR 0025). A plugin never runs inside the Bluestone process: it runs in a
worker process, `Bluestone.PluginWorker`, that Bluestone supervises. If a worker crashes, hangs, or misbehaves,
Bluestone keeps running, the affected instances report a status, their audio output becomes exact silence, and the
user can restart them without closing the project. Process isolation is crash isolation, not a security sandbox
([Security limitations](#security-limitations)).

## Scope

The worker hosts:

- Bluestone's **reference plugins**, built into the worker: `reference.gain`, `reference.sine`,
  `reference.transpose`, and `reference.delay`. Most process tests use them.
- Third-party **VST3** and **CLAP** plugins, through a Rust plugin host loaded only in the worker (ADR 0033).

Scanning, loading, parameters, state, audio and event processing, MIDI effects at plan time and live, delay
compensation, editor windows, hosting modes, and crash recovery work for both. Where each part was verified:

| Where | Plugins | What |
| ----- | ------- | ---- |
| CI (Linux x64, macOS arm64, Windows x64) | Reference plugins; Bluestone's test plugins as VST3 and CLAP (`NativePluginTests`, `ThirdPartyPluginDeviceTests`) | Everything in [Tests](#tests) except editors: scan, kinds, parameters, sample-accurate parameter changes, events, rendering into the plan, state, corrupt state, kill and restart, project save and reopen, an editor refused for a plugin without one |
| macOS arm64, by hand and in tests | The Legend HZ, REVERSIDE (VST3); Airwindows Consolidated, Surge XT (CLAP) (`InstalledPluginTests`) | Scan, load, audio out (a note for instruments, a sine through effects), a parameter set and state captured, worker killed while processing: Crashed, silence, restart restores the parameter and audio |
| macOS arm64, in tests | REVERSIDE (VST3), Airwindows Consolidated (CLAP) (`PluginEditorTests`) | Editor opens, closes, opens again; worker killed with the editor open; editor opens again after restart |
| macOS arm64, development probe | The Legend HZ (VST3), Airwindows Consolidated (CLAP) | Editor windows appear at the plugin's size (checked through the window list, not by eye); Airwindows sent parameter edits from its editor |
| macOS arm64, app and probe | SuperClassic, Scaler 3, Scaler 3 Audio, Melody Sauce 3 (VST3) | Scanned by the app at start; loaded, processed, and saved by the probe; not covered by tests |

`InstalledPluginTests` and `PluginEditorTests` skip with a reason when a plugin is missing, and the editor tests also
skip on CI and outside macOS. The CLAP test subjects, Airwindows Consolidated (MIT) and Surge XT 1.3.4 (GPL-3.0), were
installed from their GitHub releases into `~/Library/Audio/Plug-Ins/CLAP`; neither is part of Bluestone. What was not
verified is listed under [Not done](#not-done).

## Projects and processes

| Project | Runs in | References | Role |
| ------- | ------- | ---------- | ---- |
| `Bluestone.Plugins.Protocol` | both | BCL only | Control-plane frames and messages, the handshake, the memory-mapped block exchange |
| `Bluestone.Plugins` | Bluestone | Protocol | Worker launching and supervision, instance lifecycle and recovery, the real-time processing API, scanning |
| `Bluestone.Application/Plugins` | Bluestone | Plugins | `PluginLibrary`, `PluginDeviceHost`, `PluginEventRenderer`, `PluginHostingSettings`: the bridge to the project |
| `Bluestone.PluginWorker` | worker | Protocol, `bluestone_plugin_host` | Instance-host mode and scan mode; the reference plugins; `NativePlugin` over the Rust host |
| `src/native/crates/bluestone_plugin_host` | worker | (Rust) `vst3`, `clap-sys` | Loads VST3 and CLAP modules: instances, parameters, state, processing, host callbacks, editor windows; C ABI version 1 |
| `src/native/crates/bluestone_test_plugins` | tests only | (Rust) `vst3`, `clap-sys` | Test Gain, Test Sine, Test Transpose as a `.vst3` bundle and a `.clap` module, packaged by `src/dotnet/NativeTestPlugins.targets` |

Everything is C# except the plugin host, which is Rust because it loads native plugin binaries (ADR 0026's
native-interface reason; ADR 0033). The data plane stays C#: nothing has measured a need for anything else.
`Bluestone.Plugins` does not reference `Bluestone.PluginWorker`, so plugin code cannot be loaded into Bluestone's
process (`DependencyDirectionTests.Plugins_DoesNotReferenceTheWorker_SoNoPluginCanRunInTheHostProcess`).

```text
Bluestone process                                  Worker process (one per worker key; see Hosting modes)
-----------------                                  ------------------------------------------------------
PluginHostManager                                  Bluestone.PluginWorker --mode host --pipe NAME
  WorkerProcess  --- named pipe (control plane) ---  InstanceHostSession (one request at a time)
  PluginInstance --- mapped file (data plane)   ---  HostedInstance (one processing thread each)
                                                       IHostedPlugin: a reference plugin, or NativePlugin
                                                         -> bluestone_plugin_host (VST3, CLAP)
PluginScanner                                      Bluestone.PluginWorker --mode scan --pipe NAME
  one scanner process per module                     ModuleScanner (reads one module, then exits)
```

Launching a worker:

1. Bluestone creates a named-pipe server with a random name and `PipeOptions.CurrentUserOnly`.
2. It starts the worker directly, never through a shell: the `Bluestone.PluginWorker` app host beside
   `Bluestone.Plugins.dll`, else `dotnet Bluestone.PluginWorker.dll`. Standard output and error are drained
   continuously into a short ring of recent lines used in diagnostics.
3. It writes a random per-launch token to the worker's standard input (not the command line, which other users can
   read).
4. The worker connects and sends `Hello` (protocol version, token, process id, mode). Bluestone checks all four and
   answers `HelloAck`, or an error and closes. The whole handshake has a timeout (`HandshakeTimeout`).

If any step fails, the instance becomes `Unavailable(FailedToStart)` (`ProtocolError` for a bad `Hello`). **No code
path falls back to running the plugin in process**
(`PluginCrashRecoveryTests.AMissingWorker_FailsToStart_AndNothingRunsInTheHostProcess`).

A worker exits when it receives `Shutdown` or when its control pipe closes, so a dead Bluestone never leaves workers
behind (`WorkerProcessTests.AWorker_ExitsByItself_WhenItsControlPipeCloses_EvenWithAnInstanceRunning`). Disposing
`PluginHostManager` asks every worker to shut down, waits up to `ShutdownTimeout`, then kills the process tree
(`PluginCrashRecoveryTests.DisposingTheManager_LeavesNoWorkerProcessBehind`).

`PluginHostOptions` defaults:

| Option | Default | Meaning |
| ------ | ------- | ------- |
| `HandshakeTimeout` | 15 s | Launch to `HelloAck` |
| `RequestTimeout` | 10 s | Per control request; a timeout means the worker is hung and it is killed |
| `HeartbeatInterval`, `HeartbeatTimeout` | 1 s, 10 s | Silence for `HeartbeatTimeout` means hung: killed |
| `ShutdownTimeout` | 2 s | Wait for workers to exit before killing them |
| `CollectWait` | 2 ms | How long `ProcessBlock` spins for a block that is not done |
| `PipelineDepth` | 2 | Blocks in flight, 1 to `SlotCount` |
| `SlotCount` | 2 | Slots per exchange (`ExchangeOptions.DefaultSlotCount`; 2 to 8) |
| `PeriodicSnapshotInterval` | none | Capture state from every running instance this often |
| `DataDirectory` | a fresh folder under the system temporary directory | Where data-plane files go (the app uses `bluestone-plugins-<process id>` there) |
| `AllowWithinBluestone` | false | Needed even to ask for in-process hosting, which still throws |

## Plugins and formats

`IHostedPlugin` is the worker's seam: the reference plugins implement it in C#, and `NativePlugin` implements it over
the Rust host's C ABI (`Bluestone.PluginWorker/Native/NativeMethods.cs`, the worker's only unsafe code). The native
library is loaded only when a third-party plugin is first used. The data plane, failure policies, supervision, and
recovery are the same for every plugin.

- **Identity.** `PluginIdentity`: format, module ID, plugin ID, display name, vendor, kind, version. For VST3 and
  CLAP the module ID is the module's file name without its extension, and the plugin ID is the VST3 class ID (32 hex
  digits) or the CLAP plugin ID. The device definition ID is `plugin:<format>:<module>/<plugin>`, for example
  `plugin:vst3:LegendHZ/255BF0CB0F9A419992C1A715E3C58F48`. An instance has its own `PluginInstanceId`, a GUID that
  stays the same across restarts.
- **Kind.** CLAP: the `instrument` and `note-effect` features. VST3: `Instrument` in the subcategories, or the
  plugin's buses (no audio outputs with event input and output: a MIDI effect). MIDI generators that also make audio
  (Scaler 3, Melody Sauce 3) are instruments.
- **Parameters.** `uint` ids with normalized `double` values in [0, 1], with units, step counts, and traits
  (`ParameterTraits`). Hidden, read-only, and VST3 MIDI-mapping stand-ins are reported but not shown or automated
  (`ParameterDescriptor.IsUserFacing`); a plugin with more than 4096 keeps the user-facing ones first.
- **Events.** Notes, polyphonic and channel pressure, controllers, pitch bend, and program changes, with sample
  offsets. VST3 gets controllers, channel pressure, pitch bend, and program changes through the plugin's MIDI mapping
  as parameter changes (unmapped ones are dropped), and its legacy MIDI CC output comes back as events. CLAP gets
  notes as note events (or MIDI, if its note port prefers it) and the rest as MIDI. System exclusive reaches only the
  reference plugins.
- **State.** Opaque bytes plus a format string. Reference plugins: a versioned list of parameter values
  (`bluestone.reference-state`). VST3: component and controller state in one versioned container
  (`bluestone.vst3-state`). CLAP: the state extension's data in a versioned container (`bluestone.clap-state`).
- **Audio.** Bluestone always sees the channels it asked for; a plugin's own layout is adapted (a mono output feeds
  both channels, side inputs get silence, extra outputs are dropped). Transport (position, tempo, playing) goes with
  every block.
- **Latency.** Reported at creation; a change the plugin announces updates `PluginLatencyFrames`.

## Threading

- **Audio thread** (Bluestone): one thread per instance calls `PluginInstance.ProcessBlock(frames, input, output,
  inputEvents, outputEvents, transport)` and `QueueParameterChange(id, value, sampleOffset)`. These never lock,
  allocate, do I/O, log, or throw because of a plugin failure (argument misuse still throws). Audio is planar: channel
  *c* is `[c * frames, (c + 1) * frames)`. Parameter changes go into a preallocated queue and land on exactly the
  given sample (`ReferencePluginProcessTests.Gain_InAWorker_ScalesAudio_AndAMidBlockChangeLandsOnTheExactSample`).
  Zero allocation is asserted across a real worker (`PluginDataPlaneProcessTests.ProcessBlock_AcrossAWorker_AllocatesNothing`)
  and on the exchange alone (`BlockExchangeTests.HostSubmitAndCollect_AllocateNothing`).
- **Control threads** (Bluestone) use the async methods: `SetParameterAsync`, `GetParametersAsync`,
  `CaptureStateAsync`, `RestartAsync`, `OpenEditorAsync`, `CloseEditorAsync`, and the manager's create, destroy, and
  `ChangeHostingAsync`. Each request has a timeout.
- **Supervision** (Bluestone) runs on thread-pool threads: process exit, pipe failure, heartbeats. Status events are
  raised there, in order, one at a time, never on the audio thread.
- **Worker main thread** (`WorkerMainThread`) belongs to plugins: it runs the operating system's event loop (an
  accessory NSApplication on macOS, so the worker has no Dock icon), plugin timers, and each native instance's idle
  work. The worker's control loop runs on the thread pool and hands every request to the main thread, which both
  formats require for creating, configuring, saving, and showing a plugin. An idle handler that throws is logged to
  standard error and removed, so it cannot stop the loop.
- **Processing threads** (worker): one per instance, sharing the plugin with the control loop under a lock. The
  worker may block; Bluestone's real-time path never does.

## Protocol

Two channels per worker:

| | Control plane | Data plane |
| - | ------------- | ---------- |
| Transport | Named pipe (a Unix domain socket on macOS and Linux) | One memory-mapped file per instance |
| Carries | Create and destroy, state, parameters, editors, heartbeats, scanning, rendering, notifications, errors | Audio, events, timestamped parameter changes, transport |
| Threads | Control and supervision threads; never the audio thread | Bluestone's audio thread and the instance's processing thread |
| Blocking | Requests may wait, each with a timeout | Bluestone's side never blocks, locks, allocates, does I/O, or logs |

**Frames.** A little-endian `uint32` body length, then the body: `uint16` message type, `uint32` request id, payload.
Replies carry the request's id. Limits (`ProtocolLimits`): 4 MiB per frame, 4 KiB per string, 4096 parameters, 1024
plugins per module, 64 channels, 8192 frames per block, and a range check on every number. Decoding is strict:
malformed, truncated, oversized, unknown, out-of-range, or trailing data throws `ProtocolException` and nothing else,
and every length is checked before anything is allocated. Bluestone kills a worker that sends a bad frame and reports
`ProtocolError`.

**Version.** `ProtocolLimits.ProtocolVersion` is 3; both sides must agree in `Hello`. Version 3 added the module
path to `CreateInstance` and `RenderEvents`, units and traits to parameter descriptors, the editor messages, the
`Unsupported` scan result, and `PluginNotification`.

**Messages** (`MessageType`):

| Messages | Purpose |
| -------- | ------- |
| `Hello`, `HelloAck` | Handshake |
| `Ping`, `Pong` | Heartbeat |
| `CreateInstance`, `InstanceCreated`, `DestroyInstance` | Instance lifecycle |
| `CaptureState`, `StateResult`, `RestoreState`, `RestoreStateResult` | Plugin state |
| `SetParameter`, `GetParameters`, `ParameterValues` | Parameters outside the audio stream |
| `OpenEditor`, `CloseEditor` | Editor windows |
| `RenderEvents`, `RenderedEvents` | A MIDI effect rendered over the timeline at plan time |
| `ScanModule`, `ScanResult` | Scan mode |
| `PluginNotification` | The only frame a worker sends unasked (request id 0): `ParameterEdited`, `LatencyChanged`, `StateDirty`, `EditorClosed`, `ParametersChanged` |
| `Ack`, `Error`, `Shutdown` | Generic replies; ask the worker to exit |
| `InduceTestFault` | Tests only ([Test hooks](#test-hooks)) |

## Data plane

Each instance has one file, created by the worker in the data directory and named
`<instance id>-g<generation>.bluestone-exchange`. Named shared memory is Windows-only in .NET, so the mapping is
file-backed on every platform. The layout (`ExchangeLayout`):

- **Header** (128 bytes): magic, layout version, **generation**, slot count, max frames, channel counts, event and
  parameter-change capacities, record and slot sizes, the worker's heartbeat counter, and state flags (**faulted**,
  **closed**).
- **Slots** (`SlotCount`), each with a state word, a block header (block index, frames, transport position, tempo,
  playing, event and parameter-change counts, dropped-event counters, block status), planar float32 input audio,
  timestamped input events, timestamped parameter changes, planar float32 output audio, and output events.

Events are fixed-size `PluginEvent` records: sample offset, kind, channel, two data bytes, and up to 256 bytes of
inline system exclusive. Events beyond the block's capacity (256 by default), outside the block, or system exclusive
longer than 256 bytes are dropped and counted, never truncated silently. Both sides validate everything they read
from the other process, so a misbehaving peer cannot make either side index out of range. The only pointer code is
`Exchange/MappedRegion.cs`: it acquires the view's pointer once and hands out bounds-checked spans and `ref`s for
`Volatile` and `Interlocked`.

**Slot states.**

```text
          Bluestone TrySubmit         worker TryBeginBlock         worker CompleteBlock
  Free  ------------------->  Submitted  --------------->  Processing  --------------->  Done
   ^                             |                                                       |
   +-- Bluestone withdraws (its  |                 Bluestone TryCollect (block index     |
   |   wait expired before the --+                 matches), or discard of a late result |
   |   worker started)                                                                   |
   +-------------------------------------------------------------------------------------+
```

Only Bluestone moves Free to Submitted, Submitted back to Free, and Done to Free; only the worker moves Submitted to
Processing to Done. Each transition is one atomic write or compare-and-swap on the state word, with release/acquire
ordering so the data written before it is visible after it
(`BlockExchangeTests.OwnershipStateMachine_FreeSubmittedProcessingDoneFree`).

**Faults and generations.** When a worker dies, Bluestone calls `MarkFaulted()` on the instance's exchange before
anything else; from then on `TrySubmit` and `TryCollect` return `Faulted` without touching mapped memory. The
generation increases on every (re)creation and every worker loss; a handle that finds another generation in the
header returns `Stale` and faults itself, so it can never read a newer worker's data
(`BlockExchangeTests.AFileRecreatedUnderANewGeneration_MakesTheOldHandleStale`). Disposal waits for any call in
flight, is idempotent, and is safe in any state.

## Pipelining and latency

With pipeline depth *d* (`PipelineDepth`, default 2), each `ProcessBlock` call submits block *k* and collects block
*k* − (*d* − 1). Output lags input by *d* − 1 blocks, and `PluginInstance.ReportedLatencyFrames` is the plugin's own
latency plus (*d* − 1) × `MaxBlockFrames`. Use one block size throughout. While the pipeline fills, output is exact
silence (`ProcessOutcome.Priming`).

`TryCollect` spins for at most `CollectWait`. A block that is not done by then is **late**: Bluestone withdraws it if
the worker has not started it, otherwise abandons it, and the failure policy decides the output. When an abandoned
block's result arrives later, the block-index check discards it; it is never delivered as another block's audio
(`BlockExchangeTests.ALateResult_IsDiscarded_NeverDeliveredForALaterBlock`).

Pipeline latency at 48 kHz (the plugin's own latency and both processes' scheduling jitter come on top):

| Block size | Depth 2 (default) | Depth 3 |
| ---------: | ----------------- | ------- |
| 64 frames (1.3 ms) | 64 frames, 1.3 ms | 128 frames, 2.7 ms |
| 128 frames (2.7 ms) | 128 frames, 2.7 ms | 256 frames, 5.3 ms |
| 256 frames (5.3 ms) | 256 frames, 5.3 ms | 512 frames, 10.7 ms |
| 512 frames (10.7 ms) | 512 frames, 10.7 ms | 1024 frames, 21.3 ms |

Depth 1 adds no pipeline latency, but the worker's wake-up, the plugin's processing, and the hand-back must all fit
inside the same callback while Bluestone spins; any scheduling hiccup becomes a late block and silence. Depth 2
gives the worker a whole block period; larger depths tolerate more jitter for more latency. The tests prove ordering
and correctness, not real-time performance under a real device.

**Worker wake-up.** The processing thread polls for submitted blocks: after the last block it spins for 200 µs,
then yields until 5 ms, then sleeps 1 ms between checks. While blocks keep arriving (block periods below about 5 ms)
each active instance keeps roughly one core busy; idle instances cost little. In the sleep phase a new block waits up
to about 1 ms on macOS and Linux and up to one timer tick (often 15.6 ms) on Windows. Depth 2 or more absorbs this
for continuous playback; the first blocks after a pause may be late. A real wake-up primitive (futex, eventfd, a
Windows event) would remove both costs; it is not built.

## Failure policies

`PluginInstanceRequest.FailurePolicy` decides what `ProcessBlock` outputs whenever the plugin cannot deliver a block
(late, plugin error, worker dead, not started, quarantined, unloaded):

- **`Silence`** (default, and the only choice for instruments and MIDI effects): exact digital zero and no events.
- **`BypassInput`** (opt-in, audio effects with matching channel counts only): the dry input, delayed by the pipeline
  so its timing does not jump. Plugin latency is not added to the bypass path.

Stale plugin audio is never returned
(`PluginCrashRecoveryTests.KillingTheWorkerMidStream_MarksCrashedOnce_SilencesAtOnce_AndRestartRestoresStateAndParameters`,
`PluginCrashRecoveryTests.BypassInput_AfterACrash_PassesTheDryInputDelayedByThePipeline_NeverStaleAudio`). Events
in a block that could not be submitted are lost; a note-off lost that way can leave a note hanging in the plugin
until it is restarted.

## Status, supervision, and recovery

`PluginInstance.Status` and the `StatusChanged` event. States: `Starting`, `Running`, `Unavailable` (with a reason:
`Crashed`, `Hung`, `FailedToStart`, `ProtocolError`, `Missing`), `Restarting`, `Quarantined`, `Unloaded`. Any other
transition than these throws:

| From | To |
| ---- | -- |
| Starting | Running, Unavailable, Unloaded |
| Running | Unavailable, Restarting (moved to another worker by a hosting change), Unloaded |
| Unavailable | Restarting, Quarantined, Unloaded |
| Restarting | Running, Unavailable, Unloaded |
| Quarantined | Restarting (manual restart only), Unloaded |
| Unloaded | none |

**Supervision.** A worker is dead when its process exits, its pipe breaks, it misses heartbeats for
`HeartbeatTimeout` (killed: `Hung`), a request times out (killed: `Hung`), or it sends an invalid frame (killed:
`ProtocolError`). The first signal is reported once. Then, for every instance on that worker:

1. its exchange is faulted first, so the audio thread outputs the failure policy at once;
2. the link is dropped and the generation bumped;
3. `LastWorkerLoss` records the process, the reason, and every instance that was in it;
4. the status becomes `Unavailable(reason)`, with exactly one event per instance;
5. the last captured state and the remembered parameter values are kept;
6. the restart policy is applied.

`PluginHostManager.WorkerLost` is raised once per loss. Instances on other workers are not affected
(`PluginCrashRecoveryTests.KillingOneOfSeveralWorkers_LeavesTheOthersProducingCorrectAudio`).

**Restart policy** (`RestartPolicy`). Automatic restart is off by default (in the app: "Restart crashed plugins
automatically" in the Plugins view, which applies to running instances from their next failure). When on, at most
`MaxRestarts` (3) restarts happen within any sliding `Window` (5 minutes), each after a back-off that doubles from
`InitialBackoff` (1 s) up to `MaxBackoff` (30 s); the next failure **quarantines** the instance. A quarantined
instance stays down until a manual restart, which also resets the counters
(`PluginCrashRecoveryTests.AWorkerThatCrashesAtStartup_WithAutomaticRestart_EndsQuarantined_NotInALoop`,
`RestartGuardTests`).

**Manual restart** (`PluginInstance.RestartAsync`, from `Unavailable` or `Quarantined`) starts a worker as the
hosting mode says, recreates the instance under a new generation, restores the last snapshot, then applies the
remembered parameter values (they are newer than the snapshot). Nothing else has to be closed.

## Hosting modes

How instances share worker processes (ADR 0034). A crash takes down every instance in its worker and nothing else,
so the mode trades the blast radius of a crash against the number of processes (each worker is a .NET process of
roughly 50 to 75 MB before the plugin's own memory, sampled on macOS arm64).

| `HostingMode` | Instances that share a worker |
| ------------- | ----------------------------- |
| `Together` | Every instance in this mode |
| `ByManufacturer` | Plugins with the same vendor from the scan (trimmed, any case), across formats and modules; a plugin without a vendor is hosted by plugin |
| `ByPlugin` | Plugins from the same module (one VST3 bundle or CLAP file; a VST3 and a CLAP build are different modules). **The app's default** (`PluginHostingSettings.DefaultMode`) |
| `Individually` | None: one worker per instance. The default of `PluginInstanceRequest.Hosting` |
| `WithinBluestone` | **Not implemented.** Refused unless `AllowWithinBluestone` is set, and then throws `NotSupportedException` |

- **Worker keys.** `WorkerKeys.For(mode, plugin, instance, architecture)`; equal keys share a worker. Shared keys
  include the worker's processor architecture, because x64 and ARM64 binaries cannot share a process (today every
  hosted plugin has the worker's architecture; others are `Unsupported` at scan time).
- **Settings** (`PluginHostingSettings`, per user in `plugin-hosting.json` beside `audio-settings.json`, not in the
  project): the global mode, automatic restart, and a mode per plugin by device definition ID, which wins over the
  global mode. Unknown or in-process modes in the file are dropped.
- **Changing a mode** (`PluginHostManager.ChangeHostingAsync`, `PluginDeviceHost.ApplyHostingAsync`) moves every
  instance whose worker key changes and keeps the instance: a running one has its state captured and goes Running →
  Restarting → Running in its new worker; a crashed, hung, or quarantined one is restarted there from its last
  snapshot. Instances whose key does not change are not touched; a worker left without instances exits. A move is a
  restart: a block or two of silence, held notes cut, and an open editor closes with its instance.
- **What went down together.** The device strip shows under a crashed device which other devices went down with it,
  and the messages list says it once. **Restart** restarts every device from that worker that is still down, so they
  share a worker again. Each plugin device says which process it is in ("Hosted by plugin (Surge XT) · shares its
  process with Pad").
- **Crash counts** (`PluginDeviceHost.CrashCounts`): per plugin, for this session, the worker losses that took it
  down and how many of them also took down other plugins (any of which may have caused it). Not saved.
- **In the Plugins view**: a Hosting section (global mode and automatic restart), a mode per plugin ("Global (By
  plugin)" or a mode), and for a plugin that crashed, its count and **Host individually**. Changes are saved and
  applied at once; the result line says how many devices were restarted in a new process.

## State persistence

- The project owns plugin state; the worker only holds the running instance.
- `CaptureStateAsync` asks the **live** plugin for its state and keeps it, with the parameter values, as
  `LastSnapshot`. The application stores it in the project ([The application bridge](#the-application-bridge)).
- `PluginInstanceRequest.RestoreFrom` restores a snapshot when an instance is created; recovery restores
  `LastSnapshot`. Periodic snapshots are off by default (`PeriodicSnapshotInterval`).
- A crashed plugin is never asked for state: only the last snapshot and the remembered parameter values exist
  (`PluginCrashRecoveryTests.CaptureState_FromACrashedInstance_IsRefused_AndTheLastSnapshotIsKept`).
- State the plugin rejects (corrupt, unknown version) is reported in `LastStateRestoreError`. A reference plugin then
  runs with default parameters (`PluginCrashRecoveryTests.CorruptState_IsReported_AndTheInstanceRunsWithDefaults`);
  what a VST3 or CLAP plugin holds then is up to the plugin.

## Scanning

`PluginScanner` launches **one scanner process per module**, with a hard timeout (`ScanTimeout`, 20 s) covering
launch, handshake, and the scan. Nothing about a module is read in Bluestone's process. Each module yields its plugin
descriptors or a `ScanFailure`: `Crashed`, `TimedOut`, `Malformed`, `ProtocolError`, or `Unsupported`. Scanning never
throws because of a bad module.

- **Modules.** A VST3 module (`.vst3`: a bundle, or a single file on Windows) or a CLAP module (`.clap`: a bundle on
  macOS, a file elsewhere) is loaded by the plugin host in the scanner process and asked for its plugins. Loading:
  macOS bundles through CFBundle (VST3 `bundleEntry` gets the bundle); Windows VST3 bundles load
  `Contents/<arch>-win/<name>.vst3`; Linux VST3 bundles load `Contents/<arch>-linux/<name>.so`; CLAP modules on
  Windows and Linux are plain shared libraries. A `.bluestone-reference-plugin` file is a small JSON manifest listing
  reference plugins and is never loaded as code. Every other file is `Malformed`.
- **Architecture.** Before loading, the scanner reads the binary's headers (Mach-O, including universal binaries;
  PE; ELF). A module without code for the worker's architecture is `Unsupported` ("built for x86_64 only; this
  worker runs on arm64 and cannot load it"), cached, and not quarantined. There is no Rosetta or second-architecture
  worker.
- **Cache.** `scan-cache.json`, keyed by path plus file size and last-write time (for a bundle, the total size and
  latest write time of its binaries and `Info.plist`), written atomically (temporary file, then rename). A changed
  fingerprint triggers a rescan. An unreadable cache is treated as empty and reported in `PluginScanner.Diagnostics`.
- **Quarantine.** `scan-quarantine.json`, beside the cache (the app keeps both in `<application data>/Bluestone/plugin-cache`).
  A module that crashed or hung its scanner is quarantined and skipped later **without launching a scanner**, until
  `RescanAsync(path, ignoreQuarantine: true)` or `ClearQuarantine(path)`. Malformed and unsupported modules are
  ordinary failures: cached, not quarantined. A scanner that fails before it was given the module does not
  quarantine the module.

### The plugin library and the Plugins view

`PluginLibrary` (`Bluestone.Application/Plugins`) holds the scan folders and what the last scan found in them. It
scans through `IPluginScanner`, which `PluginScanner` implements (view-model tests use a fake that launches nothing).

- **Folders**, in order: the app passes `<application data>/Bluestone/plugins`, then `PluginLibrary.StandardFolders()`:

  | OS | Folders |
  | -- | ------- |
  | macOS | `~/Library/Audio/Plug-Ins/VST3`, `/Library/Audio/Plug-Ins/VST3`, `~/Library/Audio/Plug-Ins/CLAP`, `/Library/Audio/Plug-Ins/CLAP` |
  | Windows | `%LocalAppData%\Programs\Common\VST3`, `%CommonProgramFiles%\VST3`, `%LocalAppData%\Programs\Common\CLAP`, `%CommonProgramFiles%\CLAP` |
  | Linux | `~/.vst3`, `/usr/local/lib/vst3`, `/usr/lib/vst3`, `~/.clap`, `/usr/local/lib/clap`, `/usr/lib/clap` |

  `FindModules` lists, by name, every non-hidden file directly in each folder, every `.vst3` and `.clap` bundle, and
  `.vst3` and `.clap` modules in subfolders up to three levels down (vendors often install into one); a bundle is
  never searched inside. Missing folders are skipped. When two modules provide the same definition ID, the first wins.
- **Scan** (at start): every module, through the cache, skipping quarantined modules.
- **Rescan all**: every module again, ignoring the cache, so new, changed, and removed modules are found. Quarantined
  modules stay skipped.
- **Retry**: one quarantined module again. If it reads cleanly it leaves quarantine; if it crashes or hangs its
  scanner again it stays quarantined.
- After each, `Changed` is raised. `PluginDeviceHost` takes the new list (`SetAvailable`) and raises
  `AvailableChanged`; the main view model rebuilds the device catalog, starts workers for devices whose plugin was
  found, and recompiles. A newly found plugin appears in the device strip without a restart; a device whose plugin is
  gone stops and shows "Not installed".

The **Plugins view** (`PluginsViewModel`, `PluginsWindow`; Window ▸ Plugins… in the macOS menu bar, File ▸ Plugins…
elsewhere) shows the hosting settings, the scan folders, the available plugins with kind, vendor, version, and
module, the modules that could not be read and why, and the quarantined modules, each with **Retry**. **Rescan all**
shows a progress bar and the module being scanned; one operation runs at a time and the buttons are disabled
meanwhile. The result is a line such as "Rescanned. 3 plugins available from 4 modules. 1 module quarantined."

## The application bridge

`PluginDeviceHost` (`Bluestone.Application/Plugins`) connects the project to the workers; the project stays the
authority (ADR 0025).

- A plugin device has the definition ID `plugin:<format>:<module>/<plugin>`. Each available plugin joins the device
  catalog as data, with the parameters its worker reports, never with an in-process factory.
- `SyncAsync(project)` gives every plugin device whose plugin is available an instance, created from the device's
  saved `PluginState` and parameter values, with the hosting mode and restart policy from the settings, and destroys
  instances whose device is gone. A device whose plugin is missing gets no worker, keeps its state, and shows "Not
  installed".
- Parameter values changed in the project go to running plugins on the next sync, only where they differ from what
  the bridge last sent or recorded. `RecordEdit` stores an edit the plugin made itself (from a `ParameterEdited`
  notification) as an undoable parameter change, merged per parameter like the strip's sliders, and never sends it
  back, so a plugin that keeps moving is not pulled back to an older value.
- `CaptureStateAsync` and `CaptureAllAsync` store a running plugin's state and parameter values in the project as one
  undoable step; the app does this before every save (not on autosave). A crashed plugin is never asked; the last
  stored state stays.
- `StatusOf(device)` gives the device strip its words ("Running", "Running, editor open", "Starting", "Crashed",
  "Not responding", "Failed to start", "Stopped after repeated crashes", "Not installed", …) and the hosting line; `RestartAsync` restarts from
  the last captured state.
- `SetSampleRateAsync` restarts every instance at a new rate from its captured live state each time an audio device
  opens (`PluginDeviceHostTests.ARateChange_*`).

## Editors

**Open editor** in the device strip (for a running VST3 or CLAP plugin; `OpenEditorAsync`) asks the worker to open
the plugin's editor in a top-level window of its own: an NSWindow on macOS (VST3 `NSView`, CLAP `cocoa`), a Win32
window on Windows (`HWND`, `win32`), an X11 window on Linux (`X11EmbedWindowID`, `x11`, with the plugin's timers and
file descriptors served by the worker's run loop). A CLAP plugin that can only float shows its own window. The window
follows the plugin's resize requests and passes user resizes to the plugin; content scale is given on Windows and
Linux (macOS sizes are points). Closing the window or `CloseEditorAsync` destroys the view; a window the user closes
is reported (`EditorClosed`). A worker that dies takes its window with it: the device shows Crashed, and after a
restart the editor opens again. Keyboard focus is whatever the operating system gives the worker's window. Edits made
in the editor reach the project as described in [The application bridge](#the-application-bridge).

## Plugin MIDI effects

**At plan time** (ADR 0028). `PluginDeviceHost.Extend` attaches a `PluginEventRenderer` to the device catalog. When
the plan is compiled, the chain runner hands each plugin MIDI effect the events it handles and its own automation, and
the renderer sends `RenderEvents` to that device's worker. The worker runs a fresh copy of the plugin from the
project's stored state and parameter values over the timeline, in blocks of the instance's block size, with the
tempo map as transport, and replies with what the plugin put out. The live instance is not touched.

- Ticks become sample frames on the tempo map and come back the same way; an output that answers an input (the n-th
  out at a frame for the n-th in) takes that input's exact tick, and a note shorter than a frame is lengthened to one
  frame on the way in. Note-on and note-off pairs become notes again; a note that never ends is closed at the last
  input and reported. Limits: 131,072 events and 32,768 parameter changes per request.
- A worker that is gone or not running, does not answer within the request timeout (it is then stopped), or reports a
  plugin error leaves the events unchanged and adds a diagnostic. The plan always compiles.
- `PlaybackController` compiles off the caller's thread when a project has such a device; the app compiles again
  when a plugin MIDI effect's status changes.

**Live** (ADR 0035). Live input (MIDI thru, the on-screen keyboard, auditioned notes) passes through every device on
the track's live route. A plugin MIDI effect on the route is processed by its own running instance through the data
plane (`PluginLiveStage`; the plan-time render is separate); its output comes one pipeline block later. While its
worker cannot deliver, events pass through unchanged, and every note it had put out is released first. A plugin
instrument at the end of the route gets the notes in its `PluginNode` on the audio thread, at the first frame of the
next block. Routes that change release what they sound.

## Plugins in the audio engine

When an audio device is open (ADR 0032), each plugin instrument or audio effect whose audio is connected somewhere
becomes a `PluginNode` in the render graph. Every block it calls `ProcessBlock` with the chain's audio so far
(effects), the software-instrument feed's events and the device's automation scheduled in that block, and the
transport; plugins run every block, playing or not. Instances must run at the device's rate and accept the engine's
block size, or they are reported and left silent.

- On a start, stop, seek, or loop wrap, the notes the node started are released first. An edit that changes the
  schedule releases only the notes the new schedule no longer holds at the play position; an edit that leaves it
  unchanged (a fader, a send, a clip gain) keeps held notes sounding (`AudioPluginTests.ReplacingTheSchedule_*`,
  `SoftwareInstrumentAudioTests.AMixerEdit_*`).
- A worker that fails gives silence under the instance's failure policy, never stale audio, and the engine carries on
  (`AudioPluginTests.AKilledWorker_LeavesExactSilence_*`).
- **Delay compensation** (ADR 0035; see [architecture.md](architecture.md#delay-compensation)): each node's latency
  (`ReportedLatencyFrames`: pipelining plus the plugin's own) is compensated across the graph. A `LatencyChanged`
  notification gets the graph rebuilt, and a once-a-second check catches a latency that changed without one (a
  restarted plugin). `reference.delay` delays its input by exactly the latency it reports and announces a change when
  its Latency parameter or state is set, so compensation is tested sample by sample with real workers.

## Security limitations

Process isolation is **crash isolation, not a security boundary**:

- A worker runs as the same user, with the same operating-system permissions as Bluestone.
- There is no filesystem, network, or inter-process sandboxing. A plugin can read and write anything Bluestone can.
- The pipe is restricted to the current user, and the per-launch token only proves that the connecting process is
  the one Bluestone launched; neither protects against code already running as the user.
- Hosting modes decide which plugins share a process, not what a process may do: "Individually" is the smallest blast
  radius for a crash, not a sandbox.

Operating-system sandboxing (App Sandbox, seccomp, AppContainer) is not done and not planned: plugins need the
network and the file system (licences, presets, samples), and ADR 0034 chose crash isolation with hosting modes.

## Platform notes

- **Data plane**: file-backed memory mappings on every platform, in a fresh folder under the system temporary
  directory, removed when the manager is disposed. On Windows a mapped file cannot be deleted, so cleanup there is
  best effort until then.
- **Control plane**: .NET named pipes: Unix domain sockets in the temporary directory on macOS and Linux
  (`CoreFxPipe_<name>`), real named pipes on Windows.
- **Architectures**: a worker can load only plugin binaries of its own architecture. Hosting others would need one
  worker build per architecture (on macOS, an x64 worker under Rosetta); not built.
- **Crash reports**: on macOS every `Environment.FailFast` (the FailFast test, the scanner crash test) leaves a report
  in `~/Library/Logs/DiagnosticReports`.

## Test hooks

All failure behaviours are test-only and explicit:

- `InduceTestFault` (sent by the internal `PluginInstance.InduceTestFaultAsync`): `FailFast`
  (`Environment.FailFast`), `Hang` (the worker stops answering on both planes), `GarbageFrames` (invalid bytes on the
  control pipe).
- `BLUESTONE_PLUGINWORKER_TEST_CRASH_AT_STARTUP=1` in the worker's environment (set only through the internal
  `PluginHostOptions.WorkerEnvironment`) makes the worker kill itself before connecting: no message can reach a worker
  before its connection exists. It kills itself rather than calling `FailFast` so the repeated restarts in that test
  do not fill the crash-report folder.
- A module file whose first line is `#bluestone-test: crash-scanner` makes the scanner call `Environment.FailFast`;
  `#bluestone-test: hang-scanner` makes it hang. **The scanner crash tests use this marker, not a crashing native
  binary.**

Crash tests use real processes: `Process.Kill()` from outside, `Environment.FailFast` inside, or a heartbeat kill.
No crash notification is mocked.

## Tests

Unit tests are in `src/dotnet/Bluestone.Tests.Unit/Plugins/` (and `Presentation/`, `Audio/`, `Signal/`, `Architecture/`
for the ones named there); process tests are in `src/dotnet/Bluestone.Tests.Integration/Plugins/` and
`src/dotnet/Bluestone.Tests.Integration/Audio/`; headless UI tests are in `src/dotnet/Bluestone.Tests.Ui/`. The
integration project references the worker with `ReferenceOutputAssembly="false"`, so the worker is built and copied
beside the tests without being loaded.

### Acceptance scenarios

| # | Scenario | Tests (real worker processes) |
| - | -------- | ----------------------------- |
| 11 | A worker crashes; Bluestone keeps running, the project survives, the device shows as unavailable, and can be restarted | `PluginCrashRecoveryTests.KillingTheWorkerMidStream_*`, `PluginDeviceHostTests.Scenarios11And13_*`, `PluginDeviceStripTests` |
| 12 | A restarted worker is restored from persisted state; automation references stay valid | `PluginDeviceHostTests.Scenario12_*`, `PluginCrashRecoveryTests.ASnapshot_RestoresIntoANewInstance` |
| 13 | Several workers; one fails, the others keep working | `PluginCrashRecoveryTests.KillingOneOfSeveralWorkers_LeavesTheOthersProducingCorrectAudio`, `PluginDeviceHostTests.Scenarios11And13_*` |
| 15 | A module crashes the scanner; Bluestone carries on, reports it, and skips it next time | `PluginScannerProcessTests.ACrashingModule_IsQuarantined_SkippedNextTime_AndRetriedOnRequest_WhileOthersStillScan` |

These use the reference plugins. Scenario 13 hosts its devices individually; under the app's default (by plugin) two
copies of one plugin share a worker and go down together, which `HostingModeProcessTests` covers. Scenarios 11 and
12 with VST3 and CLAP are covered by `NativePluginTests`, `ThirdPartyPluginDeviceTests`, and `InstalledPluginTests`.

### By area

| What | Tests |
| ---- | ----- |
| Frames of every message, strict decoding, fuzzing, version mismatch, protocol version 3 fields and notifications | `FrameCodecTests`, `HandshakeTests` |
| Exchange state machine, withdraw, late-result discard, pipelining, faults, generations, disposal, dropped counts; two-thread stress with pattern-checked audio; zero allocation | `BlockExchangeTests`, `PipelineLatencyTests` |
| The data plane across a real worker, zero allocation | `PluginDataPlaneProcessTests` |
| Reference plugins (sample-accurate gain, polyphonic deterministic sine, transpose passing controllers and SysEx), state | `ReferencePluginTests`, `ReferencePluginProcessTests` |
| Kill mid-stream, hung worker, protocol error, FailFast, missing worker, missing plugin, corrupt state, several workers, startup crash ending quarantined, bypass after a crash | `PluginCrashRecoveryTests` |
| No worker survives the manager; a worker exits when its pipe closes | `PluginCrashRecoveryTests.DisposingTheManager_LeavesNoWorkerProcessBehind`, `WorkerProcessTests` |
| Restart arithmetic with an explicit clock; status transitions | `RestartGuardTests`, `PluginInstanceStatusTests` |
| In-process hosting refused; bypass rules | `PluginHostManagerTests` |
| Hosting modes: worker keys, settings and the settings file, moves with state, shared crashes, crash counts, automatic restart at run time | `HostingModeTests`, `HostingModeProcessTests` |
| Scanning, cache, bundle fingerprints, quarantine, timeout, malformed modules, manifests | `PluginScannerProcessTests`, `ScanCacheTests`, `ModuleScannerTests` |
| Architecture checks on Mach-O, PE, and ELF headers | `BinaryArchitectureTests` |
| Folders, bundle discovery, rescan and retry; the device catalog follows | `PluginLibraryTests`, `PluginLibraryProcessTests` |
| The Plugins view and Device View: listing, progress, retry, hosting choices, a shared crash, Host individually | `PluginsViewModelTests`, `PluginDeviceStripTests`, `PluginsViewTests` (headless, with the mouse) |
| The bridge: save, reopen, kill, restart, automation still valid, a missing plugin, removing a device, rate changes | `PluginDeviceHostTests` |
| VST3 and CLAP through the test plugins: scan, process, state, render, crash and restart, project devices, edits both ways | `NativePluginTests`, `ThirdPartyPluginDeviceTests` |
| Installed VST3 and CLAP plugins; editors (macOS) | `InstalledPluginTests`, `PluginEditorTests` |
| Plugin MIDI effects in the plan: transposition, automation, taps, killed and hung workers | `PluginMidiInThePlanTests`, `ChainRunnerTests.AnOutOfProcessDevice_*` |
| Plugins in the audio engine: pitch through a mixer strip, connected audio, a killed worker, release on locate, zero allocation, instrument tracks | `AudioPluginTests`, `SoftwareInstrumentAudioTests` |
| Live input through a plugin MIDI effect and instrument at the measured latency; killed workers leave no hanging note | `LiveInputPluginTests` |
| Delay compensation sample by sample, a latency change, a send to a return | `DelayCompensationTests`, `LatencyCompensationTests` |
| Only the worker loads the native host; plugin code never in Bluestone's process | `DependencyDirectionTests` |

## Not done

Not verified:

- Editors on Windows and Linux (the code builds and the rest of the plugin path runs in CI; no window was opened
  there); closing or resizing an editor window by hand; changing a hosting mode with an editor open.
- Plugin MIDI output from a third-party MIDI effect (none installed: Scaler 3 and Melody Sauce 3 are instruments that
  also put out MIDI, which Bluestone does not route from an instrument).
- Third-party plugins live, delay-compensated, or played through the audio engine on a real audio device (those tests
  use the reference plugins).
- Intel-only plugins on Apple silicon (refused as `Unsupported`; tested on synthetic headers).
- Plugin timing under a real device: measured only with the reference plugins.

Not built:

- **VST3 and CLAP features**: system exclusive to native plugins, VST3 note expression, CLAP note expressions and
  parameter modulation, MPE, audio-port configurations, side-chain inputs, routing an instrument plugin's MIDI output,
  reloading a VST3 component on `kReloadComponent`, a protocol message for a failed worker idle handler (today it is
  logged and that instance's notifications stop).
- **Workers for a second architecture** (Rosetta).
- **Editors embedded in Bluestone's own window.**
- **Wake-up primitives** (futex, eventfd, Windows events) for the data plane.
- **MIDI 2.0** events on the data plane.
- **In-process hosting** of trusted plugins.
- **Operating-system sandboxing** of workers (not planned; ADR 0034).
