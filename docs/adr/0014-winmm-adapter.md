# 0014. Windows WinMM output adapter in managed code

- Status: Accepted; input and endpoint polling amended by 0045
- Date: 2026-10-04

## Context

Bluestone had no MIDI adapter on Windows, so only the built-in monitor output was available. The
brief names Windows MIDI Services as the primary Windows adapter, with WinMM as the fallback.

- **Windows MIDI Services** schedules messages and supports MIDI 2.0. It needs its own runtime
  installed on the machine and a WinRT SDK package in the build.
- **WinMM** is present on every Windows version, needs no dependency, and every MIDI driver still
  exposes it. It cannot schedule: `midiOutShortMsg` delivers immediately. Devices are identified only
  by an index, which shifts when devices are added or removed. It has no device-change
  notifications.

ADR 0013 made immediate delivery on Windows accurate to about 1 ms (p99), so an adapter that sends
when due is now usable.

## Decision

- **Ship WinMM first**, as `WinMmProvider` in `Bluestone.Platform.Windows`: managed `LibraryImport`
  only, behind `IMidiEndpointProvider`, with no new package dependencies. Windows MIDI Services
  follows as its own adapter and becomes the preferred provider when its runtime is present.
- **Delivery class `None`.** Every WinMM endpoint reports `EndpointCapabilities.None`, so the
  playback thread waits and sends each message when due.
- **Endpoint IDs are device names**, with `#2`, `#3` … for repeated names. This is as stable as WinMM
  allows; a device that changes its name looks like a new endpoint, which routing already handles
  (ADR 0008).
- **Transport** comes from the device's reported technology: MIDI ports are `Physical`, software
  synthesizers are `Software`, anything else is `Unknown`.
- **Send** packs channel, system common and realtime messages into `midiOutShortMsg`, with no
  allocation, locking or logging per call. Driver errors map to `QueueFull`, `Disconnected`
  (device gone) or `Closed` (faulted). Disposal waits for any in-flight send, resets the device so
  no notes are left sounding, and closes it.

## Consequences

- **Not yet supported**, each a follow-up slice:
  - System exclusive: rejected for now. It needs prepared long-message buffers that are completed
    asynchronously without blocking the playback thread. Needed before the QY100 profile.
  - Inputs: `OpenInputAsync` throws `EndpointUnavailableException` (needed for recording).
  - Hot-plug: `EndpointsChanged` is never raised. Unplugging is noticed on the next send, which
    reports `Disconnected`.
- Opening the Microsoft GS Wavetable Synth can be slow (the five WinMM integration tests took about
  12 s in the VM). `OpenOutputAsync` completes synchronously, so callers should not open outputs on
  the UI thread in a tight loop.
- Integration tests open only software synthesizers and never send a sounding message, in line
  with the rule that unattended tests do not send to hardware.

## Amendment: input and endpoint changes (ADR 0045)

WinMM input and endpoint-change polling were added in ADR 0045. The original decision to use WinMM for output remains;
the Windows MIDI Services adapter and virtual Bluestone output remain deferred.
