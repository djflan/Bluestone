# Contributing to Bluestone

Thanks for your interest in Bluestone. Contributions are welcome in sequencing, MIDI interoperability, device profiles, platform adapters, accessibility, documentation, and testing.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Bugs and small fixes:** open a pull request directly, or open an issue first if you are unsure.
- **New features or architectural changes:** open an issue to discuss the approach before writing much code. Consequential decisions are recorded as architecture decision records in [docs/adr/](docs/adr/).
- **Security problems:** do not open a public issue; see [SECURITY.md](SECURITY.md).

Read the [architectural principle](README.md#architectural-principle), the [architecture overview](docs/architecture.md), and the decision records first. In particular, device profiles (what an instrument understands) must stay independent of MIDI endpoints (where messages are sent).

## Development setup

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json` for the required feature band) and a stable [Rust toolchain](https://rustup.rs) (Rust 1.85 or later; `dotnet build` builds the crates in `native/` with `cargo`). On Linux, also install `libasound2-dev` (ALSA, for audio output). Then from the repository root:

```sh
dotnet restore src/Bluestone.slnx
dotnet build src/Bluestone.slnx
dotnet test src/Bluestone.slnx
dotnet format src/Bluestone.slnx --verify-no-changes
```

If you change Rust code, also run `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and `cargo test` in `native/`. After changing a crate's dependencies, run `cargo build` there once and commit `native/Cargo.lock` (builds use `--locked`).

Packages are restored only from nuget.org: the repository's `nuget.config` clears inherited sources and maps every package to it, as central package management requires. Add a mapping there if a package ever needs another source.

Warnings are treated as errors and code style is enforced during the build; `.editorconfig` defines the style. Build conventions are described in [ADR 0002](docs/adr/0002-build-and-test-conventions.md).

Rust is used only where [ADR 0026](docs/adr/0026-native-technology-policy.md) allows it. Rust dependencies must be MIT-compatible (MIT, Apache-2.0, BSD, ISC, Zlib); GPL crates such as `vst3-sys` are not used. Plugins used for testing are installed locally and never committed.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each pull request focused on one change.
3. Include tests that do not require contributors to own specific hardware. Platform tests must skip cleanly when their MIDI service is unavailable.
4. State which hardware behavior was physically verified and which was simulated (for example with a software synth or the MIDI monitor). See [docs/hardware-test-plan.md](docs/hardware-test-plan.md).
5. Update documentation, and add or update an ADR when you change a consequential design decision.
6. Make sure build, tests, and `dotnet format --verify-no-changes` pass. CI runs them on Linux, macOS, and Windows, except the CoreMIDI tests, which are unreliable on hosted runners; if you change the CoreMIDI adapter, run them locally on a Mac.
7. Write commit messages as a short imperative summary, for example `Add WinMM MIDI output adapter for Windows`.

## Device profiles

Profiles are data, not code. Follow the rules in [profiles/README.md](profiles/README.md) and the format in [docs/profiles.md](docs/profiles.md). In short: include provenance, state verification honestly, and only submit material you have the right to redistribute.

## Content you must not contribute

- Vendor logos, manual scans, firmware, ROM content, proprietary binaries, or copied vendor artwork or text.
- MIDI files or other samples you do not have the right to redistribute.

Compatibility names must be used factually and must not imply vendor affiliation, certification, sponsorship, or endorsement.

## License

Bluestone is licensed under the [MIT License](LICENSE). By submitting a contribution you agree that it is licensed under the same terms.
