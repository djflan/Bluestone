# 0007. Data-first device profiles

- Status: Accepted; amended by 0031
- Date: 2026-10-04

## Context

Bluestone must support many instruments without recompiling, accept community contributions safely,
and keep vendor knowledge out of the sequencing core and out of endpoint adapters.

## Decision

- Profiles are human-reviewable JSON files (`*.bluestone-profile.json`) with a `schemaVersion`. The
  loader (`Bluestone.Profiles`) contains no device content; shipped content lives in `/profiles`.
- Loading is validating and non-throwing: every finding carries a JSON path; any error rejects the
  profile; warnings do not. Unknown properties warn, so newer files degrade gracefully, and custom
  data belongs in namespaced `extensions`, which round-trip.
- Profiles are untrusted: size (4 MiB), JSON depth (32), array counts, string lengths, and control
  characters are bounded. SysEx templates are validated token by token and rendered only through
  typed parameters with declared ranges.
- Every profile must carry provenance with confirmed redistribution rights and an honest
  verification level. Resets and bulk messages are marked so the UI confirms before sending.
- Program numbers in JSON are one-based, matching printed voice lists; the model stores wire values.
- `Bluestone.Profiles` depends on `Bluestone.Domain` only; the domain never references profiles.

## Consequences

Routine instrument support is a data contribution with automated validation in the test suite.
Features such as checksummed SysEx, NRPN tables, or inheritance between profiles require a schema
version bump and a migration path.
