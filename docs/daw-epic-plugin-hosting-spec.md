# Plugin hosting work package (spec given to the background agent)

This is the concrete spec for epic phases 4 and 5 (`docs/daw-epic-prompt.md` sections 15 and 19, scenarios 11
to 13 and 15). It was handed to a background agent working in a separate git worktree. If that work is
missing or unfinished, use this to continue or restart it. The agent's own report, if one exists, is not
evidence: review its diff and run its tests.

Status: **implemented and merged** (commits `4fc7ee9` to `0172644`, merged in `0676a8d`), reviewed and re-tested on
the main branch, then connected to the project by `PluginDeviceHost`. The result is described in
`docs/plugin-hosting.md` and ADR 0025; this spec is kept as the record of what was asked for.

## Rules that apply to everything below

- New projects only, plus the wiring listed at the end. Do not touch `Bluestone.Domain`, `Application`,
  `Presentation`, `Desktop`, `Playback`, `Infrastructure`, `Midi`, `Profiles`, `Platform.*`, any ADR, `README.md`,
  or `global.json`.
- Language policy (owner clarification, 2026-10-10; see `docs/daw-epic-handoff.md` section 4, decision 17): C# is
  the default for everything here (protocol, host, supervision, scanner, tests). Rust is allowed, and preferred over
  C or C++, for a performance-critical or native part such as the worker's VST3 hosting layer or the real-time
  data-plane path, when the reason is stated and measured or forced by a native ABI. No new Bluestone-owned C or C++
  beyond thin vendor/ABI shims. No VST3 loading and **no VST3 support claims** until loading, processing, state, and
  recovery are demonstrated by tests. Process isolation is crash isolation only; never call it a security sandbox.
- Build environment, analyzer rules, and test-running quirks are in `docs/daw-epic-handoff.md` section 2 and 4.
- Style: file-scoped namespaces, braces always, sparse comments that explain why, tests named `Subject_Condition_Expectation`.
- Everything builds with 0 warnings; the whole existing suite still passes; format check passes.

## Projects (under `src/`, each added to `src/dotnet/Bluestone.slnx`)

### 1. `Bluestone.Plugins.Protocol` (library, BCL only)

Shared by host and worker.

- **Control plane**: length-prefixed little-endian binary frames, a hard maximum frame size, a protocol version
  checked in a Hello handshake, strict validation. Malformed, truncated, oversized, or unknown frames raise
  `ProtocolException`; never a crash, hang, or unbounded allocation. Messages: Hello/HelloAck, Ping/Pong,
  CreateInstance (instance id, plugin identity, sample rate, max block frames, channel counts, optional state),
  InstanceCreated (reported latency, parameter descriptors, data-plane file path, generation), DestroyInstance,
  CaptureState/StateResult, RestoreState/Result, SetParameter, GetParameters, Error, Shutdown, ScanModule/ScanResult.
- Plugin identity and instance identity are different types. `PluginIdentity`: format, module id, plugin id,
  display name, vendor, kind (Instrument, MidiEffect, AudioEffect), version. `PluginInstanceId`: a GUID.
  Parameters are `uint` ids with normalized `double` values in [0, 1].
- **Data plane**: file-backed memory-mapped block exchange per plugin instance (named maps are Windows-only
  in .NET). Header: magic, layout version, **generation**, slot count (>= 2, default 2), max frames, channel counts,
  event capacity, worker heartbeat counter, state flags including **faulted/closed**. Each slot: sequence/state word,
  block header (block index, frames, transport position/tempo/playing, event and parameter-change counts), planar
  float32 input audio, timestamped input events (sample offset, kind, channel, data, bounded inline SysEx payload with a
  "dropped/truncated" counter), timestamped parameter changes, output audio, output events, per-block status
  (ok / plugin error / deadline missed). Ownership state machine: Free, Submitted, Processing, Done, Free.
  Any unsafe pointer code lives in one small, justified file.
- **Host-side calls never block, allocate, lock, do I/O, or log.** `TrySubmit` / `TryCollect` return a status.
  The worker side may spin then back off (document the CPU and latency trade-off; a futex/eventfd wake-up is a later optimisation, not built).
- **Pipelining**: the host submits block k and collects block k - (depth - 1). Reported latency to the host =
  plugin latency + (depth - 1) blocks. A block not Done by the host's bounded wait returns `NotReady`; the caller's failure
  policy decides the output (default silence). A late result for an abandoned block is discarded by block-index check, never delivered into a later block.
- **Faults**: `MarkFaulted()` (called when the worker dies) makes every later submit or collect return `Faulted`
  without touching mapped memory beyond the header flag. Generation numbers make a stale handle detectable if a new worker
  recreates the file. Disposal is idempotent and safe in any state.

### 2. `Bluestone.Plugins` (library, references Protocol only; runs in the main process)

- **Worker launching**: resolve the worker (apphost `Bluestone.PluginWorker` beside the host assembly, else `dotnet Bluestone.PluginWorker.dll`);
  never a shell; pass a pipe name and random per-launch token; redirect and drain standard streams.
- **`WorkerProcess`**: owns one `Process` and one `NamedPipeServerStream`; handshake with timeout; correlated requests with
  per-request timeouts; heartbeat monitor (missed pings beyond a threshold means hung, so kill it); detect exit from both
  `Process` exit and a broken pipe; callbacks never run on a real-time path.
- **`PluginHostManager`**: creates and destroys instances. Isolation policies: `PerInstance` (default), `PerModule`,
  `SharedTrusted` (opt-in), and `InProcessTrusted`, which exists as a value but is rejected unless explicitly opted into, and even
  then throws `NotSupportedException` ("deferred"). **No code path may silently fall back to in-process hosting** if launch or handshake fails; surface a failure state. Test this.
- **Instance status** (event + property, thread-safe, documented transition table): Starting, Running,
  Unavailable(Crashed | Hung | FailedToStart | ProtocolError | Missing), Restarting, Quarantined, Unloaded.
- **Supervision and recovery**: on unexpected worker death mark every hosted instance Unavailable, **fault each exchange first**, bump
  the generation, raise one event per instance, keep the last captured state and parameter values, and apply `RestartPolicy`
  (auto-restart default off; max restarts in a sliding window; capped exponential back-off; tripping the guard means Quarantined until a manual
  `RestartAsync`, which resets the counters). Manual restart works from Unavailable and Quarantined, recreates worker and instance,
  restores the last state and parameters, and needs nothing else closed. One worker's crash must not affect another's instances.
- **State**: `CaptureStateAsync` pulls opaque state plus a format string from the live worker and caches it as the recovery snapshot;
  optional periodic snapshots (off by default); expose the cached snapshot so the application can persist it; `RestoreFrom(snapshot)` on create. Never assume a crashed plugin can return unsaved state.
- **Failure policy**: default `Silence`; opt-in `BypassInput` for audio effects when channel counts match. After any failure no stale audio is ever returned and output is exactly zero.
- **Processing API**: allocation-free, lock-free `ProcessBlock`, `QueueParameterChange(paramId, normalized, sampleOffset)` writing into preallocated buffers; events in and out as fixed-size records. One audio thread calls `ProcessBlock`; control threads use the async APIs.
- **`PluginScanner`**: one **separate scanner process per module file**, hard timeout, never in the host. Result per file: descriptors or `ScanFailure`
  (Crashed, TimedOut, Malformed, ProtocolError). A scan cache (JSON, atomic write) keyed by path + size + last-write time. A quarantine list:
  modules that crashed or timed out are skipped on later scans until `Rescan(path, ignoreQuarantine: true)` or the entry is cleared. Malformed files are an
  ordinary failure, not a crash, and are not quarantined as crashes. Scanning never throws because of a bad plugin. The test hook for a real scanner crash is a module file whose first line is a
  documented marker, on which the scanner calls `Environment.FailFast`; docs must say the crash is induced by the marker, not by a native binary.

### 3. `Bluestone.PluginWorker` (Exe, references Protocol only)

One executable with instance-host and scan modes. Hosts **reference plugins** behind a small internal `IHostedPlugin`
(describe parameters, process block with events and timestamped parameter changes, save/load state bytes, report latency):
`reference.gain` (audio effect; a test proves a mid-block parameter change lands on exactly the specified sample), `reference.sine`
(instrument, polyphony >= 4, short attack/release, deterministic), `reference.transpose` (MIDI effect that transposes notes and **passes
controllers and SysEx through unchanged**), and test-only failure behaviours (`FailFast`, hang, garbage frames) reachable only through an explicit test message.
State is versioned parameter values; corrupt or unknown-version state fails cleanly (reported, instance still usable with defaults). The worker exits promptly when its control pipe closes.
In scan mode, files ending `.bluestone-reference-plugin` (a small manifest) are modules; everything else is `Malformed`. Never `dlopen` arbitrary libraries.

## Tests (meaningful, passing)

Fast tests in `src/dotnet/Bluestone.Tests.Unit/Plugins/`; process-spawning tests in `src/dotnet/Bluestone.Tests.Integration/Plugins/`
(the integration project references the worker with `ReferenceOutputAssembly="false"` so it is built and copied; verify with a clean build).

- Protocol: round-trip every message; CsCheck fuzz that arbitrary bytes, truncations, and oversize lengths throw only `ProtocolException`; version mismatch rejected.
- Exchange: ownership state machine; pipelining and latency math; assert **zero allocations** on host submit/collect (`GC.GetAllocatedBytesForCurrentThread`
  after warm-up); late-result discard; faulted and generation mismatch; two-thread stress with pattern-checked audio, and the same across a real worker process.
- Restart policy maths with an injectable clock, no sleeping.
- Scan cache and quarantine persistence (atomic write, corrupt cache recovers empty with a diagnostic, fingerprint change triggers rescan).
- **Real processes**: create each reference plugin and check actual output; `Process.Kill()` the worker mid-stream (not a mocked notification) and assert the test process
  survives, the instance becomes Unavailable(Crashed) once, `ProcessBlock` returns exact silence at once with no stale audio and no exception, then `RestartAsync()` restores captured state and parameters and processing resumes.
  Hung worker means heartbeat kill and Unavailable(Hung). Garbage frames mean ProtocolError. Bad worker path means FailedToStart and **no in-process fallback happened**.
  Several workers: kill one, the rest keep producing correct audio. A worker that crashes at startup repeatedly ends Quarantined, not in a loop. No worker processes remain after the manager is disposed.
  Scan: a marker module crashes the scanner, the host survives, the module is quarantined, a second scan skips it without launching a scanner (assert with a launch counter), `Rescan(ignoreQuarantine: true)` retries it,
  a malformed file is `Malformed`, and a good module beside them still scans.
- Bounded timeouts everywhere, unique pipe and file names per test, cleanup in `finally`; no test leaves a process behind. Run the integration tests at least 10 times in a loop and fix any flakiness found.

## Wiring (only shared files that may change)

`src/dotnet/Bluestone.slnx`; the two test csproj files; `DependencyDirectionTests` (Protocol references nothing, Plugins references Protocol, PluginWorker references Protocol; none reference other Bluestone assemblies);
`.github/workflows/ci.yml` only if the integration tests need it to find the worker.

## Documentation

`docs/plugin-hosting.md`: process model, control versus data plane, the exchange state machine, pipelining and a **latency trade-off table** (at least depth - 1 blocks plus scheduling jitter; no zero-latency claims),
failure policies, supervision and recovery sequence, state persistence rules, isolation policies and which are implemented, scanning and quarantine, **security limitations** (same OS permissions as Bluestone; no filesystem or network sandboxing),
threading, platform notes (file-backed mappings, Unix-socket pipes, x64 versus ARM64 plugin binaries cannot mix), what is implemented and tested (naming the test), and what is deferred (real VST3 loading and parameter enumeration, plugin editors and their focus/DPI/worker-termination issues,
delay compensation across a graph, OS sandboxing, wake-up primitives, MIDI 2.0).

## After it exists (done by the main engineer, not the agent)

An Application bridge from a project's `DeviceInstance` to a plugin instance (state captured into the project as `PluginState`, status surfaced to the device strip), and an ADR for out-of-process hosting.
