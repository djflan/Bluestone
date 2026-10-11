# 0034. Plugin hosting modes: how plugins share worker processes

- Status: Accepted; amends 0025
- Date: 2026-10-11

## Context

ADR 0025 runs every plugin in a worker process, so a plugin crash never takes Bluestone down, and it defined three
isolation policies (per instance, per module, shared by trusted instances). Only the plugin host API could choose
one; the application always used one worker per instance and the user could not change it.

The owner asked for Bitwig-style "sandboxing": crash isolation the user controls, not operating-system containment
(plugins need the network and the file system for licences, presets, and samples). Bitwig offers "Within Bitwig",
"Together", "By manufacturer", "By plug-in", and "Individually", and defaults to "By plug-in".

The choice trades the blast radius of a crash against processes. A worker is a .NET process: sampled with `ps` during
the hosting tests on macOS arm64, about 50 MB resident with a reference plugin and about 75 MB with Bluestone's Rust
test plugin loaded, before the plugin's own memory and threads. Each worker also costs a launch and handshake (well
under a second on that machine) and its own processing threads. A project with ten instances of one synth used ten
such processes under the old default.

## Decision

- **Four modes, plus the refused one.** `HostingMode` replaces `IsolationPolicy`:
  `Individually` (was `PerInstance`), `ByPlugin` (was `PerModule`), `ByManufacturer` (new), `Together` (was
  `SharedTrusted`), and `WithinBluestone` (was `InProcessTrusted`), which is still refused: `InvalidOperationException`
  unless `PluginHostOptions.AllowWithinBluestone`, and `NotSupportedException` even then.
- **Worker keys.** Instances share a worker exactly when their key (`WorkerKeys.For`) is equal:
  - Individually: the instance ID.
  - By plugin: the plugin's **module** (format plus module ID: one VST3 bundle or one CLAP file), not the plugin ID.
    Plugins in one module share its code and usually its global state, so a crash in one is likely to hit the others;
    one process per module also loads each binary once. A VST3 and a CLAP build of the same product are different
    modules and get different workers.
  - By manufacturer: the vendor the scan reported, trimmed and compared without case, across formats and modules. A
    plugin with no vendor is hosted by its module. Vendors that spell their name differently in different products
    are different manufacturers.
  - Together: one key for every instance in this mode.
  - Every shared key also names the worker's processor architecture: x64 and ARM64 plugin binaries cannot share a
    process. Today every hosted plugin runs on the worker's (native) architecture, because modules without it are
    `Unsupported` at scan time; the key keeps that true if a second-architecture worker is ever added.
- **The application default is "By plugin", as in Bitwig.** It bounds processes by the number of distinct plugins, not
  instances, while a crash takes down only copies of the plugin (and its module) that crashed, which are the ones most
  likely to crash for the same reason. "Individually" stays the default of `PluginInstanceRequest` (the low-level API
  keeps the safest choice) and is one click away for a plugin that crashes.
- **Settings, per user, not per project.** `PluginHostingSettings` (in `plugin-hosting.json` beside the audio settings)
  holds the global mode, automatic restart (off by default; on, it uses the existing `RestartPolicy`: 3 restarts in
  5 minutes with back-off, then quarantine), and a mode per plugin keyed by device definition ID
  (`plugin:<format>:<module>/<plugin>`), which wins over the global mode. A file naming an unknown or in-process mode
  falls back to the defaults for that entry. There is **no per-device override saved in the project**: how a plugin
  behaves on this machine is not a property of the song, a project opened elsewhere should not bring hosting choices
  with it, and the per-plugin override covers the case that matters (a crash-prone plugin). Project format 4 is
  unchanged.
- **Changing a mode moves instances at once.** `PluginHostManager.ChangeHostingAsync` keeps the instance (its ID,
  parameters, and `PluginInstance` object, so the audio graph and the device keep it). If the worker key changes: a
  running instance has its state captured first, then goes Running → Restarting (a new transition) → Running in the
  new worker, restored from that state and its remembered parameter values; a crashed, hung, or quarantined one is
  restarted there from its last snapshot, as a manual restart would. A worker left without instances exits. If the key
  does not change, nothing restarts. `PluginDeviceHost.ApplyHostingAsync` applies new settings to every instance;
  the restart policy is changed in place and applies from the next failure.
- **The blast radius is shown.** When a worker ends under running instances, each gets `LastWorkerLoss` (process,
  reason, every instance that was in it) before it is reported Unavailable, and the manager raises `WorkerLost` once.
  The device strip says which devices went down with a device ("Pad and Bass went down with it: they shared its worker
  process"), the message list says so once, and **Restart restarts every device that went down with it** that is still
  down, so they share a worker again. Each plugin device also shows which process it is in ("Hosted by plugin (Surge
  XT) · shares its process with Pad").
- **Crash counts per plugin, for the session.** `PluginDeviceHost.CrashCounts` counts, per plugin, the worker losses
  that took it down, and how many of those also took down other plugins (any of which may have caused it). The Plugins
  view shows "Crashed 2 times, hosted alone." and offers **Host individually** for a plugin that crashed and is not
  hosted individually yet. Counts are not saved: they reset at start, so an updated plugin is not held to its old
  crashes; the per-plugin override is the durable decision.
- **Still crash isolation only.** No operating-system sandbox is added (no App Sandbox, seccomp, or AppContainer):
  a worker runs with the user's permissions, as ADR 0025 says.

## Consequences

- With the default, all copies of a plugin share a process: one crash silences all of them (they show Crashed
  together, and one Restart brings them back). Users who want the old behaviour choose "Individually".
- A worker's control requests are served one at a time, so instances that share a worker also share its control
  plane: a slow request (state capture, rendering a MIDI effect into the plan) delays the others' requests. Audio is
  not affected: each instance keeps its own data-plane file and processing thread.
- A hang in a shared worker (missed heartbeats or a timed-out request) kills the worker and every instance in it.
- Moving a running instance is a restart: a block or two of silence (or the dry input under `BypassInput`), and notes
  held by an instrument are cut. The editor window, if open, closes.
- Blame is approximate in shared modes: Bluestone knows which worker died, not which plugin in it caused it. The crash
  counts say how often a plugin was alone when its worker died.
