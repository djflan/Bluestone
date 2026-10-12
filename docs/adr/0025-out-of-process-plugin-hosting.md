# 0025. Plugins run out of process; isolation is crash isolation, not a sandbox

- Status: Accepted; amended by 0028 (plan compilation renders plugin MIDI effects in their worker), 0033 (VST 3 and CLAP through a Rust host in the worker), and 0034 (the isolation policies became user-chosen hosting modes, "by plugin" by default in the app)
- Date: 2026-10-10

## Context

Third-party plugins (VST 3 instruments and effects, MIDI plugins) are native code Bluestone did not write and
cannot fix. Loaded into Bluestone's process, a plugin that crashes takes the application and the musician's
unsaved work with it; one that hangs freezes it. Scanning a malformed plugin can crash a host before it has
shown a window.

## Decision

- **Plugins never run in Bluestone's process.** Each runs in a worker process (`Bluestone.PluginWorker`). There is
  no in-process fallback: if a worker cannot start or hand-shake, the instance is `Unavailable(FailedToStart)`.
  An in-process policy exists only as a value that is refused (and unsupported even when explicitly allowed).
- **Isolation policies:** one worker per instance (default), per module, or shared by trusted instances.
- **Two planes.** A control plane (length-prefixed binary frames over a local pipe, versioned handshake, strict
  validation, per-request timeouts, heartbeats) carries create, destroy, state, parameters, and errors. A data
  plane (a file-backed memory-mapped block exchange per instance, with a slot state machine, generations, and a
  faulted flag) carries audio, timestamped events, and parameter changes. Host-side submit and collect never
  block, lock, allocate, do I/O, or log.
- **Pipelining.** The host submits block *k* and collects block *k − (depth − 1)*; reported latency is the
  plugin's latency plus *(depth − 1)* blocks. A result that is not ready by a bounded wait is treated as not
  ready, and a late result is never delivered into a later block.
- **Failure.** On worker death (exit, broken pipe, missed heartbeats, protocol errors) every hosted instance
  becomes `Unavailable` once, its exchange is faulted first so no stale audio is read, and output is exact
  silence (or, opt-in for effects, the delayed dry input). Other workers are unaffected. Restart is manual by
  default; automatic restart is bounded by a sliding window and back-off, after which the instance is
  `Quarantined` until a manual restart.
- **The project is the authority.** Plugin identity, instance identity (the `DeviceId`), parameters, and saved
  state live in the project; the worker only runs the plugin. State is captured into the project before saves
  and restored when an instance is created or restarted. A crashed plugin is never asked for newer state.
- **Scanning** runs one scanner process per module with a timeout; modules that crash or hang are quarantined
  and skipped until a rescan is asked for; results are cached by path, size, and modification time.
- **Security.** Process isolation is crash isolation only. A worker runs with the same user permissions as
  Bluestone and can read and write the same files and use the network. Operating-system sandboxing is deferred.
- **No VST 3 support is claimed.** The worker hosts Bluestone's reference plugins behind `IHostedPlugin`, the seam
  where a native hosting layer will go (Rust, per ADR 0026, given the native-binary reason).

## Consequences

- A plugin crash, hang, or misbehaving worker cannot terminate Bluestone or corrupt the project; tests kill real
  worker processes to prove it (scenarios 11, 12, 13, 15; see docs/plugin-hosting.md).
- Out-of-process audio adds at least *(depth − 1)* blocks of latency plus scheduling jitter; no zero-latency
  claim is made. Measured real-time behaviour under an audio device awaits an audio engine.
- Plugin editors, delay compensation across a graph, wake-up primitives, MIDI 2.0 on the data plane, and real
  VST 3 loading are deferred.
