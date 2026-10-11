# Plugin hosting

Bluestone hosts plugins **out of process**. A plugin never runs inside the Bluestone process: it runs in a separate
worker process, `Bluestone.PluginWorker`, supervised by the main process. If a worker crashes, hangs, or misbehaves,
Bluestone keeps running, the affected instances report a status, their audio output becomes exact silence, and the
user can restart them without closing the project.

**Scope today.** The worker hosts Bluestone's built-in **reference plugins** (`reference.gain`, `reference.sine`,
`reference.transpose`) and third-party **VST3** and **CLAP** plugins through a Rust plugin host loaded only in the
worker (ADR 0033). Scanning, loading, parameters, state, processing, MIDI effects at plan time, crash recovery, and
editor windows work; exactly what was verified on which plugin and machine is in
[Third-party plugins](#third-party-plugins-vst3-and-clap). Bluestone has no audio engine yet, so plugins process
blocks when asked (as the tests do) but nothing drives them from playback.

**Process isolation is crash isolation, not a security sandbox.** See [Security limitations](#security-limitations).

Everything here is C# except the plugin host, which is Rust because it loads native plugin binaries (ADR 0026's
native-interface reason; ADR 0033). The data plane stays C#: it has not been measured to need anything else.

## Projects

| Project | Runs in | References | Role |
| ------- | ------- | ---------- | ---- |
| `Bluestone.Plugins.Protocol` | both | BCL only | Control-plane frames and messages, the handshake, the memory-mapped block exchange |
| `Bluestone.Plugins` | Bluestone | Protocol | Worker launching and supervision, instance lifecycle and recovery, the real-time processing API, scanning |
| `Bluestone.PluginWorker` | worker process | Protocol, `bluestone_plugin_host` | Instance-host mode and scan mode; the reference plugins; `NativePlugin` over the Rust host |
| `native/crates/bluestone_plugin_host` | worker process | (Rust) `vst3`, `clap-sys` | Loads VST3 and CLAP modules; instances, parameters, state, processing, host callbacks, editor windows; C ABI version 1 |
| `native/crates/bluestone_test_plugins` | tests only | (Rust) `vst3`, `clap-sys` | Test Gain, Test Sine, Test Transpose as a `.vst3` bundle and a `.clap` module, packaged by `src/NativeTestPlugins.targets` |

`Bluestone.Plugins` does not reference `Bluestone.PluginWorker`, so the plugin code cannot even be loaded into the
host (`DependencyDirectionTests.Plugins_DoesNotReferenceTheWorker_SoNoPluginCanRunInTheHostProcess`).

## Process model

```text
Bluestone process                                     Worker process (one per instance by default)
---------------                                     ---------------------------------------------
PluginHostManager                                   Bluestone.PluginWorker --mode host --pipe NAME
  WorkerProcess  --- named pipe (control plane) ---  InstanceHostSession (one request at a time)
  PluginInstance --- mapped file (data plane)   ---  HostedInstance (one processing thread each)
                                                       IHostedPlugin: reference plugins, or NativePlugin
                                                         → bluestone_plugin_host (VST3, CLAP)
PluginScanner                                       Bluestone.PluginWorker --mode scan --pipe NAME
  one scanner process per module                    ModuleScanner (reads one manifest or loads one module, then exits)
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
`Ack`, `Error`, `Shutdown`, `ScanModule`/`ScanResult`, `RenderEvents`/`RenderedEvents` (see "Plugin MIDI effects in
the plan"), `OpenEditor`/`CloseEditor`, `PluginNotification`, and the test-only `InduceTestFault`. Protocol version 3
added the module path to `CreateInstance` and `RenderEvents`, units and traits to parameter descriptors, the editor
messages, the `Unsupported` scan result, and `PluginNotification`: the only frame a worker sends unasked (request id 0),
for an edit the plugin made itself, a latency change, state the plugin marked dirty, a closed editor, or a changed
parameter list. Its values are checked per kind like every other field.

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
whole block period. Larger depths tolerate more jitter at the cost of more latency. None of these figures has
been measured under a real audio device yet; the tests prove ordering and correctness, not real-time performance.

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
- **The worker's main thread** (`WorkerMainThread`) belongs to plugins: it runs the operating system's event loop
  (an accessory NSApplication on macOS, so the worker has no Dock icon), plugin timers, and each native instance's
  idle work. The control loop runs on the thread pool and hands every request to the main thread, which both plugin
  formats require for creating, configuring, saving, and showing a plugin. The native library is loaded only when a
  third-party plugin is first used; a worker with only reference plugins never loads it.

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

- **Modules**: a VST3 bundle (`.vst3`, or a single-file module on Windows) or a CLAP module (`.clap`, a bundle on
  macOS and a file elsewhere) is loaded by the plugin host in the scanner process and asked for its plugins. A file
  ending `.bluestone-reference-plugin` is a small JSON manifest listing reference plugins and is never loaded as code.
  Every other file is `Malformed`.
- **Architecture**: before loading, the scanner reads the binary's headers (Mach-O, including universal binaries;
  PE; ELF). A module without code for the worker's architecture is `Unsupported` ("built for x86_64 only; this worker
  runs on arm64"), cached, and not quarantined. There is no Rosetta or second-architecture worker.
- **Cache**: `scan-cache.json`, keyed by path plus file size and last-write time (for a bundle, the total size and
  latest write time of its binaries and Info.plist), written atomically (temporary file, then rename). A changed fingerprint triggers a rescan. An unreadable cache is treated as empty and
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

### The plugin library and the Plugins view

`Bluestone.Application/Plugins/PluginLibrary` holds the list of scan folders and what the last scan found in them. It
scans through `IPluginScanner`, which `PluginScanner` implements (view-model tests use a fake that launches nothing).

- **Folders**: a list, in order. The desktop app passes `<application data>/Bluestone/plugins` and then
  `PluginLibrary.StandardFolders()` (`App.PluginFolders`): on macOS `~/Library/Audio/Plug-Ins/VST3`,
  `/Library/Audio/Plug-Ins/VST3`, and the same for `CLAP`; on Windows `%LocalAppData%\Programs\Common\VST3`,
  `%CommonProgramFiles%\VST3`, and the same for `CLAP`; on Linux `~/.vst3`, `/usr/local/lib/vst3`, `/usr/lib/vst3`,
  `~/.clap`, `/usr/local/lib/clap`, `/usr/lib/clap`. `FindModules` lists, by name, every file directly in each folder
  (leaving out hidden files such as `.DS_Store`), every `.vst3` and `.clap` bundle, and `.vst3` and `.clap` modules in
  subfolders up to three levels down (vendors often install into one); a bundle is never searched inside. Missing
  folders are skipped. The first module that provides a plugin wins when two provide the same definition ID.
- **Scan** (at start): every module, through the cache, skipping quarantined modules.
- **Rescan all**: every module again, ignoring the cache, so new, changed, and removed modules are found. Quarantined
  modules stay skipped.
- **Retry**: one quarantined module again (`RescanAsync(path, ignoreQuarantine: true)`). If it reads cleanly it
  leaves quarantine; if it crashes or hangs its scanner again it stays quarantined.
- After each of these, `Changed` is raised. `PluginDeviceHost`, built over the library, takes the new list of
  available plugins (`SetAvailable`) and raises `AvailableChanged`; the main view model then rebuilds the device
  catalog, starts workers for devices whose plugin was found, and recompiles. A newly found plugin appears in the
  device strip without a restart; a device whose plugin is gone stops and shows "Not installed".

The **Plugins view** (`PluginsViewModel`, `PluginsWindow`; Window ▸ Plugins… in the macOS menu bar, File ▸ Plugins…
elsewhere) lists the scan folders, the available plugins with their kind, vendor, version, and module, the modules
that could not be read and why, and the quarantined modules, each with **Retry**. **Rescan all** shows a progress bar
and the module being scanned; one operation runs at a time and the buttons are disabled meanwhile. The result is a
line such as "Rescanned. 3 plugins available from 4 modules. 1 module quarantined."

Tested by `PluginLibraryTests` and `PluginsViewModelTests` (fake scanner: listing, progress, retry, disabled commands
while scanning, a rescan adding a plugin to the device strip), `PluginLibraryProcessTests` (real scanner processes:
a module that crashes its scanner is quarantined, a retry that crashes again keeps it there, a retry after the module
is fixed takes it out, and its plugin is added from the strip and runs in a worker, all without a restart), and
`PluginsViewTests` (headless: the view opened from the File menu and from the macOS Window menu, Rescan all and Retry
clicked with the mouse, real scanner processes).

## Third-party plugins (VST3 and CLAP)

`native/crates/bluestone_plugin_host` (Rust, ADR 0033) loads VST3 modules (through the MIT-licensed `vst3` bindings)
and CLAP modules (through `clap-sys`) in the worker. `NativePlugin` implements `IHostedPlugin` over its C ABI
(`Native/NativeMethods.cs`, the worker's only unsafe code). The data plane, failure policies, supervision, and
recovery are the same as for the reference plugins.

- **Identity**: format `vst3` or `clap`, module ID the module's file name without its extension, plugin ID the VST3
  class ID (32 hex digits) or the CLAP plugin ID. The device definition ID is `plugin:<format>:<module>/<plugin>`, for
  example `plugin:vst3:LegendHZ/255BF0CB0F9A419992C1A715E3C58F48`. `PluginLibrary.ModulePaths` gives the module each
  plugin is loaded from.
- **Kind**: CLAP `instrument` and `note-effect` features; for VST3, `Instrument` in the subcategories or the plugin's
  buses (no audio outputs with event input and output: a MIDI effect). MIDI generators that also make audio (Scaler 3,
  Melody Sauce 3) are instruments.
- **Parameters**: normalized to [0, 1], with units, step counts, and traits (`ParameterTraits`). Hidden, read-only, and
  VST3 MIDI-mapping stand-ins are reported but not shown or automated (`ParameterDescriptor.IsUserFacing`); a plugin
  with more than 4096 keeps the shown ones first. Edits made in the project reach the running plugin; edits the plugin
  makes itself (in its editor) come back as `PluginNotification`s and are stored in the project as undoable parameter
  changes (merged per parameter, as for the strip's sliders).
- **Events in and out**: notes, polyphonic and channel pressure, controllers, pitch bend, and program changes, with
  sample offsets. VST3 controllers, channel pressure, pitch bend, and program changes go through the plugin's MIDI
  mapping as parameter changes (unmapped ones are dropped) and come out as legacy MIDI CC events. CLAP gets notes as
  note events (or MIDI, if its note port prefers it) and the rest as MIDI. System exclusive is not passed to native
  plugins.
- **State**: VST3 component and controller state in one versioned container (format `bluestone.vst3-state`); CLAP
  state from its state extension, in a versioned container (format `bluestone.clap-state`). State a plugin rejects is
  reported in `LastStateRestoreError`; unlike the reference plugins, what the plugin then holds is up to the plugin.
- **Processing**: the host side always sees the channels it asked for. A plugin's own layout is adapted: a mono
  output feeds both host channels, side inputs get silence, extra outputs are dropped. Transport (position, tempo,
  playing) goes with every block.
- **Latency**: reported at creation; a change the plugin announces updates `PluginLatencyFrames`.

### Editors

**Open editor** in the device strip (for a running VST3 or CLAP plugin) asks the worker to open the plugin's editor
in a top-level window of its own: an NSWindow on macOS (VST3 `NSView`, CLAP `cocoa`), a Win32 window on Windows
(`HWND`, `win32`), an X11 window on Linux (`X11EmbedWindowID`, `x11`, with the plugin's timers and file descriptors
served by the worker's run loop). A CLAP plugin that can only show a floating window shows its own. The window
follows the plugin's resize requests and passes user resizes to the plugin; content scale is given on Windows and
Linux (macOS sizes are points). Closing the window or `CloseEditorAsync` destroys the view; a window closed by the
user is reported (`EditorClosed`). A worker that dies takes its window with it: the device shows Crashed, and after
Restart the editor opens again. Keyboard focus is whatever the operating system gives the worker's window.

### What was verified

| Where | Plugins | What |
| ----- | ------- | ---- |
| CI (Linux x64, macOS arm64, Windows x64) | Bluestone's test plugins, VST3 and CLAP (`NativePluginTests`, `ThirdPartyPluginDeviceTests`) | Scan, kinds, parameters, sample-accurate parameter changes, sine pitch, transpose events, offline rendering into the plan, state in and out, corrupt state, kill and restart, project save and reopen, automation feeds, an editor refused for a plugin without one |
| macOS arm64, this machine | The Legend HZ, REVERSIDE (VST3); Airwindows Consolidated, Surge XT (CLAP) (`InstalledPluginTests`) | Scan, load, audio out (a note for instruments, a sine through effects), a parameter set and state captured, worker killed while processing: Crashed, silence, restart restores the parameter and audio |
| macOS arm64, this machine | REVERSIDE (VST3), Airwindows Consolidated (CLAP) (`PluginEditorTests`) | Editor opens, closes, opens again; worker killed with the editor open; editor opens again after restart |
| macOS arm64, this machine | The Legend HZ (VST3), Airwindows Consolidated (CLAP), with a development probe | Editor windows appear on screen at the plugin's size (checked through the window list; the screen was locked, so not by eye); Airwindows sent parameter edits from its editor |
| macOS arm64, this machine | SuperClassic, Scaler 3, Scaler 3 Audio, Melody Sauce 3 (VST3) | Scanned by the app at start and loaded, processed, and saved by the probe; not covered by tests |

Not verified: editors on Windows and Linux (the code compiles and runs headless in CI; no window was opened on
those systems), closing or resizing an editor window by hand (no one was at the machine), plugin MIDI output from a
third-party MIDI effect (none of the installed plugins is a VST3 or CLAP MIDI effect; Scaler 3 and Melody Sauce 3
are instruments that also put out MIDI, which Bluestone does not route from an instrument), Intel-only plugins on
Apple silicon (refused as `Unsupported`, by a unit test on synthetic headers), real-time behaviour under an audio
device (there is no audio engine).

The installed-plugin and editor tests skip with a reason when the plugin is missing (and the editor tests on CI and
outside macOS). Test subjects downloaded for this: Airwindows Consolidated (MIT) and Surge XT 1.3.4 (GPL-3.0), from
their GitHub releases, installed in `~/Library/Audio/Plug-Ins/CLAP`; neither is part of Bluestone.

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
  binaries cannot be mixed in one process. Modules without code for the worker's architecture are reported as
  `Unsupported` when scanned. Hosting both would need one worker build per architecture (on macOS, an x64 worker
  under Rosetta); not built.
- **Bundles**: macOS VST3 and CLAP bundles are loaded with CFBundle (VST3 `bundleEntry` gets the bundle); Windows
  VST3 bundles load `Contents/<arch>-win/<name>.vst3`; Linux VST3 bundles load `Contents/<arch>-linux/<name>.so`; CLAP
  modules on Windows and Linux are plain shared libraries.
- **Crash reports**: on macOS every `Environment.FailFast` (the FailFast test and the scanner crash test) leaves a
  report in `~/Library/Logs/DiagnosticReports`.
- **Verified on**: macOS ARM64 by hand and in tests; Linux x64, macOS arm64, and Windows x64 in CI (workflow runs on
  this branch), including the VST3 and CLAP test plugins.

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
- The desktop app scans its plugin folders at start, one scanner process per module, and the bridge follows later
  rescans and retries from the Plugins view (see [The plugin library and the Plugins view](#the-plugin-library-and-the-plugins-view)).

- Values changed in the project go to running plugins on the next sync (only those that differ); `RecordEdit` stores
  an edit the plugin made itself; `OpenEditorAsync` and `CloseEditorAsync` show and hide its editor, and the status
  says "Running, editor open".

Tests: `PluginDeviceHostTests` (scenarios 11 to 13 through the project: save, reopen, kill, restart, automation still
valid; one of two workers killed; a missing plugin; removing a device stops its worker),
`PluginDeviceStripTests` (the strip shows `[Reference Gain: Crashed]` with Restart after a real kill), and
`ThirdPartyPluginDeviceTests` (the same through VST3 and CLAP test plugins, plus edits both ways and a MIDI effect
rendered into the plan).

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

## Acceptance scenarios

| # | Scenario | Tests (real worker processes) |
| - | -------- | ----------------------------- |
| 11 | A worker crashes; Bluestone keeps running, the project survives, the device shows as unavailable, and can be restarted | `PluginCrashRecoveryTests.KillingTheWorkerMidStream_*`, `PluginDeviceHostTests.Scenarios11And13_*`, `PluginDeviceStripTests` |
| 12 | A restarted worker is restored from persisted state; automation references stay valid | `PluginDeviceHostTests.Scenario12_*`, `PluginCrashRecoveryTests.ASnapshot_RestoresIntoANewInstance` |
| 13 | Several workers; one fails, the others keep working | `PluginCrashRecoveryTests.KillingOneOfSeveralWorkers_LeavesTheOthersProducingCorrectAudio`, `PluginDeviceHostTests.Scenarios11And13_*` |
| 15 | A module crashes the scanner; Bluestone carries on, reports it, and skips it next time | `PluginScannerProcessTests.ACrashingModule_IsQuarantined_SkippedNextTime_AndRetriedOnRequest_WhileOthersStillScan` |

These use the reference plugins in `Bluestone.PluginWorker`. The same scenarios with VST3 and CLAP plugins (11 and 12)
are covered by `NativePluginTests`, `ThirdPartyPluginDeviceTests`, and, with installed plugins, `InstalledPluginTests`.

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
| Rescan and retry from the Plugins view; the device catalog follows | `PluginLibraryProcessTests`, `PluginsViewModelTests`, `PluginLibraryTests`, `PluginsViewTests` |
| VST3 and CLAP through the test plugins: scan, process, state, render, crash and restart, project devices | `NativePluginTests`, `ThirdPartyPluginDeviceTests` |
| Installed VST3 and CLAP plugins (skipped when absent); editors with installed plugins (macOS) | `InstalledPluginTests`, `PluginEditorTests` |
| Protocol version 3 fields and notifications, architecture checks, bundle discovery, bundle fingerprints, only the worker loads the native host | `FrameCodecTests`, `BinaryArchitectureTests`, `PluginLibraryTests`, `ScanCacheTests`, `DependencyDirectionTests` |

Unit tests are in `src/Bluestone.Tests.Unit/Plugins/`; process tests are in `src/Bluestone.Tests.Integration/Plugins/`
and run as one non-parallel collection. The integration project references the worker with
`ReferenceOutputAssembly="false"` so the worker is built and copied beside the tests without being loaded.

## Deferred

- **More of VST3 and CLAP**: workers for a second architecture (Rosetta), system exclusive to native plugins, VST3
  note expression, CLAP note expressions and parameter modulation, MPE, audio-port configurations, side-chain
  inputs, routing the MIDI output of an instrument plugin, reloading a VST3 component on `kReloadComponent`.
- **Editors embedded in Bluestone's own window**, and editors on Windows and Linux verified on hardware.
- **Plugin delay compensation across a graph.** Instances report their latency; nothing compensates for it yet.
- **Operating-system sandboxing** of workers.
- **Wake-up primitives** (futex, eventfd, Windows events) for the data plane.
- **MIDI 2.0** events on the data plane.
- **In-process hosting** of trusted plugins.
- **Audio from plugins into a mixer.** There is no audio engine: plugin instances process blocks when asked (as the
  tests do), but nothing drives them from playback yet. Plugin MIDI effects reach the plan by rendering (above); live
  input does not pass through plugins.
