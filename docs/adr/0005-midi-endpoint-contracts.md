# 0005. MIDI endpoint contracts

- Status: Accepted
- Date: 2026-10-04

## Context

Bluestone must send to Core MIDI, Windows MIDI Services, ALSA, network sessions, and test doubles
behind one contract, from a playback thread that must never block or allocate. Endpoints appear,
disappear, and get renamed while a project is open. Device knowledge must never leak into endpoints.

## Decision

- `IMidiEndpointProvider` owns discovery and capability reporting for one adapter family. It exposes
  `EndpointDescriptor`s and raises `EndpointsChanged` on hot-plug. Opening is asynchronous and
  cancellable; failures raise `EndpointUnavailableException`.
- `EndpointId` is `(provider, opaque stable value)`, for example a Core MIDI unique ID. It is never a
  port index. Display name, manufacturer, and model are *hints* for rebinding, never profile identity.
- `IMidiOutput.Send(ReadOnlySpan<byte>, MidiTimestamp)` takes exactly one complete MIDI 1.0 message
  and returns a `SendResult` rather than throwing. Adapters validate messages with `MidiWire.Classify`,
  so malformed bytes never reach a device. Future timestamps are honored only by endpoints that
  declare `ScheduledDelivery`; for the rest, the scheduler waits and sends immediately.
- Handles move one way through `Open → Disconnected/Faulted → Closed` and report transitions via
  `StateChanged`. A disconnected handle stays disconnected; reconnection means opening a new handle
  once the endpoint reappears.
- All time is in `IMonotonicClock` time. `VirtualClock` makes scheduling deterministic in tests.
- `LoopbackMidiProvider` is the reference test double: a virtual bus with hot-plug, rename, fault
  injection, and recording.

MIDI 2.0/UMP is not modelled yet. When it is, it will be a separate send path with its own capability
flag, not a reinterpretation of MIDI 1.0 bytes.

## Consequences

Adapters carry the burden of honest capability reporting (`TimingDescription`, capability flags).
Consumers must handle `SendResult` on every send, which keeps the playback thread free of exceptions.
