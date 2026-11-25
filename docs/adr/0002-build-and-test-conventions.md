# 0002. Build and test conventions

- Status: Accepted; runtime amended 2026-11-24 (.NET 11)
- Date: 2026-10-04

## Context

Bluestone needs reproducible builds across macOS, Windows, and Linux, strict compiler feedback, and a
default test suite that never depends on MIDI hardware or wall-clock timing.

## Decision

- **Runtime:** .NET 11 (STS), C# `latest`. `global.json` pins SDK 11.0.100 with `latestFeature` roll-forward (see the
  amendment below).
- **Shared settings:** `src/Directory.Build.props` enables nullable reference types, strict features,
  deterministic builds, `latest-recommended` analyzers, and code-style enforcement during build, with
  warnings treated as errors. Missing-XML-doc warnings (CS1591) are off: documentation is expected where
  intent, units, ownership, or timing are not obvious, not on every member.
- **Formatting:** `.editorconfig` is the source of truth; `dotnet format --verify-no-changes` must pass.
- **Dependencies:** central package management in `src/Directory.Packages.props` with transitive pinning.
- **Tests:** xUnit v3 on Microsoft.Testing.Platform (opted in via `global.json`, required by xUnit v3 4.x
  since the .NET 10 SDK). CsCheck provides property-based and fuzz-style tests; it is framework-agnostic and has
  no F# runtime dependency, unlike FsCheck.
- **Test projects:** `Bluestone.Tests.Unit` is fast and deterministic. Tests that touch the real file system
  or OS services go in `Bluestone.Tests.Integration` once needed. Tests of the Avalonia views go in
  `Bluestone.Tests.Ui`, on Avalonia's headless platform, so they need no display. Hardware-dependent checks are
  opt-in and never part of the default suite.
- **Boundaries:** architecture tests assert project dependency direction (for example, that
  `Bluestone.Domain` references only the base class library).

## Consequences

Analyzer upgrades can break the build when the SDK rolls forward; that is intended, and suppressions
require a written rationale. The new `dotnet test` experience differs from VSTest (`--filter` is replaced
by framework-specific options such as `--filter-method`), which contributors must learn.

## Amendment (2026-11-24): .NET 11

The runtime moved from .NET 10 (LTS) to .NET 11 (STS): SDK `11.0.100`, target `net11.0`. When checked on 2026-11-22,
both ended support on November 14, 2028, so STS costs no support time. The reason is the engine's GC pauses on
Windows; the evidence is in [the .NET 11 migration report](../dotnet11-feasibility.md). The migration fixed the new
CA2027 and CA1832 findings in code; warnings stay errors.

Before the migration, the runtime line above said SDK 10.0.300 while `global.json` pinned 10.0.403.
