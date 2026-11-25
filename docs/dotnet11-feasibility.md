# .NET 11 migration report

- **Issue:** [#112](https://github.com/djflan/Bluestone/issues/112) (feasibility, November 22–24, 2026) and its
  [November 24 physical-Mac comment](https://github.com/djflan/Bluestone/issues/112#issuecomment-7147782483)
- **Report date:** 2026-11-24
- **Decision:** migrate to .NET 11 (SDK `11.0.100`, `net11.0`), decided by @djflan on 2026-11-24.
- **Baseline before the migration:** SDK `10.0.403` with `latestFeature` roll-forward, shared target `net10.0`.

This report has three parts: the decision, the outcome of the migration pull request, and the earlier evidence
that led to the decision. The earlier evidence is historical. It describes disposable probes and diagnostic builds,
not the migrated code, and none of it is a release claim.

## Summary and decision

- **Windows:** on one Ryzen 7 2700X machine, the .NET 10 NativeAOT engine missed ADR 0043's 1 ms GC-pause gate in
  32 of 32 runs under app or full pressure, at about 2.5 ms per pause. On .NET 11 the median maximum pause was about
  0.4 ms (47 of 48 runs met the gate). The .NET 10 cause is not identified.
- **macOS:** on a physical M5 Max with one compacting collection at engine startup, both runtimes passed the 1 ms gate
  in all 4 experiment runs (2 per runtime; maximum pauses 0.50–0.62 ms on .NET 11, 0.50–0.56 ms on .NET 10). Without
  it, .NET 11 missed 1 ms in 5 of 7 runs. The compaction has not yet been run on a Mac on the migrated commit.
- **MIDI dispatch timing:** no difference between runtimes on either platform tested.
- **Linux:** untested on both runtimes; it neither supports nor blocks the decision.
- **Support:** when checked on 2026-11-22, .NET 10 (LTS) and .NET 11 (STS) both ended support on
  November 14, 2028. The migration is for the Windows GC result, not for a longer support window.

The migration is one reversible pull request. It changes:

1. `global.json`: SDK `10.0.403` to `11.0.100`, still `latestFeature`.
2. `src/dotnet/Directory.Build.props`: `net10.0` to `net11.0`.
3. `Bluestone.Engine/EngineHost.cs`: one forced, blocking, compacting full collection
   (`GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true)`) before the engine
   sends Ready. It runs once per engine process start, off every real-time path. It moves about 200 KB left over from
   startup out of gen0, so the first gen0 collection during playback stays short.
4. CA2027 (`WorkerProcess`, `SplashWindow`) and CA1832 fixed in code. Warnings stay errors; no analyzer is
   downgraded.
5. BenchmarkDotNet upgraded to `0.16.0-preview.1` (0.15.8's SDK validator did not recognize
   .NET 11).
6. `.github/workflows/ci.yml`: a new opt-in dispatch input (`release_rids`) that publishes the desktop app and the
   engine for all six release RIDs with trim/AOT warnings as failures and runs `--check-allocations`.

## Hardware requirements on .NET 11

Microsoft pages checked on 2026-11-24: the
[minimum hardware requirements breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/11/minimum-hardware-requirements)
(updated 2026-11-11) and
[What's new in the .NET 11 runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
(updated 2026-11-12).

The engine ships as NativeAOT, so the JIT/AOT minimum is a hard floor for it. The desktop app is self-contained and
JIT-compiled: the same minimum applies, and the ReadyToRun (R2R) target only decides whether framework code starts
precompiled. On a CPU below the R2R target the app still runs, but compiles more at startup.

| Release RID | Minimum (JIT and NativeAOT) | R2R target (desktop startup only) |
| --- | --- | --- |
| `osx-arm64` | Apple M1, unchanged | Unchanged |
| `osx-x64` | x86-64-v2 | x86-64-v2 (Apple unchanged) |
| `win-x64` | x86-64-v2 | x86-64-v3 |
| `linux-x64` | x86-64-v2 | x86-64-v3 |
| `linux-arm64` | armv8.0-a, unchanged | armv8.0-a + LSE |
| `win-arm64` | **armv8.0-a + LSE** (Bluestone's stated requirement; see the conflict below) | armv8.2-a + RCPC |

x86-64-v2 adds CX16, POPCNT, SSE3, SSSE3, SSE4.1, and SSE4.2 to the earlier x86-64-v1 floor.

**Windows Arm64 conflict.** The breaking-change page says the Windows Arm64 JIT/AOT minimum is unchanged
(armv8.0-a). The runtime What's-new page says it becomes armv8.0-a + LSE. Both pages still disagreed on 2026-11-24.
Bluestone states the stricter armv8.0-a + LSE, which Windows 11 on Arm already requires. This is Bluestone's choice,
not a Microsoft-confirmed requirement. No Windows Arm64 hardware ran the migrated build.

## Migration outcome (this PR)

Results on the migrated commit. Each row is filled from a recorded run; a row left as a placeholder was not run.

| Gate | Class | Result |
| --- | --- | --- |
| Commit measured | — | `c37b9eb` (code `141df41` plus docs). SDK `11.0.100`, target `net11.0`. |
| Strict Release build of `Bluestone.slnx`, warnings as errors, no downgrades | Verified (local Windows) | 0 warnings, 0 errors. |
| `dotnet format --verify-no-changes` | Verified | Clean. |
| Tests, local | Verified (local Windows) | `dotnet test src/dotnet/Bluestone.slnx --no-build -c Release --max-parallel-test-modules 1` at `c37b9eb`: 2,625 total, 2,606 passed, 19 skipped, 0 failed. |
| Tests, [CI run 45520109671](https://github.com/djflan/Bluestone/actions/runs/45520109671) at `c37b9eb` | Simulated (hosted VMs) | Success. ubuntu-26.04: 2,585 passed, 37 skipped, 0 failed. windows-2025: 2,583 / 37 / 0. macos-26-arm64: 2,588 / 34 / 0. |
| Rust | Simulated (hosted VMs) | `cargo test`, `cargo fmt --check`, and `cargo clippy --locked --all-targets -- -D warnings` passed (ubuntu job). |
| Desktop and engine NativeAOT publish, six RIDs | Simulated (hosted VMs, native architecture, no emulation) | Same CI run. 0 IL/AOT warnings on all six: `osx-arm64` (macos-26-arm64), `osx-x64` (macos-15, Intel), `win-x64` (windows-2025), `win-arm64` (windows-11-arm64), `linux-x64` (ubuntu-26.04), `linux-arm64` (ubuntu-24.04-arm). The desktop app was not launched on any RID. |
| `Bluestone.Engine --check-allocations` | Simulated (hosted VMs); win-x64 also local | 0 bytes for MIDI, audio, and live on all six RIDs. Live-input depth-1 deadlines missed (not gated, shared VMs): linux-x64 2, osx-arm64 3, osx-x64 3, others 0. Local win-x64: 0 / 0 / 0 bytes; its live check printed "2 depth-1 deadlines missed" (not compared with `main`). |
| Zero JIT-compiled methods in the NativeAOT engine | Verified (local Windows) | The engine heartbeat reported `JitCompiledMethods` = 0 in both Windows probe runs. |
| EngineHost startup compaction: once per process, before Ready, again after a restart | Reviewed | Placement reviewed by Grace, Milo, and Theo: once per process, before Ready; a restart is a new process; within the supervisor's 10 s `StartTimeout`. Test: `EngineSupervisorTests.TheEngine_StartsReady_Answers_ReportsItsFigures_AndShutsDownWithoutCountingAsAStop` and `EngineSupervisorTests.AKilledEngine_IsReplaced_AndTheRestartIsAWarning` assert that the first heartbeat reports a collection; both fail with the `GC.Collect` removed. They cannot tell the forced collection from an ordinary one. |
| Windows engine GC probe, 300 s, 1 ms gate | Verified (physical Windows) | AMD Ryzen 7 2700X, quiet desktop but not isolated; the probe forwarded output to itself (no MIDI device). `Bluestone.Benchmarks --jitter 300 --engine --engine-path <win-x64 AOT engine> --pressure app`, 2 runs. 12 engine GCs each. Max GC pause 0.330 ms (p50 0.204) and 0.316 ms (p50 0.200). Dispatch lateness p99.9 / max 0.66 / 0.963 ms and 0.72 / 1.048 ms. 0 late, missing, or dropped of 115,264 messages; engine allocation about 165 KB/s; working set about 40 MB. Both runs: all targets met. Raw reports are attached to the pull request. |
| Physical-Mac engine GC probe, 300 s, 1 ms gate | **Unverified** | Not run: no Mac was reachable from the Windows host. The owner accepted proceeding without it on 2026-11-24; it remains a follow-up. |
| Desktop app memory, .NET 11 against .NET 10 | Unverified | Not measured in this run. #112's observation (higher `gc.committed` in a JIT-run process) is unconfirmed on the migrated commit. |
| BenchmarkDotNet on .NET 11 | Verified (local Windows) | 0.15.8 reproduced the `NotImplementedException`. `0.16.0-preview.1` ran `MixerMeterBenchmarks` (`ShortRunJob`, 60 benchmarks), exit 0. |
| Unix `CurrentUserOnly` pipes (mode 0600) | Reviewed; simulated | Engine and worker pipes are same-user. Covered by the passing Linux and macOS integration suites; no direct permission test. |

### Verified, simulated, and unverified

- **Verified on physical hardware (migrated commit):** the strict build, `dotnet format`, BenchmarkDotNet, and the
  Windows engine GC probe with zero JIT, all on one Windows x64 desktop.
- **Simulated (hosted VMs):** the CI test suites on Linux, Windows, and macOS; Rust tests; the six-RID desktop and
  engine publishes and their `--check-allocations` runs.
- **Unverified:**
  - Physical-Mac GC confirmation on the migrated commit.
  - `osx-x64` and `win-arm64` were launched only on hosted CI runners (`--check-allocations`, run 45520109671,
    0 bytes); no physical hardware or timing.
  - The desktop app launched on any RID.
  - Desktop memory on .NET 11 against .NET 10.
  - Linux timing on native hardware ([#117](https://github.com/djflan/Bluestone/issues/117)), on either runtime.
  - Physical MIDI and audio devices and third-party plugins on .NET 11.
  - The Windows Arm64 minimum (Microsoft's pages conflict).

These are tracked in [engineering-handoff.md](engineering-handoff.md#7-unresolved-implementation-backlog).

## Rollback

Revert the single migration pull request. That restores `global.json` (`10.0.403`), `Directory.Build.props`
(`net10.0`), package versions, and the CI input. The EngineHost startup compaction does not depend on .NET 11 and may
be kept when reverting: on the physical Mac it also cut .NET 10's maximum pause from about 0.8 ms to about 0.53 ms.
The migration does not change the project format or any persisted data, so no project file needs to be converted
back.

## Effort

The November 22 planning estimate was **3–6 engineer-days** if dependencies and AOT work cleanly, or **1–3
engineer-weeks** if analyzer, package, or AOT fixes are needed. The November 24 campaign kept the 3–6 day estimate,
provided the analyzer fixes and the BenchmarkDotNet upgrade are routine. These are planning ranges, not measured
durations. They exclude vendor delays, hardware, and open timing or platform work.

## Affected files

| Area | Paths | Why |
| --- | --- | --- |
| SDK and target | `global.json`; `src/dotnet/Directory.Build.props` | Select SDK `11.0.100` and `net11.0`; strict warnings stay |
| Packages | `src/dotnet/Directory.Packages.props` | BenchmarkDotNet upgrade |
| Engine | `src/dotnet/Bluestone.Engine/EngineHost.cs` | Startup compaction before Ready |
| Analyzer fixes | CA2027: `src/dotnet/Bluestone.Plugins/Workers/WorkerProcess.cs`, `src/dotnet/Bluestone.Desktop/Views/SplashWindow.cs`; CA1832: integration and unit test files | Fix in code, no downgrades |
| CI | `.github/workflows/ci.yml` | Opt-in six-RID desktop and engine publish |
| Developer settings | `.vscode/launch.json` | Output path `net11.0` |
| Docs | `README.md`, `CONTRIBUTING.md`, `docs/engineering-handoff.md`, `docs/architecture.md`, ADRs 0002, 0026, 0043, 0044, `.github/agents/max.agent.md`, this report | SDK, paths, hardware floor, startup compaction |

Unchanged but reviewed in the feasibility work: the other `*.csproj` files, `src/dotnet/Native.targets`,
`src/native/**` (the Rust crates and C ABI do not depend on the .NET version), and
`.github/workflows/version.yml` (it selects the SDK from `global.json`).

### .NET 11 behavior changes to keep in mind

From the November 22 review. None caused a test failure in the probes, and the passing suites are not targeted
checks of these behaviors.

| Change | Bluestone surface | What the probe saw |
| --- | --- | --- |
| [`Math.Round` with `digits` rounds from the exact value](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/math-round-digits) | `SignalGradientEditor` shows `Math.Round(Position * 100, 1)` | No differences on a 0.001 grid (the UI's drag step). On a 0.000001 grid, 329 differences: 0.0005 shows 0.0 on .NET 10 and 0.1 on .NET 11. Imported positions can be finer than the UI step. |
| [Unix `NamedPipeServerStream` with `CurrentUserOnly` uses mode 0600](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions) | `EngineSupervisor`, `WorkerProcess` | macOS and Linux integration tests passed; no targeted permission check. |
| [NativeAOT rejects non-blittable `[UnmanagedCallersOnly]` signatures](https://learn.microsoft.com/en-us/dotnet/core/compatibility/interop/11/unmanagedcallersonly-non-blittable) | `AudioInput`, `AudioOutput`, `CoreMidiProvider` callbacks | AOT publishes passed for `osx-arm64`, `linux-arm64`, and emulated `linux-x64`. |

## Historical evidence (November 22–24, before the migration)

Everything below ran on disposable copies, usually with CA2027 and CA1832 downgraded
(`-p:WarningsNotAsErrors=CA2027%3BCA1832`). Those are diagnostic builds, not clean migration validation. The complete
original November 22 report, with every command, is preserved in the body of
[#112](https://github.com/djflan/Bluestone/issues/112).

### November 22: compatibility probes

Commit `0769073a62ed6ebbb6fd12e7a27d3d27cf1c7d82`. SDK `11.0.100` installed only in a scratch directory.

| Probe | Host | Result |
| --- | --- | --- |
| Restore full solution | macOS arm64 | Passed |
| Strict build | macOS arm64 | Failed on CA2027 at `WorkerProcess.cs:252`; downgrading CA2027 exposed eight CA1832 errors in audio/plugin tests |
| Build with both downgraded | macOS arm64 | Passed, 10 warnings |
| Solution tests | macOS arm64 | 2,495 passed, 24 skipped, 0 failed |
| Engine AOT publish + `--check-allocations` | macOS arm64 | Passed; Xcode-selection warning (full Xcode not installed) |
| Desktop and engine publish + `--check-allocations` | Linux arm64, native container | Passed |
| Solution tests | Linux arm64, native container | 2,477 passed, 37 skipped, 0 failed |
| Desktop and engine publish + `--check-allocations` | Linux x64, **emulated** container | Passed |
| Rust fmt, Clippy, tests | Linux x64, **emulated** | Passed (26 audio, 12 plugin-host tests) |
| Unit / UI / Integration test executables | Linux x64, **emulated** | 2,072 / 243 / 204 total, 0 failed; 6 / 1 (+2 not run) / 33 skipped |
| Rust checks | Linux arm64 | Not completed (command run from the wrong directory) |

Package metadata (Avalonia 12.1.6, SkiaSharp 3.119.4, CommunityToolkit.Mvvm 8.4.2, xUnit v3 4.0.3, CsCheck 5.0.1,
BenchmarkDotNet 0.15.8) allowed `net11.0`; that is framework compatibility, not a maintainer statement of .NET 11
support. BenchmarkDotNet 0.15.8's .NET 11 SDK validator threw `NotImplementedException` for `[ShortRunJob]` classes.

### November 23: Windows x64 campaign

Windows `10.0.26300`, Ryzen 7 2700X, 16 logical processors, 32 GB; commit `91715cd8fde5b35ebef4414717f15748a8d4f466`.
SDK/runtime `10.0.404`/`10.0.15` and `11.0.100`/`11.0.0`.

- Unit/integration/UI tests on .NET 11 (diagnostic build): 2,076 / 179 / 340 passed, 5 / 23 / 2 skipped. .NET 10
  solution tests: 2,595 passed, 30 skipped.
- An eight-round randomized campaign ran 216 test/variant pairs (eight samples per cell), five engine variants
  (ordinary and speed-tuned .NET 10; ordinary, speed-tuned, and runtime-async .NET 11). All passed
  `--check-allocations`.

| NativeAOT engine, app or full pressure | .NET 10 | .NET 11 |
| --- | --- | --- |
| 1 ms GC-pause gate | Missed in 32/32 runs | Met in 47/48 runs; one pause 1.32 ms |
| Median maximum GC pause | About 2.5–2.6 ms | About 0.40–0.43 ms |
| Five-minute GC-logged sample | 12 collections; about 312 KB promoted per gen0; pauses 1.3–3.1 ms | 12 collections; about 0–2 KB promoted after the first; pauses 0.15–0.27 ms |

- Engine dispatch p99 was about 0.52–0.55 ms across variants; no significant p99.9 or maximum difference was
  detected, which is not proof of equivalence.
- WinMM software loopback matched messages by position, so it is not identity, ordering, or physical-device evidence.
- Plugin-pipeline and in-process full-pressure probes missed their gates on both runtimes.
- One BenchmarkDotNet suite per runtime: .NET 11/.NET 10 geometric-mean ratio 0.911 over 11 benchmarks. An indication
  only; no performance improvement is claimed.
- `runtime-async` gave no reliable reason to change the default.
- Forced full collection at startup did **not** remove the .NET 10 pauses on Windows. The surviving objects were not
  identified.

### November 23: macOS CoreMIDI, virtual destination

Physical M5 Max, macOS 27.2.1; commit `f4147e59a64e26b3f4e48bb77da3bb5a46a2818a` plus uncommitted CoreMIDI and probe
changes. Two 60 s app-pressure runs per runtime, 23,104 messages each, **virtual** CoreMIDI destination.

| Runtime | Scheduled-arrival p99.9 / max, run A | Run B |
| --- | --- | --- |
| .NET 10 | 0.1920 / 0.1921 ms | 0.1094 / 0.1198 ms |
| .NET 11 | 0.3519 / 0.3520 ms | 0.1185 / 0.1191 ms |

These are arrival errors against native packet timestamps, not physical-hardware results. Same-time ordering
reversals and a first-due startup error of about 8 ms appeared on **both** runtimes. A CoreMIDI endpoint-visibility
race (property exports cached before `MIDIClientCreate`) also affected both; it was fixed separately in #119 (merged
before the migration, not verified on a physical Mac). One 60 s run per runtime had no engine
GC; a 240 s .NET 11 run hung, cause unknown, and did not recur on November 24.

### November 24: hosted macOS runner (simulated)

[Run 45300513263](https://github.com/djflan/Bluestone/actions/runs/45300513263), commit `24c6762` plus diagnostic
instrumentation, on a 3-vCPU virtual M1. Three alternating 300 s pairs: all six missed the 1 ms GC and 2 ms lateness
gates; median maximum GC pauses 4.87 ms (.NET 10) and 5.35 ms (.NET 11). In the CoreMIDI job, two of three .NET 11
runs could not open the sink (the known race). A noisy VM; not an acceptance gate. Artifacts `issue112-forwarded`
and `issue112-coremidi` expire on February 22, 2027.

### November 24: physical-Mac campaign

MacBook Pro Mac17,6, Apple M5 Max, 18 logical cores, 64 GB, macOS 27.2.1, on AC power. `main` at `24c6762` plus the
uncommitted CoreMIDI fix and an identity-checked loopback probe. SDK/runtime `10.0.403`/`10.0.14` and
`11.0.100`/`11.0.0`. Both NativeAOT engines (`osx-arm64`) passed `--check-allocations`. No thermal warning; load1
2.3–5.1 during the forwarded step, 1.0–2.3 after.

| Step (300 s each, app pressure) | .NET 10 | .NET 11 |
| --- | --- | --- |
| Forwarded output, 3 pairs | 3/3 passed; GC max 0.77–0.90 ms; 98.6 KB/s; working set 46.8 MB | 2/3 passed; GC max 0.80–1.19 ms; 98.6 KB/s; 47.0 MB |
| Forwarded with GC log, 1 each | Passed; gen0 promoted 193 / 3.3 / 3.3 KB; pauses 0.70 / 0.39 / 0.38 ms | Failed; 216 / 4.7 / 6.7 KB; pauses 1.16 / 0.49 / 0.62 ms |
| Virtual CoreMIDI, 3 pairs | 3/3 passed; longer pause 0.80–0.82 ms | 0/3 passed; longer pause 1.15–1.38 ms |
| Virtual CoreMIDI with startup compaction, 2 pairs | 2/2 passed; first GC 28–31 KB promoted; max 0.50–0.56 ms | 2/2 passed; first GC 24–32 KB promoted; max 0.52–0.62 ms |
| Tagged loopback, 3 runs each | All messages matched; 16,410–16,646 same-time reorders | All matched; 16,275–16,848 same-time reorders |

- MIDI dispatch: 0 ms on CoreMIDI; p99.9 0.03–0.06 ms forwarded, on both runtimes.
- The CoreMIDI fix held: 12 of 12 engine-to-CoreMIDI starts found the sink.
- Side observation, not investigated: the JIT-hosted probe process used more memory on .NET 11 (`gc.committed`
  329–371 MB against 189 MB; working set 477–549 MB against 261 MB). The NativeAOT engine's working set was about
  47 MB on both. This is why the desktop memory check is a gate above.
- Limits: one Mac, n=3 per step, n=1 for GC logging, n=2 for the compaction experiment, virtual CoreMIDI only, no
  Linux or Intel Mac.

Raw JSON, GC logs, and run logs from the Windows and physical-Mac campaigns are local session evidence; no persistent
public link was supplied.

### Not migration regressions

- Same-time MIDI ordering and the first-due startup observation occur on both runtimes and are tracked separately.
- The CoreMIDI initialization race affects both runtimes; its fix is a separate pull request.
- The Windows .NET 10 GC root cause is unexplained. It no longer blocks anything once the engine runs on .NET 11, but
  it stays open in case of rollback.

## Sources

Checked on 2026-11-22 unless noted:

1. [.NET and .NET Core Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core):
   .NET 11 GA 2026-11-10 (STS), .NET 10 LTS; both end November 14, 2028.
2. [.NET 11 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) and
   [.NET 11.0.0 release notes](https://github.com/dotnet/core/blob/main/release-notes/11.0/11.0.0/11.0.0.md).
3. [What's new in the .NET 11 runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
   (updated 2026-11-12; rechecked 2026-11-24).
4. [Minimum hardware requirements updated](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/11/minimum-hardware-requirements)
   (updated 2026-11-11; rechecked 2026-11-24).
5. [Breaking changes in .NET 11](https://learn.microsoft.com/en-us/dotnet/core/compatibility/11): the index says it
   is not a complete list.
6. [C# compiler breaking changes since C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/breaking-changes/compiler%20breaking%20changes%20-%20dotnet%2011).
7. [.NET 11 supported OS versions](https://github.com/dotnet/core/blob/main/release-notes/11.0/supported-os.md): its
   header ("Last Updated: 2026/09/28; Support phase: Go Live") predates GA.
8. [`global.json` SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).
