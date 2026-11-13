# 0049. Audio and plugin-worker scheduling, and Automatic sample rate and block size

- Status: Accepted; amends 0032 and 0046
- Date: 2026-11-12

## Context

On Windows, Bluestone clicked and popped on both WASAPI and ASIO while Bitwig, on the same machine and output, played
cleanly. Bitwig's settings were WASAPI shared, sample rate Automatic, and block size "auto" (480 samples, 10 ms).
Bluestone's were 44.1 kHz with 256-frame blocks.

There were three causes:

- **The WASAPI device thread had normal priority.** `cpal` promotes its WASAPI threads only with its `realtime`
  feature, which Bluestone does not enable, so they ran at normal priority. Measured with the callback probe on a
  Realtek output, with every core busy at normal priority (`--pressure cpu`):
  - At 44.1 kHz with 256-frame blocks: a render took up to 10 ms (the budget is 10 ms), 1 callback was late, and 7
    callbacks came after a gap, in 10 s.
  - At 48 kHz with 480-frame blocks: the longest render took 20 ms, 2 callbacks were missing, and 12 came after a gap.
  - ASIO, whose driver runs its own high-priority thread, was clean under the same load.
- **The rate didn't follow the device.** A fixed 44.1 kHz on a device the Windows mixer runs at 48 kHz makes Windows
  resample in shared mode, and callbacks become about 441 frames.
- **The engine block didn't follow the device callback.** The engine rendered fixed blocks of at most 256 frames, so a
  441- or 960-frame callback alternated between rendering 1 and 2 blocks (or 3 and 4). That doubles the work, and the
  plugin collect waits, in some callbacks.

The dropouts were also invisible: the Audio settings showed driver underruns and render-ahead underruns, but not late
callbacks or late renders.

Some plugins popped far more than others: Dune 3 most of all, while Bitwig played it cleanly. Two more causes were
measured, by hosting Dune 3 in a real worker at 48 kHz with 480-frame blocks and a six-note chord, and timing each
block from submit to done:

- **The plugin worker process was throttled.** It has no visible window and plays no audio itself, so Windows 11 treats
  it as background work: it may slow the process down and ignores its timer resolution. The worker also never asked for
  a 1 ms timer. Dune 3's own threads wait with timeouts, so they woke on the ~16 ms tick:
  - Before: 588 of 1,479 blocks took 16–17 ms (p50 0.62 ms, p95 16.64 ms), longer than the 10 ms period.
  - Surge XT, on the same machine, never took more than 2.91 ms.
- **Back-to-back blocks only got the 2 ms collect wait.** Bluestone's own process renders ahead of the device, and when
  its render chunk is larger than the engine block (a saved 1024-frame buffer, or catching up after a delay), it
  renders several blocks back to back.
  - Pipelining gives a block a full period only when the blocks are a period apart. The second of two back-to-back blocks
    has just the 2 ms collect wait, and a block that misses it is played as silence.
  - Dune 3 at depth 2, two blocks every 20 ms: 121 of 1,478 blocks dropped.

## Decision

- **Audio priority for backend threads that lack it.** On the first callback of an output or input stream on a Windows
  host other than ASIO, the native layer registers the callback thread with MMCSS as a "Pro Audio" task. If MMCSS
  refuses, it falls back to `THREAD_PRIORITY_TIME_CRITICAL`. It calls `avrt` directly rather than enabling `cpal`'s
  `realtime` feature, which would add an MPL-2.0 dependency and report a refused promotion as a stream error.
  - ASIO drivers' threads and the other platforms' backend threads are left as they are.
  - The result is reported as the stream status's `callback_thread_policy`, in the struct's former reserved field, so
    the struct's size is unchanged.
- **Automatic sample rate and block size, by default, on every host.**
  - In `AudioSettings`, `SampleRate` and `BufferFrames` default to 0 (`AudioSettings.Automatic`), which opens the
    device at its own rate and default period: the Windows mixer's rate and 10 ms period in WASAPI shared mode, the
    driver's buffer on ASIO, and Core Audio's and ALSA's defaults.
  - Settings that were already saved keep their values.
  - When the device doesn't offer the chosen rate, the choice becomes Automatic.
  - On WASAPI, a fixed rate other than the mixer's shows a warning that Windows resamples it.
- **The engine block follows the device callback** (`AudioBlockSize.For`). The block is the device's callback size when
  it is known and at most `AudioBlockSize.DefaultMaximum` (1024). Otherwise it is the largest divisor of the callback
  that fits, otherwise the requested size.
  - Plugins are prepared for the stream's rate and block: the prepare callback now takes an `AudioFormat`, and
    `PluginDeviceHost.SetFormatAsync` restarts instances when either changes.
  - This keeps the existing invariant that a plugin's maximum block is the engine's block, which the pipeline-latency
    calculation relies on.
- **Dropouts are shown.** The Audio settings page shows what the device runs at ("48 kHz · 480 samples / 10.00 ms") and
  a health line. The health line gives late device callbacks, renders longer than their block, driver underruns, and
  render-ahead underruns, plus the callback thread's priority. It turns amber when anything dropped, or when the thread
  could not be promoted.
  - The first callback's warm-up (the promotion, and the first call into .NET) is not counted as a dropout.
  - The engine wire protocol is version 11, because `AudioCounters` now carries the callback thread policy.
- The settings page uses DAW terms: "Driver", "Sample rate" (Automatic, or a rate in Hz), and "Block size" (Automatic,
  or a size in samples).
- **The plugin worker, the engine, and Bluestone opt out of Windows power throttling.** Each process turns off
  execution-speed and timer-resolution throttling with `SetProcessInformation(ProcessPowerThrottling)`, then asks for a
  1 ms timer.
  - The worker does this at startup, in `ProcessingThreadScheduling.PrepareProcess`.
  - The app and the engine do it through `ThreadScheduling.RequestHighTimerResolution`.
- **A render-ahead thread lets plugin collects wait for longer.** Before each render, it allows collects that a device
  callback doesn't bound to wait until half the time its queue covers the device for (`CollectBudget.Allow`, in whole
  device buffers). The 2 ms collect wait remains the minimum.
  - A hung worker can therefore never drain more than half the cushion per render, and the heartbeat still restarts it.
  - Collects in the engine's device callback keep their shared budget (ADR 0044 step 7).

## Consequences

- With the same `--pressure cpu` load, WASAPI at both 44.1 kHz/256 and 48 kHz/480 had no late callbacks, gaps,
  missing callbacks or xruns, and its longest render was 0.53 ms and 0.78 ms. The thread was registered as an MMCSS
  "Pro Audio" task.
  - This is a measurement of the silent callback probe, not a listening test.
  - The hardware test plan has the command to repeat it.
- Plugins restart once when audio first opens with a block other than the one they were created for, as they already
  do for a sample-rate change. Their status shows the restart.
- Dune 3 after both plugin fixes:
  - With the worker unthrottled, no block went over the period: p50 1.46 ms, p95 2.50 ms, max 3.06 ms.
  - With the allowance, none of the 1,478 back-to-back blocks were dropped.
  - These are timings of the worker's processing, not a listening test.
  - About a quarter of Dune 3's blocks still take longer than 2 ms. That's harmless when blocks are a period apart, but
    in the engine process's device callback at depth 1 (a live-played instrument) they are late, and the instrument
    falls back to depth 2 as before.
- A device whose callback size varies is still served from the last rendered block, as before. Only the block it opens
  with is matched.
- Choosing WASAPI Exclusive mode (Bitwig's other mode) isn't possible: `cpal` 0.18 opens shared mode only.
- ASIO pops on the Realtek driver weren't reproduced by the probe, even under load. If they remain after the block
  alignment, the Realtek ASIO driver, which wraps WASAPI, is the next suspect. A real interface's driver is the
  reference (ADR 0046).
