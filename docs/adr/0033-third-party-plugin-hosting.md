# 0033. Third-party plugins: VST 3 and CLAP through a Rust host in the worker

- Status: Accepted; amends 0025 and 0026
- Date: 2026-10-11

## Context

ADR 0025 put plugins in worker processes and left `IHostedPlugin` as the seam for a native hosting layer; only
Bluestone's reference plugins ran there. ADR 0026 allows Rust where a native interface requires it, and loading
plugin binaries is that case. The owner asked for VST 3 first, tested with plugins installed on his Mac, then CLAP
with an open-source plugin, and editor windows on every operating system.

Licences checked on 2026-10-11:

- The VST 3 SDK is MIT licensed (`LICENSE.txt` in steinbergmedia/vst3sdk; MIT since SDK 3.8). The licence does not
  cover the trademark: VST is a registered trademark of Steinberg Media Technologies GmbH, whose usage guidelines
  set separate rules for the name and logo. Bluestone uses the name only to say which format it loads; see
  [TRADEMARKS.md](../../TRADEMARKS.md).
- The `vst3` crate (coupler-rs/vst3-rs) is MIT OR Apache-2.0. Its bindings are generated from the SDK headers and
  ship with the crate, so building needs no SDK. `vst3-sys` (GPL-3.0) is not used.
- `clap-sys` is MIT OR Apache-2.0; the CLAP headers are MIT.
- Other dependencies: `libloading` (ISC), `serde_json` (MIT OR Apache-2.0), `objc2`, `objc2-foundation` (MIT),
  `objc2-app-kit` (Zlib OR Apache-2.0 OR MIT), `windows-sys`, `x11rb`, `libc` (MIT OR Apache-2.0).
- Test subjects, installed locally and never vendored: Airwindows Consolidated (MIT) and Surge XT (GPL-3.0), both
  from their projects' GitHub releases.

## Decision

- **A Rust crate, `native/crates/bluestone_plugin_host`, hosts VST 3 and CLAP.** It is a cdylib with a C ABI
  (`bph_abi_version` = 1). Only `Bluestone.PluginWorker` builds or imports it
  (`DependencyDirectionTests.OnlyTheWorker_BuildsOrImportsTheNativePluginHost_*`); Bluestone's own process never
  loads it. `NativePlugin` implements `IHostedPlugin` over the ABI; the data plane, supervision, and recovery are
  unchanged.
- **The ABI is coarse.** Handles for a module and an instance; one call per block (`bph_plugin_process`, planar float
  audio, MIDI 1.0 channel events and parameter changes with sample offsets, output events); metadata (plugins,
  parameters) as JSON strings the caller frees; state as a byte buffer the caller frees; errors as returned strings.
  Panics abort the worker (the workspace builds with `panic = "abort"`), which Bluestone already treats as a crash.
- **Identity.** A plugin's definition ID is `plugin:<format>:<module>/<plugin>`: the format (`vst3`, `clap`), the
  module's file name without its extension (the same on every machine), and the VST 3 class ID in hex or the CLAP
  plugin ID. Instances are created with the module's path (`CreateInstance.ModulePath`).
- **Kinds.** CLAP features say it: `instrument`, `note-effect` (a MIDI effect), otherwise an audio effect. VST 3 has no
  MIDI-effect category, so the scanner creates each class briefly: no audio outputs with event input and output is a
  MIDI effect; `Instrument` in the subcategories, or no audio inputs with event input, is an instrument.
- **Parameters** are normalized to [0, 1] (CLAP plain values are mapped through each parameter's range) with units,
  step counts, and traits. Hidden, read-only, and VST 3 MIDI-mapping stand-ins (the parameters a plugin maps MIDI
  controllers to, such as JUCE's 2048 "MIDI CC" parameters) are reported but not shown or automated.
- **MIDI in.** CLAP gets notes as CLAP note events, or as MIDI when its note port prefers MIDI, and controllers,
  pitch bend, pressure, and program changes as MIDI. VST 3 gets notes and polyphonic pressure as events; controllers,
  channel pressure, pitch bend, and program changes become parameter changes through the plugin's `IMidiMapping`;
  unmapped controllers are dropped. System exclusive is not passed to native plugins.
- **State.** VST 3 component and controller state are stored together in a versioned container (`BSV3`, format
  `bluestone.vst3-state`); CLAP state from the state extension in a container of its own (`BSCL`,
  `bluestone.clap-state`). A host parameter edit reaches a VST 3 processor with its next block while blocks are
  streaming (one processed in the last 500 ms), so the stream is never disturbed. When none are, it is delivered at
  once in a one-frame block of silence at the last block's transport, whose audio is dropped and whose events are
  passed on with the next block: some plugins ignore the empty "flush" block the VST 3 API allows. Saving the state
  first waits for the streaming blocks to take pending edits.
- **Edits from the plugin** (VST 3 `performEdit`, restart flags, `setDirty`; CLAP output parameter events, rescan,
  `mark_dirty`, latency) become `PluginNotification` frames (protocol version 3), the worker's first unsolicited
  messages. The application stores edited values in the project, which stays the authority.
- **Channel layouts.** The host side always sees the channels it asked for (stereo, or none for an instrument's
  input). VST 3 is offered the host's layout on its main buses (stereo, or mono for one channel) and keeps its own if it refuses; CLAP keeps its ports.
  A mono plugin output feeds every host channel; extra plugin channels and side buses get silence.
- **Threads.** The worker's process main thread runs the operating system's event loop (on macOS an accessory
  NSApplication). The control plane runs on the thread pool and hands each request to the main thread; plugins are
  created, configured, saved, and shown there, as both formats require. Each instance's processing thread calls
  `bph_plugin_process` in the audio role; the host serializes it with activation and state (CLAP's `is_audio_thread`
  is true for whichever thread holds that role). Plugin requests (CLAP `request_callback`, flushes, timers; VST 3 restart
  flags) are served on the main thread after each turn of the loop.
- **Editors** open in a top-level window owned by the worker: NSWindow and NSView on macOS, a Win32 window on
  Windows, an X11 window (through x11rb) on Linux, with VST 3 `IPlugView` and the CLAP gui extension (embedded, or the
  plugin's own floating window when that is all it offers). The window follows the plugin's resize requests and
  passes user resizes on; closing it from either side destroys the view and tells Bluestone. Content scale is given
  on Windows and Linux. A worker that dies takes its window with it; the instance reports Crashed, and after a
  restart the editor can be opened again.
- **Scanning** handles VST 3 bundles and CLAP files or bundles in the existing scanner process per module, with its
  timeout, quarantine, and cache (a bundle's fingerprint covers its binaries and Info.plist). Before loading anything
  the scanner reads the binary's headers (Mach-O, PE, ELF); a module without code for the worker's architecture is
  `Unsupported`, cached, and not quarantined. No second-architecture (Rosetta) worker is built.
- **Test plugins.** `native/crates/bluestone_test_plugins` builds a gain effect, a sine instrument, and a transpose
  MIDI effect as one library exporting both formats; the integration test build packages it as a `.vst3` bundle and
  a `.clap` module in each platform's layout, so CI runs the full path on Linux, macOS, and Windows.

## Consequences

- Building needs a Rust toolchain (stable, 2024 edition); `dotnet build` runs cargo.
- VST 3 and CLAP plugins load, scan, process, save and restore state, survive worker crashes, render as MIDI effects
  at plan time, and show editors. What was verified on which machine is listed in docs/plugin-hosting.md.
- A plugin with thousands of parameters costs a round trip per changed value, not per parameter: recovery and
  project sync send only values that differ.
- Not built: Rosetta workers for Intel-only plugins on Apple silicon, VST 3 note expression and SysEx, CLAP
  note expressions, MPE, parameter modulation, audio-ports configurations, multiple processing threads per instance.
  Delay compensation and hosting modes are separate work.
