# Source

`Bluestone.slnx` is the solution. Each project is a direct child folder. Which project a concern
belongs in, and the dependency rules between projects, are described in
[docs/architecture.md](../../docs/architecture.md); the plugin projects are described in
[docs/plugin-hosting.md](../../docs/plugin-hosting.md).

The tests are in `Bluestone.Tests.Unit`, `Bluestone.Tests.Integration` (real processes, files, and
platform services; tests skip when what they need is missing), and `Bluestone.Tests.Ui` (headless
Avalonia). `Bluestone.Benchmarks` holds the benchmarks and real-time probes.

The Rust crates are in [src/native/](../native/), built by `dotnet build` through `Native.targets`.
`NativeTestPlugins.targets` packages the Rust test plugins as a VST3 bundle and a CLAP module for the
integration tests.
