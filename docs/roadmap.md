# Roadmap and working notes

Working notes for whoever continues Bluestone: where things stand, how to build and test, traps already hit,
decisions not to re-decide, the regression net, what is not verified, and what remains. The design itself is in
[architecture.md](architecture.md), [plugin-hosting.md](plugin-hosting.md), [project-format.md](project-format.md),
and the [ADRs](adr/README.md). Bluestone grew from a MIDI workstation into a DAW in two steps: the routing
architecture (ADRs 0021 to 0029), then the audio engine and plugins (ADRs 0030 to 0035).

## 1. Status

| Area | State |
| ---- | ----- |
| Domain: roles, devices, chains, instruments, connections, mixer, device automation | Done, tested |
| `Bluestone.Signal`: chain runner, built-in devices, signal graph | Done, tested; built-in devices also have live, allocation-free forms (ADR 0035) |
| Routing model, format 4, migrations, chain presets, commands, inspector | Done, tested |
| Group tracks: mute/solo reach members, joining routes into the group, folding saved (ADR 0030) | Done, tested; headless UI test |
| Hardware parameter automation: profile parameter maps → CC, RPN, NRPN, SysEx with checksums, thinning, chase (ADR 0031) | Done, tested; XG map unverified on hardware |
| Audio engine: cpal output in Rust, render graph in C# behind a render-ahead ring, WAVE/AIFF clips with SRC and disk streaming, mixer with sends, clip gain and fades, meters, device-loss fallback, one transport (ADR 0032) | Done, tested with an offline driver and the real default device; heard on macOS only |
| Live input through every device on the live route (built-in, plugin MIDI effects, plugin instruments), live render-ahead queue, plugin delay compensation across the graph (ADR 0035) | Done, tested sample-accurately; latency measured on macOS |
| Third-party plugins: VST 3 and CLAP through a Rust host in the worker; scanning, parameters, state, processing, editors, crash recovery (ADR 0033) | Done; CI runs Rust test plugins as VST 3 and CLAP; installed plugins tested on macOS |
| Plugin hosting modes: together, by manufacturer, by plugin (default), individually; per-plugin override; crash blast radius and counts (ADR 0034) | Done, tested with real workers |
| UI: Plugins view (rescan, quarantine retry, hosting), Mixer console, Audio Settings, editor button, group menu, instrument parameter lanes | Done; view-model and headless UI tests; the app launched as a smoke test only |
| Device-centric workspace: design tokens, semantic regions and rails, dockable/detachable Browser, multiple follow/lock Device Views with per-view presentation, drag-and-drop insertion and reordering, local layout persistence with monitor recovery (ADR 0036); parallel signal bundle in chains (ADR 0037) | Done; view-model and headless UI tests with frames at 1280×800 and 1920×1080; the real app launched under Xvfb; not checked on real multiple monitors |
| Interaction polish: arranger loop kept while off, clip loops on note and audio clips with Consolidate, format 5 (ADR 0039); ruler drag zoom and scroll, Zoom to Fit/Selection/Loop; drag to replace a device; floating editors that follow the selection, pin, and send keys to Bluestone (ADR 0040) | Done; unit, view-model, and headless UI tests; editor hiding and key forwarding checked on macOS only ([worklog](worklogs/bitwig-interaction-polish.md)) |
| Headless UI tests: Device View, Browser, Racks, Mixer, Track, Routing, Connections, arrangement, piano roll, shortcuts, groups, plugins, mixer console | Done |
| Acceptance scenarios 1–15 | All have tests: tables in [architecture.md](architecture.md#acceptance-scenarios-and-their-tests) and [plugin-hosting.md](plugin-hosting.md#acceptance-scenarios) |
| Rust | Two cdylib crates (`bluestone_audio`, `bluestone_plugin_host`) plus test plugins, each with a measured or native-ABI reason (ADRs 0032, 0033) |

## 2. Build environment

Use the README commands on a machine with the .NET SDK from `global.json` (10.0.300 or a later 10.0 feature band) and
a Rust toolchain (`cargo`; the hosted CI runners have one). `dotnet build` builds the Rust crates through
`src/dotnet/Native.targets` (`cargo build --locked`; after changing Rust dependencies run `cargo build` in
`src/native/` once). Linux needs `libasound2-dev`. One test project, one namespace:
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
- To look at the UI: `./src/dotnet/Bluestone.Desktop/bin/Debug/net10.0/Bluestone.Desktop path/to/project.bluestone`.

## 3. Analyzer traps already hit (warnings are errors)

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
number ("decision 16 in docs/roadmap.md"), so the numbers never change: amend a decision by adding a new one that
says so.

1. **Role is persisted and reconciled** with content (it cannot be derived for empty/Effect/Group tracks). `Track` never rejects clips because of role. Effect/Group refuse clips; narrowing that would hide content is refused; conversion never discards.
2. **One routing model**: the `SignalConnection` list. Sources: track output, rack output, device tap. Destinations: track/rack input, external-instrument part (port + channel via mapping), mixer channel, master. The strip and the inspector must both edit this list only.
3. **Chains are first-class**, one owner each, addressed by `DeviceChainId`; a track has at most one chain (optional, so `project with { Sequence = ... }` keeps working).
4. **External instruments are project entities**; the profile is stated once per instrument. There is **no** "external instrument device kind": it would be a second way to express a route.
5. **Passthrough** is by `EventClass`: a device is given only the classes in `Handles`; everything else is merged back in canonical order. Only explicit filter devices drop events (SysEx/XG data must survive note processors).
6. **Processing runs at plan-compile time** for built-in processors (immutable plans stay; the real-time thread only dispatches). Plugins process in workers at runtime. Software-instrument event streams are produced as `SoftwareInstrumentFeed`s; a plugin instrument plays them through the audio engine while audio output is on (decision 29, ADR 0032), and otherwise a `NotAudible` diagnostic says so: never imply support.
7. **Device automation is never MIDI**: it becomes `ParameterChange`s. SMF export reports device lanes it cannot write (SMF204).
8. **Presets are `DevicePreset` templates without ids**, so loading always creates new identities; presets exclude connections (no hardware references).
9. Old `TrackRoute.Transpose` migrates to a **Transpose device** in the track's chain; `Voice` moves onto the connection; `Channel` becomes `ChannelMapping.Force`; one `ExternalInstrument` per distinct (endpoint, profile) pair, including an "Unassigned" one for routes that have settings but no endpoint (the old code kept those).
10. `TrackRendering.Render(track, ppqn, channelOverride)`: the override is the **unique Force channel** of the track's outgoing event connections, else none. This preserves the old "two lanes collide after the channel override" behaviour.
11. **Mute and solo apply to a track's own content** (the old rule: solo overrides mute). Events routed *into* a muted track from a playing one still pass through its chain. Revisit only if the UI needs "mute silences the track's output". Amended for groups by decision 30 (ADR 0030).
12. **Device automation is not signal**: it is rendered for every track, playing or muted, applied to built-in processors at its ticks, and returned as `ParameterFeed`s for every other device. A lane whose device is missing is reported (`AutomationTargetMissing`) and kept.
13. **At compile time, a plugin MIDI effect runs in its own worker (ADR 0028)**: a `RenderEvents` request runs a fresh copy from the project's stored state and parameters over the timeline (sample frames on the tempo map), and its output joins the chain. If its worker is gone, hung, or failing, or the request is over the protocol's limits, its events pass through unchanged with a diagnostic. An unknown device always passes events through, with a diagnostic. An instrument without an in-process processor takes its `Handles` classes out of the chain (this matches `SignalPresence.Through`); SysEx and other unhandled classes go on to the track's connections.
14. **The evaluator never fails on bad routing.** It leaves out every connection named in a validator error, reports it (`InvalidConnection`), and as a backstop drops any connection that still closes a loop during topological ordering (`Feedback`). Audio connections are validated and reported here but followed by the audio engine's render graph (ADR 0032).
15. **"Not routed"** is reported for a playing track with no outgoing event connection (from the track or a tap on its devices) and no instrument in its chain, whatever its content, like the old compiler. With an instrument but no connection, it is reported only when events remain after the chain (for example SysEx the instrument does not handle). A tap on a chain whose owning track is gone is left out and reported (it can never run).
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
    plugin instrument in a chain ends the route.
22. **Edits that add a routing error are refused** (`CommandRefusedException`, shown as a message by
    `MainViewModel.Execute`): device moves, bypass, inserts, connections, instrument and mixer edits, role changes,
    adding audio, and inspector output edits. Events that would land inside an audio clip are refused; a recording
    that hits this keeps its take and says why it was not added.
23. **Connection IDs are unique** in `Project` (a file listing one twice is a format error).
24. **Plugin devices** have definition IDs `plugin:<format>:<module>/<plugin>`; available plugins join the catalog as
    data with the parameters their worker reports. State is captured into the project before every save (not on
    autosave). Removing a device stops its worker.
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

Format 3 projects must play and export exactly as they did before the routing model (ADR 0023) replaced per-track routes. `Format3EquivalenceTests` asserts this through the current pipeline (format 4 migration, signal graph, routing preparation, compiler). The expected values were frozen from the old pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with every endpoint present except `gone-*` (missing) and `old-*` (present as `new-*` with the same name, so bound by name), profiles from `profiles/`:

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`: Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override. `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must stay green unchanged. The fixtures live in `src/dotnet/Bluestone.Tests.Unit/Infrastructure/Fixtures/` and must never be regenerated.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`; initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 6. Not verified

- Real MIDI hardware, and the XG parameter map against a real XG instrument.
- Audio output heard on Windows and Linux (CI has no audio devices); unplugging a real device (device loss is tested
  with a test backend).
- Plugin editors on Windows and Linux (they build, and CI runs the rest of the plugin path there); closing or
  resizing an editor by hand; hiding and showing editors and sending their keys to Bluestone on Windows (compiled
  and linted only), and editor hiding under a Linux window manager.
- A third-party MIDI effect plugin (none installed: Scaler 3 and Melody Sauce 3 report as instruments); third-party
  plugins live and delay-compensated through the audio engine on a real device (reference and test plugins only).
- The acoustic round trip of live input (no loopback recording); measured numbers include the device's reported
  output latency.
- The views added with the audio engine and plugins (Plugins view, mixer console, Audio Settings) were not looked at
  by eye: the app was launched as a smoke test with the screen locked. Headless UI tests cover the workflows and save
  frames.

## 7. Remaining work

Suggested order:

1. **Audio engine:** importing audio files as clips (today they come only from project files), audio input and recording, compressed formats (FLAC, MP3, Ogg), send and fader automation,
   real-time-priority verification on Windows and Linux.
2. **Plugins:** editors verified on Windows and Linux, embedded (not floating) editors, second-architecture workers
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
