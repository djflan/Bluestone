# 0022. Device chains: definitions, instances, ownership, and processing semantics

- Status: Accepted; amended by 0028 (plugin MIDI effects run in their worker at plan time)
- Date: 2026-10-10

## Context

Bluestone needs devices that transform and generate signals: MIDI effects, instruments, audio effects,
built-in and plugin. They must be ordered in a chain the musician sees as a device strip, keep stable
identities that automation and routing can name, be shareable between tracks without duplicating an
instrument, and be saved as presets that create independent copies. A note processor must never
destroy unrelated data such as XG SysEx or controllers.

## Decision

**Declarative model (`Bluestone.Domain/Devices`).**

- A `DeviceDefinition` says what a device is: ID, origin (`BuiltIn` or `Plugin`), what it `Consumes`
  and `Produces` (events, audio), which `EventClass`es it `Handles`, and its parameters. It is data; a
  project can name a device whose implementation is not installed.
- A `DeviceInstance` is one configured occurrence: a stable `DeviceId`, a `DeviceReference` to its
  definition (with the last-seen name and version), parameter values (normalized 0 to 1), bypass, and
  an opaque `PluginState`. Plugin identity (the definition) and instance identity stay distinct.
- A `DeviceChain` is an ordered, independently identified list of instances with exactly one owner: a
  track (its device strip; at most one per track) or a free-standing rack. Ownership is not routing:
  any number of tracks can route into a rack, and the rack keeps one owner and one instrument instance.
- Presets (`DeviceChainPreset`, file format `bluestone-chain-preset` v1) are templates **without IDs or
  connections**. Loading one always creates new instances; moving a chain keeps its identity.
- The built-in definitions (`BuiltInDevices`: Transpose, Event Filter, Arpeggiator, and the Note and Audio Receivers
  of ADR 0037) are domain data.
- A definition may declare **input ports** (`Inputs`) that connections end at (ADR 0023): a receiver's `receive`, a
  plugin's `sidechain`. A device with an event input gets what it receives added to what it puts out by the
  `ChainRunner`, which asks the evaluation (`IChainReceiver`) at that device, so a source earlier in the same chain is
  the events seen there in the same run. The Note Receiver's own processor passes the chain's events (mix) or drops
  them (replace); bypassed, it brings nothing in.

**Processing (`Bluestone.Signal`).**

- Sequential: each device receives the previous device's output, never a fresh copy of the track's events.
- Host-managed passthrough by event class: the `ChainRunner` gives a device only the classes in its
  `Handles`; everything else goes around it and is merged back in canonical order (tick, phase, origin
  track, index, ADR 0003). Only an explicit filter (the Event Filter device) removes events.
- A bypassed device is transparent. An instrument consumes the classes it handles. A device with no
  in-process implementation (a plugin, or a definition that is not installed) passes events through
  unchanged, with a diagnostic; it is never loaded into Bluestone's process (ADR 0025).
- `ISignalProcessor` works on a tick window and appends to a reusable `SignalBuffer`; parameter changes
  split the window so automation lands on its tick. Buffers are reused: a passthrough chain allocates
  nothing after warm-up (tested). Processors may allocate only the new events they create.
- Built-in event processing runs when the playback plan is compiled, not on the real-time thread, so
  the real-time path still only dispatches a prepared plan (ADR 0006). Plugins run at run time in
  worker processes.

## Consequences

- Reordering or moving a device changes nothing that refers to it; refused moves keep routing valid
  (`DeviceCommands`, ADR 0023).
- XG/GS SysEx and controllers survive any note processor (scenario 6).
- Software instruments receive merged, ordered event feeds (`SoftwareInstrumentFeed`), but there is no
  audio engine: they do not sound yet, and Bluestone says so.
- Built-in processors that need state across blocks (the arpeggiator) must produce the same output
  whatever the block size; tests hold them to it.
- Tests: `ChainRunnerTests`, `BuiltInProcessorTests`, `SignalGraphTests`, `DeviceAndRoutingCommandsTests`,
  `ChainPresetSerializerTests`, `Domain/Devices/*`.
