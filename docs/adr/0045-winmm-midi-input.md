# 0045. Windows MIDI input and WinMM endpoint polling

- Status: Accepted
- Date: 2026-11-03

## Context

WinMM output existed, but `OpenInputAsync` was unsupported, preventing MIDI recording, hardware thru, and measuring MIDI arrival on Windows. WinMM identifies ports by index and does not provide endpoint-change notifications. MIDI Services supplies software loopback ports, but Bluestone's Windows adapter remains WinMM for compatibility with every Windows version.

## Decision

- Implement input in `WinMmProvider` with `midiInOpen` and a function callback. Packed short messages and `MIM_LONGDATA` chunks are fed through one `MidiStreamParser` per input.
- Prepare eight reusable 8 KiB native buffers for system-exclusive input. Requeue completed headers in the callback; the parser bounds a complete message at 65,536 bytes.
- Convert WinMM's millisecond input timestamp, relative to `midiInStart`, to Bluestone's monotonic clock. The timestamp resolution is therefore 1 ms; it is not an arrival-time hardware clock.
- Poll input and output endpoint snapshots once per second and raise `EndpointsChanged` when they differ.
- Keep name-based IDs, with an `input:` prefix for inputs and numeric suffixes for duplicate names. WinMM's index-based identity is not stable across device changes.
- Keep WinMM as the Windows MIDI API layer for now: no Windows MIDI Services adapter, virtual Bluestone port, or MIDI 2.0 support is added.

## Consequences

- Windows input, recording, and thru use the same `IMidiInput` contract as Core MIDI and ALSA.
- A device-list change can take up to one second to reach endpoint consumers. An already-open WinMM handle can still fail independently.
- The input buffer pool and parser are bounded and reused; MIDI callbacks must not wait on the UI or disk.
- The automated Windows integration tests exercise short messages, SysEx, timestamps, disposal, and endpoint changes through the Windows MIDI Services MIDI 1.0 loopback. They do not establish physical-device clock accuracy or hardware hot-plug behavior.
