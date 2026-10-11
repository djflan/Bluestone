# 0032. Audio engine: cpal output in Rust, the render graph in C#, buffered by a ring, one transport

- Status: Accepted
- Date: 2026-10-11

## Context

Bluestone modelled audio clips, audio connections, the mixer, and software instruments, but nothing made sound
(handoff section 7.1). The owner decided: a Rust native layer with `cpal` for device I/O; the real-time render
path in C# first, measured as ADR 0026 requires; move only the callback path to Rust if the measurement fails.

The bar (ADR 0026): no allocation on the real-time path after warm-up; callback time at the 99.9th percentile
and in the worst case within the buffer period; garbage-collection pauses during playback within the same period.

## Decision

### Device layer (Rust, `native/crates/bluestone_audio`)

- A `cdylib` with a C ABI (`bluestone_audio_abi_version()` = 1): list output devices as JSON (id, name, host,
  default rate and channels, supported standard rates, channel counts, buffer range); open, start, stop, and close
  an output stream for a device id (or the default) with a rate, buffer size in frames, and channel count; read a
  status struct (state, rate, channels, buffer sizes seen, callbacks, frames, xruns the backend reported,
  late-callback "discontinuities", output latency, the last device error such as a removed device, render-time
  maximum, total, and late renders) and a render-time histogram (10 µs buckets).
- Two stream modes. **Direct:** each device callback calls a function pointer registered by .NET
  (`[UnmanagedCallersOnly]`) with the interleaved `f32` buffer, frames, channels, and block information (frame
  position, output latency, callback index). **Buffered:** the callback only copies from a single-producer,
  single-consumer ring that a dedicated .NET render thread fills; missing frames play as silence and are counted.
  The writer waits for space in native code (`std::thread::park_timeout`), so a .NET collection never holds it there.
- The callback path does no allocation, locking, or logging: atomics, a conversion buffer allocated at open (for
  devices without `f32`), and the copy. Render time is measured natively around the call into .NET, so it includes
  entering the runtime and any wait for a collection to finish.
- No input stream (recording audio is out of scope). Built and tested on macOS (CoreAudio). CI builds and tests the
  crate on Linux (ALSA; `libasound2-dev` is installed) and Windows (WASAPI); hosted runners have no audio device,
  so device tests there only check that a failed open reports a reason.

### Render graph (C#, `Bluestone.Audio`)

- `AudioEngine` renders an immutable `AudioGraph` snapshot; control threads publish a new snapshot with one
  reference write, and the audio thread picks it up between blocks without waiting. Nodes (`ToneNode`,
  `ClipPlayerNode`, `PluginNode`) and strip state (`MixerStripState`: meter and last gains) live across snapshots,
  so edits do not reset playback.
- Every block has exactly the engine's block size (plugins need one size throughout); device callbacks of other
  sizes are served from the last rendered block. A loop end inside a block is a wrap at an offset that nodes handle
  (`AudioRenderContext.WrapOffset`).
- Mixer strips: gain (ramped over one block when it changes), pan, mute, solo, routing to a bus strip or the
  master. **Pan law:** stereo balance with a sine taper: the near side stays at unity and the far side is
  multiplied by `cos(|pan| × π/2)` (−3 dB half way, silent at the edge); centre is unity. **Solo:** while any strip
  is soloed, a strip sounds if it is soloed, feeds a soloed strip, or is a bus a soloed strip reaches; mute always
  silences its own strip. **Meters** (per strip and master, post-fader): peak held and falling 20 dB/s, RMS with a
  300 ms time constant, read lock-free from any thread.
- `OfflineAudioDriver` drives the same render path with virtual time, for deterministic tests and measurement.
- Sends are strip-to-strip edges with their own gain; see "Sends, clip gain and fades, and device loss" below.

### Audio clips

- `AudioFileReader` decodes WAVE (PCM 8/16/24/32-bit, IEEE float 32/64, `WAVE_FORMAT_EXTENSIBLE`) and AIFF/AIFF-C
  (big-endian integers, `sowt`, `fl32`, `fl64`), any channel count; mono plays on both sides, wider files play their
  first two channels. FLAC, MP3, Ogg, ALAC, μ-law/A-law, and RF64 are refused with a reason: a decoder crate
  (symphonia, MPL-2.0) would need a streaming ABI, which was not cheap enough for this step.
- Sample-rate conversion: a Kaiser-windowed sinc (β 8.6, 32 zero crossings each side, cutoff 0.91 of the lower
  Nyquist, 512 phases with linear interpolation, stretched when downsampling), in C#. It is stateless: each output
  frame is computed from its song position, so a jump needs no filter history and clips stay sample-exact at the
  device rate. Tested: a 1 kHz sine at 44.1 kHz played at 48 kHz stays below −90 dB error; a 30 kHz tone at 96 kHz
  played at 48 kHz is below −80 dB. Chosen over `rubato` because it runs in the same allocation-free graph and
  measured cheaply (64 multiply-adds per channel and frame).
- Streaming: the first second of each clip is decoded when the graph is built (starts never wait for the disk);
  a disk-streamer thread keeps a 4-second ring per clip filled ahead of the play position. The audio thread never
  reads a file; data that is late plays as silence and is counted (`UnderrunFrames`), never as other audio. Seeking
  into a clip beyond its preload is silent until the ring has the data (a few milliseconds). On every jump or loop
  wrap the ring is asked for the first frame no preload holds from the new position, so playing the start again
  never waits. When the loop starts inside a clip, the disk thread also decodes one preload length at the loop start
  (again when the loop moves), so wraps are seamless. The audio thread does not wake the disk thread (that is a
  system call); the disk thread polls every 5 ms, less than one 256-frame block at 48 kHz.
- A clip's source plays at its own rate from the song time of its origin, at the clip's gain and with its fades (see
  below); there are no clip loops. A missing or unreadable file is a problem in the graph result, never an exception.

### Plugins and the project

- `PluginNode` drives a `PluginInstance` each block: input audio, the events and parameter changes scheduled in the
  block, the transport. Plugins run every block, playing or not. On a jump (start, stop, seek, loop wrap), notes the
  node started are released. When the schedule is replaced during playback, notes the new schedule still holds at
  the play position keep sounding and the others are released; a rebuild that does not change a device's events,
  automation, or their frames (a fader move, a send, a clip gain) keeps its schedule. A worker failure gives silence (the instance's
  failure policy), never stale audio. `LatencyFrames` (pipelining plus the plugin's own) is reported per device in
  `ProjectAudioGraphResult.LatencyFrames`; nothing compensates yet.
- `ProjectAudioGraph` (Application) builds the graph from the project: a chain's audio starts with the owner's
  audio clips (when the track plays by the plan's mute and solo rule, groups included, ADR 0030) plus audio connected into it; a plugin audio
  effect processes it, a plugin instrument adds to it, other devices pass it on; taps take the audio after their
  device; audio connections lead to mixer channels, the master, or other chains. Routing errors leave connections
  out. Software-instrument feeds and parameter feeds from the signal graph become per-device schedules in song frames.
  A plugin instance at another sample rate or with a smaller block size than the engine's is reported and silent.

### One clock

- The MIDI playback engine (`PlaybackEngine`) keeps the single transport: play, stop, seek, loops, tempo, count-in.
  Its time base is the system monotonic clock, the same clock MIDI timestamps and recorded input use.
- The audio engine follows it (`AudioTransport.Follow`): for each block it asks the transport for the song time at the
  moment the block will be heard (the callback's time plus the device's output latency, plus the render-ahead queue
  in buffered mode) and starts there. After that the **device's sample clock advances the audio**: each block moves
  the song position by exactly its frames, so audio is continuous; it relocates only when the transport is more than
  a tolerance away (a seek or a start; 10 ms or two blocks, whichever is larger).
- The slow difference between the device's crystal and the system clock is measured (`TakeDrift`) and the MIDI
  transport is nudged towards the audio once a second (`PlaybackEngine.Nudge`, half the average difference). So the
  audio device's clock drives the transport while audio runs, MIDI stays aligned with what is heard, and audio never
  jumps for drift.
- Without an audio device nothing changes: MIDI-only playback, its tests, and the regression fixtures are untouched.
  Considered: making the device clock the engine's `IMonotonicClock`. Rejected because MIDI timestamps sent to
  CoreMIDI, WinMM, and ALSA and those of recorded input are in system time; every MIDI edge would need a conversion,
  and a rate correction would still be needed somewhere.

### The measurement and the decision

Harness: `dotnet run -c Release --project src/Bluestone.Benchmarks -- --audio 20 [--pressure none|app|churn|full]
[--queue N] [--sustained]`. It renders 32 tone nodes into 32 strips into 4 buses (silent at the master) on the
default device at 48 kHz for 20 s per buffer size while other threads put the collector under pressure: **app**
(a 200 MB retained heap and about 50 MB/s of small allocations, a quarter kept a while: roughly an editing session),
**churn** (two unthrottled allocation threads), **full** (churn plus forced, blocking, compacting gen2 collections of
a changing 1-million-object graph every second). GC pauses come from the runtime's suspend and restart events.
macOS 26 on Apple Silicon, a Jabra Evolve2 50 USB headset, Workstation concurrent GC unless stated; other agents'
builds and tests ran on the machine at the same time.

Direct mode (the device callback calls .NET), late = native render time over the buffer period:

Each cell is for 64 / 128 / 256-frame buffers (1.33 / 2.67 / 5.33 ms).

| Pressure, GC | Callbacks | Late | Xruns | p99.9 | Max | GC pause max |
| ------------ | --------: | ---: | ----: | ----: | --: | -----------: |
| none | 15008 / 7506 / 3752 | 0 / 0 / 0 | 0 / 0 / 0 | 0.06 / 0.12 / 0.17 ms | 0.08 / 0.14 / 0.22 ms | no collections |
| app | 14753 / 7396 / 3745 | 89 / 60 / 10 | 81 / 49 / 5 | 5.0 / 8.2 / 6.4 ms | 10.2 / 11.8 / 8.6 ms | 10.7 / 12.3 / 9.5 ms |
| app, SustainedLowLatency | 14690 / 7452 / 3743 | 99 / 33 / 10 | 93 / 23 / 7 | 5.5 / 7.0 / 7.0 ms | 11.6 / 10.5 / 8.7 ms | 11.5 / 10.8 / 9.6 ms |
| app, gen0 budget 64 MB | 14706 / 7414 / 3704 | 18 / 17 / 15 | 18 / 17 / 15 | 11.4 / 12.8 / 19.8 ms | over 20 ms | 51 / 36 / 32 ms |
| app, Server GC | 15009 / 7505 / 3752 | 1 / 0 / 0 | 1 / 0 / 0 | 0.07 / 0.11 / 0.18 ms | 4.2 / 0.11 / 0.18 ms | 4.9 ms (one collection in 60 s) |
| churn | 9603 / 7326 / 3753 | 2962 / 301 / 0 | 2754 / 93 / 0 | 7.2 / 8.9 / 3.4 ms | 17.4 / 20 / 4.2 ms | 17.7 / 22.7 / 7.8 ms |
| full | 11354 / 6502 / 3419 | 1656 / 473 / 70 | 1501 / 278 / 55 | over 20 ms | over 20 ms | 99 / 115 / 247 ms |

"Late" counts callbacks whose native render time exceeded the buffer period; "xruns" are the overloads CoreAudio
reported. Fewer callbacks than 20 s needs (15000 / 7500 / 3750) are callbacks the device skipped.

The managed render itself took 0.02–0.2 ms per block at p50 and never missed its deadline *inside* the callback
(the "late (managed)" count stayed 0–11 in every run); every late callback was the thread waiting for a
collection. A .NET collection suspends the audio thread when it enters managed code, and tuning only moves the
pauses: SustainedLowLatency does not help ephemeral collections; a larger gen0 budget makes them rarer but longer;
Server GC collected once in 20 s under the app load (huge budgets), which postpones the problem rather than removing
it. **Managed code in the device callback does not meet the bar.**

Buffered mode (Rust callback copies from the ring; the same C# graph renders ahead on a .NET thread), queue
underrun frames in 20 s:

| Pressure | Queue | 64 | 128 | 256 |
| -------- | ----: | --: | --: | --: |
| none | 512 (10.7 ms) | 0 | 0 | 0 |
| app | 256 (5.3 ms) | 768 | 1024 | 2560 |
| app | 512 (10.7 ms) | 0 | 128 | 0 |
| app | 1024 (21.3 ms) | 0 | 0 | 0 |
| app, SustainedLowLatency | 1024 | 0 | 0 | 0 |
| full | 1024 | 31616 | 30208 | 34304 |
| full | 4096 (85 ms) | 1344 | 0 | 0 |

The device callback itself (native) took at most 0.03 ms and never missed a deadline or caused an xrun in any
buffered run.

**Decision:** the callback path moves to Rust; the render graph stays in C#. The device callback is native and only
copies from a ring; a dedicated .NET thread renders the graph ahead by a queue. The default queue is **1024 frames
(21.3 ms at 48 kHz)**: no underruns under the app load in these runs. The queue is a setting (0 = direct mode for
the lowest latency on a quiet system; up to 4096 frames for heavy load). For playback this latency is invisible:
the transport is followed for the moment a block is heard, so MIDI and audio stay aligned. For live input through
plugins (later work) it is real latency; the next steps if that matters are a smaller default with the render
thread made real-time (ADR 0012's macOS time-constraint policy) and, if still needed, moving graph execution to
Rust. Forced blocking gen2 collections of a large heap (`full`) cause dropouts below an 85 ms queue; Bluestone does
not force collections, so this is a bound, not the expected case.

## Consequences

- Building needs a Rust toolchain (stable, edition 2024) in addition to the .NET SDK; `dotnet build` runs `cargo`.
  CI installs `libasound2-dev` on Linux and runs `cargo test`, `cargo fmt --check`, and `cargo clippy`.
- The app has an Audio Settings window (device, rate, buffer, render-ahead, start/stop, test tone, master level);
  settings are saved in `<application data>/Bluestone/audio-settings.json`. While audio runs, audio clips and plugin
  instruments and effects are heard during playback, in time with MIDI.
- Plugin instances must run at the device's rate and accept the engine's block size (256 frames); each time a device
  opens, the plugin bridge restarts them at its rate (see below).
- Not done: plugin delay compensation (latencies are reported), audio input and recording, compressed file formats,
  and send or fader automation. Linux and Windows device output is built and unit-tested in CI but has not been heard.
- The measurements are from one Mac and one USB headset; rerun the harness on other machines before changing the
  default queue.

## Sends, clip gain and fades, and device loss (added 2026-10-11)

### Sends

- A mixer channel has sends (`MixerSend`: target channel, level in dB, pre or post fader), at most one per target
  and never to itself. Any channel can be a target; one that receives sends is shown as a return. Sends are stored
  on the channel in format 4 as an optional `sends` list (docs/project-format.md).
- Validation treats a send as an edge between mixer channels, with outputs: a path of outputs and sends back to a
  channel is a `Cycle` error, and a send to a missing channel is an error. The commands (`AddMixerSend`,
  `UpdateMixerSend` with the merge key `send:{channel}:{target}`, `RemoveMixerSend`) are refused when they add one.
  Removing a channel removes the sends to it. A file edited by hand that still has a loop plays without the edge
  that closes it, with a problem in the graph result.
- In the render graph a send adds the strip's input to the target at a gain ramped over one block, like the fader.
  **Pre-fader** sends take the strip's input before its fader and pan (level only); **post-fader** sends take it after
  both (level × fader × pan, per side). Strips render in dependency order over outputs and sends, so a return
  always has its sends before it renders; meters are on every strip, returns included.
- **Mute** silences a strip's sends too. **Solo:** a strip sounds if it is soloed, feeds a soloed strip through its
  output, or is reached from a soloed strip through outputs and sends (so a soloed channel keeps its returns). A
  send carries audio when its strip sounds or its target is soloed, so soloing a return plays what is sent to it
  without the dry channels. Rendering with sends allocates nothing (tested).

### Clip gain and fades

- `AudioClip` has a gain (−120 to +24 dB), a fade-in and a fade-out in ticks from its edges (so they follow the
  tempo), and one shape for both. Stored fades may exceed a trimmed clip; the fade-in plays first and the fade-out
  in what remains. Splitting keeps the fade-in on the left part and the fade-out on the right.
- In the engine a frame `p` frames into a fade of `n` frames plays at `curve(p / n)`, so a fade-in starts at silence
  and a fade-out ends at silence. **Linear:** `curve(x) = x` (−6 dB half way). **Equal power:** `curve(x) =
  sin(x·π/2)` (−3 dB half way), so two overlapping fades of uncorrelated material keep their power. Tests check the
  curves sample by sample.
- A change of gain or fades only replaces the clip node's envelope (one reference write the audio thread reads per
  block); the files are not reopened.
- The inspector's Audio Clip section edits them (fades in beats); the arrangement draws the fades.

### Clip node ownership

The project graph owns the clip nodes it opens. A node it replaces (a clip moved or trimmed) is closed at the next
build, when the graph that replaced it is the one playing; disposing the project graph (when audio stops) closes
the rest. Closing is idempotent and serialized with disk reads, so the engine's disk thread closing the same node
is harmless, and a node the disk thread closed while its track was silent is opened again when the track plays. A
file that fails while streaming (deleted, or its disk gone) plays as silence and is counted; it no longer stops
the disk thread.

### Device rate and plugins

Plugins cannot change rate while running. Each time a device opens (start, fallback, return), `AudioPlayback` asks
the plugin bridge for its rate (`PluginDeviceHost.SetSampleRateAsync`, on the UI thread): every instance's live
state and parameters are captured (not stored in the project, so nothing is added to undo), the instance is
destroyed and created again at the new rate from that state; a crashed instance starts from its last snapshot or
the project's state. The graph is rebuilt when that finishes; until then the plugins are silent. An open plugin editor closes with its instance.

### Device loss

`AudioPlayback` checks the stream once a second. When a stream fails (the device was removed or invalidated) it is
closed with its engine and graph, and:

- if a specific device was chosen, the system default output opens in its place (`IsOnFallback`, with a message);
  when the chosen device is listed again, it is opened and the stand-in closed;
- if nothing can be opened, audio stays off and waits (`IsWaitingForDevice`): it tries again whenever the list of
  devices changes, and opens as soon as it can. Choosing another device or stopping audio ends the wait.

Every stream opened is closed exactly once (tested). The device layer is behind `IAudioBackend`/`IAudioOutput` so
this is tested with a backend whose devices come and go; unplugging real hardware was not tested.

### Render thread priority

The buffered mode's render-ahead thread asks for the operating system's audio priority when it starts
(`RenderThreadScheduling`): on macOS the Mach time-constraint policy (computation half of the queue's duration, at
most 25 of 50 ms, preemptible; the thread blocks in native code between bursts, so it stays inside the budget), on
Windows the MMCSS "Pro Audio" task, on Linux `SCHED_FIFO` at priority 60, which needs an rtprio limit and otherwise
leaves the thread at normal priority. What was granted is shown in the Audio Settings status. .NET's own
`ThreadPriority` only has an effect on Windows. The direct mode renders on the backend's own real-time thread.
The default queue is unchanged; the measurement above was made without this priority, so a smaller default needs a
new measurement.

