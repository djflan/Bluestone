# Bluestone product vision

## Product definition

Bluestone is a modern, device-centric digital audio workstation built for tight timing and accuracy. It treats hardware instruments, software instruments, effects, and note processors as first-class creative devices within one modular environment. Bluestone on its own is a complete virtual studio; plugins and hardware join it as peers, never as prerequisites.

Bluestone combines deep, flexible signal flow with clear, approachable workflows. Sophisticated capabilities are progressively disclosed instead of exposed as engineering machinery. Its interface follows a cohesive, intentional design language across every feature and interaction. Bluestone should look and feel like a polished professional product: fresh, responsive, musically focused, and designed as a unified whole.

## Who it serves

Bluestone is for serious musicians, whatever their studio looks like: a laptop running only Bluestone, a plugin collection, or a room of professional synthesizers, boutique instruments, Eurorack systems, audio interfaces, external effects, and other dedicated equipment. Its primary users are intermediate and professional musicians, producers, and studio owners, but committed amateurs should be able to become productive without facing a steep initial learning curve.

The product must scale from a focused home studio to professional production without simplifying away its capabilities or requiring musicians to think like engineers.

## Product priorities

When priorities conflict, use this order:

1. Tight timing, synchronization, latency handling, and accuracy.
2. Bluestone devices, third-party plugins, and hardware as equal participants, in any combination.
3. Deep modularity and extensibility.
4. Clear defaults, progressive disclosure, and approachable workflows.
5. A cohesive, contemporary, professional user experience.

Correctness includes more than avoiding crashes. Recorded material must land where the musician played it; playback and export must agree; routing, latency compensation, automation, and project recall must be deterministic; and the application must describe unsupported or lossy behavior honestly.

## A complete studio in any configuration

Bluestone does not assume what a musician owns. Bluestone alone, Bluestone with plugins, Bluestone with hardware, and any mix of the three are all complete, first-class studios, not degraded versions of a "real" setup.

- **Bluestone alone is a complete virtual studio.** With only Bluestone and its free core devices, a musician can compose, record, arrange, mix, and export a finished production.
- **The guarantees do not change with the setup.** Timing, latency compensation, automation, recall, and export are held to the same standard whether a project uses only Bluestone devices, only external gear, or both.
- **Adding gear extends the studio; it does not change how it works.** A hardware synth or a third-party plugin takes its place in the same device chain, with the same add-play-record-recall workflow as a Bluestone device.

## The music-making model

### Start without ceremony

Bluestone opens into a usable project. It selects sensible defaults and lets the musician begin without an onboarding gate or mandatory studio-configuration exercise. A musician adds a hardware instrument or a plugin as a device, plays it, and records it. Bluestone establishes the ordinary track and master routing automatically while keeping every choice inspectable and editable.

### Devices are the common language

The device chain is Bluestone's primary modular surface. Instruments, note processors, audio effects, hardware, and routing utilities appear as peers in the musician's workflow even when their implementations differ underneath.

A hardware-instrument device ultimately brings together:

- MIDI or CV output;
- an optional audio return;
- a device profile and named parameters;
- clock and synchronization behavior;
- measured latency correction; and
- note and audio effects around the external device.

Ordinary tracks retain familiar inputs, outputs, sends, faders, and a default route to the master. Nested chains, modulators, routing devices, and an optional signal-graph view provide progressively deeper control. A global patch bay must never be required for an ordinary musical task.

### One production from idea to master

Bluestone is intended to complete an entire production. Composition, MIDI and audio recording, editing, arrangement, automation, mixing, stems, and final export belong to the core product. It is not designed merely to hand a sequence to another DAW for completion.

The linear arranger and piano roll are the first composition workflow. A clip and scene launcher will later provide a second, interchangeable way to compose and perform. Material should move between linear and launcher contexts without becoming a separate kind of project. Modular and generative device workflows grow within the same system.

## Progressive depth

Bluestone should feel simple because it makes good decisions, not because it lacks power.

- New projects and tracks use musical defaults.
- Common controls appear where musicians expect them.
- Contextual inspectors explain the selected object without becoming the only way to work.
- Advanced routing is available through devices, containers, and dedicated views.
- Errors say what happened, what remains safe, and what the musician can do next.
- Technical names are shown only when they help identify a real device, protocol, or diagnostic.

No important state should be invisible. Automatic behavior must remain inspectable, reversible, and saved as ordinary project state where appropriate.

## Visual and interaction quality

Bluestone is designed as one product, not as a collection of independently styled features. New work must use the shared design system and preserve consistent hierarchy, spacing, typography, color, motion, terminology, focus behavior, and editing gestures.

A feature is not complete merely because its controls exist. It must fit the surrounding workflow, work at supported window sizes, remain understandable without documentation, support keyboard use and accessibility, and be reviewed in the running application. Headless tests protect behavior; visual review protects the product.

## Product and device ecosystem

The initial product emphasizes reliable third-party plugin hosting, hardware integration, and a small set of useful Bluestone devices. Those built-in devices establish the native device contract: state, parameters, automation, presets, latency, signal types, live processing, project recall, and future nesting and modulation.

Over time, those core devices grow into a complete free studio: instruments for melodic and rhythmic material and the essential effects for shaping and mixing them, enough to finish a production with nothing else installed. Premium devices may be distributed as optional paid products that add depth, character, or specialized instruments, but no part of the complete-studio workflow may require a purchase.

Bluestone devices will support selective oversampling. A musician can oversample only the devices that benefit, such as a saturator, a filter, or a synth voice, without raising the cost of the whole project. The setting belongs to the device instance, can differ between live playback and export, and its added latency is compensated like any other.

## Current release posture

Bluestone is alpha software. It is usable for music production today and is appropriate for musicians who enjoy adopting new creative tools early. Missing workflows, platform gaps, and breaking changes must be stated plainly. Alpha does not excuse data loss, misleading claims, poor timing, or careless interaction design.

## Direction, not immediate scope

The vision describes the destination. The public [roadmap](roadmap.md) states the current priorities, and the agent-ready [Release 1.0 plan](release-1.0.md) breaks them into deliverable slices.

The following are part of the long-term direction but are not prerequisites for the next milestone unless a slice says otherwise:

- a clip and scene launcher;
- a full modulation system and generative environment;
- selective, per-device oversampling;
- a visual project signal graph;
- a first-party instrument and effect collection complete enough to finish a production with Bluestone alone;
- MIDI 2.0, MPE, and broad per-note expression; and
- specialized sequencing environments for individual legacy formats.

## Decision test

Before accepting a product change, ask:

1. Does it improve or preserve timing, accuracy, and dependable recall?
2. Does it treat Bluestone devices, plugins, and hardware as peers, and work as well in a Bluestone-only studio as in one full of gear?
3. Does it deepen modularity without making the common path harder?
4. Can a musician understand the result without learning Bluestone's internals?
5. Does it strengthen the product's visual and interaction coherence?
6. Can its behavior be verified automatically and, where necessary, on real hardware?

If a change fails one of these tests, its design is incomplete or its tradeoff must be recorded explicitly.
