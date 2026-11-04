# 0043. Timing targets, how they are measured, and how C# meets them

- Status: Accepted; amends 0012 (playback thread's final approach), 0026 (no Rust render path), 0032 and 0035 (where
  the real-time work runs); Windows measurements are recorded in 0047
- Date: 2026-11-03

## Context

Bluestone's first product priority is "tight timing, synchronization, latency handling, and accuracy". Until now the
claim had no numbers behind it that a contributor could check or fail. Release 1.0 Slice 0 asks for exactly that, and
ADR 0026 asks for a measurement before any real-time component moves from C# to Rust. Three questions were open:

1. What does "tight" mean, in numbers?
2. Does the current C# code meet those numbers, and where not, why?
3. Can tuning C# fix the misses, or does a component need Rust?

## Decision

### The targets

At 48 kHz, a 128-frame device buffer (2.67 ms), a Release build, 60-second runs on a quiet machine, under each load
below. "Max" is the worst single event in the run; p99.9 is reported next to it, and both must meet the target.

| Path | Metric | Target |
| ---- | ------ | ------ |
| MIDI output, immediate delivery (sent when due) | Lateness from due time to hand-off to the endpoint | ≤ 2 ms max |
| MIDI output, scheduled delivery (Core MIDI timestamps) | Arrival time at a receiving process minus the timestamp | ≤ 2 ms max |
| Live MIDI thru (input to output) | Round trip through Bluestone less two operating-system MIDI hops | ≤ 2 ms max |
| Live MIDI to audio | Input timestamp to heard, less the device's reported output latency and one buffer period | ≤ 2 ms max |
| Audio device stream | Xruns, late callbacks, missing callbacks, render-ahead underruns | 0 |
| Audio callback | Render time inside the device callback | ≤ the buffer period, max |

The live-audio target subtracts one buffer period because a note that arrives just after a callback must wait for the
next one in every host. The device's reported output latency already contains the hardware and safety offsets. So
"≤ 2 ms" means Bluestone adds less than 2 ms beyond what the chosen buffer size and audio device impose.

### The loads

- **none**: only the probe.
- **app**: a synthetic editing session in the same process. A 200 MB retained heap and about 50 MB/s of small
  allocations, a quarter of them kept in a rolling window of references (ADR 0032).
- **edit**: Bluestone's own work while a musician edits during loop playback. Every edit recompiles the playback plan
  (16 tracks × 1,000 notes, about 8 MB), four times a second.
- **isolated**: the probe runs in a child process (`--isolate`) while the parent process carries the **app** load. This
  is the experiment for a separate real-time engine process.

### How it is measured

`src/dotnet/Bluestone.Benchmarks` holds the probes. Each prints its metrics and writes a JSON report with the machine,
OS, runtime, GC settings, commit, load average, settings, distributions, targets, and verdicts. `--compare a.json b.json`
diffs two reports. Every probe takes `[seconds] [--pressure none|app|edit|churn|full] [--isolate]
[--child-pressure P] [--repeat N] [--report DIR]`.

| Probe | What it measures |
| ----- | ---------------- |
| `--jitter` | Dispatch lateness on the real playback thread, immediate and scheduled, from a 10 µs histogram |
| `--midi-loopback` | End to end through Core MIDI, with no hardware. A child process publishes a virtual destination and source and timestamps arrivals with the shared host clock. It covers scheduled delivery, the immediate operating-system hop, and live thru through `PlaybackEngine.SendNow` while dense material plays. |
| `--audio` | Render time around the call into .NET from the native callback, plus xruns, gaps, and missing callbacks, on the default device |
| `--live` | Input timestamp to heard, through the engine's heard-time model, with `--queue 0` for rendering in the callback |
| `--wake` | Oversleep of timed waits by primitive and thread policy |

`TimingStatistics` gained a 10 µs lateness histogram (`TimingSnapshot.FinePercentile`) so the playback engine can
state p99.9, not only the 0.25 / 0.5 / 1 / 2 ms buckets the status bar uses.

## Measurements

Apple M5 Max, macOS 27.2, .NET 10.0.300, built-in MacBook Pro speakers (4.9 ms reported output latency), playback
thread with the real-time time-constraint policy, 60 s per run unless stated. Reports are under
`TestResults/timing/` (not committed). Numbers are max, with p99.9 in brackets where they differ.

### Baseline: the code before this change

| Path | none | app |
| ---- | ---- | --- |
| MIDI immediate lateness | 2.86 ms ✗ | 5.90 ms ✗ |
| MIDI scheduled, Core MIDI loopback | 0.31 ms ✓ | 0.12 ms ✓ |
| MIDI thru added | 0.19 ms ✓ | 5.71 ms (4.35) ✗ |
| Audio, rendering in the callback | render 0.12 ms, 0 xruns ✓ | render 9.0 ms, 224 xruns ✗ |
| Audio, 1024-frame render-ahead queue | — | 0 xruns, 0 underruns ✓ (21 ms of queue) |
| Live added, rendering in the callback | 0.005 ms ✓ | 8.3 ms (6.5), 226 xruns ✗ |
| Live added, the 512-frame live queue (today's default) | — | 17.3 ms (14.1), 256 underrun frames ✗ |

Under **app** every GC suspended the real-time threads: pauses were 4.5 ms at the median and up to 13.7 ms.

### Round 1: tuning C# in one process

| Change | Result |
| ------ | ------ |
| `DOTNET_gcConcurrent=0` | Pauses 18 ms median, up to 65 ms ✗ |
| `DOTNET_GCgen0size` 1 MB / 4 MB / 64 MB | Pauses up to 8.3 / 8.3 / 41 ms ✗ |
| `GCLatencyMode.SustainedLowLatency` | Pauses up to 10.3 ms ✗ |
| Server GC (`DOTNET_gcServer=1`), 30 s | No collection ✓, but only postponed: |
| Server GC, 180 s | Four collections of 32–37 ms, 3 xruns ✗ |
| **edit** load instead of **app** | Live: pauses ≤ 1.3 ms, added max 0.36 ms ✓. MIDI (with a 320,000-event plan also retained): one 24 ms pause ✗ |
| Precise final approach on the playback thread (below), none | MIDI immediate max 3.85 ms (spin) → **0.12 ms** ✓ |
| The same, Core MIDI loopback with the production thru path | Thru added max 0.38 ms ✓ |

Wake-ups (`--wake`, 30 s): on the real-time thread `WaitOne`, `Thread.Sleep`, and `mach_wait_until` all woke within
0.08 ms; on a default thread they overslept by about 1 ms. `WaitHandle.WaitOne(TimeSpan)` has whole-millisecond
resolution and woke about 0.5 ms early at the median, which is why the playback thread spun the final approach.

No GC setting removes multi-millisecond pauses for a large, reference-heavy heap such as an editing session's.
Pauses are short when the retained heap is small and simple: about 0.5–1.3 ms. They grow when it is large or has
many references to scan.

### Round 2: the same C# in a process of its own

The child renders, dispatches, and receives in C# exactly as above; the **app** load runs in the parent.

| Path | Result |
| ---- | ------ |
| Audio in the callback, 64 / 128 frames | Render max 0.07 / 0.12 ms, 0 GCs, 0 xruns ✓ |
| Live added, 128 frames | Max 0.001 ms, 0 xruns ✓ |
| MIDI immediate lateness | Max 1.58 ms (p99.9 0.03 ms) ✓ |
| MIDI scheduled, Core MIDI loopback | Max 0.36 ms ✓ |
| MIDI thru added | Max 0.41 ms ✓ |
| Live added, with the engine compiling plans on its own small heap (**edit** in the child) | GC pauses ≤ 0.95 ms, added max 0.008 ms, 0 xruns ✓ |

### Re-test on the final code

Repeated 60-second runs with the **app** load in the other process. In the middle of the series the default output
device changed from the built-in speakers to a USB headset (Jabra Evolve2 50), so the 64-frame runs used the headset.

| Path | Runs | Result |
| ---- | ---: | ------ |
| Live added, 128 frames | 9 | Max 0.034 ms in the worst run; 0 GCs, 0 xruns in every run ✓ |
| Live added, 64 frames (headset) | 3 | Max 0.046 ms; 0 xruns ✓ |
| Audio in the callback, 128 frames (headset) | 2 | Render max 0.13 ms; 0 xruns ✓ |
| MIDI scheduled, Core MIDI loopback | 3 | Max 0.27 ms ✓ |
| MIDI thru added | 3 | Max 0.30 ms ✓ |
| MIDI immediate, measured after a warm-up (the loopback's second phase) | 3 | Max 0.21 ms ✓ |
| MIDI immediate, measured from a cold start (`--jitter`) | 3 | Max 2.05 / 1.86 / 1.14 ms, p99.9 ≤ 0.15 ms in two runs ✗ |

The cold-start outliers are not GC and not the operating system. They come from .NET tiered compilation: shortly after
code starts running, the runtime replaces it with optimized code, and installing that code briefly suspends managed
threads.

| `--jitter 60 --pressure none`, 3 runs each | Max per run |
| ------------------------------------------- | ----------- |
| Default (tiered compilation) | 0.024, 3.48, 3.31 ms |
| `DOTNET_TieredCompilation=0` | 0.057, 0.215, 0.055 ms |

The same effect explains the baseline's 2.86 ms immediate outlier with no load.

Re-measured later on the current code (precise final approach, decision 3) on a quiet machine, `--jitter 30`, 5 cold
starts each: the default reached at most 0.038 ms, `DOTNET_TieredCompilation=0` 0.082 ms, and `DOTNET_TieredPGO=0`
0.131 ms. Turning tiered compilation off did not slow the engine's start-up (main entered at 35–43 ms, against 27–36 ms;
one second rendered in 1.18–1.19 s, against 1.23–1.25 s). The earlier 3.5 ms pauses, taken while other work
shared the machine, did not reappear. No interim setting was needed in the application. The engine ships with NativeAOT
and has no JIT; a JIT build of the engine, for development, runs with tiered compilation off (ADR 0044 step 10).

### Audio loopback (added 2026-11-03)

`--audio-loopback` plays a single-sample click on a timestamped live note, as a live route would. The output device
is looped back to an input; the probe finds the click in the captured audio and takes the capture time from the input
stream's own timestamps. It compares that with the model's heard time: the callback's time, plus the device's
reported output latency, plus any render-ahead queue. The Rust audio layer gained an input stream for this (ABI 2).

BlackHole 2ch at 48 kHz, 128-frame buffer, 10 s, about 100 clicks per run:

| Mode | Model error, median | Spread (max − min) |
| ---- | ------------------: | -----------------: |
| Rendering in the callback (queue 0) | 0.001 ms | 0.06 ms |
| Render-ahead 512 frames, before the fix | 2.65 ms | 0.09 ms |
| Render-ahead 512 frames | −0.02 ms | 0.07 ms |
| Render-ahead 1,024 frames | −0.02 ms | 0.07 ms |

The loopback found a bug. With a render-ahead queue, the model said a frame would be heard one device buffer earlier
than it was: the render thread wakes just after a callback has taken a block, so a frame written then also waits for
the next callback. Audio was therefore rendered for a song position one buffer period too early relative to MIDI
(2.7 ms at 128 frames), and the live latency meter read one buffer low. The render thread now adds the device's buffer;
both modes agree with the loopback within 0.02 ms. BlackHole reports its latency correctly, so the clicks' total time
from note to capture (3.8 ms median in callback mode) is the device's buffer and reported latency.

### Plugin workers (added 2026-11-03)

`--plugin-pipeline` drives the reference sine instrument in a real worker process from a real-time-paced thread,
as the audio callback would, with notes arriving. It counts blocks that come back not processed (late: silence),
leaving out the first 100 ms, when a new instance's worker is still cold, which it reports apart. 48 kHz, macOS,
Apple silicon. The worker's processing thread used to wait for a block by spinning 200 µs and then calling
`Thread.Yield`, at normal priority:

| Worker wait | Depth | Block | Quiet, 120 s | Every core busy, 60 s |
| ----------- | ----: | ----: | -----------: | --------------------: |
| Yield (before) | 2 | 128 | 0 | 4,658 (21 %) |
| Yield (before) | 1 | 128 | 0 | 15,906 (71 %) |
| Yield (before) | 1 | 64 | 10 | 25,880 (58 %) |
| Real-time, precise pause | 2 | 128 | 0 | 0 |
| Real-time, precise pause | 1 | 128 | 0, 0 | 0 (call max 0.30 ms) |
| Real-time, precise pause | 1 | 64 | 0, 0 | 0 (call max 0.57 ms) |

("Every core busy": 18 shell busy-loops on 18 cores.) A worker at normal priority loses the CPU whenever anything
else wants it, so even today's depth 2 dropped a fifth of its blocks under load. A real-time thread cannot poll by
yielding: that would use its whole computation budget and the kernel would demote it. So it now waits in 50 µs
`nanosleep` slices, blocked rather than computing. That costs about 30 µs of median wake-up at depth 1 and uses
less CPU than yielding did.

## Decisions

1. **The targets above are Bluestone's timing contract** and are judged by these probes. Release 1.0 Slice 0 takes them
   as its starting tolerances.
2. **Nothing moves to Rust.** In a process whose managed heap stays quiet, the C# render path uses 0.06–0.12 ms of a
   1.3–2.7 ms buffer, and every MIDI path is far inside its target. No measurement meets ADR 0026's bar for a Rust
   component. The existing Rust device layer (`bluestone_audio`) and plugin host are unchanged.
3. **The playback thread no longer spins on macOS** (done here). The final approach to a due time is slept in
   ≤ 0.25 ms slices (`PlaybackThread.FinalSlice`) with `mach_wait_until`, re-pumping between slices
   (`PlatformProviders.PlaybackPreciseSleep`). Spinning used the real-time thread's computation budget and produced
   rare multi-millisecond lateness. It also left commands and `SendNow` waiting for up to 1.5 ms. Windows and Linux
   keep spinning until measured on their own hardware.
4. **The real-time work moves into a C# engine process** (built in ADR 0044, where its measurements are; this
   ADR records the decision and its evidence). The UI process will always allocate: a plan compile is 8–100 MB and opening a project about 1 GB
   (ADR 0012). Any collection in a process stops every managed thread in it, and no GC setting kept those pauses
   under 2 ms. The engine process owns the audio stream with rendering in the device callback, the playback engine and
   thread, MIDI input and output, live routes, and the connection to plugin workers. It must keep these rules, each
   checked by a test or a probe:
   - **No allocation in steady state** on the real-time threads (as `ChainRunnerTests` checks), so playback alone
     never causes a collection.
   - **A small, reference-light retained heap** (arrays of structs, not object graphs), so any collection that does
     happen is short. An engine that compiled plans on its own small heap still met every target, with pauses of at
     most 0.95 ms. With a 320,000-event plan also retained, one pause took 24 ms.
   - **Plans and graphs arrive in a compact form**, in preallocated or shared memory and swapped in whole, rather than
     as large object graphs.
   - **The engine reports every collection and its pause.** A probe fails on any pause longer than 1 ms during
     playback.
   - **No JIT compilation during playback.** The engine is published with NativeAOT, or runs with tiered compilation
     off, so code is never replaced while it plays.
6. **Plugin workers process on a real-time thread on macOS** (done here). `ProcessingThreadScheduling` puts each
   instance's processing thread on the time-constraint policy (half the block period of computation per period) and
   pauses it in 50 µs `nanosleep` slices between checks while blocks are arriving. `BLUESTONE_WORKER_WAIT=yield`
   restores the old wait for comparison. With it, depth 1 delivered every block in 60 s with every core busy, at
   64 and 128 frames. Instruments being played live therefore get a depth-1 exchange when live input moves into
   the engine (ADR 0044 step 7), with the pipelined depth kept for playback, where it lets workers run in parallel.
   Windows and Linux keep their wait until measured on their own hardware.
7. **The engine process exists** (ADR 0044) and is the default on macOS, where it met every target its probes measure:
   audio rendered in the device callback with no xruns, scheduled MIDI dispatched by its send-ahead deadline, and live
   notes from a Core MIDI input heard within the device's output latency plus one buffer period, plus at most 0.04 ms.
   MIDI arrival, immediate output, and thru have loopback measurements only in process (above), not yet through the
   engine. On Windows and Linux the app keeps
   the render-ahead queues of ADRs 0032 and 0035 until the engine is measured there. Under those queues audio is
   glitch-free under load, but live input carries the queue as latency, and immediate MIDI and thru can be late by a
   collection in the application process.

## Consequences

- A live plugin instrument adds its worker pipeline (ADR 0025: one engine block at depth 2, 2.67 ms at 128 frames) on
  top of the figures above. That exceeds the 2 ms target whatever the language. Depth 1 adds nothing and, with real-time
  workers, met its deadline in every measured block (decision 6). It applies to live-played instruments in the engine
  (ADR 0044 step 7), where a Core MIDI key was heard within the output latency plus one buffer period, plus at most
  0.04 ms.
- The engine process adds an IPC boundary between the UI and the engine. It carries plans, graph snapshots, commands,
  and meters, but nothing per event or per sample on the real-time path, because MIDI devices and the audio device
  open in the engine process.
- The live figures use the engine's heard-time model, built on the device's reported output latency. The audio
  loopback below confirms that model on a virtual device; an interface with OUT patched to IN also measures its
  converters, which the device may not report.
- The application process still uses tiered compilation. With the engine process it does no real-time work, and the
  re-measurement found no JIT pause over 0.14 ms on the in-process path either.
- All numbers come from one Mac with a synthetic load. Rerun the probes on other machines, and on Windows and Linux
  when their adapters gain precise waits, before changing a target or a default.
