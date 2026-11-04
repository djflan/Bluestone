# 0047. Windows precise waits and native timing evidence

- Status: Accepted
- Date: 2026-11-03

## Context

ADR 0013's 1 ms timer-resolution request and MMCSS registration improved ordinary playback waits, but the final approach to a MIDI deadline still spun on Windows. Plugin workers also used `Thread.Yield` while waiting for blocks outside macOS. Windows now has a high-resolution waitable timer, and native x64 measurements are available from the system MIDI loopback and installed audio devices.

## Decision

- Keep `timeBeginPeriod(1)` and MMCSS `Pro Audio` for playback. Use a thread-local `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer for the final playback sleep, falling back to a bounded spin if the timer cannot be created or armed.
- Register Windows plugin-processing threads with MMCSS. Use the high-resolution waitable timer during idle waits: the 64-frame probe had fewer steady-state misses with the timer than with `Thread.Yield`, although neither path met the zero-miss target at that size. Fall back to `Thread.Yield` if timer creation or arming fails.
- Keep the engine process opt-in on Windows. The engine's live-input probe measured 9.5 ms average and 16.8 ms maximum added latency through WinMM software loopback and WASAPI, above ADR 0043's 2 ms target. Revisit the default only after suitable hardware/interface measurements meet the target.

## Measurements

On a native x64 Windows desktop, no pressure, with a 10-second `--jitter` run and WinMM immediate delivery:

| Final approach | p50 | p95 | p99 | Maximum | Messages >2 ms late | Dropped |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| High-resolution waitable timer | 0.32 ms | 0.51 ms | 0.87 ms | 0.902 ms | 0 | 0 |
| Spin | 0.01 ms | 0.01 ms | 0.24 ms | 3.067 ms | 32 | 0 |

A three-second-per-phase `--midi-loopback` run sent and received 1,216 messages with no drops. Median immediate WinMM loopback hop was 0.077 ms; median thru round trip was 0.312 ms. The path uses a Windows MIDI Services software loopback, not a physical MIDI interface.

A five-second buffered WASAPI run at the default Realtek output had zero missing, late, xrun, gap, or queue-underrun events; reported output latency was 31.958 ms. A historical, explicitly enabled local ASIO build used the Realtek ASIO driver at its default 960 frames (20 ms) and reported zero late callbacks or xruns; standard Windows builds do not include ASIO pending licensing. Neither result is evidence of acoustic latency or a listening test.

The plugin-pipeline probe on Windows, depth 1, no pressure, compared the worker idle wait:

| Buffer | Wait | Steady blocks missed | Startup blocks not processed | Call p99 | Call max |
| ---: | --- | ---: | ---: | ---: | ---: |
| 128 frames | High-resolution timer | 0 | 3 | 0.51 ms | 1.22 ms |
| 64 frames | High-resolution timer | 2 | 6 | 0.53 ms | 0.80 ms |
| 64 frames | `Thread.Yield` | 19 | 40 | 0.18 ms | 0.82 ms |

The timer reduces missed blocks versus yielding in these runs, but it does not meet the 64-frame zero-miss target. At 128 frames it had no steady-state misses, with three cold-start blocks reported separately. These are short, no-pressure measurements with Bluestone's reference plugin, not evidence for every third-party worker or audio driver.

## Consequences

- The precise wait reduces the worst observed immediate dispatch lateness versus spinning on this machine, while its median is higher. Continue to judge release claims by the probe and configuration, not by a single number.
- The plugin worker keeps the timer path because it missed fewer blocks than yielding in the 64-frame comparison; 64-frame depth-1 plugin playback remains unverified against the zero-miss target.
- The software-loopback live-input result does not meet the live target; the Windows engine remains opt-in.
- Physical-device unplug recovery, a hardware MIDI clock, and acoustic monitoring remain manual checks in `docs/hardware-test-plan.md`.
