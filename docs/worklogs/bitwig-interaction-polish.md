# Interaction polish milestone: worklog and decision record

This is the durable record of the `feature/bitwig-interaction-polish` milestone: arranger and clip looping, direct
timeline zoom and pan, drag-to-replace devices, floating plugin-editor behaviour, and a repository-wide documentation
and trademark pass. It records where the work started, the plan, every product and architecture decision worth
examining later, the independent reviews and the responses to them, and the final assessment. Screenshots are in
[../images/bitwig-interaction-polish/](../images/bitwig-interaction-polish/).

The workflow references (Bitwig Studio for looping, zooming and device chains; JetBrains Rider for density and
tool-window character; Studio One for visual refinement) describe qualities to aim for. Bluestone is an independent
product and copies no third-party code, artwork, or text.

## Working arrangement

- **Agent A, implementation owner**: the lead agent. Edits code, tests, implementation documentation, and
  screenshots; commits stable checkpoints on the feature branch.
- **Agent B, independent reviewer**: a separate, read-only context. Assesses product fidelity, DAW feel, interaction
  behaviour, visual quality, accessibility, real-time safety, plugin isolation, domain model, tests, cross-platform
  risk, and scope. Classifies findings as *Required now*, *Important follow-up*, *Optional refinement*, or *Rejected
  scope*. Agent A answers every finding here and does not grade its own work. Reviews: after reconnaissance and plan,
  after core domain/runtime changes, after the UI interactions work, and at completion.
- **Agent C, documentation and attribution auditor**: a third, separate context. A read-only inventory first; after
  the implementation is stable, edits documentation and narrowly scoped public text in its own commit.

## Starting state

- **Branch**: `feature/bitwig-interaction-polish`, created from `main` at `707e18e` ("Merge pull request #32 from
  djflan/feature/daw-first-polish").
- **Working tree**: clean (only ignored `bin/`, `obj/`, `target/`, `TestResults/`).
- **Toolchain**: .NET SDK 10.0.300, cargo 1.99.0, macOS on Apple silicon.
- **Relevant records**: ADR 0003 (deterministic event ordering), 0004 (musical time), 0006 (playback engine), 0009
  (project persistence), 0020 (hybrid tracks, clips, automation), 0022 (device chains), 0024 (device automation),
  0025/0033/0034 (out-of-process and third-party plugin hosting), 0027 (project format 4), 0032 (audio engine), 0036
  (semantic workspace and Device View), 0038 (DAW-first tracks and settings); `docs/architecture.md`,
  `docs/plugin-hosting.md`, `docs/project-format.md`; the previous worklog, [daw-first-polish.md](daw-first-polish.md).

### Baseline tests

| Suite | Command | Result |
| ----- | ------- | ------ |
| .NET | `dotnet test src/dotnet/Bluestone.slnx` | 1735 total, 1723 passed, 12 skipped, 0 failed |
| Rust | `cargo test` in `src/native` | 22 passed, 0 failed |

The 12 skips are platform-specific (WinMM, ALSA) or need an enabled IAC bus. The CoreMIDI failure recorded in the
previous milestone did not occur in this run.

### Baseline screenshots

`WorkspaceShowcaseTests` rendered alone (the previous worklog notes that full-suite frames can be stale):
[before-normal-chain.png](../images/bitwig-interaction-polish/before-normal-chain.png) (1440×900) and
[before-narrow-chain.png](../images/bitwig-interaction-polish/before-narrow-chain.png) (1100×720). They show the
arranger header's zoom slider between two magnifier buttons, the cycle strip at the top of the ruler, and the Device
View chain.

## Reconnaissance

### What exists

- **Arranger loop**: `Project.Loop` is a nullable `TickRange`; null means off. The transport button and **L** toggle
  between "no loop" and four bars from the playhead's bar, discarding the range. The ruler's cycle strip creates a
  range by dragging and deletes it on a click inside it (`RulerView`). Playback (`PlaybackController` →
  `PlaybackEngine.SetLoop`), audio (`PlaybackTimeline` → `AudioTransport`), the recorder's preview and cycle takes all
  read `Project.Loop`. The engine already truncates notes crossing the loop end, releases sounding notes at the wrap,
  and chases controllers, programs, and hardware parameters (`ChaseState.AtWrap`). A loop engages only while the
  playhead is before its end.
- **Clips**: `Clip` has `Start`, `Length`, `ContentOffset` (content ticks trimmed off the left), and `Origin`
  (`Start − ContentOffset`). `NoteClip.Arrange()` yields the visible window with notes cut at the clip's end;
  `Track.ArrangedEvents` caches the concatenation; `TrackRendering.Render` feeds both the signal graph (playback,
  plugins) and MIDI-file export. `AudioClip` plays its source from disk at the source's rate from `Origin`
  (`ProjectAudioGraph` builds an `AudioClipRegion` in song frames; `ClipSource` maps song frames linearly to source
  frames, preloads the clip start and the arranger-loop start, and streams the rest through a ring). Fades are
  measured from the clip's outer edges. No clip has loop state. There is no Consolidate command.
- **Zoom**: the arranger's zoom (`MainViewModel.Zoom`, pixels per quarter, 6–160) has a slider and magnifier
  buttons in the arranger header, ⌘/Ctrl + wheel, pinch, and plain wheel over the ruler (`MainWindow.axaml.cs`).
  The piano roll has its own `EditorViewModel.Zoom` and `ZoomAround`. The ruler seeks on press and scrubs on drag.
  The arranger scrolls through a `ScrollViewer`; the piano roll keeps its own scroll offset.
- **Device chains**: Browser drags insert at a gap; chain drags reorder (`ChainDrag`, `DeviceViewControl`). No
  replace command exists.
- **Plugin editors**: one native top-level window per plugin instance, created and owned by the plugin's worker
  process (`OpenEditor`/`CloseEditor`, `EditorClosed` notification). macOS: an `NSWindow` in an accessory app whose
  events the worker pumps itself; Windows: a top-level `HWND` with the worker's own message pump; Linux: an X11 window,
  with the plugin's child window usually on the plugin's own X connection. No hide/show, pin, follow, or keyboard
  routing exists.
- **Undo**: `EditHistory` snapshots immutable projects. Drags already commit once on release; a one-second
  `MergeKey` window merges slider-like edits.

### Large coordinators

`MainViewModel` (1591 lines) and `MainWindow.axaml.cs` (1172 lines), `TimelineView` (1050), and `PianoRollView`
(1168). New behaviour goes into focused types: a timeline-navigation model, a ruler gesture, a clip-loop model and
mapper, a device-replacement command, and a plugin-editor window coordinator.

### Agent B: baseline gap matrix (condensed)

Agent B's own classifications.

| Area | Gap | Class |
| ---- | --- | ----- |
| Arranger loop | Range and enablement inseparable; a click deletes the loop; no move, resize, snap inversion, feedback, nudge, or exact editing | Required now |
| Arranger loop | Keep the engine's note truncation, release, and chase at the wrap | Required now |
| Arranger loop | Format bump with migration; tests for disabled bounds in playback and recording | Required now |
| Arranger loop | Use release-time commands for drags, not the time-based merge window | Important follow-up |
| Clip loop | No loop state; define a validated loop value in content coordinates with a pickup | Required now |
| Clip loop | Repeat expansion needs hard bounds before plan construction; never expand on real-time threads | Required now |
| Clip loop | Repeats need note truncation and controller/program chase like `ChaseState` | Required now |
| Clip loop | Audio needs a piecewise frame mapping, a clip-loop-start preload, several repeats inside one block, fades at outer bounds | Required now |
| Clip loop | Specify split, copy, trim, consolidate; Consolidate does not exist | Required now |
| Clip loop | Inspector is single-selection and audio-only; add multi-select mixed state for both types | Required now |
| Zoom | Navigation split between `MainWindow` and `MainViewModel` and the piano roll; extract a shared model; seek needs a threshold | Required now |
| Zoom | Remove the slider; keep wheel, pinch, shortcuts; add zoom to selection and fit | Required now |
| Zoom | Deterministic tests of anchoring, limits, thresholds | Important follow-up |
| Replace | Drop targeting knows gaps only; add panel-body targets and an overlay | Required now |
| Replace | No atomic command; remove + insert would delete taps, and reusing the ID would misbind automation | Required now |
| Replace | Plugin insertion commits before the worker starts; a failure must leave the original or be one undo away | Required now |
| Editors | Keep the process boundary; add a coordinator outside `MainViewModel`; define hide as native visibility | Required now |
| Editors | Keyboard routing needs native interception and IPC per OS; do not claim parity where unreliable | Required now |
| Editors | Persisted editor geometry | Optional refinement |
| Editors | Embedding editors in Avalonia | Rejected scope |

Top risks: coordinators growing; expansion, I/O, IPC, or window work on real-time threads; identity reuse on replace;
deleting loop bounds when disabling; modulo-only audio mapping that misses several repeats in one block.

### Agent C: initial terminology inventory (summary)

Read-only. Third-party names found: Yamaha (with XG, QY100), Roland (GS), Steinberg/VST/VST3 ("VST 3" in three
places), CLAP, Apple/macOS/CoreMIDI/CoreAudio, Microsoft/Windows/WinMM/WASAPI, Linux/ALSA/JACK, the MIDI Association,
GitHub, Avalonia, .NET, Rust, Inter (font package), "Bitwig-style" and "as in Bitwig" (ADR 0034, roadmap), "Rider-like"
(previous worklog), "Reaper's" (ADR 0035), Airwindows Consolidated and Surge XT (local test subjects, not vendored).
Existing notices: a Yamaha non-affiliation notice in the README (repeated in the About window), an XG notice in the
generic XG profile, and a short one in the hardware test plan. No TRADEMARKS file, no README trademark section, no
third-party logos, icons, vendor screenshots, or font files. No endorsement, clone, or port wording found. Edits are
deferred until the feature terminology is stable.

## Milestone plan

### Order and checkpoints

1. **Arranger loop model** (domain, format 5, playback/recording on the enabled loop only) and **ruler gestures**
   (create, move, resize, snap with Shift inversion, one undo, feedback, no click deletion), Loop Selected Region,
   exact start/length, nudging. *Checkpoint 1.*
2. **Clip loop model** (domain, rendering with bounded expansion and chase, audio mapping and preload, editing
   semantics, Consolidate, serialization). *Checkpoint 2* → Agent B review of core domain and runtime.
3. **Clip loop UI** (lower-right loop handle, repeat markers, inspector with mixed state).
4. **Timeline navigation** (shared model and ruler gesture in arranger and piano roll; slider removed; fit and
   zoom-to-selection commands).
5. **Device replacement** (command, drop targeting, overlay).
6. **Plugin editor windows** (protocol and native show/hide and key forwarding, coordinator, settings, affordances).
   *Checkpoint 3* → Agent B review of UI interactions.
7. **Screenshots** of every representative state, inspected.
8. **Agent C documentation and trademark pass** (separate commit), Agent B review of the public wording.
9. **Final Agent B assessment**, responses, final test run, final assessment here.

## Decisions

Decisions are numbered for reference from reviews and commits.

- **D1 Arranger loop model.** `Project.Loop` is an `ArrangerLoop`: an optional `TickRange` and `IsEnabled`; an
  enabled loop must have a range. `ArrangerLoop.Active` (the range while enabled) is the only thing playback, the
  audio transport, cycle recording, the recording preview, and the content shading read. Disabled bounds therefore
  cannot affect playback. Project format 5; `Format4To5Migration` turns a format 4 `loop` (which existed only while
  looping was on) into an enabled loop. *Alternative rejected*: two nullable properties on `Project` (an enabled
  flag without a range would be representable).
- **D2 Turning looping on.** The transport button, **L**, and a double-click on the loop toggle `IsEnabled` and keep
  the range. With no range yet, turning on loops four bars from the bar containing the playhead (the previous
  behaviour), in the meter there (`LoopCommands.DefaultRange`).
- **D3 Start beyond the loop.** Unchanged and documented: a loop engages only while the playhead is before its end,
  so playback that starts after the loop plays on instead of jumping back (`ArrangerLoop` remarks, ADR 0039).
- **D4 Loop-strip gestures.** A pure `LoopStripDrag` (Presentation) decides what a press grabbed (empty space, body,
  either edge: 5 px grips, a narrow loop keeps a middle third for its body) and the range at each pointer position;
  `RulerView` converts pixels and commits once on release, so a drag is one undo step (no time-based merging). Drawing
  in empty space turns looping on; moving and resizing keep the loop's state. A 3 px threshold separates a click from
  a drag; a click selects the loop (or clears the selection) and never deletes it. Snapping is the arranger's own
  adaptive grid (beats when at least 24 px wide, else bars), now shared by clips and the ruler
  (`TimeGrid.SnapAdaptive`); ⌘/Ctrl ignores it while dragging, the convention the piano roll already documents
  ("⌘-drag ignores the grid"). Cursors: resize at edges, hand on the body, crosshair on empty strip space. While
  dragging, the start and length are read out over the bar numbers.
- **D5 Loop commands and precision.** `LoopCommands` (Application) holds every loop edit; `ArrangerLoopViewModel`
  (Presentation) is the transport/ruler-facing model, so `MainViewModel` only constructs it. **Loop Selected Region**
  (⌘/Ctrl + L, Transport menu) loops the selected clips' span, or the piano roll's selected notes when no clip is
  selected. Exact start (bar.beat.tick) and length (bars.beats.ticks, or ticks with a `t` suffix) are typed in two
  fields in the transport display, dimmed while looping is off; invalid text reverts. With the loop selected in the
  ruler (it takes focus on click), ← → nudge it by the adaptive grid step and ⇧ ← → by a bar; Esc deselects.
  *Alternative rejected*: an inspector section; the inspector is about the selected track, and the transport already
  holds the comparable tempo and signature fields.
- **D6 Clip loop model.** `Clip.Loop` is an optional `ClipLoop` (content-tick start, length, enabled) on note and
  audio clips; null means "never looped", so a loop turned off keeps its region. `ContentOffset` stays absolute and is
  never wrapped (Review 1 #1, Review 2 #4); `Clip.ContentAt` maps clip time to content (pickup from `ContentOffset`,
  then wrapping into the loop), and `Clip.Passes` enumerates passes without allocating. Defaults on first enabling: the
  span the clip plays now (`[ContentOffset, + Length)`, an audio loop stopping at its file's end), so turning looping on
  changes nothing audible until the clip is lengthened. *Rejected*: loop bounds relative to `ContentOffset` (moving
  the left edge would drag the loop); destructive duplication into clips.
- **D7 Note-clip playback.** Repeats are produced by `NoteClip.Arrange()` at plan time, so playback, plugin rendering,
  and MIDI export agree. Notes are cut at the end of their pass (a note starting in the loop retriggers each pass; one
  crossing the loop's end stops there: the convention in DAWs, and no note can hang). At each repetition
  `ClipLoopChase` restores the loop start's controller state by the engine's own wrap rule (`ChaseState.AtWrap`),
  including bank-with-program restoration (Review 2 #3). Repeats keep their source event's ID. Track automation is
  separate and never loops.
- **D8 Bounded expansion.** At most 262,144 arranged events per clip and 1,048,576 from looping clips across the
  sequence (Review 2 #2), counted arithmetically when tracks and sequences are built; an edit beyond either is refused
  with a message (`ClipLoopLimitException` → `CommandRefusedException`), and such a file is refused when read. A loop
  whose region is empty is never walked pass by pass (Review 2 #1). Marker drawing finds visible repeats arithmetically.
  *Rejected*: lazy expansion inside the engine (it would duplicate the chase/ordering machinery on the real-time thread).
- **D9 Audio-clip playback.** No time-stretching exists, so an audio loop is a fixed stretch of the file in whole source
  frames, measured where the clip's content starts (ADR 0039). `ClipSource` resamples over the played (virtual) stream:
  interpolation across a wrap reads the loop's start; any number of wraps fit in one block; output depends only on song
  position. The loop start is preloaded (or found in the clip-start preload) and the disk ring is sent ahead to where
  that preload ends once per pass. Fades stay at the clip's outer edges; no crossfade is added at wraps.
- **D10 Editing semantics.** Move and copy keep loop and phase. Trim never wraps the offset or drops content. Content
  rebased by an edit moves the loop with it. Split of a clip with a loop keeps all content in both parts (right part with
  new event IDs and the unwrapped offset), so both sound as before. Consolidate: note clips write out their arranged
  events (chase values included) without a loop; audio clips get a new 32-bit float WAVE file of the played frames at
  the file's rate (`AudioConsolidation`), refused if the clip changed while writing or if the file would exceed 4 GiB,
  the unused file then deleted (Review 2 #5, #6).
- **D11 Clip-loop UI.** The upper half of a clip's right edge keeps changing its length; the lower half is the loop
  handle (link cursor, a small corner tab always visible on looping clips and faint on others, a tooltip), which turns
  looping on and extends or shortens with repeats. The first repeat boundary is a dashed line that can be dragged to
  change the loop's length; later ones are lighter dotted lines, thinned to at least 6 px apart. The inspector's
  **Loop** section (shown for any clip selection) has a two-state *Loop the clip* box (unset when the clips differ),
  exact start (from the content's start) and length fields with *Mixed* placeholders, *Consolidate*, and a one-line
  summary ("Plays a pickup, then 7 passes."). Every change, over any number of clips, is one undo step.

## Reviews and responses

### Review 1: reconnaissance and plan (Agent B)

Agent B reviewed the proposed decisions before implementation. Findings, Agent B's classes, and responses:

| # | Finding | Class | Response |
| - | ------- | ----- | -------- |
| 1 | Do not wrap the stored `ContentOffset` into the loop on trims; keep it absolute and apply the modulo only when mapping | Required now | Accepted. `ContentOffset` stays the absolute trimmed position; only the content mapping wraps (D7). |
| 2 | Silently stopping repetition at an event budget makes music disappear | Required now | Accepted. Edits that would exceed the budget are refused with a message; the budget is enforced where tracks are built, so every path (resize, loop handle, content edit, file load) is covered (D8). |
| 3 | Notes starting inside the loop retrigger on every pass; only notes crossing a pass boundary are cut. Define repeat identity | Required now | Accepted and specified (D8). Repeats keep the ID of the content event they come from: an arranged event names its source event, and the track-wide uniqueness invariant is about content, which repeats do not change. |
| 4 | Chase at repeats should follow `ChaseState` semantics, not ad-hoc events; decide the export policy | Required now | Accepted (D8): the repeat chase uses an explicit policy mirroring the engine's chase, and export includes it, because playback and export must agree on what plays. |
| 5 | Resampling must not read source frames past the loop end at the boundary | Required now | Accepted (D9): interpolation runs over the played ("virtual") stream, whose frames after the loop end are the loop start's. |
| 6 | State the audio-loop rule under tempo changes | Required now | Accepted (D9): audio is not time-stretched, so an audio clip loop is a fixed length of source audio, measured at the clip's position. |
| 7 | Multi-wrap rendering must be allocation-free | Required now | Accepted (D9); covered by an allocation test. |
| 8 | Splitting a looped clip must not duplicate event IDs on the track | Required now | Accepted (D10): the right half gets the content with new IDs. |
| 9 | One clip per pass is "Unroll", not consolidation | Required now | Accepted in part (D10): audio Consolidate writes one new file of the audible span (source frames through the loop mapping, no resampling); note Consolidate materializes the events. |
| 10 | A plugin replacement that fails must leave the original intact | Required now | Accepted (D13): the replacement plugin is started in its worker first; the project changes only when it runs. |
| 11 | Do not delete automation of the replaced device | Required now | Accepted (D13): lanes stay, targeting the old device, as removal already leaves them; retargeted only for the same plugin or built-in. |
| 12 | Key forwarding needs releases on mode change, hide, focus loss, worker loss, close; bounded protocol; serialized visibility requests | Required now | Accepted (D15). |
| 13 | Simultaneous free pan and zoom is imprecise; prefer dominant-axis locking | Important follow-up | Partly accepted (D11): both axes stay live (the established DAW ruler gesture), but each has its own dead zone, so a vertical zoom does not drift sideways and a pan does not zoom. |
| 14 | Decide between a transport popup and the inspector for exact loop values | Important follow-up | Decided: transport display fields (D5). |
| 15 | Monitor-loss recentring adds scope | Optional refinement | Done where cheap and reliable (macOS, Windows) when an editor is shown (D15); Linux documented. |
| 16 | No Wayland key interception or editor embedding | Rejected scope | Agreed. |

### Review 2: core domain and runtime (Agent B)

After commits `404f59f` and `97ae6cd`. Fixed in the clip-loop UI commit unless noted.

| # | Finding | Class | Response |
| - | ------- | ----- | -------- |
| 1 | An empty one-tick loop over a huge clip passes the budget but walks billions of passes | Required now | Fixed: an empty loop region yields only the first pass; drawing computes visible repeats arithmetically; test with a `long.MaxValue / 4` clip |
| 2 | The per-clip limit does not bound the whole plan | Required now | Fixed: a sequence-wide limit of 1,048,576 looped events, cached per track (`Track.LoopedEventCount`); tested |
| 3 | Clip chase misses bank select changed inside the loop | Required now | Fixed: bank MSB/LSB tracked with the program and restored together, as `ChaseState.AtWrap` does; tested |
| 4 | Split stored the wrapped phase as the right part's offset | Required now | Fixed: the unwrapped offset is stored; `ContentAt` maps it; test updated to assert both |
| 5 | Audio consolidation could overwrite edits made while writing | Required now | Fixed: the edit is refused unless the clip is unchanged, and the unused file is deleted; tested |
| 6 | Large consolidation wrote an invalid WAVE file | Required now | Fixed: refused over 4 GiB before writing, the writer throws rather than lie in its header, partial output is deleted |
| 7 | Checked arithmetic for loop bounds and pass counts | Important follow-up | Fixed: `ClipLoop` refuses bounds past `long.MaxValue / 2`; pass count without overflow |
| 8 | More `ClipSource` edge tests | Important follow-up | Added: loop beyond the file's end; an arranger wrap with a one-frame clip loop. Slow-disk across clip wraps and graph rebuilds while playing remain covered only by the existing generic tests (deferred) |
| 9 | Broader serialization tests | Important follow-up | Partly: disabled loops round-trip; zero length refused; over-limit loops refused through the shared domain check. Path-specific assertions per case deferred |

## Deviations and discoveries

- **Audio transport, pre-existing**: the audio transport wraps an arranger loop at most once per block, so an arranger
  loop shorter than one audio block (a few milliseconds) plays incorrectly in audio; the MIDI engine wraps any number
  of times per pump. Not changed by this milestone (clip loops wrap any number of times per block). Recorded as
  deferred work.
- **Audio clips have no import UI** (they come from project files), so audio clip loops are reachable today only on
  projects that already contain audio clips.
- **Tick resolution**: new projects use 960 PPQN; some older tests assume 480. New tests derive bar lengths from the
  project.

## Deferred work

(Recorded as it is identified.)
