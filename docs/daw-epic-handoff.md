# DAW architecture epic: handoff

Read this before continuing. The original requirements are in `docs/daw-epic-prompt.md` (the source of truth;
sections 19, 22, 25, and 26 matter most). The plan and assessment are in `docs/daw-architecture-plan.md`; the result
is described by `docs/architecture.md`, `docs/plugin-hosting.md`, `docs/project-format.md`, and ADRs 0021 to 0035.
These `daw-epic-*` files can be deleted once the owner accepts the epic.

The first epic (branch `claude/busy-gates-qengpq`, PR #8) built the architecture. The second
(`epic/audio-engine-and-plugins`) delivered everything its section 7 listed as remaining: the audio engine,
VST3 and CLAP plugins, more headless UI tests, plugin rescanning, group tracks, hardware parameter automation, a mixer
console, and from the deferred list live input through devices and plugins, plugin delay compensation, and hosting
modes. Owner decisions for the second epic (2026-10-11) are decisions 27 to 33 below.

## 1. Status

| Area | State |
| ---- | ----- |
| Domain: roles, devices, chains, instruments, connections, mixer, device automation | Done, tested |
| `Bluestone.Signal`: chain runner, built-in devices, signal graph | Done, tested; built-in devices also have live, allocation-free forms (ADR 0035) |
| Routing model, format 4, migrations, chain presets, commands, inspector | Done, tested (first epic) |
| Group tracks: mute/solo reach members, joining routes into the group, folding saved (ADR 0030) | Done, tested; headless UI test |
| Hardware parameter automation: profile parameter maps → CC, RPN, NRPN, SysEx with checksums, thinning, chase (ADR 0031) | Done, tested; XG map unverified on hardware |
| Audio engine: cpal output in Rust, render graph in C# behind a render-ahead ring, WAVE/AIFF clips with SRC and disk streaming, mixer with sends, clip gain and fades, meters, device-loss fallback, one transport (ADR 0032) | Done, tested with an offline driver and the real default device; heard on macOS only |
| Live input through every device on the live route (built-in, plugin MIDI effects, plugin instruments), live render-ahead queue, plugin delay compensation across the graph (ADR 0035) | Done, tested sample-accurately; latency measured on macOS |
| Third-party plugins: VST3 and CLAP through a Rust host in the worker; scanning, parameters, state, processing, editors, crash recovery (ADR 0033) | Done; CI runs Rust test plugins as VST3 and CLAP; installed plugins tested on macOS |
| Plugin hosting modes: together, by manufacturer, by plugin (default), individually; per-plugin override; crash blast radius and counts (ADR 0034) | Done, tested with real workers |
| UI: Plugins view (rescan, quarantine retry, hosting), Mixer console, Audio Settings, editor button, group menu, instrument parameter lanes | Done; view-model and headless UI tests; app launched as a smoke test only (screen locked) |
| Headless UI tests: Devices, Racks, Mixer, Track, Routing, Connections, arrangement, piano roll, shortcuts, groups, plugins, mixer console | Done |
| Acceptance scenarios 1–15 | All have tests: table in `docs/architecture.md` and `docs/plugin-hosting.md` |
| Rust | Two cdylib crates (`bluestone_audio`, `bluestone_plugin_host`) plus test plugins, each with a measured or native-ABI reason (ADRs 0032, 0033) |
| Code review | Three review passes over the second epic; 13 confirmed findings fixed with regression tests |

**Last full run:** see the PR for this epic for the final numbers (macOS arm64, SDK 10.0.300, and CI on Ubuntu,
macOS, Windows).

## 2. Build environment

Use the README commands on a machine with SDK 10.0.300 (`global.json`) and a Rust toolchain (`cargo`; the hosted
CI runners have one). `dotnet build` builds the Rust crates through `src/Native.targets` (`cargo build --locked`;
after changing Rust dependencies run `cargo build` in `native/` once). Linux needs `libasound2-dev`. One test project:
`dotnet test --project src/Bluestone.Tests.Unit/Bluestone.Tests.Unit.csproj -- --filter-namespace "Bluestone.Tests.Unit.Signal"`.
Rust: `cargo test`, `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings` in `native/`.

- `CoreMidiProviderTests` fail intermittently on macOS under load (real CoreMIDI timing; up to three of them in a
  full run while other builds run; they pass alone). CI leaves them out. Not a regression.
- Plugin tests that make a worker call `Environment.FailFast` leave macOS crash reports in
  `~/Library/Logs/DiagnosticReports`. Harmless.
- Tests with installed third-party plugins (`InstalledPluginTests`, `PluginEditorTests`) and the real audio device
  (`AudioDeviceTests`) skip with a reason where those are absent (CI).
- The app cannot start while the Mac's display is asleep (Avalonia: "RenderTimer -6661"). `caffeinate -u -t 60 &`
  wakes it; with the screen locked, screenshots show only the lock screen.
- To look at the UI: `./src/Bluestone.Desktop/bin/Debug/net10.0/Bluestone.Desktop path/to/project.bluestone`.

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
- `dotnet format` reorders imports after edits; run `dotnet format src/Bluestone.slnx` before `--verify-no-changes`.

## 4. What exists, and the decisions behind it (do not re-decide these)

Where things are: `docs/architecture.md` ("Projects and their references", "Where does this belong?", and "Tracks,
devices, and routing"). Plugin hosting: `docs/plugin-hosting.md`, including the application bridge.

### Decisions
1. **Role is persisted and reconciled** with content (it cannot be derived for empty/Effect/Group tracks). `Track` never rejects clips because of role. Effect/Group refuse clips; narrowing that would hide content is refused; conversion never discards.
2. **One routing model**: the `SignalConnection` list. Sources: track output, rack output, device tap. Destinations: track/rack input, external-instrument part (port + channel via mapping), mixer channel, master. The strip and the inspector must both edit this list only.
3. **Chains are first-class**, one owner each, addressed by `DeviceChainId`; a track has at most one chain (optional, so `project with { Sequence = ... }` keeps working).
4. **External instruments are project entities**; the profile is stated once per instrument. There is **no** "external instrument device kind": it would be a second way to express a route.
5. **Passthrough** is by `EventClass`: a device is given only the classes in `Handles`; everything else is merged back in canonical order. Only explicit filter devices drop events (SysEx/XG data must survive note processors).
6. **Processing runs at plan-compile time** for built-in processors (immutable plans stay; the real-time thread only dispatches). Plugins process in workers at runtime. Software-instrument event streams are produced but **not played** (no audio engine): emit a diagnostic, never imply support.
7. **Device automation is never MIDI**: it becomes `ParameterChange`s. SMF export reports device lanes it cannot write (SMF204).
8. **Presets are `DevicePreset` templates without ids**, so loading always creates new identities; presets exclude connections (no hardware references).
9. Old `TrackRoute.Transpose` migrates to a **Transpose device** in the track's chain; `Voice` moves onto the connection; `Channel` becomes `ChannelMapping.Force`; one `ExternalInstrument` per distinct (endpoint, profile) pair, including an "Unassigned" one for routes that have settings but no endpoint (the old code kept those).
10. `TrackRendering.Render(track, ppqn, channelOverride)`: the override is the **unique Force channel** of the track's outgoing event connections, else none. This preserves the old "two lanes collide after the channel override" behaviour.
11. **Mute and solo apply to a track's own content** (the old rule: solo overrides mute). Events routed *into* a muted track from a playing one still pass through its chain. Revisit only if the UI needs "mute silences the track's output". Amended for groups by decision 30 (ADR 0030).
12. **Device automation is not signal**: it is rendered for every track, playing or muted, applied to built-in processors at its ticks, and returned as `ParameterFeed`s for every other device. A lane whose device is missing is reported (`AutomationTargetMissing`) and kept.
13. **At compile time, a plugin MIDI effect runs in its own worker (ADR 0028)**: a `RenderEvents` request runs a fresh copy from the project's stored state and parameters over the timeline (sample frames on the tempo map), and its output joins the chain. If its worker is gone, hung, or failing, or the request is over the protocol's limits, its events pass through unchanged with a diagnostic. An unknown device always passes events through, with a diagnostic. An instrument without an in-process processor takes its `Handles` classes out of the chain (this matches `SignalPresence.Through`); SysEx and other unhandled classes go on to the track's connections.
14. **The evaluator never fails on bad routing.** It leaves out every connection named in a validator error, reports it (`InvalidConnection`), and as a backstop drops any connection that still closes a loop during topological ordering (`Feedback`). Audio connections are validated and reported but not followed (no audio engine).
15. **"Not routed"** is reported for a playing track with no outgoing event connection (from the track or a tap on its devices) and no instrument in its chain, whatever its content, like the old compiler. With an instrument but no connection, it is reported only when events remain after the chain (for example SysEx the instrument does not handle). A tap on a chain whose owning track is gone is left out and reported (it can never run).
16. **Arpeggiator phrases are anchored at the first note** played while nothing is held, including after a gap shorter than a step; then they step every rate interval while anything is held, so every phrase sounds (a legato note, starting exactly where the last one ends, continues the phrase). Held pitches are deduplicated and played lowest first; generated notes take the source note's channel, velocities and ordering key, and an `EventId` derived from the source note's ID and the step's tick. Output, IDs included, is identical across evaluations and whether processed in one block or many (tested).
17. **Language policy (owner clarification, 2026-10-10; overrides any "C# only, no Rust" wording in earlier task instructions or the plugin spec).** C#/.NET is the default for all code. Rust is the choice for performance-critical or native sections, and replaces C/C++ for new Bluestone-owned native code; C/C++ only for third-party libraries, vendor SDK requirements, and thin ABI shims. Likely Rust candidates: the plugin worker's native VST3 hosting layer, the real-time shared-memory data plane and audio callback path, and DSP once an audio engine exists. Each needs a stated reason (a measurement or a native ABI constraint), a small versioned C ABI to .NET (prompt section 16.3), and must not become a second application language. This is the same as prompt section 16.
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
    `gain:{channel}`, `pan:{channel}`, `master-gain`.
26. **`MainViewModel.RefreshAsync` coalesces**: a refresh asked for while one runs sets a flag and the running one goes
    round once more. (It used to re-post itself, which spins the UI queue, and overflows the stack with an inline
    test dispatcher.) `PlaybackController` evaluates the graph on the thread pool only when a device will be rendered
    out of process, so refreshes without plugin MIDI effects keep their old synchronous behaviour.
27. **Second-epic scope (owner, 2026-10-11).** All of the first epic's section 7.1 to 7.4, plus from 7.5 live input
    through plugins and built-in devices, plugin delay compensation, and hosting modes. MIDI 2.0/UMP, MPE, the visual
    node graph, and feedback routing stay out of scope.
28. **VST3 first, then CLAP**, tested with the owner's installed plugins and open-source CLAP plugins installed
    locally (never vendored). The native host is Rust and loads only in the worker (ADR 0033).
29. **Audio output is Rust (cpal); the render graph is C#.** The callback was built in C# first and measured: .NET GC
    pauses missed deadlines, so only the device callback moved to Rust, copying from a ring a .NET thread fills ahead
    (ADR 0032). Live input shrinks the ring (ADR 0035).
30. **A group's mute and solo reach its members** (amends decision 11 for groups; ADR 0030).
31. **"Sandboxing" means crash isolation with hosting modes** (Bitwig-style), not operating-system containment;
    plugins keep network and file access (ADR 0034).
32. **Hardware parameters are automation targets** through profile parameter maps; still no "external instrument
    device kind" (decision 4; ADR 0031).
33. **New project data is additive optional fields in format 4** (group folding, parked connections, instrument
    automation targets, sends, clip gain and fades): no version bump, files without them are unchanged.

## 5. Regression net (hold the refactor to these)

Asserted by `Format3EquivalenceTests` through the **new** pipeline (migration, signal graph, routing preparation, compiler). Frozen from the **old** pipeline (resolve routes, `PlaybackRouting.Prepare`, compile), with every endpoint present except `gone-*` (missing) and `old-*` (present as `new-*` with the same name, so bound by name), profiles from `/profiles`:

| Fixture | Playback hash | SMF export hash | Slots | Plan events |
| ------- | ------------- | --------------- | ----- | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

Hash = `PlanDump.Hash(PlanDump.PlaybackStream(plan))` / `ExportStream`. Old diagnostics for `format3-routing-full`: Orphan, Settings only, Unrouted are "not routed"; Pad has 1 clip event replaced by automation and 1 lane dropped after the channel override. `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` (format 2 fixtures) must stay green unchanged. The fixtures live in `src/Bluestone.Tests.Unit/Infrastructure/Fixtures/` and must never be regenerated.

Ordering that must be preserved: plan events sort by `(tick, EventPhase, source track index, index within source)`; initial voice events use negative indexes so they precede the track's events in the same phase; split SysEx joining is per source track.

## 6. Not verified

- Real MIDI hardware, and the XG parameter map against a real XG instrument.
- Audio output heard on Windows and Linux (CI has no audio devices); unplugging a real device (device loss is tested
  with a test backend).
- Plugin editors on Windows and Linux (they build, and CI runs the rest of the plugin path there); closing or
  resizing an editor by hand.
- A third-party MIDI effect plugin (none installed: Scaler 3 and Melody Sauce 3 report as instruments); third-party
  plugins live and delay-compensated through the audio engine on a real device (reference and test plugins only).
- The acoustic round trip of live input (no loopback recording); measured numbers include the device's reported
  output latency.
- The app was launched as a smoke test, but no view was looked at by eye in the second epic (the screen was locked);
  headless UI tests cover the workflows and save frames.

## 7. Remaining work

Nothing here blocks the epic. Suggested order:

1. **Repository layout (owner request, after this epic, as its own PR):** move everything under `src/` to
   `src/dotnet/` and `native/` to `src/native/`. `Native.targets`' `../native/` path then still resolves; update the
   solution path in CI and docs, CI's `working-directory: native`, `.gitignore`, Dependabot (add a Cargo entry), and
   check tests that find the repository root by walking up folders.
2. **Audio engine, remaining:** audio input and recording, compressed formats (FLAC, MP3, Ogg), send and fader
   automation, real-time-priority verification on Windows and Linux.
3. **Plugins, remaining:** editors verified on Windows and Linux, embedded (not floating) editors, second-architecture
   workers (Rosetta), SysEx to native plugins, note expressions, a protocol message for a worker-side idle-handler
   failure (today it is logged to stderr and that instance's notifications stop).
4. **Groups:** drag and drop into a group; a folded group's lane summarizing its members' clips.
5. **Deferred by design:** MIDI 2.0/UMP, MPE, visual node-graph editing, feedback routing.
