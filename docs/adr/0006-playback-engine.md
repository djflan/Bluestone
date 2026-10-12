# 0006. Playback engine: prepared plans, dual cursors, runtime releases

- Status: Accepted; loop-wrap chase superseded by 0020
- Date: 2026-10-04

## Context

Playback must be precise, never leave notes hanging, tolerate edits while playing, and be testable
without hardware or wall-clock time. Endpoints differ: some (Core MIDI) accept timestamped messages
for future delivery, others (WinMM, many network transports) only send immediately.

## Decision

- **Prepared plan.** `PlaybackPlanCompiler` turns a `Sequence` plus track bindings into an
  immutable `PlaybackPlan`: messages in canonical order (ADR 0003) with output slots and the tempo
  map. Mute/solo (solo overrides mute) and channel overrides are applied at compile time. Edits
  compile a new plan and swap it in with `Load`; the edit model is never read by the scheduler.
- **Single engine thread, command queue.** Transport commands from any thread are queued and
  applied at the start of `Pump`. The real-time path takes no locks, allocates nothing, never
  throws for endpoint failures, and reports through `SendResult` counters.
- **Two cursors.** One cursor serves scheduled-delivery endpoints, handing messages over up to
  `LookAhead` (20 ms) early with timestamps; the other serves immediate endpoints at due time. Each
  cursor carries its own tick→time anchor so loop wraps and plan swaps stay consistent per class.
- **Releases at runtime.** Plans contain note starts with durations. When a note starts, its release
  enters a fixed-size active-note table. Edits, stops, seeks, loops, and panic always release every
  note that sounded. A note handed to a scheduling endpoint but not yet started is released at its
  start time, so the release can never overtake it. Table overflow drops the new note (counted).
- **Edits during playback.** On `Load`, each cursor resumes at the first tick it has not yet
  processed; nothing is repeated, sounding notes keep their release time, newly added notes that
  span the playhead are not started, and tempo changes take effect from the playhead.
- **Seek and play** release sounding notes, release a held sustain pedal, and chase bank, program,
  controllers, pitch bend, and channel pressure. SysEx, RPN/NRPN, and sounding notes are not chased.
- **Chase while stopped.** When stopped, loading a plan, changing outputs, or seeking sends the state
  in effect at the playhead, including events at the playhead itself (so a program change at tick 0
  applies), so live input hears each track's instrument. Pedals (CC 64-69) are left out so a held
  sustain cannot latch live notes, and an unchanged state is not resent.
- **Loops** are half-open; notes crossing the loop end are released there. A loop engages only when
  the playhead is before its end. Controllers are not re-chased at the wrap.
- **Lateness policy.** After a stall, note starts more than `MaxNoteLateness` (250 ms) late are
  skipped; everything else is still sent so device state stays correct.
- **Diagnostics.** `TimingStatistics` uses lock-free counters and a fixed lateness histogram
  (p50/p95/p99 resolved to bucket bounds), safe to read from the UI.
- **Driver.** `PlaybackThread` pumps on a dedicated high-priority thread, blocking on the engine's
  signal for long waits and spinning for the last ~1.5 ms. Immediate-class precision is therefore
  bounded by OS wake-up latency; scheduled endpoints are not affected by it.

## Consequences

- The scheduler is fully deterministic under `VirtualClock`, so timing behavior is unit-tested.
- Releases always use the note's own release velocity and go to the output that received the start.
- Count-in, metronome, punch, and recording are not implemented yet; they will be added as plan
  sources and transport states without changing the dispatch model.
- No native code: managed timing is used until measurements (see later benchmark ADR) show otherwise.
