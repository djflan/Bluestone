# 0035. Live input through every device, a live render-ahead queue, and plugin delay compensation

- Status: Accepted; amends 0028 (live input) and 0032 (latency)
- Date: 2026-10-11

## Context

Live notes (MIDI thru, the on-screen keyboard, auditioned notes) followed a track's live route to the nearest
playable instrument, applying only Transpose devices (decision 21). Plugin MIDI effects shaped the plan only
(ADR 0028), and plugin instruments were not played live at all. The audio engine renders 1024 frames (21.3 ms)
ahead of the device (ADR 0032), which is invisible for playback but is latency for a player. Plugins report their
latency (pipelining plus their own), and nothing compensated for it. The owner put live input through plugins and
built-in devices, and plugin delay compensation, in scope for this epic.

## Decision

### Live input through every device on the live route

- **The route.** `LiveRoutePlan.Find` keeps decision 21's breadth-first search but now records every device on the
  way: each device that is not bypassed and consumes events is a stage in chain order (built-in devices and plugin
  MIDI effects), and each connection's forced channel is a stage where the connection is. A plugin instrument in a
  chain ends the route there: it takes the notes, as at plan time. Otherwise the first usable external port does. A
  device with nothing running it passes events through, as at plan time.
- **Built-in devices run in a real-time form.** Each built-in device has a live processor beside its plan-time one
  (`ILiveProcessor`: `LiveTransposeProcessor`, `LiveEventFilterProcessor`, `LiveArpeggiatorProcessor`), registered in
  `DeviceCatalog.BuiltIn`. They work on `LiveEvent`, a MIDI 1.0 channel message value at a tick, in fixed buffers, and
  allocate nothing (tested). `LiveChain` gives each only the classes it handles and merges the rest back by tick.
- **Same output as plan time.** Given the same notes, the live processors put out the same notes (start, end,
  channel, pitch, velocity) as their plan-time twins, in any block sizes: CsCheck property tests over random notes,
  controllers, settings, and block partitions (`LiveProcessorTests`). Two deliberate differences: a generated
  arpeggio note is released at velocity 64 (when a step plays, the key's release velocity is not known yet), and a
  live note-off always follows the transposition or filter decision of its own note-on, so changing a device while a
  key is held never leaves a note hanging.
- **The live clock.** Ticks advance at the transport's tempo while it plays and at the tempo at the playhead while it
  is stopped, and never jump back (a loop wrap or seek does not move them). The arpeggiator anchors phrases at the
  first note (decision 16), so it needs no bar grid. The clock's resolution is the sequence's, multiplied up to 64
  times (within the PPQN limit), so a tick is shorter than a sample at usual tempos and events keep their exact frame
  through plugin stages.
- **Plugin MIDI effects run in their own instance**, through the data plane (`PluginInstance.ProcessBlock`), not the
  plan-time render. Their output comes one pipeline block later. While the instance cannot deliver (crashed, hung,
  restarting) events pass through unchanged, as at plan time; whenever that changes, every note the stage put out is
  released first.
- **Where it runs.** A route to a plugin instrument runs on the audio thread, inside the instrument's `PluginNode`
  (`ILiveEventSource`, an interface in `Bluestone.Audio` that the application implements, because the audio layer may
  not reference the application): input is queued lock-free and goes into the next block at its first frame;
  generated events (arpeggio steps) land on their own frame. Any other route runs on the thread that posts the input,
  so MIDI thru stays immediate, and on a live thread every millisecond for what happens later (arpeggio steps,
  plugin effect blocks of 64 frames). One thread processes a route at a time. Several tracks can play one plugin
  instrument at once (a track whose events go to the track holding it, and that track): the node's live source is a
  `LiveSourceSet` of every route that ends there, rendered in turn. A route's input queue holds at most 4096 events;
  more are dropped and counted.
- **Nothing hangs.** The target keeps the notes it sounds and sends a release only for a note that sounds. When a
  route changes (a device added, removed, bypassed, or edited; a connection; the target), the old route releases
  every note it sounds before the new one takes over (on the audio thread for plugin instruments: the set asks the
  route it drops). A plugin effect whose worker dies releases what it put out. Panic releases every live note.
  A key released after its route changed finds nothing sounding and sends nothing.
- **Recording records the input**, before any device, as before. The devices run again when the take plays, so
  recording their output would apply them twice. Recording the processed output is not offered; it would need a
  separate "print" option and is left for later.

### A live render-ahead queue

- The render-ahead queue can change while the stream runs (`IAudioOutput.SetQueuedFrames`): a smaller queue drains,
  a larger one fills, and the stream stays continuous because the device plays every frame either way, so the
  transport is not disturbed. When live input reaches a plugin instrument, `AudioPlayback` switches to the live queue
  (`AudioSettings.LiveQueuedFrames`); ten seconds without live input switch back to the playback queue.
- Live notes go into the next block rendered (the latest one not yet handed to the device), at its first frame. That
  is the lowest latency the queue allows; the price is jitter of up to one render chunk.
- **Measured** (`dotnet run -c Release --project src/Bluestone.Benchmarks -- --live 20 ...`; macOS 26, Apple
  Silicon, Jabra Evolve2 50 USB headset reporting 4.8 ms output latency, render thread with real-time priority,
  32 tone strips rendering, other agents' builds running): from the input's timestamp to when its block is heard.

  | Buffer, pressure | Queue | p50 | p95 | Max | Underrun frames in 20 s |
  | ---------------- | ----: | --: | --: | --: | ----------------------: |
  | 128, none | 1024 | 24.3 ms | 26.0 ms | 26.1 ms | 0 |
  | 128, none | 256 | 8.5 ms | 10.0 ms | 10.1 ms | 0 |
  | 128, app | 1024 | 24.1–24.4 ms | 26.0 ms | 27.6 ms | 0 |
  | 128, app | 512 | 13.8 ms | 15.2 ms | 22.4 ms | 128 |
  | 128, app | 256 | 8.1 ms | 9.8 ms | 16.2 ms | 1152 |
  | 64, app | 256 | 8.0 ms | 8.7 ms | 12.6 ms | 3008 |

  Switching back to 1024 always returned to 0 underruns. A plugin adds its pipeline on top (one block at depth 2,
  2.7 ms at 128 frames), and a plugin MIDI effect before it another block.
- **Decision:** the live queue defaults to **512 frames** (10.7 ms): it halves the playback queue's latency and, under
  the app load, lost 128 frames in 20 s. 256 frames (or 128) can be chosen in the Audio window for a quiet system.
  The playback queue stays 1024. The offline driver measures the same path exactly
  (`LiveInputPluginTests`: a key played 5 ms before the next block is heard sounds two pipeline blocks after that
  block, through a real plugin MIDI effect and instrument, and the latency meter reports exactly that).
- `LiveInput.Latency` measures every live note into a plugin instrument (input timestamp to heard, the plugins'
  latency and compensation included); the Audio window shows it. After the instrument it counts everything on the
  way to the master (`AudioGraph.DelayToMasterOf`): compensation delays at every channel and bus, and the latency of
  plugin effects after the instrument (before 2026-10-11 it counted only the delay on the instrument's own output
  and under-reported).

### Plugin delay compensation

- `AudioGraphBuilder.Build` computes every node's output latency (its own `LatencyFrames` plus its slowest input),
  every summing point's (a node's input, a mixer channel, the master: the slowest arrival, through outputs and sends),
  and delays each faster arrival by the difference, with a `DelayLine` per edge. The graph's `LatencyFrames` is the
  master's. Built-in nodes (clips, tones) report 0; a plugin node reports pipelining plus the plugin's own latency.
- Delay lines are kept by edge across rebuilds (`LatencyCompensation`, owned by `ProjectAudioGraph`), so editing does
  not drop the audio inside them. A changed delay moves the read position (a click, as in other hosts). Rendering
  allocates nothing (tested).
- **The transport compensates.** Following the MIDI transport, the audio engine renders the graph's latency ahead of
  the song position heard, so the master puts out what the transport plays at that moment: audio clips, plugin
  instruments, and external MIDI stay in sync. A change of that latency relocates the audio once.
- **Latency changes recompensate.** A plugin's `LatencyChanged` notification (protocol version 3) makes
  `ProjectAudioGraph` raise `LatencyChanged` and the graph is rebuilt; a once-a-second check also rebuilds when a node
  reports another latency than the graph was built for (`AudioGraph.IsLatencyStale`; a restarted plugin, say). The
  reference plugins gained `reference.delay`, which delays its input by exactly the latency it reports and announces
  changes, so compensation is tested sample by sample with real workers.
- **Low-latency monitoring** (an Audio setting, off by default, as in other hosts): the plugin instrument the
  monitored track plays live (and the plugin effects after it) is not delayed for compensation anywhere on its way to
  the master, and does not hold the others back, so it is heard after its own latency. A mixer channel, bus, or
  return it passes through cannot delay its output without delaying the instrument, so it delays its other inputs to
  the graph's latency on the way in instead: everything else (the track's clips, the other channels on the bus)
  stays aligned (`LatencyCompensationTests.AMonitoredInstrument_Through*`). Audio that goes through the same plugin
  effects as the instrument is early with it. The instrument's own playback is early against the other tracks by
  the compensation it skips, while it is monitored. Recording is not affected: it uses input timestamps.
  (Before 2026-10-11 the channel after the instrument delayed its output to the graph's latency, so the monitored
  instrument was heard later than without monitoring.)

## Consequences

- Live notes now sound like playback: an arpeggiator, filter, plugin MIDI effect, or plugin instrument on the route
  applies live, and the arpeggiator follows the tempo. The "Live notes apply only Transpose" and "Plugin MIDI effects
  are rendered, not played live" compromises are gone.
- A route with a plugin MIDI effect keeps its instance processing while the route exists; a route to an external
  instrument through a plugin effect runs 64-frame blocks on the live thread.
- Device automation of built-in devices does not apply to live input (stored values do); automation shapes the plan.
- Not measured: the acoustic round trip (no loopback recording); live input on Windows and Linux devices (the code is
  the same; CI has no audio devices). Plugin delay compensation is tested with the reference plugins and the
  offline driver; third-party plugins' reported latencies were not checked against their audio.
- Considered and rejected: rendering live tracks in a second, low-latency graph outside the queue (Reaper's
  anticipative processing). It needs managed code in the device callback or a second ring, which ADR 0032 measured
  against; the adjustable queue gets most of the gain without it.
