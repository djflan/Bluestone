# 0016. Linux ALSA sequencer output adapter in managed code

- Status: Accepted
- Date: 2026-05-20

## Context
Linux had no MIDI output; only the built-in monitor. The ALSA sequencer is present on every desktop
distribution, reaches hardware ports, software synthesizers and other applications, and lets an
application publish its own ports. PipeWire and JACK bridge to it.

## Decision
- `Bluestone.Platform.Alsa` calls `libasound.so.2` through `LibraryImport`; no native shim, matching
  Core MIDI (ADR 0011) and WinMM (ADR 0014).
- Messages are sent with `snd_seq_event_output_direct` (no ALSA queue), so endpoints are in the
  immediate delivery class and Bluestone's playback thread owns timing.
- Bytes are converted to sequencer events by ALSA's `snd_midi_event_encode`, which handles system
  exclusive, so SysEx needs no separate path. Messages up to 65,535 bytes are accepted; nothing is
  allocated per send. `EAGAIN` maps to `QueueFull`; a vanished port to `Disconnected`.
- Endpoint IDs are `client name:port name` with `#n` for repeats, because ALSA client and port numbers
  are reassigned when devices or applications restart.
- Enumeration uses a separate sequencer handle from sends. A hidden source port is used for direct
  sends; `CreateVirtualOutput` publishes subscribable ports (the desktop publishes *Bluestone Out*).
- Input and hot-plug announcements are deferred.

## Consequences
- The adapter was built on Windows and its integration tests skip there; it must be validated on
  Linux (including the 28-byte `snd_seq_event_t` layout) before it is called supported.
- If libasound or `/dev/snd/seq` is missing, provider construction fails and the desktop falls back to
  the monitor.
- Integration tests use only Bluestone's virtual port and *Midi Through*, never hardware.
