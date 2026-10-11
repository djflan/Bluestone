# Source

`Bluestone.slnx` is the solution. Each project is a direct child folder. Which project a concern
belongs in, and the dependency rules between projects, are described in
[docs/architecture.md](../../docs/architecture.md). The Rust crates are in
[src/native/](../native/), built by `dotnet build` through `Native.targets`.
