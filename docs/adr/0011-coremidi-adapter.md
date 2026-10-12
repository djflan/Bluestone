# 0011. macOS Core MIDI adapter in managed code

- Status: Accepted
- Date: 2026-10-04

## Context

macOS is the development platform and the first target for hardware testing. Core MIDI is a C API.
The brief allows native code only where measurements justify it and requires a narrow C ABI if it
is introduced.

## Decision

- Implement `Bluestone.Platform.CoreMidi` in C# with source-generated P/Invoke (`LibraryImport`) and
  `[UnmanagedCallersOnly]` callbacks. No native library is built or shipped. Nothing has been
  measured that would justify one.
- **Identity.** Endpoint IDs are Core MIDI unique IDs. Display name, manufacturer, and model are
  reported as hints. Offline endpoints are hidden.
- **Classification.** Endpoints without an entity are virtual (application) ports; IAC is virtual;
  RTP/network drivers are network; everything else is physical.
- **Output.** `MIDISend` with timestamps converted from Bluestone's clock to host time, relative to
  "now" on both clocks. Destinations therefore advertise `ScheduledDelivery`, and the playback
  engine hands messages over 20 ms early. Packet lists are built on the stack (or a pooled buffer
  for large SysEx), so sending does not allocate. Messages are validated before they reach CoreMIDI.
- **Virtual output.** Bluestone publishes a virtual source ("Bluestone Out") with a stable unique ID, so
  software synths and DAWs can receive from it. `MIDIReceived` delivers immediately, so the port does
  not claim scheduled delivery.
- **Input.** `MIDIInputPortCreate` read callbacks parse packets (honouring arm64 packet alignment)
  with the platform-independent `MidiStreamParser`, and stamp messages with converted host time.
- **Notifications.** Core MIDI delivers setup notifications for all clients on the run loop that was
  current when the process *first* created a client. Every client is therefore created on one
  process-wide run-loop thread that never exits. This was found by an integration test, where
  disposing the first provider silently stopped notifications for all later ones.

## Consequences

- Input callbacks run managed code on Core MIDI's thread. A GC pause can delay delivery, but not the
  recorded timestamp, which Core MIDI assigns. Record-path latency must be measured before
  recording ships.
- MIDI 2.0 (UMP, `MIDISendEventList`) is not used yet; `MIDISend` remains supported by macOS.
- Default tests use only virtual ports. IAC tests run when the bus is enabled. Hardware behaviour is
  verified manually (see docs/hardware-test-plan.md).
