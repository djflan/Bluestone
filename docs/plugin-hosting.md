# Plugin hosting

Bluestone hosts plugins **out of process**. A plugin never runs inside the Bluestone process: it runs in a separate
worker process, `Bluestone.PluginWorker`, supervised by the main process. If a worker crashes, hangs, or misbehaves,
Bluestone keeps running, the affected instances report a status, their audio output becomes exact silence, and the
user can restart them without closing the project.

**Scope today.** The worker hosts only Bluestone's built-in **reference plugins** (`reference.gain`,
`reference.sine`, `reference.transpose`). It does not load any third-party plugin binary and Bluestone does not
support any third-party plugin format yet. Real VST3 loading is deferred (see [Deferred](#deferred)); nothing in
this package should be read as VST3 support.

**Process isolation is crash isolation, not a security sandbox.** See [Security limitations](#security-limitations).

Everything here is C#. The language policy (`docs/daw-epic-handoff.md`, decision 17) allows Rust for a part of
this only with a measured performance reason or a native ABI constraint. Neither exists yet for the plugin data plane:
no native plugin binary is loaded, and the data plane has not been measured to need it. (The audio device callback
is Rust for a measured reason; ADR 0032.)

## Projects

| Project | Runs in | References | Role |
| ------- | ------- | ---------- | ---- |
| `Bluestone.Plugins.Protocol` | both | BCL only | Control-plane frames and messages, the handshake, the memory-mapped block exchange |
| `Bluestone.Plugins` | Bluestone | Protocol | Worker launching and supervision, instance lifecycle and recovery, the real-time processing API, scanning |
| `Bluestone.PluginWorker` | worker process | Protocol | Instance-host mode and scan mode; the reference plugins |

`Bluestone.Plugins` does not reference `Bluestone.PluginWorker`, so the plugin code cannot even be loaded into the
host (`DependencyDirectionTests.Plugins_DoesNotReferenceTheWorker_SoNoPluginCanRunInTheHostProcess`).

## Process model

```text
Bluestone process                                     Worker process (one per instance by default)
---------------                                     ---------------------------------------------
PluginHostManager                                   Bluestone.PluginWorker --mode host --pipe NAME
  WorkerProcess  --- named pipe (control plane) ---  InstanceHostSession (one request at a time)
  PluginInstance --- mapped file (data plane)   ---  HostedInstance (one processing thread each)
                                                       IHostedPlugin (reference plugins only)
PluginScanner                                       Bluestone.PluginWorker --mode scan --pipe NAME
  one scanner process per module file               ModuleScanner (reads one manifest, then exits)
```

Launching a worker:

1. The host creates a named-pipe server with a random name and `PipeOptions.CurrentUserOnly`.
2. It starts the worker directly, never through a shell: the `Bluestone.PluginWorker` app host beside
   `Bluestone.Plugins.dll`, else `dotnet Bluestone.PluginWorker.dll`. Standard input, output, and error are redirected;
   output and error are drained continuously into a short ring of recent lines used in diagnostics.
3. A random per-launch token is written to the worker's standard input (not the command line, which other users
   can read).
4. The worker connects and sends `Hello` (protocol version, token, process id, mode). The host checks all four and
   answers `HelloAck`, or an error and closes. The whole handshake has a timeout (`HandshakeTimeout`).

If any step fails, the instance becomes `Unavailable(FailedToStart)` (or `ProtocolError` for a bad Hello). **No
code path falls back to running the plugin in process**
(`PluginCrashRecoveryTests.AMissingWorker_FailsToStart_AndNothingRunsInTheHostProcess`).

The worker exits when the host sends `Shutdown` or when the control pipe closes, so a dead host never leaves
workers behind (`WorkerProcessTests.AWorker_ExitsByItself_WhenItsControlPipeCloses_EvenWithAnInstanceRunning`).
Disposing the manager asks every worker to shut down, waits a bounded time, then kills the process tree
(`PluginCrashRecoveryTests.DisposingTheManager_LeavesNoWorkerProcessBehind`).

## Control plane versus data plane

| | Control plane | Data plane |
| - | ------------- | ---------- |
| Transport | Named pipe (a Unix domain socket on macOS and Linux) | A memory-mapped file per instance |
| Used for | Create/destroy, state, parameters, health, scanning, errors | Audio, events, timestamped parameter changes, transport |
| Threads | Control threads, `async`; never the audio thread | The audio thread (host) and one processing thread per instance (worker) |
| Blocking | Requests may wait (each has a timeout) | Host side never blocks, locks, allocates, does I/O, or logs |

### Control-plane frames

A frame is a little-endian `uint32` body length, then the body: `uint16` message type, `uint32` request id, and
the payload. Replies carry the request's id. Limits (`ProtocolLimits`): 4 MiB per frame, 4 KiB per string,
4096 parameters, 1024 plugins per module, and range checks on every number. Decoding is strict: malformed,
truncated, oversized, unknown, out-of-range, or trailing data throws `ProtocolException` and nothing else, and
every length is checked before anything is allocated. A host that receives a bad frame kills the worker and
reports `ProtocolError`.

Messages: `Hello`/`HelloAck`, `Ping`/`Pong`, `CreateInstance`/`InstanceCreated`, `DestroyInstance`,
`CaptureState`/`StateResult`, `RestoreState`/`RestoreStateResult`, `SetParameter`, `GetParameters`/`ParameterValues`,
`Ack`, `Error`, `Shutdown`, `ScanModule`/`ScanResult`, `RenderEvents`/`RenderedEvents` (protocol version 2; see
"Plugin MIDI effects in the plan"), and the test-only `InduceTestFault`.

Plugin identity (`PluginIdentity`: format, module id, plugin id, display name, vendor, kind, version) and instance
identity (`PluginInstanceId`, a GUID that stays the same across restarts) are different types. Parameters are
`uint` ids with normalized `double` values in [0, 1].

### The block exchange (data plane)

Each instance has one file, created by the worker in the host's data directory and named
`<instance id>-g<generation>.bluestone-exchange`. Named shared memory is Windows-only in .NET, so the mapping is
file-backed on every platform. The layout (`ExchangeLayout`):

- **Header**: magic, layout version, **generation**, slot count (at least 2), max frames, channel counts, event and
  parameter-change capacities, record and slot sizes, the worker's heartbeat counter, and state flags
  (**faulted**, **closed**).
- **Slots** (`SlotCount`, default 2), each with a state word, a block header (block index, frames, transport
  position, tempo, playing, event and parameter-change counts, dropped-event counters, per-block status), planar
  float32 input audio, timestamped input events, timestamped parameter changes, planar float32 output audio, and
  output events.

Events are fixed-size records (`PluginEvent`: sample offset, kind, channel, two data bytes, and up to 256 bytes of
inline system exclusive). Events that do not fit the block's capacity, lie outside the block, or are system
exclusive messages longer than 256 bytes are dropped and counted, never truncated silently. Both sides validate
everything they read from the other process, so a misbehaving peer cannot make either side index out of range.

The only pointer code is `Exchange/MappedRegion.cs`: it acquires the view's pointer once and hands out
bounds-checked spans and `ref`s for `Volatile` and `Interlocked`.

#### Slot state machine

```text
          host TrySubmit            worker TryBeginBlock          worker CompleteBlock
  Free  ----------------->  Submitted  ----------------->  Processing  ----------------->  Done
   ^                           |                                                            |
   |   host withdraws (its     |                                                            |
   |   wait expired before     |                    host TryCollect (block index matches),  |
   +---- the worker started) --+                    or discard of a late result             |
   +----------------------------------------------------------------------------------------+
```

Only the host moves Free to Submitted, Submitted back to Free (withdraw), and Done to Free; only the worker moves
Submitted to Processing to Done. Each transition is a single atomic write or compare-and-swap on the state word,
with release/acquire ordering so the data written before it is visible after it
(`BlockExchangeTests.OwnershipStateMachine_FreeSubmittedProcessingDoneFree`).

#### Faults and generations

When a worker dies, the host calls `MarkFaulted()` on the instance's exchange **before** anything else. From then
on every `TrySubmit` and `TryCollect` returns `Faulted` without touching mapped memory (the call itself only sets
the header flag). The generation increases on every (re)creation and on every worker loss; a handle that finds a
different generation in the header returns `Stale` and faults itself, so a stale handle can never read a newer
worker's data (`BlockExchangeTests.AFileRecreatedUnderANewGeneration_MakesTheOldHandleStale`). Disposal waits for
any call in flight, is idempotent, and is safe in any state.

## Pipelining and latency

With pipeline depth *d* (`PluginHostOptions.PipelineDepth`, default 2, at most the slot count), each
`ProcessBlock` call submits block *k* and collects block *k* − (*d* − 1). The output therefore lags the input by
*d* − 1 blocks, and `PluginInstance.ReportedLatencyFrames` is the plugin's own latency plus (*d* − 1) ×
`MaxBlockFrames`. Use one block size throughout. While the pipeline fills, output is exact silence
(`ProcessOutcome.Priming`).

`TryCollect` spins for at most `CollectWait` (default 2 ms). A block that is not Done by then is **late**: the host
takes it back if the worker has not started it, otherwise abandons it, and the failure policy decides the output.
When the abandoned block's result arrives later, the block-index check discards it; it is never delivered as the
audio of another block (`BlockExchangeTests.ALateResult_IsDiscarded_NeverDeliveredForALaterBlock`).

Added latency at 48 kHz (plugin latency comes on top; scheduling jitter of both processes comes on top of
everything and is not included):

| Block size | Depth 1 | Depth 2 (default) | Depth 3 |
| ---------: | ------- | ----------------- | ------- |
| 64 frames (1.3 ms) | 0 blocks, see below | 64 frames, 1.3 ms | 128 frames, 2.7 ms |
| 128 frames (2.7 ms) | 0 blocks, see below | 128 frames, 2.7 ms | 256 frames, 5.3 ms |
| 256 frames (5.3 ms) | 0 blocks, see below | 256 frames, 5.3 ms | 512 frames, 10.7 ms |
| 512 frames (10.7 ms) | 0 blocks, see below | 512 frames, 10.7 ms | 1024 frames, 21.3 ms |

Depth 1 adds no pipeline blocks, but the worker's wake-up, the plugin's processing, and the hand-back must all fit
inside the same audio callback while the host spins; any scheduling hiccup becomes a late block and silence. It is
not "zero additional latency": it moves the cost into the callback and into dropouts. Depth 2 gives the worker a
whole block period. Larger depths tolerate more jitter at the cost of more latency. The audio engine now drives instances from
its render thread, but plugin timing under a real device has only been exercised with the reference plugins, not
measured; the tests prove ordering and correctness, not real-time performance.

### Worker wake-up: spin, then back off

The worker's processing thread looks for submitted blocks in a loop. After the last block it spins for 200 µs,
then yields for up to 5 ms, then sleeps 1 ms between checks. The trade-off:

- **CPU**: while blocks keep arriving (block periods below about 5 ms), each active instance keeps roughly one core
  busy spinning or yielding. Idle instances cost little.
- **Latency**: in the sleep phase a new block waits up to about 1 ms on macOS and Linux, and up to one timer tick
  (often 15.6 ms) on Windows, which can exceed a short block period. Depth 2 or more absorbs this for continuous
  playback; the first blocks after a pause may be late.

A real wake-up primitive (futex, eventfd, or a Windows event) would remove both costs. It is deferred, not built.

## Processing API and threading

- One **audio thread** per instance calls `PluginInstance.ProcessBlock(frames, input, output, inputEvents,
  outputEvents, transport)` and `QueueParameterChange(id, value, sampleOffset)`. These never lock, allocate, do
  I/O, log, or throw because of a plugin failure (argument misuse still throws). Audio is planar: channel *c* is
  `[c * frames, (c + 1) * frames)`. Parameter changes go into a preallocated queue and land on exactly the given
  sample (`ReferencePluginProcessTests.Gain_InAWorker_ScalesAudio_AndAMidBlockChangeLandsOnTheExactSample`).
  Zero allocation is asserted across a real worker
  (`PluginDataPlaneProcessTests.ProcessBlock_AcrossAWorker_AllocatesNothing`) and on the exchange alone
  (`BlockExchangeTests.HostSubmitAndCollect_AllocateNothing`).
- **Control threads** use the async methods: `SetParameterAsync`, `GetParametersAsync`, `CaptureStateAsync`,
  `RestartAsync`, and the manager's create and destroy. Each request has a timeout; a timeout means the worker is
  hung and it is killed.
- **Supervision** runs on thread-pool threads: process exit, pipe failure, and heartbeat checks. Status events are
  raised there, in order, one at a time, never on the audio thread.
- In the worker, the control loop and each instance's processing thread share the plugin under a lock. The worker
  may block; the host's real-time path never does.

## Failure policies

`PluginInstanceRequest.FailurePolicy` decides what `ProcessBlock` outputs whenever the plugin cannot deliver a block
(late, plugin error, worker dead, not started, quarantined, unloaded):

- **`Silence`** (default, and the only choice for instruments and MIDI effects): exact digital zero and no events.
- **`BypassInput`** (opt-in, audio effects with matching channel counts only): the dry input, delayed by the
  pipeline so its timing does not jump. Plugin latency is not added to the bypass path.

Under either policy, stale plugin audio is never returned
(`PluginCrashRecoveryTests.KillingTheWorkerMidStream_MarksCrashedOnce_SilencesAtOnce_AndRestartRestoresStateAndParameters`,
`PluginCrashRecoveryTests.BypassInput_AfterACrash_PassesTheDryInputDelayedByThePipeline_NeverStaleAudio`).
Events submitted with a block that could not be submitted are lost; a note-off lost that way can leave a note
hanging in the plugin until it is restarted.

## Instance status

`PluginInstance.Status` and the `StatusChanged` event. States: `Starting`, `Running`, `Unavailable` (with a reason:
`Crashed`, `Hung`, `FailedToStart`, `ProtocolError`, `Missing`), `Restarting`, `Quarantined`, `Unloaded`.

| From | To |
| ---- | -- |
| Starting | Running, Unavailable, Unloaded |
| Running | Unavailable, Unloaded |
| Unavailable | Restarting, Quarantined, Unloaded |
| Restarting | Running, Unavailable, Unloaded |
| Quarantined | Restarting (manual restart only), Unloaded |
| Unloaded | none |

## Supervision and recovery

A worker is considered dead when its process exits, its pipe breaks, it misses heartbeats for `HeartbeatTimeout`
(it is then killed: `Hung`), a request times out (killed: `Hung`), or it sends an invalid frame (killed:
`ProtocolError`). Whichever signal comes first is reported once. Then, for every instance on that worker:

1. its exchange is **faulted first**, so the audio thread stops reading it and outputs the failure policy at once;
2. the link is dropped and the generation bumped;
3. the status becomes `Unavailable(reason)`, with exactly one event per instance;
4. the last captured state and the remembered parameter values are kept;
5. the restart policy is applied.

Instances on other workers are not affected (`PluginCrashRecoveryTests.KillingOneOfSeveralWorkers_LeavesTheOthersProducingCorrectAudio`).

**Restart policy** (`RestartPolicy`): automatic restart is **off** by default. When on, at most `MaxRestarts`
restarts happen within any sliding `Window`, each after a back-off that doubles from `InitialBackoff` up to
`MaxBackoff`; the next failure **quarantines** the instance. A quarantined instance stays down until a manual
`RestartAsync`, which also resets the counters
(`PluginCrashRecoveryTests.AWorkerThatCrashesAtStartup_WithAutomaticRestart_EndsQuarantined_NotInALoop`,
`RestartGuardTests`).

**Manual restart** (`PluginInstance.RestartAsync`) works from `Unavailable` and `Quarantined`. It starts a worker as
the isolation policy says, recreates the instance under a new generation, restores the last snapshot, then applies
the remembered parameter values (they are newer than the snapshot), and processing resumes. Nothing else has to be
closed.

## State persistence

- The project owns plugin state; the worker only holds the running instance.
- `CaptureStateAsync` asks the **live** plugin for its opaque state and format string and keeps it, together with
  the parameter values, as `LastSnapshot`. The application persists `LastSnapshot`.
- Periodic snapshots are off by default (`PluginHostOptions.PeriodicSnapshotInterval`).
- `PluginInstanceRequest.RestoreFrom` restores a snapshot when an instance is created; recovery restores
  `LastSnapshot`.
- A crashed plugin is never asked for state: after a crash only the last snapshot and the remembered parameter
  values exist (`PluginCrashRecoveryTests.CaptureState_FromACrashedInstance_IsRefused_AndTheLastSnapshotIsKept`).
- State the plugin rejects (corrupt, unknown version) is reported in `LastStateRestoreError` and the instance runs
  with default parameters (`PluginCrashRecoveryTests.CorruptState_IsReported_AndTheInstanceRunsWithDefaults`).
  The reference plugins' state is a versioned list of parameter values (format `bluestone.reference-state`).

## Isolation policies

| Policy | Status | Meaning |
| ------ | ------ | ------- |
| `PerInstance` | Implemented, default, tested | One worker per instance |
| `PerModule` | Implemented, tested | Instances of the same module share a worker; a crash takes down all of them |
| `SharedTrusted` | Implemented (opt-in), tested | All instances that choose it share one worker |
| `InProcessTrusted` | **Not implemented** | Refused unless `AllowInProcessTrusted` is set, and even then throws `NotSupportedException` |

## Scanning and quarantine

`PluginScanner` launches **one scanner process per module file**, with a hard timeout (`ScanTimeout`) covering
launch, handshake, and the scan. Nothing about a module is read in the Bluestone process. Each file yields its plugin
descriptors or a `ScanFailure`: `Crashed`, `TimedOut`, `Malformed`, or `ProtocolError`. Scanning never throws
because of a bad module.

- **Modules**: in scan mode, a file ending `.bluestone-reference-plugin` is a module, a small JSON manifest listing
  plugins. Every other file is `Malformed`. The scanner never loads a file as code.
- **Cache**: `scan-cache.json`, keyed by path plus file size and last-write time, written atomically (temporary
  file, then rename). A changed fingerprint triggers a rescan. An unreadable cache is treated as empty and
  reported in `PluginScanner.Diagnostics`.
- **Quarantine**: `scan-quarantine.json`. A module that crashed or hung its scanner is quarantined and skipped on
  later scans **without launching a scanner**, until `RescanAsync(path, ignoreQuarantine: true)` or
  `ClearQuarantine(path)`. Malformed files are ordinary failures: cached, not quarantined. A scanner that fails
  before it was given the module does not quarantine the module.
- **Test hook**: a module whose first line is `#bluestone-test: crash-scanner` makes the scanner call
  `Environment.FailFast`, and `#bluestone-test: hang-scanner` makes it hang. **The crash in the scanner tests is
  induced by this marker, not by a native binary.**

Tested by `PluginScannerProcessTests` (crash, quarantine, skip without launch, rescan, malformed, good module beside
them, timeout, fingerprint change) and `ScanCacheTests`.

## Test hooks

All failure behaviours are test-only and explicit:

- `InduceTestFault` messages (sent by the internal `PluginInstance.InduceTestFaultAsync`): `FailFast`
  (`Environment.FailFast`), `Hang` (the worker stops answering on both planes), `GarbageFrames` (invalid bytes on the
  control pipe).
- `BLUESTONE_PLUGINWORKER_TEST_CRASH_AT_STARTUP=1` in the worker's environment (set only through the internal
  `PluginHostOptions.WorkerEnvironment`) makes the worker kill itself before connecting. An environment variable
  is used because no message can reach a worker before its connection exists. It kills itself rather than calling
  `FailFast` so the repeated restarts in that test do not fill the operating system's crash-report folder.
- The scanner markers above.

Crash tests use real processes: `Process.Kill()` from outside, `Environment.FailFast` inside, or a heartbeat kill.
No crash notification is mocked.

## Security limitations

Process isolation here is **crash isolation, not a security boundary**:

- A worker runs as the same user, with the same operating-system permissions as Bluestone.
- There is no filesystem, network, or inter-process sandboxing. A plugin can read and write anything Bluestone can.
- The pipe is restricted to the current user and the per-launch token only proves that the connecting process is
  the one Bluestone launched; neither protects against code already running as the user.

Operating-system sandboxing (App Sandbox, seccomp, AppContainer, and so on) is deferred.

## Platform notes

- **Data plane**: file-backed memory mappings on every platform. Default directory: a fresh folder under the system
  temporary directory, removed when the manager is disposed. On Windows a file still mapped cannot be deleted, so
  cleanup there is best effort until the manager is disposed.
- **Control plane**: .NET named pipes, which are Unix domain sockets in the temporary directory on macOS and Linux
  (`CoreFxPipe_<name>`) and real named pipes on Windows.
- **Architectures**: a worker must have the same architecture as the plugin binaries it loads; x64 and ARM64 plugin
  binaries cannot be mixed in one process. Hosting both would need one worker build per architecture (on macOS,
  an x64 worker under Rosetta). Not needed for the reference plugins, and not built.
- **Crash reports**: on macOS every `Environment.FailFast` (the FailFast test and the scanner crash test) leaves a
  report in `~/Library/Logs/DiagnosticReports`.
- **Verified on**: macOS ARM64 only. The code is cross-platform C#, but nothing here has been run on Linux or
  Windows yet; the CI matrix will be the first to do so.

## The application bridge

`Bluestone.Application/Plugins/PluginDeviceHost` connects the project to the workers; the project stays the
authority (ADR 0025):

- A plugin device in a chain has a definition ID `plugin:<format>:<module>/<plugin>`. Each available plugin is
  added to the device catalog as data (with the parameters its worker reports), never with an in-process factory.
- `SyncAsync(project)` gives every plugin device whose plugin is available an instance, created from the device's
  saved `PluginState` and parameter values, and destroys instances whose device is gone. A device whose plugin is
  missing gets no worker, keeps its state, and shows "Not installed".
- `CaptureStateAsync` / `CaptureAllAsync` store a running plugin's state and parameter values in the project as one
  undoable step; the app does this before every save. A crashed plugin is never asked for state; the last stored
  state stays.
- `StatusOf(device)` turns the instance status into the device strip's words ("Running", "Crashed",
  "Not responding", "Stopped after repeated crashes"), and `RestartAsync` restarts from the last captured state.
- The desktop app scans `<application data>/Bluestone/plugins` at start, one scanner process per module.

Tests: `PluginDeviceHostTests` (scenarios 11 to 13 through the project: save, reopen, kill, restart, automation still
valid; one of two workers killed; a missing plugin; removing a device stops its worker) and
`PluginDeviceStripTests` (the strip shows `[Reference Gain: Crashed]` with Restart after a real kill).

### Plugin MIDI effects in the plan

`PluginDeviceHost.Extend` also attaches a `PluginEventRenderer` to the device catalog (ADR 0028). When the plan is
compiled, the chain runner hands each plugin MIDI effect the events it handles and its own automation, and the
renderer sends a `RenderEvents` request to that device's worker. The worker runs a fresh copy of the plugin from the
project's stored state and parameter values over the timeline, in blocks of the instance's block size, with the
tempo map as transport, and replies with what the plugin put out. The live instance is not touched.

- Ticks become sample frames on the tempo map and come back the same way; an output that answers an input (the n-th
  out at a frame for the n-th in) takes that input's exact tick, and a note shorter than a frame ends a frame after it
  starts on the way in. Note-on and note-off pairs become notes again; a note that never ends is closed at the last
  input and reported. Limits: 131,072 events and 32,768 parameter changes per request.
- A worker that is gone or not running, does not answer within the request timeout (it is then stopped), or reports a
  plugin error leaves the events unchanged and adds a diagnostic. The plan always compiles.
- `PlaybackController` compiles off the caller's thread when a project has such a device; the app compiles again
  when a plugin MIDI effect's status changes.

Tests (`PluginMidiInThePlanTests`, real workers): the reference Transpose moves notes in the plan and the transposed
bytes reach a loopback port while SysEx and controllers go around it; its automation changes the transposition from
its tick on and a tap carries its output to another track; a killed worker and a hung worker both leave the notes
unchanged with a reason, and a restart brings the transposition back.

## Plugins in the audio engine

When an audio device is open (ADR 0032), each plugin instrument or audio effect in a chain whose audio is connected
somewhere becomes a `PluginNode` in the render graph. Every block it calls `ProcessBlock` with the chain's audio so far
(effects), the software-instrument feed's events and the device's automation scheduled in that block, and the
transport; plugins run every block, playing or not. On a start, stop, seek, loop wrap, or a new schedule after an
edit, the notes the node started are released first. A worker that fails gives silence under the instance's failure
policy, never stale audio, and the engine carries on (`AudioPluginTests.AKilledWorker_LeavesExactSilence_*`). Each
node's latency (`ReportedLatencyFrames`) is reported per device; nothing compensates for it yet. Instances must run
at the device's rate and accept the engine's block size, or they are reported and left silent.

Tests (real workers, offline driver): `AudioPluginTests` (the Sine at its pitch through a mixer strip after one
block of latency, the Gain processing connected audio, a killed worker, release on locate, zero allocation) and
`SoftwareInstrumentAudioTests` (an instrument track through the signal graph, the plugin bridge, and the project audio
graph).

## Acceptance scenarios

| # | Scenario | Tests (real worker processes) |
| - | -------- | ----------------------------- |
| 11 | A worker crashes; Bluestone keeps running, the project survives, the device shows as unavailable, and can be restarted | `PluginCrashRecoveryTests.KillingTheWorkerMidStream_*`, `PluginDeviceHostTests.Scenarios11And13_*`, `PluginDeviceStripTests` |
| 12 | A restarted worker is restored from persisted state; automation references stay valid | `PluginDeviceHostTests.Scenario12_*`, `PluginCrashRecoveryTests.ASnapshot_RestoresIntoANewInstance` |
| 13 | Several workers; one fails, the others keep working | `PluginCrashRecoveryTests.KillingOneOfSeveralWorkers_LeavesTheOthersProducingCorrectAudio`, `PluginDeviceHostTests.Scenarios11And13_*` |
| 15 | A module crashes the scanner; Bluestone carries on, reports it, and skips it next time | `PluginScannerProcessTests.ACrashingModule_IsQuarantined_SkippedNextTime_AndRetriedOnRequest_WhileOthersStillScan` |

These use the reference plugins in `Bluestone.PluginWorker`, not third-party plugins: the crash, recovery, and
isolation machinery is real, the plugins are Bluestone's own.

## Implemented and tested

| What | Tests |
| ---- | ----- |
| Frame round trip of every message, strict decoding, fuzzing, version mismatch | `FrameCodecTests`, `HandshakeTests` |
| Exchange state machine, withdraw, late-result discard, pipelining, faults, generations, disposal, dropped counts | `BlockExchangeTests`, `PipelineLatencyTests` |
| Zero allocation on submit and collect; two-thread stress with pattern-checked audio | `BlockExchangeTests.HostSubmitAndCollect_AllocateNothing`, `BlockExchangeTests.TwoThreads_UnderStress_NeverDeliverAnotherBlocksAudio` |
| Plugin MIDI effects rendered into the plan: transposition, automation, taps, killed and hung workers | `PluginMidiInThePlanTests`, `ChainRunnerTests.AnOutOfProcessDevice_*` |
| The same across a real worker | `PluginDataPlaneProcessTests` |
| Reference plugins (sample-accurate gain, polyphonic deterministic sine, transpose passing controllers and SysEx), state | `ReferencePluginTests`, `ReferencePluginProcessTests` |
| Kill mid-stream, Unavailable(Crashed) once, silence at once, restart restores state and parameters | `PluginCrashRecoveryTests.KillingTheWorkerMidStream_MarksCrashedOnce_SilencesAtOnce_AndRestartRestoresStateAndParameters` |
| Hung worker, protocol error, FailFast, missing worker, missing plugin | `PluginCrashRecoveryTests` |
| Several workers, one killed; per-module and shared workers | `PluginCrashRecoveryTests` |
| Startup crash with auto restart ends quarantined; manual restart resets | `PluginCrashRecoveryTests.AWorkerThatCrashesAtStartup_WithAutomaticRestart_EndsQuarantined_NotInALoop` |
| No worker survives the manager; worker exits when its pipe closes | `PluginCrashRecoveryTests.DisposingTheManager_LeavesNoWorkerProcessBehind`, `WorkerProcessTests` |
| Restart arithmetic with an explicit clock; status transition table | `RestartGuardTests`, `PluginInstanceStatusTests` |
| In-process hosting refused; bypass rules | `PluginHostManagerTests` |
| Scanning, cache, quarantine | `PluginScannerProcessTests`, `ScanCacheTests`, `ModuleScannerTests` |

Unit tests are in `src/Bluestone.Tests.Unit/Plugins/`; process tests are in `src/Bluestone.Tests.Integration/Plugins/`
and run as one non-parallel collection. The integration project references the worker with
`ReferenceOutputAssembly="false"` so the worker is built and copied beside the tests without being loaded.

## Deferred

- **Real VST3 loading and parameter enumeration.** No third-party plugin format is supported. The worker's
  `IHostedPlugin` is the seam where a native hosting layer would go (that is where the language policy allows
  Rust, given a native-ABI reason).
- **Plugin editors**: separate windows versus embedding, focus and keyboard handling, DPI scaling, and what happens
  to an open editor when its worker dies.
- **Plugin delay compensation across a graph.** Instances report their latency; nothing compensates for it yet.
- **Operating-system sandboxing** of workers.
- **Wake-up primitives** (futex, eventfd, Windows events) for the data plane.
- **MIDI 2.0** events on the data plane.
- **In-process hosting** of trusted plugins.
- **Scanning from the UI.** The app scans `<application data>/Bluestone/plugins` once at start; rescanning and
  clearing quarantine are API calls (`PluginScanner.RescanAsync`) without a menu yet.
