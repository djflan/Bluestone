# Product roadmap

This roadmap describes where Bluestone is going. It is intentionally about musician-visible outcomes rather than implementation tasks. The [product vision](product-vision.md) defines the destination, the [Release 1.0 plan](release-1.0.md) turns the next phase into agent-ready slices, and the [engineering handoff](engineering-handoff.md) records current implementation details, regression constraints, and unfinished verification.

## Today: usable alpha

Bluestone is usable alpha software for musicians who enjoy adopting new creative tools early. It already provides:

- linear MIDI arrangement, recording, piano-roll and event-list editing, automation, looping, and Standard MIDI File import and export;
- device chains with built-in note processors, VST 3 and CLAP instruments and effects, shared racks, and flexible event and audio routing;
- out-of-process plugin hosting with scanning, quarantine, crash recovery, saved state, hosting modes, and floating editors;
- audio playback for WAVE and AIFF clips, a mixer with buses and sends, meters, plugin delay compensation, and one transport for MIDI and audio;
- on macOS, real-time audio, MIDI, and live input in a separate C# engine process, so editing never delays them, measured against published timing targets ([ADR 0043](adr/0043-timing-targets-and-measurement.md), [ADR 0044](adr/0044-engine-process.md));
- external MIDI output, data-defined hardware profiles, SysEx preservation and interpretation, hardware-parameter automation, and project rebinding when an endpoint changes; and
- a device-centric desktop workspace with a Browser, Device Views, inspector, arranger, mixer, and saved local layouts.

The alpha is incomplete. Audio cannot yet be imported through the application or recorded, a production cannot be rendered as a mix or stems, hardware does not yet appear as a unified chain device with an audio return, and several platform paths have only automated coverage.

## Current focus: close the production loop

The next phase makes one complete workflow dependable:

> Add a hardware or software instrument, record MIDI and audio, edit the performance, monitor it with correct latency, mix it, and export the finished result.

### 1. Core audio production

- Measure MIDI arrival, immediate output, and thru through the engine process, and measure the engine on Windows and Linux hardware so it can be the default there, as it is on macOS ([ADR 0044](adr/0044-engine-process.md)).
- Select and use audio inputs without disrupting the existing output path.
- Record audio with accurate placement, input monitoring, punch, loop takes, and failure-safe file handling.
- Import common audio files from the Browser and operating system.
- Provide a focused take and comping workflow.
- Complete the essential audio-editing gestures needed for recorded material.
- Render a full mix and selected stems with deterministic output and honest diagnostics.
- Make track-owned gain, pan, mute, solo, metering, and output behavior conventional and immediately understandable.

### 2. Hardware as a first-class device

- Add a hardware instrument from the Browser as naturally as a plugin.
- Keep transport details underneath a unified device that can combine MIDI output, an audio return, a profile, named parameters, and latency correction.
- Add an external-effects device for send and return through an audio interface.
- Measure and compensate round-trip latency without hiding the applied correction.
- Support dependable clock and synchronization workflows.
- Add CV and Eurorack integration after the MIDI-and-audio hardware loop is established and verified with appropriate interfaces.

### 3. Arrangement and editing depth

- Make selection, movement, copying, splitting, trimming, snapping, zooming, and keyboard behavior consistent across note and audio material.
- Deepen piano-roll editing without turning the editor into a dense control panel.
- Complete automation drawing for device, mixer, and send parameters.
- Improve navigation and large-project workflows.
- Preserve fast, deterministic editing and project recall as capabilities grow.

### 4. A small, complete native-device contract

Bluestone will initially depend primarily on third-party plugins and hardware. A limited built-in collection will validate the first-party device platform and take the first steps toward a complete free studio that needs nothing else installed ([ADR 0042](adr/0042-complete-studio-in-any-configuration.md)):

- one useful instrument;
- one note or generative device; and
- a small set of essential audio utility or mixing devices.

The contract must cover parameters, state, presets, automation, latency, signal types, real-time processing, project recall, missing-device behavior, and future nesting and modulation. A large native catalog is not required for this phase.

## Quality work in every milestone

Quality is not a final cleanup phase. Every slice must include:

- timing and latency measurements where relevant;
- deterministic automated coverage and migration tests;
- real-device verification for behavior that simulation cannot prove;
- recovery from device loss, plugin failure, cancellation, and partial files;
- keyboard and accessibility review;
- visual review at supported window sizes and scaling levels; and
- documentation that distinguishes implemented, tested, and merely planned behavior.

## After the production loop

These capabilities follow after the current focus unless a dependency requires groundwork earlier:

- a clip and scene launcher that shares material with the linear arranger;
- modulators, nested containers, and richer generative devices;
- selective, per-device oversampling for Bluestone devices, chosen per instance with separate live and render settings;
- a free first-party instrument and effect collection complete enough to finish a production with Bluestone alone, plus optional premium devices that add to it;
- deeper sample and preset browsing;
- an optional visual signal-graph view;
- broader plugin-format and processor-architecture support;
- MIDI 2.0, MPE, and per-note expression; and
- specialized sequencing environments for particular hardware families or legacy formats.

## Release 1.0 outcome

Release 1.0 is not defined by feature count. It is reached when a musician can dependably complete a production in Bluestone on the supported platforms.

At minimum:

- MIDI, plugin instruments, hardware instruments, and recorded audio can coexist in one project;
- recording placement, monitoring, playback, automation, and export agree within measured tolerances;
- common work requires no manual signal graph construction;
- projects survive missing devices, renamed endpoints, plugin crashes, and application restart without silent loss;
- the essential arranger, editor, mixer, Browser, Device View, and settings workflows are coherent and visually reviewed;
- supported hardware and platform paths have named verification evidence; and
- known limitations are narrow enough to document without undermining the complete-production promise.

Detailed slice order, acceptance scenarios, and agent handoff rules are in [release-1.0.md](release-1.0.md).
