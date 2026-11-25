# Engineering handoff and working notes

Engineering notes for whoever continues Bluestone: where things stand, how to build and test, traps already hit,
decisions not to re-decide, the regression net, and what is not verified. Product direction and priorities now live in
[product-vision.md](product-vision.md), [roadmap.md](roadmap.md), and [release-1.0.md](release-1.0.md). The implemented design is in
[architecture.md](architecture.md), [plugin-hosting.md](plugin-hosting.md), [project-format.md](project-format.md),
and the [ADRs](adr/README.md). Bluestone grew from a MIDI workstation into a DAW in two steps: the routing
architecture (ADRs 0021 to 0029), then the audio engine and plugins (ADRs 0030 to 0035).

> [!IMPORTANT]
> ADR 0041 is the current product direction. This document preserves implementation facts and numbered decisions;
> it does not set product priority. When its old suggested scope conflicts with the product vision or Release 1.0
> slices, follow ADR 0041 and `release-1.0.md`.

## 1. Status

| Area | State |
| ---- | ----- |
| Domain: roles, devices, chains, instruments, connections, mixer, device automation | Done, tested |
| `Bluestone.Signal`: chain runner, built-in devices, signal graph | Done, tested; built-in devices also have live, allocation-free forms (ADR 0035) |
| Routing model, project format 6 and 2→3→4→5→6 migrations, chain presets, commands, inspector | Implemented; see `ProjectSerializerTests`, `Format5To6MigrationTests`, and routing command tests |
| Group tracks: mute/solo reach members, joining routes into the group, folding saved (ADR 0030) | Done, tested; headless UI test |
| Hardware parameter automation: profile parameter maps → CC, RPN, NRPN, SysEx with checksums, thinning, chase (ADR 0031) | Done, tested; not verified on hardware |
| Audio engine: cpal output in Rust, render graph in C# behind a render-ahead ring, WAVE/AIFF clips with SRC and disk streaming, track-owned strips, Effect returns, Group buses, Master chain and meters (ADRs 0032, 0050), clip gain and fades, device-loss fallback, one transport | Existing Core Audio/WASAPI/ASIO evidence is recorded in ADRs 0032/0049; the professional mixer has no physical audio verification recorded here |
| Live input through every device on the live route (built-in, plugin MIDI effects, plugin instruments), live render-ahead queue, plugin delay compensation across the graph (ADR 0035) | Done, tested sample-accurately; latency measured on macOS and through Windows WinMM software loopback (Windows result misses target) |
| Engine process: MIDI playback, endpoints, live input and thru, the audio device rendering in the callback, the graph, clips, meters, and the plugin data plane in a NativeAOT C# process with its own protocol and supervisor; depth 1 for the monitored plugin instrument; real-time plugin worker threads (ADRs 0043, 0044) | Done; default on macOS. Windows x64 WinMM live input and WASAPI were measured through software loopback; live added latency missed target. Linux NativeAOT allocation checks passed in a container, not on devices; the engine remains opt-in (`BLUESTONE_ENGINE=1`) on Windows and Linux |
| Third-party plugins: VST 3 and CLAP through a Rust host in the worker; scanning, parameters, state, processing, editors, crash recovery (ADR 0033) | Done; CI runs Rust test plugins on all platforms; installed CLAP processing and Pro-Q 3/Surge XT editors tested on native Windows and macOS |
| Plugin hosting modes: together, by manufacturer, by plugin (default), individually; per-plugin override; crash blast radius and counts (ADR 0034) | Done, tested with real workers |
| UI: Plugins view (rescan, quarantine retry, hosting), dockable/detachable track-owned Mixer console and meter modes, Audio Settings, editor button, group menu, instrument parameter lanes | Focused view-model/UI tests for independent Master selection and Inspector scope passed; Max reviewed the running mixer at 1318×847 (compact) and 1620×1000 (normal), including Master selection and LUFS-S warm-up. The later C2 strip rebuild (insert stack, LED meter rail, gain bar, pan ramp) was reviewed in headless renders with simulated meter data only; physical audio remains unverified |
| Device-centric workspace: design tokens, semantic regions and rails, dockable/detachable Browser, multiple follow/lock Device Views with per-view presentation, drag-and-drop insertion and reordering, local layout persistence with monitor recovery (ADR 0036); parallel signal bundle in chains (ADR 0037) | Done; view-model and headless UI tests with frames at 1280×800 and 1920×1080; the real app launched under Xvfb; not checked on real multiple monitors |
| Interaction polish: arranger loop kept while off, clip loops on note and audio clips with Consolidate, format 5 (ADR 0039); ruler drag zoom and scroll, Zoom to Fit/Selection/Loop; drag to replace a device; floating editors that follow the selection, pin, and send keys to Bluestone (ADR 0040) | Done; unit, view-model, and headless UI tests; editor lifecycle and key routing checked on macOS and native Windows ([worklog](worklogs/bitwig-interaction-polish.md)) |
| Headless UI tests: Device View, Browser, Racks, Mixer, Track, Routing, Connections, arrangement, piano roll, shortcuts, groups, plugins, mixer console | Done |
| Acceptance scenarios 1–15 | All have tests: tables in [architecture.md](architecture.md#acceptance-scenarios-and-their-tests) and [plugin-hosting.md](plugin-hosting.md#acceptance-scenarios) |
| Rust | Two cdylib crates (`bluestone_audio`, `bluestone_plugin_host`) plus test plugins, each with a measured or native-ABI reason (ADRs 0032, 0033) |

## 2. Build environment

Use the README commands on a machine with the .NET SDK from `global.json` (11.0.100 or a later 11.0 feature band) and
a Rust toolchain (`cargo`; the hosted CI runners have one). `dotnet build` builds the Rust crates through
`src/dotnet/Native.targets` (`cargo build --locked`; after changing Rust dependencies run `cargo build` in
`src/native/` once). Linux needs `libasound2-dev` and `pkg-config`. One test project, one namespace:
`dotnet test --project src/dotnet/Bluestone.Tests.Unit --filter-namespace "Bluestone.Tests.Unit.Signal"`. Rust:
`cargo test`, `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings` in `src/native/`.

- `CoreMidiProviderTests` fail intermittently on macOS under load (real Core MIDI timing; up to three of them in a
  full run while other builds run; they pass alone). CI leaves them out. Not a regression.
- Plugin tests that make a worker call `Environment.FailFast` leave macOS crash reports in
  `~/Library/Logs/DiagnosticReports`. Harmless.
- Tests with installed third-party plugins (`InstalledPluginTests`, `PluginEditorTests`) and the real audio device
  (`AudioDeviceTests`) skip with a reason where those are absent (CI).
- The app cannot start while the Mac's display is asleep (Avalonia: "RenderTimer -6661"). `caffeinate -u -t 60 &`
  wakes it; with the screen locked, screenshots show only the lock screen.
- To look at the UI: `./src/dotnet/Bluestone.Desktop/bin/Debug/net11.0/Bluestone.Desktop path/to/project.bluestone`.

## 3. Analyzer traps already hit (warnings are errors)

- `CA2027` (new in .NET 11): a `Task.Delay` raced in `Task.WhenAny` must be cancelled after the wait; pass a
  token from a `CancellationTokenSource` and cancel it (`WorkerProcess`).
- `CA1832` (new in .NET 11): use `AsSpan()` or `AsMemory()` instead of a range indexer on an array to get a slice.
- `CA2208`: `new ArgumentException(msg, nameof(X))` must name a *parameter of the enclosing method*; `nameof(Property)` is only allowed inside the property's own accessor.
- `CA1711`: an enum member cannot end in `Ex` (`SysEx` became `SystemExclusive`).
- `IDE0005`: unused `using` is an error, including in tests.
- `ImmutableArray<T>` has no `FindIndex`; use a loop. With xUnit, comparing a collection expression to an `ImmutableArray` may need an explicit `Assert.Equal<T>(...)`.
- Positional records that re-declare their properties are awkward; use an explicit constructor (as `ProfileReference` does).
- Style: file-scoped namespaces, braces always, `_camelCase` fields, XML docs only where intent/units/ownership are not obvious, tests named `Subject_Condition_Expectation`. Keep test assertions plain; two bad tests came from over-clever expressions.
- `Bluestone.Domain` may reference only the BCL. Add every new project to `DependencyDirectionTests`.
- `CA1711`: type names cannot end in `Stream` either (the feeds are `ExternalPartFeed`, `SoftwareInstrumentFeed`, `ParameterFeed`).
- `CA1822`: a property returning a static (`=> ChannelOption.All`) must be an initialized auto-property to bind from AXAML.
- Namespaces: in `Bluestone.Tests.Unit`, `Domain.Routing.X` resolves to the test namespace; import `Bluestone.Domain.Routing` instead. In `Bluestone.Application.Plugins`, `Plugins.Protocol.X` resolves to the Application namespace; use `using` aliases.
- `EventOrder.Compare` boxes `EventPhase` (`Enum.CompareTo(object)`); on hot paths compare `(int)phase`.
- `dotnet format` reorders imports after edits; run `dotnet format src/dotnet/Bluestone.slnx` before `--verify-no-changes`.

## 4. Decisions not to re-decide

Where things are: [architecture.md](architecture.md) ("Projects and their references", "Where does this belong?",
"Tracks, devices, and routing") and [plugin-hosting.md](plugin-hosting.md). Code comments cite these decisions by
number ("decision 16 in docs/engineering-handoff.md"), so the numbers never change: amend a decision by adding a new one that
says so.

1. **Role is persisted and reconciled** with content (it cannot be derived for empty/Effect/Group tracks). `Track` never rejects clips because of role. Effect/Group refuse clips; narrowing that would hide content is refused; conversion never discards.
2. **One routing model**: the `SignalConnection` list. Sources: track output, rack output, device tap. Destinations: track/rack input, external-instrument part (port + channel via mapping), or Master. Audio routes are `Output`, `Send`, or advanced `Internal`; the strip and the inspector must both edit this list only.
3. **Chains are first-class**, one owner each, addressed by `DeviceChainId`; a track has at most one chain (optional, so `project with { Sequence = ... }` keeps working).
4. **External instruments are project entities**; the profile is stated once per instrument. There is **no** "external instrument device kind": it would be a second way to express a route.
5. **Passthrough** is by `EventClass`: a device is given only the classes in `Handles`; everything else is merged back in canonical order. Only explicit filter devices drop events (SysEx data must survive note processors).
6. **Processing runs at plan-compile time** for built-in processors (immutable plans stay; the real-time thread only dispatches). Plugins process in workers at runtime. Software-instrument event streams are produced as `SoftwareInstrumentFeed`s; a plugin instrument plays them through the audio engine while audio output is on (decision 29, ADR 0032), and otherwise a `NotAudible` diagnostic says so: never imply support.
7. **Device automation is never MIDI**: it becomes `ParameterChange`s. SMF export reports device lanes it cannot write (SMF204).
8. **Presets are `DevicePreset` templates without ids**, so loading always creates new identities; presets exclude connections (no hardware references).
9. Old `TrackRoute.Transpose` migrates to a **Transpose device** in the track's chain; `Voice` moves onto the connection; `Channel` becomes `ChannelMapping.Force`; one `ExternalInstrument` per distinct (endpoint, profile) pair, including an "Unassigned" one for routes that have settings but no endpoint (the old code kept those).
10. `TrackRendering.Render(track, ppqn, channelOverride)`: the override is the **unique Force channel** of the track's outgoing event connections, else none. This preserves the old "two lanes collide after the channel override" behaviour.
11. **Mute and solo apply to a track's own content** (the old rule: solo overrides mute). Events routed *into* a muted track from a playing one still pass through its chain. Revisit only if the UI needs "mute silences the track's output". Amended for groups by decision 30 (ADR 0030).
12. **Device automation is not signal**: it is rendered for every track, playing or muted, and for project-owned Master-chain lanes, applied to built-in processors at its ticks, and returned as `ParameterFeed`s for every other device. A lane whose device is missing is reported (`AutomationTargetMissing`) and kept.
    Track and Master fader/pan automation hold the first value before the first breakpoint. Automated blocks must retain their final applied stereo gains, including pan and mute, for manual gain handoff across graph snapshots. Elapsed points are located with a binary search; seeking and in-block loop wraps reset the cursors without allocation or locks.
13. **At compile time, a plugin MIDI effect runs in its own worker (ADR 0028)**: a `RenderEvents` request runs a fresh copy from the project's stored state and parameters over the timeline (sample frames on the tempo map), and its output joins the chain. If its worker is gone, hung, or failing, or the request is over the protocol's limits, its events pass through unchanged with a diagnostic. An unknown device always passes events through, with a diagnostic. An instrument without an in-process processor takes its `Handles` classes out of the chain (this matches `SignalPresence.Through`); SysEx and other unhandled classes go on to the track's connections.
14. **The evaluator never fails on bad routing.** It leaves out every connection named in a validator error, reports it (`InvalidConnection`), and as a backstop drops any connection that still closes a loop during topological ordering (`Feedback`). Audio connections are validated and reported here but followed by the audio engine's render graph (ADR 0032).
15. **"Not routed"** is reported for a playing track with no outgoing event connection (from the track or any point inside its chain) and no instrument in its chain, whatever its content, like the old compiler. With an instrument but no connection, it is reported only when events remain after the chain (for example SysEx the instrument does not handle). A tap on a chain whose owning track is gone is left out and reported (it can never run).
16. **Arpeggiator phrases are anchored at the first note** played while nothing is held, including after a gap shorter than a step; then they step every rate interval while anything is held, so every phrase sounds (a legato note, starting exactly where the last one ends, continues the phrase). Held pitches are deduplicated and played lowest first; generated notes take the source note's channel, velocities and ordering key, and an `EventId` derived from the source note's ID and the step's tick. Output, IDs included, is identical across evaluations and whether processed in one block or many (tested).
17. **Language policy (owner, 2026-10-10; ADR 0026).** C#/.NET is the default for all code. Rust is the choice for performance-critical or native sections, and replaces C/C++ for new Bluestone-owned native code; C/C++ only for third-party libraries, vendor SDK requirements, and thin ABI shims. Rust today: the audio device callback (measured, ADR 0032) and the plugin host (native ABI, ADR 0033); the plugin data plane stayed C#. Each Rust component needs a stated reason (a measurement or a native ABI constraint) and a small versioned C ABI to .NET (ADR 0026), and Rust must not become a second application language.
18. **The inspector's fields are a view of the routing model** (`TrackOutputs`): the instrument is reused per (endpoint,
    profile); the primary connection is the track's first event connection to an external instrument (its channel
    filter is kept); transposition lives in the chain's first Transpose device, added at the end when needed, and is
    only rewritten when it changes. Format 3 migration goes through the same `Write`.
19. **Plan order and slots**: parts are ordered by source track (taps count as their chain's track, racks last), then
    connection order; each distinct available endpoint is a slot in that order. The compiler sorts by
    (tick, phase, origin, sequence, part). Voice selection is an initial event (origin = source track index, or -1 for
    a rack; sequence -1) on the forced channel, else the source track's first channel mapped by the connection, else 1.
20. **"Not routed"** at plan level is also reported for a playing track whose only external parts are unavailable, unless
    it has a software instrument or another event destination.
21. **Live notes** (audition, thru, on-screen keyboard, metronome, instrument setup) follow the nearest playable
    instrument through connections (breadth-first), with the last forced channel and the Transpose devices on the way.
    Since ADR 0035 every device on the way that consumes events is a live stage, plugin MIDI effects included, and a
    plugin instrument in a chain ends the route. Since ADR 0037 the route fans out instead: every plugin instrument in the
    chain and every usable external port reached is played, each with the stages in front of it.
22. **Edits that add a routing error are refused** (`CommandRefusedException`, shown as a message by
    `MainViewModel.Execute`): device moves, bypass, inserts, connections, instrument and mixer edits, role changes,
    adding audio, and inspector output edits. Events that would land inside an audio clip are refused; a recording
    that hits this keeps its take and says why it was not added.
23. **Connection IDs are unique** in `Project` (a file listing one twice is a format error).
24. **Plugin devices** have definition IDs `plugin:<format>:<module>/<plugin>`; available plugins join the catalog as
    data with the parameters their worker reports. State is captured into the project before every save (not on
    autosave). Removing a device stops its worker. Arrangement whole-chain replacement first captures outgoing
    plugins' live state with `PluginDeviceHost.CaptureForUndoAsync`; `EditHistory.Execute` applies that command as
    undo preparation, atomically with replacement and without another undo step or notification. A failed/no-op edit
    discards preparation; undo-only capture must not update the host's last-applied value cache, or a later sync could
    overwrite surviving live settings after a refused edit. Pending drops also check `ProjectSession.Generation`
    (document loads, not saved IDs), their target chain, current plugin availability, and the staged instance's running
    state immediately before committing. Undo capture holds the host lifecycle gate, waiting for in-flight rate
    recreation rather than missing temporarily removed outgoing instances, and includes the last recoverable snapshot
    of an outgoing plugin that is already unavailable.
25. **Undo merging** is opt-in per command: `IProjectCommand.MergeKey`. Consecutive commands with the same key within
    `EditHistory.MergeWindow` (1 s, measured with an injectable `TimeProvider`) replace the top undo step's result and
    keep its "before" state. Undo, redo, and reset end the run. Keys: `parameter:{device}:{parameter}`,
    `gain:{channel}`, `pan:{channel}`, `master-gain`, `send:{channel}:{target}`, `clip-gain:{clip}`, `clip-fades:{clip}`.
26. **`MainViewModel.RefreshAsync` coalesces**: a refresh asked for while one runs sets a flag and the running one goes
    round once more. (It used to re-post itself, which spins the UI queue, and overflows the stack with an inline
    test dispatcher.) `PlaybackController` evaluates the graph on the thread pool only when a device will be rendered
    out of process, so refreshes without plugin MIDI effects keep their old synchronous behaviour.
27. **Scope of the audio and plugin work (owner, 2026-10-11).** The audio engine, VST 3 and CLAP plugins, plugin
    rescanning, group tracks, hardware parameter automation, the mixer console, more headless UI tests, live input
    through plugins and built-in devices, plugin delay compensation, and hosting modes (ADRs 0030 to 0035). MIDI
    2.0/UMP, MPE, the visual node graph, and feedback routing stay out of scope.
28. **VST 3 first, then CLAP**, tested with installed plugins and open-source CLAP plugins installed locally (never
    vendored). The native host is Rust and loads only in the worker (ADR 0033).
29. **Audio output is Rust (cpal); the render graph is C#.** The callback was built in C# first and measured: .NET GC
    pauses missed deadlines, so only the device callback moved to Rust, copying from a ring a .NET thread fills ahead
    (ADR 0032). Live input shrinks the ring (ADR 0035).
30. **A group's mute and solo reach its members** (amends decision 11 for groups; ADR 0030).
31. **"Sandboxing" means crash isolation with hosting modes** (as Bitwig Studio uses the word), not operating-system
    containment; plugins keep network and file access (ADR 0034).
32. **Hardware parameters are automation targets** through profile parameter maps; still no "external instrument
    device kind" (decision 4; ADR 0031).
33. **New project data is additive optional fields in format 4** (group folding, parked connections, instrument
    automation targets, sends, clip gain and fades): no version bump, files without them are unchanged.
34. **Format 5 separates a loop's range from whether it loops** (ADR 0039): disabling a loop never deletes it, and
    format 4 files migrate as enabled arranger loops. Clip-loop repeats are produced at plan time, never on the
    real-time thread, within a per-clip and per-sequence event bound; edits beyond it are refused, not truncated.

## 5. Regression net

Format 3 projects must play and export exactly as they did before the routing model (ADR 0023) replaced per-track routes. `Format3EquivalenceTests` asserts this through the current pipeline (format 4 migration, signal graph, routing preparation, compiler). The expected values were frozen from the old pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with every endpoint present except `gone-*` (missing) and `old-*` (present as `new-*` with the same name, so bound by name), profiles from `Bluestone.Desktop/Profiles` plus the Generic XG profile kept as a test fixture in `Infrastructure/Fixtures/Profiles` (no longer shipped):

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`: Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override. `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must stay green unchanged. The fixtures live in `src/dotnet/Bluestone.Tests.Unit/Infrastructure/Fixtures/` and must never be regenerated.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`; initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 6. Not verified

- Real MIDI hardware and a profile parameter map against a hardware instrument; Windows MIDI input timing is measured only through a Windows MIDI Services software loopback.
- Audio output heard on Windows and Linux (Windows WASAPI callbacks run but output was not listened to; ASIO was measured in a local build and is currently excluded from standard builds); unplugging a real device (device loss is tested with a test backend).
- Linux ALSA sequencer, audio and engine timing on native devices: the 2026-11-23 WSL2 container had no ALSA sequencer or audio device. Loaded dispatch and a 64-frame reference plugin worker missed targets there; see ADR 0043 for limits and results, and [issue 117](https://github.com/djflan/Bluestone/issues/117) for the native Linux follow-up.
- Plugin editors on Linux and hand-checking Windows editor resize/focus/multi-monitor behavior; Windows editor lifecycle, follow/pin, and key-routing tests pass with installed plugins.
- ASIO on an external audio interface: the installed Realtek ASIO driver was exercised, but the RME MADIface was not connected; the acoustic round trip is unmeasured.
- A third-party MIDI effect plugin (none installed: Scaler 3 and Melody Sauce 3 report as instruments); third-party
  plugins live and delay-compensated through the audio engine on a real device (reference and test plugins only).
- The acoustic round trip of live input (no loopback recording); measured numbers include the device's reported
  output latency.
- The Plugins view and Audio Settings were not looked at by eye: the app was launched as a smoke test with the screen
  locked. Headless UI tests cover the workflows and save frames.
- Max reviewed the track-owned professional mixer in the running app at 1318×847 (compact) and 1620×1000 (normal),
  including independent Master selection and Inspector scope, LUFS-S warm-up, and the since-removed Bar/Round meter
  presentation. This was a no-signal UI review only; physical audio remains unverified.
- The Identity R6 redesign (Machined theme, rebuilt mixer strip and Device View, bottom transport bar, signal gradient
  editor) was verified headless only: Avalonia UI tests with simulated meter data, rendered at 1040×700 and 1440×900.
  The screen was locked, so there was no capture of the running app. It has not been checked with real-time audio, on
  HiDPI or GPU rendering, or with a screen reader.
- LED motion on `a34f9b7` was checked with manual-clock headless tests, not hardware: application clock ticks and
  the Out meter's ladder draw reported 0 managed B; animated gain draws matched their at-rest allocation baseline.
  Key and MIDI offscreen draws allocated backend bytes. This does not establish zero allocation for all rendering,
  or verify the real frame-request/compositor path, hardware timing, or macOS/Windows system-preference reads
  (ADR 0043). The ladder's first fade after a gradient change now also measures 0 managed B (its glow steps are
  prebuilt with its colors; the clip lamp's cached step brushes are warmed by the test's first pass, so that part is
  not proven). If a system reduce-motion read fails, LEDs reduce motion and a warning is traced
  once; that failure path is not exercised by a test.
- The Settings popover, color picker, and toolbar selected states on the `9f2d092` tree were verified headless only:
  Avalonia UI tests plus real-control renders at 1040 and 1440 wide. The live app was not captured (macOS
  Accessibility and Screen Recording permissions were missing), and the user waived final UI approval for this
  change. Not checked on HiDPI or GPU rendering or with a screen reader.

## 7. Unresolved implementation backlog

This is the backlog captured before ADR 0041 redefined the product direction. It remains useful as an inventory of
known gaps, but it is not priority order. Use [release-1.0.md](release-1.0.md) for execution order and acceptance gates.

1. **Audio engine:** importing audio files as clips (today they come only from project files), audio input and recording, compressed formats (FLAC, MP3, Ogg), send and fader automation,
   real-device timing and recovery verification on Windows and Linux. Native Linux ALSA, engine and plugin-worker timing checks remain open in [issue 117](https://github.com/djflan/Bluestone/issues/117); WSL2 results are not hardware evidence.
2. **Plugins:** editors verified on Linux, embedded (not floating) editors, second-architecture workers
   (Rosetta), SysEx to native plugins, note expressions, a protocol message for a worker-side idle-handler failure
   (today it is logged to standard error and that instance's notifications stop). The full list is in
   [plugin-hosting.md](plugin-hosting.md#not-done).
3. **Interaction polish follow-ups**
   ([deferred work](worklogs/bitwig-interaction-polish.md#deferred-work)): Windows key routing on non-US layouts,
   arranger loops shorter than one audio block, ghost repeats in the piano roll, audio repeat markers under tempo
   changes, per-clip loop crossfades, and remembered editor geometry.
4. **Groups:** drag and drop into a group; a folded group's lane summarizing its members' clips.
5. **Out of scope by decision 27:** MIDI 2.0/UMP, MPE, visual node-graph editing, feedback routing.
6. **Device-centric workspace (ADR 0036, 0037), next steps:** live input fanning out to every instrument of a
   layered chain; plugin preset browsing and remote-parameter pages in the compact plugin panel (the host protocol
   exposes neither yet); a stable per-note ID in the domain for expressive notes; container devices with child-chain
   slots shown inline in the Device View; declarative third-party panel layouts and optional custom Avalonia editors;
   base versus modulated parameter values and control-rate modulators; optional auto-collapse of panels; samples
   and presets in the Browser once there is an index; drag from the Browser onto a track or the arranger;
   keyboard reordering of devices; and verifying detached windows and monitor recovery on real multi-monitor setups.
7. **Professional mixer (ADR 0050, PR #95), deferred or not built:** per-strip width and spectrum analysis views
   (they need analysis DSP; collapsed must mean no analysis runs and no reserved space); a Master chain view opened from the Master inserts (today they only select
   Master); Ctrl+Left/Right jumps between strips; arming MIDI-only tracks from the mixer; a console-level LUFS-S
   warming-up status (only Master announces it); the full-height strip at the default docked height (the 330 px
   detail region puts the strips below `Mixer.RoomyStripHeight`, 360 px, so the fader zone shrinks; the meter is held
   to at least `Mixer.TightRailsHeight`, 72 px, checked at 1440×900);
   undo merging for continuous gestures that does not rely on a 1 s time window; the bx_console AMEK 9099 parameter
   mapping (needs verified parameter IDs and a normalized-to-dB conversion); and tests for the send knob's wheel,
   modifier-chord and disabled-automation handling and the Master clip-reset button. Also deferred and not yet
   verified: Copilot review of PR #95 flagged that device-parameter automation may not be chased to its value at the
   new position on play, seek, or loop wrap. Master-chain lanes use the same `ParameterChange` schedule as track
   device lanes, so any fix belongs to that shared path.
8. **Playback responsiveness and loop wraps (branch `team/playback-responsiveness`), deferred or not built:**
   - Plugin note events are dropped silently when a plugin block arrives late or is withdrawn
     (`HostBlockExchange` withdraw path, `PluginBlockPipeline` Busy submit). Design: a `TryCollect` Withdrawn
     status (a contract change; check it against ADR 0044 first), a per-slot event history, and undelivered events
     carried into the next block at offset 0, with overflow counted in `InputEventsDropped`.
   - The render-ahead `heardIn` estimate in `AudioOutput` has an error that skews audio against the timeline; the
     loop-aware follow (ADR 0032) now tolerates the skew at the wrap but does not remove it.
   - Every mixer or device parameter edit still runs a full sync and plan reload. Not built: a parameter fast path
     (no Prepare/Compile/Load for pure parameter edits) and removing the second `SyncTracks` after a refresh (it
     needs a "nothing changed" signal from `PlaybackController`). Plugin-editor edits (`PluginDeviceHost.RecordEdits`)
     also sync once per batch.
   - A synthetic unpaced cursor drag still takes about 190 ms from select to inspector with 2 LCD updates/s, and
     select to inspector in engine mode did not improve.
   - Between a reload in the pending-wrap window and the wrap, `PlaybackEngine.Position` reads the loop end rather
     than the tick before it. Cosmetic.
   - Engine mode stays opt-in on Windows and Linux (ADR 0047); both modes share `PlaybackEngine` and showed the same
     lag and loop behavior.
   - Not verified on hardware: MIDI output staying in the loop (the MIDI loopback received nothing), macOS and
     Linux, and an audible check that notes are continuous at the wrap.
9. **Identity R6 (branch `feature/identity-r6`), deferred or not built.** R6 supersedes the earlier ladder-LED and
   focus-lighting backlog (recessed wells, junction ladders, and exponential glows in custom-drawn controls are built).
   - The mixer's monitor key: the model has no monitor state.
   - The Quantize, Timebase, and Snap toolbar fields and the Arrange/Mix/Edit switch from the mockup.
   - The device preset field.
   - Device display wells (wave, EQ curve, filter): no data drives them yet.
   - The track header's level line: tracks have no volume to show.
   - Track header height: 66 px in the mockup, about 50 px today.
   - Still open from the earlier identity work: theme presets (Studio Blue, Amber, Mono), which need `DynamicResource`
     plumbing, and a user glow-intensity setting. The transport glyph glows still use Avalonia's blur
     (`DropShadowEffect`), not `Theme/Glow`.
10. **LED motion verification (ADRs 0037, 0043, 0050):** measure the real
    `TopLevel.RequestAnimationFrame`/compositor path and hardware timing, and verify macOS `NSWorkspace` Reduce
    motion and Windows client-area animation preference reads. These preferences refresh at startup and
    main-window activation, not through continuous system-change notifications. Current allocation evidence is manual-clock,
    headless/offscreen only, with the limits recorded in §6 and ADR 0043.
11. **Indicator colors and Settings popover (ADR 0051), not yet verified:** a running-app review of the popover,
    color picker, and toolbar selected states at compact and normal sizes, on HiDPI, and with a screen reader; a
    test for the `SystemMotion` failure path (reduce motion and trace once); and a test that a display-settings
    save failure reaches Messages (`MainViewModel` → `AddMessage`). Only the `SaveFailed` event is tested.
12. **Settings popover and LED follow-ups (from the ADR 0051 sign-offs), deferred:**
    - Product question: Space and the other single-key transport shortcuts do nothing while Settings is open (by
      design since the panel existed); decide whether transport keys should pass through.
    - Enter does not activate some focused buttons (already on `main`); Tab leaving Settings closes it without an
      announcement; opening and closing Settings moves focus as if by Tab, so focus rings show after mouse use.
    - The color picker can stay open without keyboard focus, so Escape does not close it until it is clicked; at
      wide sizes it overhangs the popover; its swatches show no selected state and are flatter than the picked P1.
    - The Display page icon matches Audio's; the "0%" readout uses the accent color; the Play glow is clipped on the
      left (already on `main`).
    - Tests still wanted: a `LedFade` detach or retarget mid-fade, the picker consuming its own clicks, and the
      search-box focus fallback.
13. **.NET 11 migration (#112), deferred or not verified** (details in [dotnet11-feasibility.md](dotnet11-feasibility.md)):
    - The engine GC probe (at least 300 s, 1 ms gate, 0 allocations, 0 JIT-compiled methods) on a physical Mac on
      the migrated commit. Not run: no Mac was reachable. This is the open acceptance item; the startup compaction's
      4-of-4 result is from experiment builds only.
    - The Windows Arm64 minimum: Microsoft's breaking-change page says armv8.0-a, its runtime What's-new page says
      armv8.0-a + LSE. Bluestone states the stricter one. Recheck both pages and update the README and the report
      when they agree.
    - `osx-x64` and `win-arm64`: launched only on hosted CI runners (`--check-allocations`, run 45520109671,
      0 bytes); no physical hardware or timing.
    - The desktop app has not been launched on any of the six RIDs; CI only publishes it.
    - Native Linux timing on either runtime ([issue 117](https://github.com/djflan/Bluestone/issues/117)).
    - Same-time MIDI ordering and the first-due startup error. Both appear on .NET 10 and .NET 11, so they are not
      migration regressions; they are tracked separately.
    - The Windows .NET 10 engine's GC pauses (about 312 KB promoted per gen0 collection, 1.3–3.1 ms pauses) are
      unexplained. .NET 11 does not show them, so they no longer block anything, but they return on a rollback.
    - Physical MIDI and audio devices and third-party plugins on .NET 11.
    - Desktop memory: on a Mac, a JIT-run probe process committed more GC memory on .NET 11 (329–371 MB against
      189 MB). Not compared on the migrated commit; measure the desktop app on both runtimes.
    - Targeted checks for .NET 11 behavior changes: signal-gradient positions finer than 0.001 can round to a
      different label, and Unix engine and worker pipes now use mode 0600. Neither has a dedicated test.
    - BenchmarkDotNet is `0.16.0-preview.1`; move to a stable release with .NET 11 support when one ships.
    - Clippy was not run on the migration commit (no Rust changes); run it on the next Rust change.
    - Optional: assert that NativeAOT publish logs contain "Generating native code", so a silent fallback to a JIT
      build fails CI.
