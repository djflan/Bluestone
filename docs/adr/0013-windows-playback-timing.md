# 0013. Windows playback timing: 1 ms timer resolution and MMCSS

- Status: Accepted
- Date: 2026-10-04

## Context

ADR 0012 left Windows scheduling to be measured. On Windows the playback thread's timed wait
(`WaitHandle.WaitOne(timeout)`) wakes on the system timer tick, which is 15.6 ms by default. The
1 ms early wake-up and 1.5 ms spin window in `PlaybackThread` cannot absorb that, so
immediate-delivery endpoints (the class WinMM will fall into) were served up to a whole tick late.

**Machine:** Windows 11 ARM64 in a Parallels VM on an Apple silicon Mac, .NET 10 SDK 10.0.401,
Release build. **These numbers are provisional:** a VM's timer and scheduler behaviour says little
about native Windows. Re-measure on native hardware before relying on them.
**Tool:** `src/Bluestone.Benchmarks -- --jitter 10 [--timer|--promote|--realtime]`, the same probe and
material as ADR 0012 (3,888 messages per 10 s run).

## Measurements (immediate endpoints)

| Playback thread setup | p50 | p95 | p99 | Late (> 2 ms) | Max |
| --------------------- | --: | --: | --: | ------------: | --: |
| Default (`ThreadPriority.Highest`) | ≤ 10 ms | ≤ 20 ms | ≤ 20 ms | 2,928–2,976 | 15.2–15.5 ms |
| MMCSS "Pro Audio" only (`--promote`) | ≤ 10 ms | ≤ 20 ms | ≤ 20 ms | 2,736 | 14.5 ms |
| 1 ms timer resolution only (`--timer`) | ≤ 0.25 ms | ≤ 0.5 ms | ≤ 1 ms | 32 | 2.8 ms |
| **Timer resolution + MMCSS (`--realtime`)** | ≤ 0.25 ms | ≤ 0.25 ms | ≤ 1 ms | 32 | 3.2 ms |

Scheduled-delivery endpoints received every message ahead of time in all runs, and no garbage
collection happened during any run.

## Decision

- **Request a 1 ms timer resolution** (`timeBeginPeriod(1)`) when the playback thread starts. This
  is the change that matters: it moves p99 from about 20 ms to 1 ms.
- **Also register the playback thread as an MMCSS "Pro Audio" task.** It made no measurable
  difference in the VM, but it is the standard priority class for audio threads and protects
  against CPU contention, which the probe does not create.
- Both live in the new managed-only `Bluestone.Platform.Windows` project, called from
  `PlatformProviders.ConfigurePlaybackThread`. No native code and no change to the platform-neutral
  `PlaybackThread`.

## Consequences

- The timer request lasts for the life of the process and raises power use slightly while Bluestone
  runs. Windows releases it when the process exits.
- Windows 11 may ignore timer-resolution requests from processes whose windows are minimised or
  hidden. If background playback drifts, switch the long wait to a high-resolution waitable timer
  (`CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) via a platform wait hook in `PlaybackThread`.
- 32 messages per run were still more than 2 ms late (one wake-up). Re-measure on native Windows
  and with the Windows MIDI adapter before tightening this further.
