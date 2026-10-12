# Architecture decision records

Consequential, hard-to-reverse decisions are recorded here as short ADRs. An ADR is never edited to
change its decision; a later ADR supersedes it and the superseded record is marked as such.

| ADR | Title | Status |
| --- | ----- | ------ |
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-build-and-test-conventions.md) | Build and test conventions | Accepted |
| [0003](0003-deterministic-event-ordering.md) | Deterministic ordering of simultaneous events | Accepted |
| [0004](0004-musical-time-representation.md) | Musical time representation | Accepted |
| [0005](0005-midi-endpoint-contracts.md) | MIDI endpoint contracts | Accepted |
| [0006](0006-playback-engine.md) | Playback engine: prepared plans, dual cursors, runtime releases | Accepted; loop-wrap chase superseded by 0020 |
| [0007](0007-device-profile-format.md) | Data-first device profiles | Accepted; amended by 0031 |
| [0008](0008-routing-and-rebinding.md) | Routing: profiles and endpoints bound independently, resolved at run time | Superseded by 0023 |
| [0009](0009-project-persistence.md) | Project persistence: versioned JSON with atomic saves | Accepted; amended by 0027 |
| [0010](0010-desktop-ui-framework.md) | Desktop UI framework: Avalonia | Accepted |
| [0011](0011-coremidi-adapter.md) | macOS CoreMIDI adapter in managed code | Accepted |
| [0012](0012-performance-baseline.md) | Performance baseline: managed scheduling with OS real-time policy, no native code | Accepted |
| [0013](0013-windows-playback-timing.md) | Windows playback timing: 1 ms timer resolution and MMCSS | Accepted |
| [0014](0014-winmm-adapter.md) | Windows WinMM output adapter in managed code | Accepted |
| [0015](0015-winmm-system-exclusive.md) | System exclusive through WinMM without blocking playback | Accepted |
| [0016](0016-alsa-adapter.md) | Linux ALSA sequencer output adapter in managed code | Accepted |
| [0017](0017-recording-and-note-editing.md) | MIDI recording and note editing | Accepted |
| [0018](0018-midi-protocol-layering.md) | MIDI protocol layering: a protocol-independent domain with MIDI 1.0 first | Accepted |
| [0019](0019-sysex-preservation-and-interpretation.md) | SysEx: preserved losslessly, interpreted optionally by dialect | Accepted |
| [0020](0020-hybrid-tracks-clips-and-automation.md) | Hybrid tracks: typed clips and track automation lanes | Accepted; supersedes part of 0006; amended by 0021 and 0024 |
| [0021](0021-track-roles.md) | Track roles: persisted, reconciled with content, never destructive | Accepted; amends 0020; amended by 0030 |
| [0022](0022-device-chains.md) | Device chains: definitions, instances, ownership, and processing semantics | Accepted; amended by 0028 and 0037 |
| [0023](0023-signal-routing-model.md) | One signal routing model: connections, external instruments, and mixer channels | Accepted; supersedes 0008 |
| [0024](0024-device-automation.md) | Device parameter automation is separate from signal flow | Accepted; amends 0020; amended by 0031 |
| [0025](0025-out-of-process-plugin-hosting.md) | Plugins run out of process; isolation is crash isolation, not a sandbox | Accepted; amended by 0028, 0033, and 0034 |
| [0026](0026-native-technology-policy.md) | Native technology policy: C# first, Rust where measurements or native interfaces demand it | Accepted; amended by 0033 |
| [0027](0027-project-format-4.md) | Project format 4 and the migration from per-track routes | Accepted; amends 0009 |
| [0028](0028-plugin-midi-effects-at-plan-time.md) | Plugin MIDI effects run in their worker when the plan is compiled | Accepted; amends 0022 and 0025; amended by 0035 |
| [0029](0029-rename-to-bluestone.md) | The project is renamed Bluestone, with no compatibility for Cadence files | Accepted |
| [0030](0030-group-tracks.md) | Group tracks: mute and solo reach members, membership routes, folding is saved | Accepted; amends 0021 and roadmap decision 11 |
| [0031](0031-hardware-parameter-automation.md) | Hardware parameter automation through profile parameter maps | Accepted; amends 0007 and 0024 |
| [0032](0032-audio-engine.md) | Audio engine: cpal output in Rust, the render graph in C#, buffered by a ring, one transport | Accepted; amended by 0035 |
| [0033](0033-third-party-plugin-hosting.md) | Third-party plugins: VST3 and CLAP through a Rust host in the worker | Accepted; amends 0025 and 0026 |
| [0034](0034-plugin-hosting-modes.md) | Plugin hosting modes: how plugins share worker processes | Accepted; amends 0025 |
| [0035](0035-live-input-and-delay-compensation.md) | Live input through every device, a live render-ahead queue, and plugin delay compensation | Accepted; amends 0028 and 0032 |
| [0036](0036-semantic-workspace-and-device-view.md) | A semantic workspace, Device Views as view instances, and local layout state | Accepted |
| [0037](0037-parallel-signal-bundle.md) | A device chain carries a parallel signal bundle | Accepted; amends 0022 |
| [0038](0038-daw-first-tracks-and-settings.md) | DAW-first tracks: hybrid by default, heard through explicit connections; Settings in the workspace's place | Accepted; amends 0021 and 0036 |
| [0039](0039-arranger-and-clip-loops.md) | Arranger and clip loops: ranges kept apart from looping, repeats produced at plan time | Accepted; amends 0009, 0020, and 0032 |

## Template

```markdown
# NNNN. Title

- Status: Proposed | Accepted | Superseded by NNNN
- Date: YYYY-MM-DD

## Context
What forces are at play, including measurements where relevant.

## Decision
What we will do.

## Consequences
What becomes easier or harder, and what we must now watch for.
```
