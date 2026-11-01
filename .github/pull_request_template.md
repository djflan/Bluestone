<!-- Versioning: merging tags a patch version. Add [minor], [major], or [skip version] to the PR title to change that,
     and [release] or [pre-release] to build and publish a release. See CONTRIBUTING.md, "Versions and releases". -->

## Summary

<!-- What does this change and why? Link related issues, e.g. "Fixes #123". -->

## Verification

<!-- How was this tested? State which hardware behavior was physically verified and which was simulated. -->

- [ ] `dotnet build src/dotnet/Bluestone.slnx` and `dotnet test src/dotnet/Bluestone.slnx` pass
- [ ] `dotnet format src/dotnet/Bluestone.slnx --verify-no-changes` passes
- [ ] If Rust code changed: `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and `cargo test` pass in `src/native/`
- [ ] Tests do not require specific hardware, an audio device, or installed plugins (they skip with a reason)
- [ ] Documentation and ADRs are updated where needed
- [ ] Contributed data (profiles, samples) is redistributable and has provenance; no third-party plugins are committed
