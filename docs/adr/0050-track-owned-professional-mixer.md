# 0050. Track-owned mixer strips, routable Effect tracks, and Master processing

- Status: Accepted
- Accepted by: project owner
- Date: 2026-11-14

## Context

ADRs 0023 and 0032 established mixer channels as a project-level list independent of tracks. That made
mixing possible, but leaves ordinary track faders, returns, buses, groups, and the Master outside the
track and device workflow. It also makes the common output and send path depend on standalone mixer
channels. The product direction calls for conventional track-owned mixing with progressive routing
([ADR 0041](0041-product-direction.md), [Release 1.0](../release-1.0.md)).

The project is at format 5. Format 6 is required for the ownership and route-shape changes below.
There are no users whose legacy behavior must constrain the new model, but format-5 project data must
be migrated deliberately, with unsupported and invalid cases reported rather than described as
losslessly converted.

The existing architecture already has track roles, track-owned device chains, a single `SignalConnection`
graph for events and audio, cycle validation, audio graph construction in `Bluestone.Application`, and
audio strip state and rendering in `Bluestone.Audio` ([ADRs 0021](0021-track-roles.md), [0022](0022-device-chains.md),
[0023](0023-signal-routing-model.md), [0032](0032-audio-engine.md), [0037](0037-parallel-signal-bundle.md)).
Plugin parameter edits are reported by workers and recorded in the project by `PluginDeviceHost`
([ADR 0033](0033-third-party-plugin-hosting.md)). These are the seams to extend; no new native component
or general-purpose routing abstraction is needed.

## Decision

### Ownership and strip state

- Every arrangement track has one mixer-console row identified by its `TrackId`. The row displays
  track identity, event/output status, and its audio controls when it has audio. Its label follows the
  track name; there is no separately named mixer-channel entity.
- Audio-capable `Audio`, `Hybrid`, `Effect`, and `Group` tracks have persisted audio strip state:
  gain, pan, audio mute/solo, and fader automation. An `Instrument` track with audio in its role,
  route, or persisted strip state retains that state even when a plugin or endpoint is unavailable.
  Role conversion and reconciliation never discard it.
- A MIDI-only hardware track remains visible in the console with its MIDI destination and routing
  status, but has no audio fader, pan control, or audio meter. Do not render a silent-looking audio
  meter or imply that MIDI output is audio. If the track becomes audio-capable, its strip state is
  created in the same undoable edit that establishes the audio path.
- The Master is one project-owned strip, not an arrangement track. It has a fader, pan, meter,
  automation, and an optional chain. It is always present, cannot be deleted, and is pinned at the
  right edge of the mixer. Extend `ChainOwner` to support this owner while retaining the existing
  track/rack identities and uniqueness rules (ADR 0022). Master pan uses the existing stereo-balance
  pan law; the Master owns project-level fader and pan automation.
- Keep MIDI content mute/solo semantics distinct from audio-strip mute/solo in the domain. The UI
  may present a coordinated control, but one command must state which state it changes. Audio strip
  mute applies after the track's chain and therefore silences the complete audio output, including
  audio routed into the chain; track content mute continues to govern that track's own MIDI content
  and the established group rules.

Persist strip ownership and whether the audio section exists. Do not infer it from a plugin scan or
the current signal graph: missing devices must not erase saved intent. An ordinary audio track sends
its output to Master when ungrouped and to its parent Group when grouped. A Group is a sum/bus strip:
it receives ordinary outputs from its direct child tracks, runs that sum through its chain and strip,
and outputs only to Master or its parent Group. An Effect is a processing/return strip: it receives
one or more Send routes, processes their sum through its chain and strip, and outputs to Master or a
parent Group. Sends target Effect tracks only; Groups are never send destinations. A return is not a
separate entity or channel kind: it is an Effect track receiving sends. Legacy topologies where one
channel acted as both a bus and a send return are split into a Group and a neutral Effect track
during migration. The ordinary workflow does not create Effect-to-Effect sends.

### Audio routing and rendering

- Keep `Project.Connections` as the single persisted route graph (ADR 0023). Add an explicit audio
  route purpose: ordinary `Output`, `Send`, or advanced `Internal`; event connections keep their
  existing meaning. Store send level and pre/post-fader tap position on the send connection, so a
  send is not duplicated in strip state.
- An audio-capable track has exactly one ordinary output route. Its destination is Master or its
  parent Group; a Group routes to Master or its parent Group. The group command changes membership
  and the corresponding route atomically. Sends may target Effect tracks only, may be pre- or
  post-fader, and may not target the sending strip. Multiple sends sum at the Effect chain input;
  that chain runs once before the Effect strip and its ordinary output. Effect-return classification
  is derived from incoming sends; a return is still an Effect track, not a Group.
- Keep device taps, device-input routes, plugin sidechains, rack routes, and other explicit
  junction-level routing in the existing graph as `Internal` audio connections. These routes do not
  silently become strip outputs or sends. Validate all audio edges, including strip output and send
  edges, together for existence and cycles. Refuse an edit that adds an error; retain invalid
  project-file connections with diagnostics and omit them from rendering.
- In the audio graph, each track's clips and incoming chain routes enter its chain; the chain output
  enters its strip; the fader/pan/mute/solo are applied at that strip; and output/send edges carry
  audio to their destinations. A pre-fader send taps before gain and pan; a post-fader send taps
  after both. A Group or Effect destination receives the routed audio at its chain input and then
  processes it through its own chain and strip.
- Sum only ungrouped track outputs and top-level Group outputs at the always-present Master chain
  input; Group members are not also routed directly to Master. Process Master devices in chain
  order, then apply the Master fader and pan with their automation, then produce the final output.
  Master and track fader changes are sample-timed in the audio graph, ramped to avoid block-boundary
  clicks, and included in delay compensation. Keep existing post-fader meter semantics; a meter is
  not a limiter, and clipping is not silently prevented.
- A new global Display meter-mode preference applies to mixer track strips and Master only; it does
  not change Device View junction meters or their per-view display settings:
  - **Peak + RMS** remains the default and keeps ADR 0032 semantics: post-fader peak held and falling
    at 20 dB/s, plus RMS with a 300 ms time constant.
  - **VU** is average-rectified level with 300 ms ballistics. A persistent global VU reference is
    adjustable from −36 to 0 dBFS in 1 dB steps, default −18 dBFS; a steady 1 kHz sine at the
    selected reference reads 0 VU under the app's dBFS convention. It is not an acoustic SPL
    calibration.
  - **K-20, K-14, K-12** are digital K-System display scales with their respective 0-reference at
    −20, −14, or −12 dBFS. They are not acoustic room or listening-level calibrations.
  - **LUFS-S** is ungated BS.1770 short-term loudness: K-weighted channel energy over the trailing
    3 seconds, with standard stereo channel weighting. It is unavailable until a full 3 seconds of
    valid samples have accumulated. After start, seek, or a discontinuity, show an explicit
    warming-up/not-ready state rather than a numeric zero or stale value.
- Implement LUFS-S with a fixed ring of thirty 100 ms energy segments plus bounded partial-segment
  state, not per-frame 3-second history. Allocate meter state off the audio thread; update it without
  allocation, locks, or logging. A focused BenchmarkDotNet ShortRun covered warmed 128-frame
  Peak+RMS, VU, and LUFS-S processing at 44.1, 48, 88.2, and 96 kHz with 1, 8, and 32 strips, plus
  build/preparation and reused-snapshot cases. The run reported no managed render allocation. It
  used three iterations with wide intervals and synthetic fan-out; it did not measure a device or
  plugin callback, establish an xrun/deadline result, or support a performance-bound claim.
- Persist meter mode, meter presentation, and VU reference in global Desktop settings, not in a
  project. Define a separate `MixerMeter` token family in `Theme/Tokens.axaml` for meter background,
  signal fill, peak hold, warning, clip, text, and not-ready state; do not reuse Device View junction
  meter tokens. **Bar** is the default presentation: signal fill and peak-hold marker share a linear
  bar scale. **Round** presents the same value and peak marker on a circular scale. Both use
  identical DSP, calibration, thresholds, numeric readout, and warm-up state; presentation changes
  never alter audio or meter measurement. (Amended: the Bar/Round presentation was removed; see
  "Amendment: C2 mixer view" below.)
- Put durable track/master data and route validation in `Bluestone.Domain`; edits and lifecycle
  transactions in `Bluestone.Application/Editing`; graph translation, plugin parameter scheduling,
  and master automation in `Bluestone.Application/Audio`; processing, delay, ramp, and meter state
  in `Bluestone.Audio`; view models and user interaction in Presentation/Desktop. Do not put
  application/plugin references in Domain or audio DSP in Presentation.

### Lifecycle, grouping, and undo

- Rename changes only the owning track name; the console row and labels follow its stable ID.
- Master is mandatory project state and is never deleted or duplicated. Its row is always present
  and pinned at the mixer's right edge, even in an empty project.
- Duplicate copies the strip settings and audio capability, duplicates its owned chain and
  track/device automation with fresh identities, and copies outgoing output/send and parked
  connections with fresh connection IDs and remapped local device IDs. Incoming audio routes are
  not copied. A duplicate in a group remains in that group and routes through it.
- Delete is one undoable command removing the track strip, owned chain, and its active and parked
  routes. Deleting a Group first dissolves its members into the parent group (or out of groups),
  restoring routes that still validate. Deleting an Effect track with incoming sends requires
  confirmation and removes those sends atomically; it must not silently reroute them to Master.
- Joining a Group parks the member's ordinary output and sends, then routes its output into that
  Group. Leaving restores parked routes that still validate. A route that cannot be restored stays
  inactive and is reported; it is not silently retargeted. Creating a Group chooses the common
  parent/output when one exists, otherwise gives the new Group an explicit Master output; member
  routes remain parked for ungrouping. A grouped track's ordinary route cannot bypass its parent.
- Fader, pan, send-level, mute, solo, route, and ownership edits are commands. Continuous fader,
  pan, and send-level gestures merge into one undo step per strip/control; route changes, group
  operations, binding/unbinding, and destructive lifecycle operations are atomic undo steps.

### Mixer interaction and presentation contract

- Selecting a track strip selects the same track in the arranger and Inspector; selecting a track
  in the arranger or Inspector selects its mixer strip. Multi-selection mirrors across these views.
  The Master is independently selectable: selecting it leaves no arrangement track selected and
  shows Master gain, pan, mute, solo, and chain in the Inspector. Selecting a track restores that
  track's Inspector scope.
  Selection is UI state, not project data or an undo step.
- Mixer section visibility and width are presentation-only Desktop view settings. They do not change
  track ownership, routing, audio, project undo history, or saved project content. Group folding
  collapses/expands member tracks in the arranger only; it does not hide their mixer strips or
  change Group membership or routing.
- Reset returns a control to its documented neutral/default value (for example, gain to unity and
  pan to center); fine adjustment uses the control's finer step; and wheel adjustment changes only
  the focused or explicitly hovered control. Reset, fine, wheel, and edits applied to selected strips
  are undoable commands. Continuous fine/wheel gestures merge by control and selected-strip set
  into one undo step; reset and discrete multi-strip operations are atomic. An operation applies
  only to strips supporting that control and reports skipped MIDI-only strips. Ordinary scrolling
  remains navigation.
- MIDI-only hardware strips are keyboard-operable and screen-reader discoverable, with an accessible
  name, track type, MIDI destination, and routing/status description. They expose no audio fader,
  pan, or audio meter and never use an empty meter as a status indicator.

### Master and track automation

Track automation may target that track's fader, pan, and device parameters. The Master owns
project-level automation lanes for its fader, pan, and devices in its chain; Master automation is
not attached to an arbitrary arrangement track. Store musical positions in ticks, convert through
the existing tempo map to audio frames, and schedule plugin parameters through the existing plugin
bridge. Automation remains separate from signal flow (ADR 0024). Saving and reopening must preserve
lane targets even when a Master-chain device is missing.

### Console-plugin detection, control, and recovery

- Support an explicit, persisted, opt-in hand-off from one strip fader to one writable, continuous
  Level parameter on that strip's last audio processor. Persist device and parameter IDs plus a
  verified parameter-to-dB conversion and formatting contract. It must define the supported fader
  range and a bidirectional normalized↔dB mapping with monotonicity and round-trip error bounds. A
  unit label of `dB` is not evidence of linear normalized-to-dB mapping.
- Detect candidates in this order: (1) a shipped identity profile matched by stable plugin identity
  and supported version; (2) parameter-name matching to discover possible controls; (3) an explicit
  user **Use as console** action to select a processor and candidate. Name matching and explicit
  selection discover or select candidates, but do not establish a scale or bypass contract checks.
  Multiple plausible candidates require explicit choice; never bind the first candidate silently.
- A usable console mapping must identify exactly one Level parameter with a verified conversion and
  formatter. If none is verified, show a per-strip **No console fader mapped** notice with the
  reason, leave the ordinary strip fader authoritative, and leave the plugin parameter untouched.
  Optional Pan, Mute, and Solo mappings are allowed only when their ranges, centers, placement, and
  mix behavior are semantically equivalent to the corresponding strip control. Host Solo remains a
  mix-wide graph decision; a plugin-local solo is not equivalent unless verified to implement that
  same mix-wide behavior.
- The current VST 3 parameter metadata exposes ID, title, units, default normalized value, step
  count, and flags, while the plugin protocol carries normalized values; it does not expose a
  trustworthy physical range or plugin value-string formatter ([`vst3.rs`](../../src/native/crates/bluestone_plugin_host/src/vst3.rs),
  [`PluginIdentity.cs`](../../src/dotnet/Bluestone.Plugins.Protocol/PluginIdentity.cs), ADR 0033).
  A hand-off is enabled only when a host capability provides the verified scale/format contract or
  a reviewed profile for the exact plugin identity/version and parameter ID supplies it and a live
  scan confirms the expected metadata. Store reviewed plugin-control profile data under
  `Desktop/Profiles`; a profile keyed only by display name or unit is insufficient.
- Before claiming `bx_console` support, perform and retain a live scan/report for the installed
  plugin identity/version, candidate and selected parameter metadata, normalized↔dB and formatting
  round trips, optional-control semantics, and bypass/removal/crash recovery. No verified VST 3
  class ID, parameter ID, physical parameter range, or normalized-to-dB conversion has been
  established for bx_console AMEK 9099. Its strip-fader hand-off therefore remains deferred; no
  plugin-specific mapping or vendor endorsement is implied. Do not claim its scale from labels or
  screenshots.
- While linked and enabled, the plugin Level parameter is the sole effective gain control and strip
  DSP gain is unity. Fader gestures and fader automation write the linked parameter. A plugin-editor
  parameter notification updates project state and the console fader without echoing another write
  or applying gain twice. Meters report the rendered strip signal.
- Bypass behavior is part of the verified control contract. If bypass removes the linked Level from
  the audio path, suspend the hand-off: transfer its last verified dB value to the strip fallback
  and make strip gain authoritative while bypassed. Fader edits while bypassed update that fallback.
  On un-bypass, write the fallback through the verified mapping to the plugin parameter and restore
  unity strip gain as one state transition. If bypass retains Level in the audio path, keep the link
  active. If bypass semantics cannot be verified, the hand-off is unavailable. Never apply both
  gains.
- Removing or replacing the linked processor atomically transfers the last known linked dB value to
  the strip fader and clears the binding; replacement is never auto-bound. Undo restores the prior
  chain and binding. A crash or missing plugin preserves the binding, last project parameter, and
  fallback; the failed processor follows the existing silence policy, and no substitute gain is
  invented. On recovery, revalidate the exact identity and mapping before restoring the link. A
  mismatch leaves the processor unavailable until explicit relinking or unlinking.
- Settings exposes a **Default console** selector. When configured, new audio-capable strips,
  including a newly created Master, use that console; MIDI-only strips never do. Existing projects
  are unchanged until the user invokes **Add to existing tracks/Master**, one undoable batch that
  appends the default console to eligible audio-capable tracks and the mandatory Master, skips
  duplicates and MIDI-only strips, and reports unavailable or skipped targets. The per-strip
  hand-off remains opt-in.
- Verify arbitration, automation, editor changes, undo/redo, save/reopen, bypass, multiple
  candidates, removal/replacement, crash/restart, and profile/host mismatch with a deterministic
  test plugin. Verify `bx_console` live as above before claiming vendor support.

### Format 6 and migration

Add a `Format5To6Migration` to the existing sequential JSON-tree migration chain. The serializer
continues to write only the current version, refuses newer versions, and does not rewrite a project
merely because it was opened.

- Replace `project.mixer.channels` with track-owned strips and explicit return Effect tracks, and
  create the mandatory Master with its fader initialized from `project.mixer.masterGain` and pan
  centered.
  For each format-5 channel, in channel order, derive its role from the old topology:
  - A channel receiving any incoming ordinary track/channel Output becomes a generated Group track
    (a bus). Derive its nested Group parent from its old channel output, or leave it ungrouped when that
    output was Master. Copy its name, gain, pan, mute, and solo to this Group strip.
  - A channel receiving incoming Sends but no ordinary Output becomes a generated Effect track (a
    return), with the old channel strip settings. Its incoming sends sum at its chain input; it processes them
    once and uses its ordinary output to reach Master or its parent Group.
  - A channel receiving both ordinary Outputs and Sends becomes a Group track with the old strip
    settings plus a generated, neutral Effect return. The return receives the old sends and its
    ordinary output feeds the generated Group's chain input. The Group then processes the legacy
    ordinary inputs and return output through its chain and strip, preserving the old channel's
    gain/pan/mute/solo on the combined audio.
  - A channel with no incoming ordinary Output or Send becomes an empty Effect track with the old strip
    settings. A channel can therefore map to two tracks only when it served as both bus and return.
    Derive all generated IDs deterministically from the project ID, old channel ID, and generated
    role; check for collisions with existing track and generated IDs.
- Convert an ordinary track-output-to-channel path into that track's Group membership and its
  ordinary output to the generated Group. Convert a channel-output-to-channel path into nested
  Group membership when both sides are buses; otherwise, if the source has no bus role, make its
  Effect track a child of the target Group and route its ordinary output there. A channel that had
  both roles uses its generated Effect return as a child of its own Group, while that Group has the
  old channel's parent/output. Keep connections from device taps and racks as
  advanced `Internal` routes into the generated destination chain. Existing ordinary connections
  to Master become ordinary Master outputs; tap routes to Master remain advanced routes.
- Convert each mixer send into a send connection to the target channel's generated Effect return.
  If the target channel is also a bus, that neutral return's ordinary output routes into the bus
  Group's chain input. Preserve send level and pre/post-fader position and preserve connection
  IDs where a prior connection is transformed rather than replaced. Returns and buses are thus
  represented by ordinary Effect and Group tracks, not a persisted channel-kind flag.
- Format 5 has no Master chain or Master automation, so create neither during migration. Add audio
  strip state for Audio, Hybrid, Effect, and Group tracks and for any other track with explicit
  audio routing; keep MIDI-only hardware rows without an audio fader or meter. Do not infer audio
  capability from plugin availability.
- Preserve the mixer-channel order for generated tracks and insert each generated Group above its
  first arrangement member; generated return tracks follow their owning Group in deterministic
  order. The console meter mode is an application preference, not project data, so migration does
  not alter it.
- This conversion is intentionally not claimed lossless for every format-5 graph. If one track has
  multiple ordinary channel destinations, a pre-existing track Group conflicts with the bus
  membership required by its primary output, a legacy bus cycle prevents a nested Group hierarchy,
  or a self/duplicate/malformed send cannot be represented, fail migration with the exact path.
  Do not silently flatten buses, reroute audio to Master, or drop sends and claim equivalence.
  Missing destinations that can be represented remain invalid routes for the normal validator to
  report and the renderer to omit. Track/connection limits and ID collisions must fail with a
  path-specific error.
- Test the 5→6 transformation, deterministic IDs, strict parsing, round-trip, and representative
  valid fan-in, returns, nested buses, direct-to-Master routes, existing Groups, missing targets,
  and cycles. Compare rendering for supported legacy fixtures; tests for deliberately unsupported
  inputs assert the exact path-specific failure. Run existing format 2→3→4→5 migration tests
  through the new step unchanged.

## Consequences

- A track’s mixer strip and routing are part of the track workflow; ordinary mixing no longer needs
  a separate mixer-channel entity. MIDI-only hardware remains visible without false audio controls.
- Effect tracks provide explicit returns and processing points; Groups provide hierarchy and summed
  processing; the Master has one final processing and automation path.
- The project format is intentionally incompatible with older Bluestone versions, which must refuse
  format 6 rather than downgrade it. Format-5 migration preserves supported channel topology but
  explicitly reports invalid or unsupported legacy shapes; it does not promise blanket
  byte-for-byte or sample-for-sample equivalence.
- Strip fader hand-off is plugin-independent in its contract, but vendor support depends on actual
  parameter metadata and recovery tests. No vendor-specific behavior is considered verified until
  exercised with the installed plugin.
- The audio render graph changes ownership and edge processing, so timing, allocation, latency,
  meter, lifecycle, migration, and real-device verification remain required before release claims.

## Amendment: C2 mixer view

The console was rebuilt to the approved "C2" layout: a Bluestone-native strip with a per-device
insert stack. The decisions above are unchanged; only the presentation changed.

- Each strip has one LED-segment meter drawn by the shared `SignalIndicatorRenderer`. The meter
  mode changes scale and ballistics, not the number of meters; meter DSP is unchanged. The Bar/Round
  presentation preference is removed; older settings files still load and ignore it.
- A slim filled gain bar shares the meter's dB scale; a horizontal pan ramp sits under the name. The
  insert stack shows up to three devices with power (bypass) toggles and a flyout for the rest.
- Reset is its own non-merging undo step. Continuous gestures still merge, using a time-based window.
- Not built: per-strip width and spectrum views, an input-monitor control, a Master chain view from
  the Master inserts, and keyboard jumps between strips. Record arm is view state only.


## Amendment: Identity R6 strip

The strip was rebuilt for the "Machined" theme; the decisions above are unchanged.

- Strips are 86 px wide. Each has one stereo LED-ladder meter (replacing the `SignalIndicatorRenderer` meter above),
  a console fader with a satin cap on the meter's taper (−60 to +6 dB), and a display-only column of gain LEDs.
- A pan knob replaces the pan ramp. Mute, solo, and record arm are square keys. Sends show as a count, or as
  **+ Send** only while the strip is hovered or focused.
- Meter colours follow the user's signal gradient (Settings ▸ Display); the clip lamp is a fixed red.
- Still not built: the monitor key (the model has no monitor state) and the items listed above.
