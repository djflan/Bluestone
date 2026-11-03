# 0041. Bluestone is a device-centric production DAW with progressive routing

- Status: Accepted; amends the product and interaction direction of 0023 and 0038; ecosystem direction amended by 0042
- Date: 2026-11-02

## Context

Bluestone began with a hardware-MIDI and XG-focused product thesis. The implementation subsequently gained an audio engine, mixer, audio clips, plugin instruments and effects, out-of-process plugin hosting, device chains, groups, racks, a Browser, and a device-centric workspace. New tracks now follow a DAW-first path, but the public goals still describe hardware-first MIDI sequencing and place XG at the product's centre.

The result is an identity conflict. The code is broader than a specialized sequencer, while the ordinary workflow is not yet broad enough to finish a production because audio input, recording, comping, and mix export are absent. Hardware remains architecturally capable but experientially separate: plugins are inserted as devices, while an external instrument is assembled from profiles, endpoints, project entities, and connections.

The product needs one direction that can guide scope, interaction design, and Release 1.0 without discarding the timing, MIDI, routing, and hardware work already present.

## Decision

- **Bluestone is a complete-production DAW.** Its destination covers composition, MIDI and audio recording, editing, arrangement, automation, mixing, stems, and final export. It is not primarily a sequencer that hands work to another DAW.
- **Timing and accuracy lead.** Recording placement, playback, synchronization, latency compensation, automation, project recall, and export must be measurable, deterministic where the environment permits it, and honest about the remainder.
- **The product is device-centric.** Hardware instruments, software instruments, note processors, audio effects, external effects, and routing utilities appear as peer devices in the musician's workflow.
- **Hardware's user-facing device is allowed to project onto existing domain entities.** ADR 0023 rejected a second external-instrument device kind because it would duplicate routing state. This ADR amends the interaction consequence, not that single-source-of-truth requirement: the Browser and Device View may present one hardware device while `ExternalInstrument`, device profiles, endpoint bindings, and `SignalConnection` remain its underlying project representation. The implementation must receive its own ADR covering identity, ownership, multi-timbral sharing, persistence, and migration before it is built.
- **Routing uses progressive disclosure.** Tracks have conventional inputs, outputs, sends, channel controls, and sensible routes to groups or the master. The device chain is the everyday modular surface. Containers, routing devices, and an optional future signal graph expose greater depth. An ordinary task must not require a global connections editor, and automatic routing must remain inspectable and reversible.
- **The linear arranger and piano roll come first.** A clip and scene launcher is a later peer workflow over compatible project material, not a prerequisite for Release 1.0. Modular and generative workflows grow through devices and, later, modulation and container systems.
- **The initial ecosystem emphasizes plugins and hardware.** A small native device set establishes the contract for state, parameters, automation, presets, latency, signal kinds, live processing, and future nesting and modulation. A broad first-party catalog and optional premium devices follow later.
- **Product quality includes visual coherence.** Shared design tokens, interaction patterns, terminology, accessibility, and running-application visual review are completion criteria. Functional controls assembled without a coherent workflow or presentation are incomplete.
- **The present release posture is alpha.** Bluestone is usable for adventurous musicians, but public claims must distinguish implemented, physically verified, simulated, experimental, and planned behavior.
- **XG is project history, not product positioning.** Existing generic XG support remains valid. Specialized XG sequencing may return as a later feature, but it is not a Release 1.0 priority and is not emphasized in public product identity.

The detailed product statement is [product-vision.md](../product-vision.md); current priorities are in [roadmap.md](../roadmap.md); ordered implementation slices are in [release-1.0.md](../release-1.0.md).

## Consequences

- Audio input, recording, monitoring, editing, comping, mix export, and stem export become core roadmap work rather than optional audio expansion.
- Hardware integration is judged by the same add-play-record-recall workflow as a plugin, even when the underlying transport and project entities differ.
- The current connection model remains valuable infrastructure, but the user interface must stop making its complexity the price of ordinary music-making.
- ADR 0038's DAW-first defaults remain directionally correct. Future work must complete the conventional track and mixer path rather than adding more isolated routing controls.
- A hardware-device implementation, track-owned mixer behavior, duplex audio input, recording placement, takes/comping, export semantics, CV safety, and the first-party device contract each require focused follow-up decisions named in the Release 1.0 plan.
- MIDI 2.0, MPE, the clip launcher, a full modulation environment, a visual project graph, a large native catalog, and specialized XG sequencing are deferred unless explicitly reprioritized.
- Public documents present the current product identity. This ADR and repository history retain the origin and change in direction.
