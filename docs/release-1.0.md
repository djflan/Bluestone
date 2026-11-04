# Release 1.0 delivery plan

This document guides implementation from the current alpha to Bluestone 1.0. It is written for maintainers and coding agents. It turns the [product vision](product-vision.md) and [product roadmap](roadmap.md) into ordered, reviewable vertical slices.

It is not a promise that every detail is already designed. Where a slice identifies a decision gate, resolve it with evidence and an ADR before building dependent behavior.

## Release objective

A musician can add a hardware or software instrument, record MIDI and audio, edit the performance, monitor it with correct latency, mix it, and export the finished result without constructing a signal graph or leaving Bluestone.

Release 1.0 is ready when this workflow is dependable, understandable, recoverable, and verified on every platform Bluestone claims to support.

## Read before starting a slice

Every agent must read:

1. [Product vision](product-vision.md)
2. [Product roadmap](roadmap.md)
3. [Engineering handoff](engineering-handoff.md), especially the decisions not to re-decide and regression net
4. [Architecture](architecture.md)
5. [Project format](project-format.md)
6. [Plugin hosting](plugin-hosting.md) when a slice touches devices, plugins, audio, or process lifetime
7. The ADRs named by the slice

Historical worklogs are evidence, not current product direction. Do not revive a deferred design merely because a worklog mentions it.

## Rules for executing the plan

### Work vertically

Each slice must deliver a musician-visible path through domain, persistence, application, presentation, desktop UI, diagnostics, and tests. Avoid landing a broad unused framework in anticipation of later slices.

### Preserve trustworthy behavior

- Never silently truncate, drop, quantize, reroute, or replace musical data.
- Playback, monitoring, recording placement, and export must use the same timing and signal rules.
- Real-time paths must have explicit allocation, blocking, and failure behavior.
- Automatic defaults must become inspectable, reversible state.
- Missing hardware, plugins, files, and endpoints must preserve project intent.
- Project-format changes require migration tests and frozen equivalence evidence where applicable.

### Make quality part of the slice

A slice is not complete until it has:

- unit coverage for domain rules and edge cases;
- integration coverage for files, processes, platform services, and timing boundaries;
- headless UI coverage for the primary workflow;
- visual review in the running app at the supported compact and normal sizes;
- accessibility and keyboard review;
- cancellation, failure, and recovery coverage; and
- updated documentation that distinguishes implemented, verified, and deferred behavior.

Hardware-dependent tests must skip with a reason when the device is absent. A simulated test is not evidence that physical timing, clock, CV, or round-trip audio works.

### Keep the product coherent

Use the shared tokens, controls, terminology, command model, undo history, diagnostics, and workspace hosting patterns. Do not add a one-off window, panel style, routing concept, or file picker when an established product pattern can be extended.

### Stop at decision gates

When a slice names an ADR, do not make the consequential implementation choice implicitly. Record the context, alternatives, decision, compatibility effect, and verification plan first. Amend earlier ADRs rather than rewriting their history.

## Slice map

| Slice | Musician-visible outcome | Depends on |
| ----- | ------------------------ | ---------- |
| 0. Release contract | Supported paths and measurable quality bars are explicit | — |
| 1. Production track | A normal track has conventional mixer and I/O behavior | 0 |
| 2. Audio in and monitoring | A musician can select, hear, and meter an input safely | 1 |
| 3. Audio recording | A musician can record an accurately placed, recoverable audio take | 2 |
| 4. Audio import and editing | Recorded and imported audio can be arranged with consistent gestures | 3 |
| 5. Takes and comping | Repeated performances can be reviewed and combined without destructive edits | 3, 4 |
| 6. Mix and stem export | The project can produce a deterministic mix and stems | 1–5 |
| 7. Hardware instrument device | Hardware is added and played like a software instrument | 1–3 |
| 8. Hardware timing and effects | Round trips, clock, and external effects are measured and compensated | 7 |
| 9. CV foundation | Compatible interfaces can control and return Eurorack signals safely | 8 |
| 10. Editing and automation pass | Arrangement, piano roll, audio, and automation feel like one editor | 4–8 |
| 11. Native device contract | A small useful device set proves the first-party platform | 1–10 as relevant |
| 12. Release hardening | A supported musician can finish and recover a production confidently | all |

Slices are ordered by dependency, not by calendar. Slices 7 and 10 may proceed alongside 4–6 only when they do not modify the same project, routing, or UI contracts. Do not parallelize changes to the project format, track/mixer ownership, audio graph, or Device View without an explicit integration owner.

### Progress tracking

Update this table only when the named slice outcome and acceptance scenarios have evidence. “In progress” must link
to a branch, pull request, or worklog; “Complete” must link to the final handoff and verification record.

| Slice | Status | Worklog / pull request | Evidence or blocker |
| ----- | ------ | ---------------------- | ------------------- |
| 0. Release contract | Not started | — | — |
| 1. Production track | Not started | — | — |
| 2. Audio in and monitoring | Not started | — | — |
| 3. Audio recording | Not started | — | — |
| 4. Audio import and editing | Not started | — | — |
| 5. Takes and comping | Not started | — | — |
| 6. Mix and stem export | Not started | — | — |
| 7. Hardware instrument device | Not started | — | — |
| 8. Hardware timing and effects | Not started | — | — |
| 9. CV foundation | Not started | — | — |
| 10. Editing and automation pass | Not started | — | — |
| 11. Native device contract | Not started | — | — |
| 12. Release hardening | Not started | — | — |

#### Groundwork already in place

Some work that predates this plan feeds the slices below. None of it meets a slice's acceptance scenarios, so the statuses above are unchanged. Start from this work rather than rebuilding it.

- **Slice 0:** [ADR 0043](adr/0043-timing-targets-and-measurement.md) sets timing targets for MIDI, live input, and the audio stream, with macOS measurements. [ADR 0044](adr/0044-engine-process.md) and [ADR 0047](adr/0047-windows-precise-wait-and-measurements.md) record engine and Windows software-loopback results. The probes are in [Bluestone.Benchmarks](../src/dotnet/Bluestone.Benchmarks/). The [README](../README.md#platform-status) platform table is the starting point for the support matrix. There is no acceptance project, visual-review matrix, or project-format policy yet.
- **Slice 1:** Track roles, mixer channels, sends, groups, and default master routing exist ([ADRs 0021](adr/0021-track-roles.md), [0023](adr/0023-signal-routing-model.md), [0030](adr/0030-group-tracks.md), [0038](adr/0038-daw-first-tracks-and-settings.md)); the slice's ownership ADR consolidates and amends them rather than starting from nothing.
- **Slice 2:** The native audio crate can capture from an input device, but only the loopback probes and device tests use it. Nothing in the application selects or monitors an audio input.
- **Slice 3:** MIDI recording, punch, and loop takes exist ([ADR 0017](adr/0017-recording-and-note-editing.md)). Audio recording does not.
- **Slice 4:** WAVE and AIFF clips play, loop, stream, and take gain and fades ([ADR 0032](adr/0032-audio-engine.md)). There is no import workflow.
- **Slice 6:** An offline audio driver renders the graph in virtual time without a device, through the same path as the device callback, for tests and measurement. There is no user-facing mix or stem export.
- **Slice 7:** Hardware profiles, rebinding, SysEx, and hardware-parameter automation exist ([ADRs 0007](adr/0007-device-profile-format.md), [0008](adr/0008-routing-and-rebinding.md), [0031](adr/0031-hardware-parameter-automation.md)). The unified device and the audio return do not.
- **Slice 8:** Plugin delay compensation and live-input latency handling exist ([ADR 0035](adr/0035-live-input-and-delay-compensation.md)). Hardware round-trip measurement, clock, and external effects do not.
- **Slice 10:** The arranger, clip loops, piano roll, event list, and automation lanes exist ([ADR 0039](adr/0039-arranger-and-clip-loops.md)); the cross-editor consistency pass has not been done.

## Slice 0: release contract and baselines

### Outcome

Contributors know exactly what 1.0 supports and how timing, latency, reliability, and visual quality will be judged.

### Deliver

- A support matrix for macOS, Windows, and Linux covering audio input/output, MIDI input/output, plugin formats and editors, hardware timing, and release packaging. Mark each path **supported**, **experimental**, or **not in 1.0**.
- Measured baseline reports for currently implemented playback jitter, audio callback stability, live-input latency, and plugin-delay-compensation paths. Record an unavailable path as unsupported or not yet implemented instead of adding production behavior in this slice.
- Measurement protocols and proposed tolerances for recording placement and offline export equivalence. State the fixture, device, buffer, rate, run length, load, sample count, percentile, and expected report format; Slices 3 and 6 respectively must supply the first executable evidence before they can complete.
- Approved tolerances for each currently measurable path. Do not reduce timing to one unexplained average. Approve the recording-placement and export tolerances no later than the decision gate of their owning slice. [ADR 0043](adr/0043-timing-targets-and-measurement.md) sets the starting targets for MIDI, live input, and the audio stream, and the probes that judge them.
- A canonical 1.0 acceptance-project specification covering MIDI, a plugin instrument, a hardware-device substitute, recorded audio, automation, buses, and export. Commit a seed project containing the currently supported elements; each owning slice extends it when its workflow becomes executable.
- A manual visual-review matrix for window sizes, scaling, focus, keyboard use, and detached views.
- A project-format policy for the 1.0 development series: next version number, migration ownership, and fixture rules.

### Decision gates

- Which platform paths are supported at 1.0 rather than merely compiled?
- Which timing tolerances are release-blocking?
- Which audio file formats are required for import and export?

### Acceptance

- Baseline commands for implemented paths are reproducible from a clean checkout.
- Results identify the machine and configuration and can be compared across runs.
- Deferred measurement protocols name their owning slice, fixture, inputs, expected report, and release tolerance; their absence is not reported as measured evidence.
- CI covers every hardware-independent gate; the manual matrix names an owner and evidence for the rest.
- No later slice uses an undefined phrase such as “sample accurate,” “low latency,” or “professional timing.”

### Non-goals

No new production feature, and no fabricated baseline for an unimplemented workflow, belongs in this slice.

## Slice 1: the production track

### Outcome

A musician creates a track and gets conventional, immediately legible input, output, gain, pan, mute, solo, metering, sends, and master routing. Advanced connections remain available but are not required.

### Deliver

- Track-owned channel behavior, or another single ownership model that makes ordinary mixer state follow track creation, duplication, grouping, deletion, undo, save, and migration.
- Clear instrument, audio, effect/return, group, and master behaviors without destructive role conversion.
- Default routing to the appropriate group or master as inspectable project state.
- The same I/O and channel state in arranger headers, inspector, and mixer; each surface edits one model.
- Stable metering and naming when devices or endpoints are missing.

### Decision gate

Write an ADR for track/channel ownership and its relationship to `SignalConnection`, `MixerChannel`, track roles, groups, and existing format-5 projects. This amends the current model in ADRs 0021, 0023, 0030, and 0038 where necessary.

### Acceptance scenarios

1. New track → insert plugin instrument → play: it is heard at the master without routing work.
2. Duplicate, group, ungroup, and delete the track: channel state and routes remain correct and undo restores them.
3. Add sends and change the primary output from either mixer or inspector: both views agree.
4. Open an older project: it sounds the same before any edit and acquires the new ownership model deterministically.
5. A missing plugin or endpoint changes status, not mixer ownership or saved musical intent.

### Non-goals

No visual node graph, feedback routing, or arbitrary mixer modularity.

## Slice 2: audio input and monitoring

### Outcome

A musician selects an interface input, arms a track, hears it through Bluestone when desired, sees trustworthy meters, and cannot accidentally create a feedback path without a clear warning.

### Deliver

- Audio input enumeration, stable identities, defaults, loss/recovery, and channel selection.
- Input choice on audio-capable tracks with mono/stereo semantics defined explicitly.
- Software monitoring with an obvious state and a path through the same device and mixer graph used by playback.
- Input and post-chain metering.
- A latency display that distinguishes device-reported, configured, measured, and render-ahead contributions.
- Feedback-risk validation and safe behavior when input and output topology is suspicious.

### Decision gates

- Extend the native audio ABI for duplex streams or adopt a separate input stream; record the callback, clock, drift, and device-loss model in an ADR.
- Define whether Bluestone 1.0 supports one aggregate input/output device, platform aggregate devices, or only a shared duplex device.

### Acceptance scenarios

1. Select input → arm → monitor → add an audio effect: live audio follows the visible chain and master route.
2. Change buffer size or sample rate: the stream reopens without stranded audio, plugins restore state, and the applied latency is updated.
3. Unplug the input device: monitoring stops safely, the saved binding remains, and reconnect restores it when unambiguous.
4. Disable monitoring: no input samples enter the audible graph.
5. The offline test backend proves block boundaries, channel mapping, drift policy, and discontinuity handling deterministically.

### Non-goals

Recording files and comping wait for later slices. Direct hardware monitoring may be described but is not controlled unless a supported interface API is deliberately added.

## Slice 3: accurate, recoverable audio recording

### Outcome

A musician records an audio performance whose clip lands where it was heard and whose file survives interruption or failure.

### Deliver

- Record-arm and input-monitor behavior integrated with the existing transport, count-in, punch, loop, and metronome.
- Timestamped input capture mapped onto the musical timeline using the measured latency model.
- Streaming recording to a temporary file with atomic finalization, unique naming, and recovery after cancellation, device loss, full disk, or application restart.
- A recorded audio clip added as one undoable project edit without placing audio data itself in undo memory.
- Clear handling of pre-roll, negative placement, punch boundaries, sample-rate changes, and transport loops.
- Recording diagnostics that state the applied placement correction.

### Decision gates

- Choose the recording file format, bit depth, naming/location policy, and ownership lifecycle.
- Record the monitoring and placement equation in an ADR, including which latency components are measured, reported, configured, or unknowable.
- Define loop-recording take storage so Slice 5 can add comping without migrating throwaway data.

### Acceptance scenarios

1. A test pulse sent through a measured loopback records within the approved placement tolerance at each supported buffer size.
2. Punch and count-in do not write unintended samples into the final take.
3. Stopping, cancelling, losing the input device, and simulating a write failure leave either a valid recoverable take or no project reference—never a corrupt referenced file.
4. Undo removes the clip but follows the documented nondestructive media-retention policy; redo restores it.
5. Save, close, move the project, and reopen: media resolution is deterministic and missing media is reported without project loss.

### Non-goals

Comping UI, destructive waveform editing, pitch correction, and time stretching.

## Slice 4: audio import and essential editing

### Outcome

Recorded and imported audio behaves like first-class arrangement material.

### Deliver

- Import through the Browser, file picker, and drag-and-drop with one shared validation path.
- Required uncompressed and compressed formats from Slice 0, decoded behind one abstraction.
- Waveform overview generation and caching that never blocks the audio thread.
- Consistent selection, move, copy, split, trim, loop, fade, gain, rename, consolidate, and delete behavior.
- Snap and modifier semantics aligned with note clips wherever the media allows it.
- Missing-file relink and project media collection.

### Acceptance scenarios

1. Import a supported file at a non-device sample rate and play it at the correct speed without modifying the source.
2. Apply every nondestructive edit, save, reopen, and consolidate: playback and the consolidated result agree.
3. Move or rename source media, relink it, and verify that project intent and waveform cache recover.
4. A long file streams without UI stalls or unbounded memory growth.
5. Unsupported, corrupt, or partially decoded files fail with an actionable message and no partial project edit.

### Non-goals

Elastic audio, pitch correction, spectral editing, and destructive sample editors.

## Slice 5: takes and comping

### Outcome

A musician records several performances, auditions them, and builds a nondestructive composite take without leaving the arrangement.

### Deliver

- A take/lane model shared by linear, punch, and cycle recording.
- Clear rules for active takes, muted material, grouping, trimming, and project persistence.
- Audition and comp gestures that follow the rest of Bluestone's editing language.
- Automatic crossfades at comp boundaries with editable, deterministic behavior.
- Flatten/consolidate as an explicit operation; originals remain until the musician chooses the documented cleanup action.

### Decision gate

Write an ADR for take ownership, lane presentation, comp segments, editing semantics, and project-format migration before building UI.

### Acceptance scenarios

1. Cycle-record several passes and build a comp by selecting ranges from different takes.
2. Move, split, trim, undo, save, and reopen the comp without changing which samples play.
3. Crossfade output is independent of audio block size.
4. Missing media identifies the affected take and leaves the remaining comp editable.
5. Consolidation produces the audible result and does not delete source takes implicitly.

### Non-goals

Playlist systems, vocal pitch correction, and automatic performance ranking.

## Slice 6: mix and stem export

### Outcome

A musician exports the mix they heard and can render useful stems for collaboration or mastering.

### Deliver

- Offline rendering through the same graph semantics as real-time playback.
- Full-mix, selected-track, group, bus, and stem export with explicit tail handling.
- Sample rate, bit depth, channel layout, normalization, and dither choices appropriate to the formats approved in Slice 0.
- Deterministic progress, cancellation, temporary-file cleanup, and atomic destination replacement.
- A report for unavailable plugins, hardware-only paths, external effects, unsupported automation, clipping, and other conditions that prevent or qualify an export.
- A deliberate policy for real-time export when a path depends on external hardware.

### Decision gates

- Define stem boundaries, pre/post-fader meaning, send/return inclusion, master processing, and naming.
- Decide which hardware paths require real-time capture and how Bluestone measures and aligns them.

### Acceptance scenarios

1. Offline mix output matches a captured real-time reference within the approved numerical tolerance for an all-software project.
2. Stems sum to the mix under the documented stem mode.
3. Plugin latency, automation, tempo changes, clip loops, fades, and effect tails appear at the correct samples.
4. Cancel and simulated write failure leave no corrupt final file.
5. A hardware-dependent project refuses offline export or offers real-time capture with a precise explanation.

### Non-goals

Mastering analysis, codec metadata editors, distribution upload, and batch mastering.

## Slice 7: unified hardware instrument device

### Outcome

A musician drags a hardware instrument into a track as naturally as a plugin and can play, automate, record, and recall it without constructing connections manually.

### Deliver

- A Browser item and Device View panel representing an external instrument as one user-facing device.
- MIDI output, channel or zone behavior, optional audio return, profile selection, named parameters, initialization, program selection, and status in that device.
- Automatic ordinary routing expressed through the existing connection model underneath.
- Rebinding when MIDI or audio endpoints are renamed or missing.
- Device presets or templates that capture reusable studio configuration without embedding machine-specific endpoint identities unintentionally.
- Compatibility with note effects before the hardware device and audio effects after its return.

### Decision gate

Write an ADR that amends ADR 0023's rejection of an external-instrument device. Prefer one source of truth: the user-facing device may project onto `ExternalInstrument` and `SignalConnection` entities rather than creating a second independent routing model. Specify identity, ownership, multi-timbral sharing, undo, persistence, and migration.

### Acceptance scenarios

1. Browser → hardware instrument → play: MIDI leaves the chosen port and returned audio reaches the master with no manual connection editing.
2. Put an arpeggiator before it and an audio effect after it: both operate in the expected domains.
3. Save with the hardware disconnected, reopen elsewhere, and bind replacement endpoints without losing the profile or automation.
4. Two tracks address separate parts of one multi-timbral instrument without duplicating the physical device or corrupting channel state.
5. Hardware parameter automation, initialization, panic, mute, solo, and export/capture behavior are explicit and tested.

### Non-goals

Specialized XG sequencing, vendor librarians, firmware management, and a profile for every instrument.

## Slice 8: hardware timing, synchronization, and external effects

### Outcome

External instruments and effects stay aligned with the project, and Bluestone shows what correction it applied.

### Deliver

- Round-trip measurement for hardware devices with manual fallback and saved per-path correction.
- MIDI clock send and receive policy, start/stop/continue behavior, song-position behavior where supported, and drift diagnostics.
- An external-effects device with audio send/return, gain staging, feedback protection, and latency compensation.
- Re-measurement and invalidation rules when device, rate, buffer, or routing changes.
- A timing diagnostics surface usable by musicians and detailed enough for bug reports.

### Decision gates

- Choose the synchronization modes Bluestone supports for 1.0 and name the clock authority in each.
- Define whether MIDI clock input is a release requirement or experimental path.

### Acceptance scenarios

1. Loopback measurement converges within the approved tolerance and stores the relevant configuration identity.
2. A hardware instrument recorded through its return aligns with the source notes after correction.
3. An external effect on a parallel path remains phase-aligned within the documented tolerance.
4. Device or rate changes invalidate stale measurements visibly.
5. Clock start, stop, tempo change, and long-run drift are measured on named physical hardware.

### Non-goals

Network ensemble synchronization and support for every vendor-specific clock protocol.

## Slice 9: CV and Eurorack foundation

### Outcome

A musician with a compatible DC-coupled interface can send calibrated pitch, gate, clock, and control signals and receive the resulting audio safely.

### Deliver

- Explicit compatibility and safety guidance for DC-coupled outputs and voltage ranges.
- CV instrument, clock, and control-output devices with channel selection and visible calibration state.
- Pitch calibration with saved curves and clear failure behavior.
- Audio return and latency correction shared with the hardware device foundation.
- Protection against accidental use of ordinary speaker/headphone paths for DC output.

### Decision gates

- Define supported voltage ranges, calibration model, interface allow/deny behavior, and liability-facing warnings with real hardware evidence.
- Decide whether generic CV input modulation is in 1.0 or deferred to the later modulation system.

### Acceptance scenarios

1. Calibrate a supported oscillator across the declared range and reproduce the curve after reopening the project.
2. Gate and clock pulses have measured width and placement at each supported rate and block size.
3. Unsafe or unknown output selection requires an explicit informed action and never defaults to a monitor path.
4. Device loss silences control output and preserves configuration for recovery.

### Non-goals

A complete modular modulation environment, polyphonic CV beyond the declared scope, and automatic support claims for untested interfaces.

## Slice 10: arrangement, piano roll, and automation consistency

### Outcome

Editing feels like one coherent application across MIDI, audio, automation, takes, and the future launcher model.

### Deliver

- A documented gesture and shortcut matrix for selection, focus, tools, snapping, modifiers, nudging, duplication, deletion, zoom, and context actions.
- Consistent implementation across arranger, piano roll, audio clips, automation lanes, and comp lanes.
- Device, mixer, send, hardware-parameter, tempo, and meter automation drawing with clear value units and interpolation.
- Navigation and filtering suitable for larger projects.
- Data-model seams that allow a future launcher to reference or transfer the same clip content without building launcher UI now.

### Acceptance scenarios

1. The same core gesture has the same meaning in each editor unless the documented media constraint requires otherwise.
2. Focus never sends a destructive command to the wrong selection domain.
3. Automation playback, display, editing, save, and export agree at boundaries and loop wraps.
4. Keyboard-only completion of the primary edit workflows is covered by UI tests.
5. Visual review finds no one-off control styling or unexplained terminology across the edited surfaces.

### Non-goals

Clip/scene launcher UI, notation, spectral editing, and full video-post workflows.

## Slice 11: small, complete native-device platform

### Outcome

Bluestone ships enough useful native devices to make music and proves a contract that can support richer free and premium devices later. The set is the first step toward a complete free studio (ADR 0042), so each device should fill a lasting role in that kit rather than serve only as a proof.

### Deliver

- One modest but musical instrument, one note/generative device, and essential utility or mixing effects chosen through separate product briefs.
- A versioned device contract covering discovery, parameters, units, state, presets, automation, latency, bypass, signal capabilities, live and offline processing, missing versions, and migration.
- A declarative presentation contract using Bluestone's design system, with a path for richer first-party editors that does not allow arbitrary visual fragmentation.
- Forward-compatible seams for nested chains and modulation without implementing speculative frameworks.
- A contract that leaves room for per-device oversampling: a device processes at the rate and block size the host gives that instance rather than assuming the engine's; reported latency can include the resampling filters; parameter smoothing and automation stay correct at a higher internal rate; and each device declares whether oversampling is meaningful for it.
- Packaging and licensing boundaries between core devices and future optional premium products.

### Decision gates

- ADR: in-process versus isolated execution for first-party devices, real-time safety, ABI/versioning, and failure containment.
- Product brief for each included device: musical job, minimum controls, presets, accessibility, CPU budget, and explicit non-goals.
- Commercial boundary: which contract and runtime remain open and what optional products may add.

### Acceptance scenarios

1. Each device behaves identically in live, playback, loop, seek, freeze/export, undo, save, and reopen paths where applicable.
2. Automation and presets survive device upgrades through an explicit migration test.
3. A missing or incompatible device version preserves state and gives an actionable status.
4. UI remains coherent at compact and expanded sizes and under keyboard and screen-reader navigation.
5. Performance budgets are measured under polyphony and project load, not inferred from microbenchmarks alone.

### Non-goals

A broad factory library, a full modular synthesis environment, premium-device implementation, or per-device oversampling itself (only the contract seam above is in scope).

## Slice 12: release hardening and evidence

### Outcome

An early adopter can become a 1.0 user without losing work, relearning the product, or guessing which paths are trustworthy.

### Deliver

- End-to-end runs of the canonical acceptance project on every supported platform.
- Long-session soak tests, repeated device loss/recovery, plugin crash loops, save/reopen cycles, and low-disk simulations.
- Project migration from every supported format with frozen playback/export equivalence where possible.
- Installer/archive, signing or clearly documented unsigned behavior, first-run defaults, crash reporting policy, and release notes.
- A complete audit of user-visible terminology, empty states, warnings, errors, keyboard behavior, accessibility, scaling, and visual cohesion.
- A public support matrix listing verified hardware, experimental paths, and known limitations.
- A documentation pass that makes the README, user workflows, roadmap, architecture, and troubleshooting agree.

### Release gates

Release 1.0 does not ship until:

- every supported path has named automated and physical evidence;
- all release-blocking timing and placement thresholds pass;
- no known issue can silently lose or misplace project media or musical events;
- mix and stem export are repeatable and qualified hardware paths have a complete capture workflow;
- plugin and endpoint failure preserve project state;
- the primary workflow can be completed without the Connections editor;
- visual review approves the supported size and scaling matrix; and
- every remaining limitation is specific, honest, and compatible with the complete-production promise.

## Explicitly after 1.0 unless reprioritized

- Clip and scene launcher UI
- Full modulation system and modular Grid-like environment
- Large first-party instrument and effect catalog
- Premium device products
- General visual project signal graph and feedback routing
- MIDI 2.0/UMP, MPE, and broad per-note expression
- Specialized XG or other legacy-format sequencers
- Video post-production, notation, and mastering/distribution services

Do not build these opportunistically inside a 1.0 slice. Preserve a clean extension path and record any required seam; implement the capability only after it is deliberately reprioritized.

## Agent handoff contract

An agent finishing or pausing a slice must update its worklog or pull request with:

### Outcome

What musician-visible path now works? Describe behavior, not files changed.

### Scope completed

- Completed acceptance scenarios
- Decisions recorded and ADRs added or amended
- Project-format and migration effects

### Verification

- Exact build and test commands run
- Timing or performance measurements, including configuration and raw artifact location
- Physical hardware and operating systems used
- UI frames reviewed, with window size and scaling
- What skipped and why

### Remaining work

- Incomplete acceptance scenarios
- Known defects or compromises
- Unverified hardware or platform behavior
- Follow-up that is required before the next dependent slice

### Compatibility and risk

- Files, projects, plugins, devices, or workflows that can behave differently
- Recovery and rollback behavior
- Any assumption the next agent must verify rather than inherit

Never mark a slice complete because its code compiles or its isolated unit tests pass. The slice is complete only when its musician-visible outcome and acceptance scenarios are demonstrated.

## Standard agent starting prompt

Use this template when handing one slice to an agent:

```text
Implement Slice <number and title> from docs/release-1.0.md.

Read docs/product-vision.md, docs/roadmap.md, docs/engineering-handoff.md,
docs/architecture.md, docs/project-format.md, and every ADR named by the slice
before changing code. Inspect the current implementation; do not assume the plan's
descriptions are still current.

Work only within this slice and its necessary prerequisites. Stop at every named
decision gate and write the required ADR before implementation. Preserve the frozen
regression fixtures and existing project behavior unless an accepted ADR explicitly
changes it. Deliver the vertical musician-visible workflow, automated tests, failure
and recovery behavior, documentation, and visual verification required by the slice.

At handoff, report Outcome, Scope completed, Verification, Remaining work, and
Compatibility and risk using the contract at the end of docs/release-1.0.md.
Do not claim physical hardware or platform verification that was simulated or skipped.
```
