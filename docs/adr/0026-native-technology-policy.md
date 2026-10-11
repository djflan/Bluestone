# 0026. Native technology policy: C# first, Rust where measurements or native interfaces demand it

- Status: Accepted; the first Rust component is the plugin host (0033)
- Date: 2026-10-10

## Context

Bluestone is a .NET application. A DAW has places where managed code may not be enough: a real-time
audio callback, digital signal processing, hosting native plugin binaries, and low-latency exchange with
plugin worker processes. Writing native code has real costs: a second toolchain, per-platform builds and
packaging (macOS arm64, Windows x64 and ARM64, Linux), and an interop boundary that is slow when it is
crossed often. The README used to allow C or C++ components; new Bluestone-owned native code should be
memory-safe.

## Decision

- **C# is the default** for everything: domain, sequencing, MIDI processing, persistence, routing,
  device chains, automation, playback orchestration, plugin management and supervision, IPC control
  plane, UI.
- **Rust is used for a component only when one of these holds, and the reason is written down:**
  - a measurement shows managed code cannot meet the requirement after reasonable tuning (spans, pooled
    or preallocated buffers, struct-based data, `stackalloc`, native memory, function pointers); or
  - the component must work with a native interface that managed code cannot handle well, such as
    loading native plugin binaries.
- **The bar for "measured":**
  - the real-time path allocates nothing after warm-up (checked with `GC.GetAllocatedBytesForCurrentThread`,
    as `ChainRunnerTests.APassthroughChain_AllocatesNothingAfterWarmUp` does);
  - callback duration at the 99.9th percentile and in the worst case stays within the audio buffer
    period (2.67 ms at 128 samples and 48 kHz);
  - garbage collection pauses during playback (`GC.GetTotalPauseDuration`, GC trace events) stay within
    the same period;
  - throughput targets are checked with BenchmarkDotNet (`Bluestone.Benchmarks`).
- **Expected first candidates**, not yet justified and not yet built: the audio callback and graph
  execution of a future audio engine (a .NET garbage collection pauses every managed thread, including an
  allocation-free one, while a Rust callback on a thread .NET does not manage is not paused), and the
  native VST3 hosting layer inside a plugin worker (for safety with native binaries). Plugin workers are
  separate processes (ADR 0025), so a worker's garbage collector cannot pause Bluestone; a C# worker is the
  default there.
- **C and C++** are used only for third-party libraries, vendor SDK requirements, and thin ABI shims.
  No new Bluestone-owned C or C++ beyond such shims.
- **Interop rules for any Rust component:** a small, versioned C ABI; no Rust object layouts across the
  boundary; documented ownership of memory and buffers, handle lifetimes, thread affinity, error
  reporting, disposal, callbacks, and synchronization; coarse calls (one per audio block, not per
  event); unsafe code isolated and justified.

## Consequences

- No Rust and no Bluestone-owned C or C++ exist today; the operating-system MIDI APIs are called from C#.
- Every native component starts with a measurement or a native-interface constraint in its ADR.
- CI gains a cargo build for each target platform only when the first Rust component lands.
