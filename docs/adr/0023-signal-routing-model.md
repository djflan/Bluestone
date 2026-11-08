# 0023. One signal routing model: connections, external instruments, and mixer channels

- Status: Accepted; supersedes 0008
- Date: 2026-10-10

## Context

ADR 0008 gave each track one `TrackRoute`: one profile, one endpoint, one channel, one transposition,
one voice. That coupled a track to exactly one MIDI output and made shared instruments, track-to-track
routing, taps after a device, mixer channels, and hybrid hardware/software setups impossible. Its
lasting insight, that *what* an instrument understands (profile) and *where* messages go (endpoint)
are independent and resolved at run time, must be kept.

## Decision

- **One list of `SignalConnection`s is the routing model.** A connection has a stable ID, a kind
  (`Events` or `Audio`), a source, a destination, a `ChannelMapping`, and an optional voice. Sources
  are **junctions** of a chain (ADR 0037): its input before the first device (`ChainInput`, named by the
  chain), the point after one device (a tap), or its output (the track or rack). Destinations are a track's
  or rack's input, an **input port of a device** inside a chain (`DeviceInput`: device + port, such as a
  receiver's `receive` or a plugin's `sidechain`, declared by `DeviceDefinition.Inputs` with the one kind it
  takes), a part of an external instrument (instrument + port), a mixer channel, or the master.
  One-to-many and many-to-one are ordinary. The device strip's indicators and the routing inspector both
  edit this list and nothing else.
- **External instruments are project entities** (`ExternalInstrument`): a name, a profile reference
  (stated once per instrument, not per track), an operating mode (never inferred from what a device
  supports), and logical ports, each with an `EndpointReference`. A part is a port plus the channel a
  connection's mapping gives it.
- **Channels belong to connections, not to tracks or clips.** `ChannelMapping` keeps the original
  channel, forces one, remaps some, and/or passes only some (filtering first). Clips are never changed
  to retarget a part. The channel is a MIDI 1.0 channel on a logical port; MIDI 2.0 groups belong to the
  port, so this model does not widen for them.
- **Mixer channels are their own list**, independent of tracks; audio connections end at a channel or
  the master. Hardware tracks need no mixer channel.
- **Validation** (`SignalRoutingValidator`) checks existence, the role of each end, whether the signal
  can be present at the source (an event tap after an instrument that consumed everything carries
  nothing), whether the destination accepts it (a device input's port must exist and take this kind; a
  device that is not installed keeps its connections), duplicates, mapping consistency, and **feedback
  cycles**, which are not allowed. Cycles are found between owners (tracks, racks, mixer channels; a
  junction or device input belongs to its chain's owner), which is conservative: owners are evaluated
  whole. A connection from a junction into a device input of the **same** chain is ordered by the chain
  instead, and allowed only from a junction at or before that device; from the device's own output or
  later it would feed back. Commands refuse edits that add an error (`CommandRefusedException`);
  loading never fails on routing, and the evaluator leaves invalid connections out and reports them.
- **Resolution stays as in ADR 0008**, per instrument port: profiles by ID only; endpoints by stable
  key, then by remembered name within the same provider (bound by name, asking for confirmation),
  missing, or ambiguous. A missing profile never prevents playback.
- **Playback** evaluates the signal graph (`SignalGraph.Evaluate`), gives each distinct available
  endpoint an output slot, and compiles each external part into the plan, keyed so the order is the
  pre-refactor order (tick, phase, track index, index). Format 3 projects send byte-identical MIDI.
- **The inspector keeps its simple fields.** `TrackOutputs` reads and writes "output, instrument,
  channel, transpose, voice" as an instrument (reused per endpoint and profile), the track's first
  connection to an external instrument, and a Transpose device in its chain. Format 3 routes migrate
  through the same code (ADR 0027).
- **Live notes** (audition, thru, on-screen keyboard, metronome) follow the track's *live route*: the
  nearest playable instrument through its connections, with the forced channel and the Transpose devices
  on the way. Other devices are applied when the plan is compiled, not to live notes.

## Consequences

- `TrackRoute`, `RoutingTable`, `ResolvedRoute`, and `PlanTrackBinding` are removed.
- A route for a track that no longer exists is kept as a connection and reported by validation.
- Feedback routing and a visual node graph are deferred; the routing inspector and device-strip
  indicators cover ordinary routing.
- Tests: `SignalRoutingValidatorTests`, `SignalGraphTests`, `TrackOutputsTests`, `RouteResolverTests`,
  `PlaybackRoutingTests`, `Format3EquivalenceTests`, `AcceptanceScenarioTests`.
