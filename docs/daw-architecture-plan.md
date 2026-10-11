# DAW architecture modernization: implementation plan

Status: implemented (see `docs/daw-epic-handoff.md` for what is left). The architecture pages and ADRs 0021 to
0028 now describe the result; this plan is kept as the record of the assessment. This page is the working plan for the epic that moves Bluestone from "a MIDI
sequencer with one output per track" to a device-chain and routing architecture that can host
instruments, effects, and out-of-process plugins. When the work is finished it is replaced by the
architecture pages and ADRs it points to; until then it records what was found and what was decided.

## 1. What the repository looked like

Assessed before any change (build clean, 691 unit tests and 44 integration tests, 16 of them
platform-skipped, all passing).

| Concern | Before | Consequence for the epic |
| ------- | ------ | ------------------------ |
| Tracks | `Track`: name, mute/solo, `Clip`s, automation lanes. Type-agnostic (ADR 0020). | Keep. Add a role and an audio clip type without subclassing. |
| Clips | `Clip` base record; the only subtype is `NoteClip`. Non-destructive trim, split, copy. | Keep. Add `AudioClip` as content metadata only (no audio engine yet). |
| Routing | `TrackRoute`: **one** profile, **one** endpoint, one channel, one transpose, one voice **per track**, held in a `RoutingTable` keyed by track. | This is the coupling to remove. A track can reach one MIDI output and nothing else. |
| Playback | `PlaybackPlanCompiler` turns tracks plus a `PlanTrackBinding` per track into an immutable plan of encoded MIDI 1.0 messages; the engine swaps plans atomically. | Keep immutable plans. Compile from processed event streams instead of per-track bindings. |
| Automation | `AutomationLane` targets a MIDI controller, pitch bend, or pressure on a channel, and is rendered into channel events. | Add device-parameter targets that are **not** rendered into MIDI. |
| Devices | None. `DeviceProfile` describes what an instrument understands, not a processing device. | New: definitions, instances, chains. |
| Persistence | Format 3 JSON, versioned migrations (`IProjectMigration`), atomic saves, preserved unknown properties. | Format 4 with a 3 to 4 migration. |
| MIDI | Domain events at MIDI 2.0 value resolution; MIDI 1.0 encoding at the edge; SysEx kept byte for byte. | Unchanged. Processing works on these events, not on wire bytes. |
| UI | Avalonia, compiled bindings; a framework-free `Bluestone.Presentation`. Per-track routing is edited in the inspector (`SelectionViewModel`). | Keep the inspector workflow working through the new model; add a device strip and routing inspector. |
| Guards | `MidiOneOutputGoldenTests` and `PlanEquivalenceTests` pin the exact MIDI 1.0 bytes. `DependencyDirectionTests` pin project references. | These are the regression net for the refactor. |

ADR 0020 says "there is no track kind" and limits automation targets to MIDI parameters. Both
statements are superseded by the new ADRs, which say exactly what changes and what stays.

## 2. Decisions

The full reasoning is in the ADRs listed in section 5. In short:

1. **Track role is persisted, and reconciled with content.** A role is the user's declared workflow
   (Instrument, Audio, Hybrid, Effect, Group). It cannot be derived for an empty track or for
   Effect and Group tracks, so it is stored. Adding content computes the role it implies and either
   converts automatically, asks for confirmation, or refuses. Nothing is dropped. `Track` stays one
   type, and the domain does not reject clips because of a role.
2. **One routing model.** `SignalConnection`: kind (events or audio), source node, destination node,
   channel mapping. Sources are a track's output, a rack's output, or a device tap. Destinations are
   a track or rack input, an external-instrument part, or a mixer channel. The device strip and the
   routing inspector both read and write this list.
3. **Chains are first-class and owned once.** `DeviceChain` has its own ID, one owner (a track or
   the project), ordered `DeviceInstance`s with stable IDs. Automation, taps, and connections point
   at device IDs, never positions.
4. **External instruments are project entities** with logical ports, each bound to an endpoint, plus
   a device profile and an operating mode. Tracks reach them through connections, so a profile is
   stated once per instrument and not once per track.
5. **Event processing is sequential, with host-managed passthrough.** A device declares which event
   classes it handles. The runner hands it only those; everything else (SysEx, controllers, program
   changes) is merged back in order. Explicit filter devices are the only way events disappear.
6. **Processing runs when the plan is compiled, not on the real-time thread.** Built-in event
   processors produce streams; the real-time thread still only dispatches a prebuilt plan. Plugin
   processing runs in worker processes.
7. **Mixer channels are their own list.** Audio connections end at a mixer channel or the master.
8. **Plugins run out of process, always.** There is no in-process fallback. A shared-memory block
   exchange carries audio and timestamped events; a pipe carries control messages. Crash recovery
   restores from the last captured state.
9. **C# first, Rust where it pays.** C# is the default. Rust is preferred over C/C++ for performance-critical or native components (likely the plugin worker's VST3 layer and the real-time data plane), each with a stated reason and a versioned C ABI. None has been needed yet (ADR in section 5 sets the bar).

## 3. Phases

Each phase leaves the solution building with every test passing.

| Phase | Work | Evidence |
| ----- | ---- | -------- |
| 0 | Freeze format 3 fixtures and plan hashes from the *current* code. | New fixtures plus hashes computed before the refactor. |
| 1 | Domain: roles, `AudioClip`, devices, chains, external instruments, connections, mixer, device automation targets, routing validation. | Domain unit tests. |
| 2 | `Bluestone.Signal`: signal buffers, event classes, chain runner with passthrough, device catalog, built-in processors (Transpose, Event Filter, Arpeggiator), graph evaluation. | Unit tests for scenarios 2 to 6, 10. |
| 3 | Replace `TrackRoute` and `RoutingTable`: compiler, resolver, controller, serializer, migration, commands. | Golden and equivalence hashes unchanged; format 3 fixtures reproduce the same MIDI bytes. |
| 4 and 5 | Plugin hosting: protocol, shared-memory exchange, supervisor, scanner, reference worker. Built in parallel in `Bluestone.Plugins.*`, merged here. | Tests that spawn and kill real worker processes. |
| 6 | Application and UI: role conversion commands, chain commands and presets, device strip, routing inspector. | View model tests; the Avalonia project builds with compiled bindings. |
| 7 | Chain preset persistence. | Round-trip and independence tests. |
| 8 | Acceptance scenarios as tests, documentation, ADRs, README, CI. | Full build and test run. |

### Phase 0 baseline (captured from the code before the refactor)

Two format 3 projects were written by the format 3 serializer and are committed as fixtures
(`src/dotnet/Bluestone.Tests.Unit/Infrastructure/Fixtures/format3-*.bluestone`). They are never regenerated. The
hashes below are what the *old* pipeline (route resolution, output preparation, plan compilation)
sent, with each endpoint present except `gone-*` (missing) and `old-*` (present under a new key, so
bound by name), and the profiles shipped in `/profiles`. After the refactor the migrated projects
must hash identically.

| Fixture | MIDI 1.0 playback hash | SMF export hash | Output slots | Plan events |
| ------- | ---------------------- | --------------- | ------------ | ----------- |
| `format3-routing-full` | `2F73C9A155441A5E` | `BAD8DAA4476B28E9` | 3 | 158 |
| `format3-canon` | `1B5E6E6E27359652` | `6F9AA00B419CABFC` | 3 | 1407 |

`format3-routing-full` also pins behaviour that is easy to lose: a profile per track on a shared
endpoint, a route with only a channel and transpose (nothing assigned, so it does not play), a
route that rebinds by name, a missing profile, voice selection, a lane that collides with another
once the route forces a channel, split SysEx joined across events, notes transposed out of range,
and a route for a track that no longer exists. Plan diagnostics: three tracks "not routed", one
clip event replaced by automation, one lane dropped after the channel override.

## 4. Out of scope, stated up front

Production VST3 hosting (loading, editors, parameter enumeration through the VST3 COM interfaces),
an audio engine and audio clip playback, advanced plugin delay compensation, MIDI 2.0 transport,
visual node graphs, feedback routing, and operating-system sandboxing. The boundaries for each are
built and tested where they are cheap; none is described as finished in the documentation.

## 5. Documents produced

Written at the end, once the code settles, so they describe what exists:

- ADR 0021: tracks have roles; roles are persisted and reconciled.
- ADR 0022: device chains, definitions, instances, ownership, and processing semantics.
- ADR 0023: one signal-routing model, external instruments, mixer channels (supersedes ADR 0008).
- ADR 0024: device-parameter automation separate from signal flow (amends ADR 0020).
- ADR 0025: out-of-process plugin hosting.
- ADR 0026: native technology policy: C# first, Rust when measured, C/C++ exceptions.
- ADR 0027: project format 4 and its migration (amends ADR 0009).
- `docs/architecture.md` rewritten around the new model; `docs/plugin-hosting.md`;
  `docs/project-format.md` updated; `README.md` status and layout.
