# 0012. Performance baseline: managed scheduling with OS real-time policy, no native code

- Status: Accepted
- Date: 2026-10-04

## Context

The brief allows native scheduling only if measurements show it is needed. This ADR records the
first measurements and the decision they support.

**Machine:** Apple M5 Max (18 cores), 64 GB, macOS 27.0.1, .NET 10.0.8, Release build.
**Tools:** `src/Bluestone.Benchmarks` (BenchmarkDotNet 0.15.8, ShortRun, so indicative rather than
precise) and its `--jitter` probe, which plays dense material on the real playback thread with the
system clock: 16 tracks of sixteenth notes plus controller automation, about 385 messages/s.

## Measurements

### Scheduler (micro-benchmarks)

| Operation | Mean | Allocated |
| --------- | ---: | --------: |
| `Pump` per 1 ms step, immediate endpoints | 30 ns | 0 B |
| `Pump` per 1 ms step, scheduled endpoints | 34 ns | 0 B |
| `TempoMap.TimeAt` (500 tempo changes) | 6 ns | 0 B |
| `TempoMap.TickAt` | 16 ns | 0 B |
| Plan compile, 16 × 1,000 notes + automation | 2.2 ms | 7.9 MB |
| Plan compile, 16 × 10,000 notes + automation | 30 ms | 103 MB |

### Real-time dispatch lateness (`--jitter`, immediate endpoints)

Lateness means how much later than due a message was handed to the endpoint. Endpoints with
scheduled delivery (Core MIDI destinations) received every message ahead of time in all runs.

| Playback thread scheduling | Runs | p99 | Late (> 2 ms) per 3,888 msgs | Max |
| -------------------------- | ---- | --: | ---------------------------: | --: |
| Default (`ThreadPriority.Highest`) | 6 × 10 s | ≤ 1 ms | 32–112 | 2.2–9.0 ms |
| Earlier wake-up (3–4 ms) + spin | 6 × 10 s | ≤ 1 ms | 0–80 | 1.1–8.8 ms |
| User-interactive QoS | 3 × 10 s | ≤ 1 ms | 32–48 | 4.7–5.1 ms |
| **Real-time time-constraint policy** | 3 × 10 s | ≤ 0.25 ms | **0** | 0.3–2.0 ms |
| Real-time policy, 60 s (23,088 msgs) | 1 | ≤ 0.25 ms | 32 (two wake-ups) | 2.0 ms |

No garbage collection happened during any playback run.

### Files (16 tracks × 10,000 notes + automation, 320,000 events)

| Operation | Mean | Allocated |
| --------- | ---: | --------: |
| Import MIDI | 120 ms | 109 MB |
| Export MIDI | 31 ms | 69 MB |
| Save project | 70 ms | 624 MB |
| Open project | 681 ms | 1.0 GB |
| Load the General MIDI profile | 75 µs | 161 KB |

## Decision

- **No native scheduler.** The managed scheduler costs tens of nanoseconds per pump and never
  allocates. The remaining jitter came from how the OS scheduled the thread, not from managed code.
- **Use the OS real-time policy on macOS.** `PlaybackThread` accepts a platform setup hook. The
  desktop app gives the playback thread the Mach time-constraint policy (2 ms of computation per 5 ms),
  falling back to user-interactive QoS. That removed multi-millisecond wake-up delays in measurement.
- Windows and Linux need their own measurements when their adapters exist (MMCSS, SCHED_FIFO).

## Consequences and follow-ups

- **Plan compile cost** grows with project size: about 30 ms after each edit for 320,000 events.
  That is acceptable for now; incremental (per-track) compilation is the optimisation if needed.
- **Project open/save** for very large projects is the slowest path, dominated by the JSON DOM
  reader and indented output. A streaming reader, or a compact event encoding in a new format
  version, should come before projects of this size are common.
- A real-time thread is demoted by the kernel if it exceeds its budget, which is why the playback
  thread must keep blocking for long waits. The 1.5 ms spin window stays within the budget.
- These numbers come from one machine with ShortRun statistics. Re-run the benchmarks before relying
  on small differences, and add hardware loopback measurements (docs/hardware-test-plan.md).
