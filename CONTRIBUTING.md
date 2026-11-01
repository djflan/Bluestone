# Contributing to Bluestone

Thanks for your interest in Bluestone. Contributions are welcome in sequencing, MIDI interoperability, audio, plugin hosting, device profiles, platform adapters, accessibility, documentation, and testing.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Bugs and small fixes:** open a pull request directly, or open an issue first if you are unsure.
- **New features or architectural changes:** open an issue to discuss the approach before writing much code. Consequential decisions are recorded as architecture decision records in [docs/adr/](docs/adr/).
- **Security problems:** do not open a public issue; see [SECURITY.md](SECURITY.md).

Read the [architectural principle](README.md#architectural-principle), the [architecture overview](docs/architecture.md), and the decision records first. In particular, device profiles (what an instrument understands) must stay independent of MIDI endpoints (where messages are sent).

## Development setup

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) (see `global.json` for the required feature band) and a stable [Rust toolchain](https://rustup.rs) (Rust 1.85 or later; `dotnet build` builds the crates in `src/native/` with `cargo`). On Linux, also install `libasound2-dev` (ALSA, for audio output). Then from the repository root:

```sh
dotnet restore src/dotnet/Bluestone.slnx
dotnet build src/dotnet/Bluestone.slnx
dotnet test src/dotnet/Bluestone.slnx
dotnet format src/dotnet/Bluestone.slnx --verify-no-changes
```

If you change Rust code, also run `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and `cargo test` in `src/native/`. After changing a crate's dependencies, run `cargo build` there once and commit `src/native/Cargo.lock` (builds use `--locked`).

Packages are restored only from nuget.org: the repository's `nuget.config` clears inherited sources and maps every package to it, as central package management requires. Add a mapping there if a package ever needs another source.

Warnings are treated as errors and code style is enforced during the build; `.editorconfig` defines the style. Build conventions are described in [ADR 0002](docs/adr/0002-build-and-test-conventions.md).

Rust code lives in `src/native/crates/` and is used only where [ADR 0026](docs/adr/0026-native-technology-policy.md) allows it: when a measurement shows managed code cannot meet a real-time requirement, or when a native interface such as plugin binaries requires it. Everything else is C#. Rust dependencies must be MIT-compatible (MIT, Apache-2.0, BSD, ISC, Zlib); GPL crates such as `vst3-sys` are not used.

Tests load Bluestone's own test plugins (`src/native/crates/bluestone_test_plugins`, built as a VST 3 bundle and a CLAP module). Third-party plugins used for local testing are installed on your machine and never committed.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Keep each pull request focused on one change.
3. Include tests that do not require contributors to own specific hardware or software. Tests must skip cleanly, with a reason, when what they need is missing: a platform MIDI service, an audio output device (CI runners have none), or an installed third-party plugin.
4. State which hardware behavior was physically verified and which was simulated (for example with a software synth or the MIDI monitor). See [docs/hardware-test-plan.md](docs/hardware-test-plan.md).
5. Update documentation, and add or update an ADR when you change a consequential design decision.
6. Make sure build, tests, and `dotnet format --verify-no-changes` pass. CI runs them on Linux, macOS, and Windows, except the Core MIDI tests, which are unreliable on hosted runners; if you change the Core MIDI adapter, run them locally on a Mac. The audio device tests and the tests with installed plugins and plugin editors (`AudioDeviceTests`, `InstalledPluginTests`, `PluginEditorTests`) skip in CI, which has no audio devices or plugins; if you change that code, run them locally.
7. Write commit messages as a short imperative summary, for example `Add WinMM MIDI output adapter for Windows`.

### Versions and releases

Every commit that lands on `main` is tagged with the next version (`vX.Y.Z`) by the [version workflow](.github/workflows/version.yml), and the app shows that version with its commit, for example `v0.4.0 a32f6d1`. The bump is patch unless the message of the commit that lands asks otherwise. Put a token in the **pull request title**: a merge commit includes it, and a squash merge uses it as the message. Tokens are case-insensitive.

| Token | Effect (from `v0.3.7`) |
|---|---|
| none | `v0.3.8` |
| `[minor]` | `v0.4.0` |
| `[major]` | `v1.0.0` |
| `[skip version]` | no new tag |
| `[release]` | bump as above, then build every platform and publish a GitHub Release |
| `[pre-release]` or `[prerelease]` | the same, marked as a pre-release |

`[skip version]` wins over `[major]`, which wins over `[minor]`, and a pre-release token wins over `[release]`. `[skip version]` with a release token builds and releases the latest tag again without a new version. To build and release an existing tag by hand (for example after a failed build), run the Version workflow from the Actions tab and give it the tag. Only the commit that lands on `main` is read, so a token in one of a pull request's own commits has no effect. Each run tags every untagged commit on `main` since the latest tag, oldest first, so a merge whose run was cancelled (GitHub keeps only the newest waiting run) is still tagged with its tokens; if several of them ask for a release, the last one is built.

A release builds a self-contained, unsigned archive for macOS (Apple silicon and Intel), Windows (x64 and ARM64), and Linux (x64 and ARM64). It does not run the tests again: the pull request checks already did. Builds made without tags (CI, or a clone without tags) show `v0.0.0`. To try the rules, run `.github/scripts/next-version.test.sh` and `.github/scripts/tag-commits.test.sh`.

## Device profiles

Profiles are data, not code. Follow the rules in [profiles/README.md](profiles/README.md) and the format in [docs/profiles.md](docs/profiles.md). In short: include provenance, state verification honestly, and only submit material you have the right to redistribute.

## Content you must not contribute

- Vendor logos, manual scans, firmware, ROM content, proprietary binaries, or copied vendor artwork or text.
- Third-party plugins, including free and open-source ones; install them locally for testing.
- MIDI files or other samples you do not have the right to redistribute.

Compatibility names must be used factually and must not imply vendor affiliation, certification, sponsorship, or endorsement. Describe other products as workflow or design references ("inspired by the workflow of …"), never as something Bluestone clones, ports, or is officially compatible with, and do not state who owns a name unless the owner's own published material says so. See [TRADEMARKS.md](TRADEMARKS.md).

## License

Bluestone is licensed under the [MIT License](LICENSE). By submitting a contribution you agree that it is licensed under the same terms.
