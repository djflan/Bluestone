---
name: pr-readiness
description: 'Self-assessment that decides whether a change is ready for a pull request. Use before opening or updating a non-trivial PR (new behavior, changed defaults, concurrency, lifecycle, IPC, timing claims, multi-file changes), when asked "is this ready for a PR", "self-review", or "pre-PR check". Catches what reviewers otherwise find one round at a time.'
---

# PR readiness

Run this before opening a non-trivial pull request, and again before pushing a large follow-up. Its job is to find in
one pass what review would otherwise find over several rounds. Do not request extra reviews to compensate: fix what
this finds, then open the PR.

**Non-trivial** means any of: new or changed behavior, a changed default, threads/locks/async, process or device
lifecycle, IPC, timing or performance claims, or more than a few files. Typos, comment fixes, and dependency bumps can
skip it.

## Procedure

Work through every step. Write the findings down (in the session, not the repository) and fix them before opening the
PR. Report the outcome in the PR's Verification section.

### 1. Diff review

1. Read the whole diff against the merge base (`git --no-pager diff origin/main...HEAD`), not just the last commit.
2. Run the `code-review` agent on the branch diff and fix every real finding. (Under `/team`, Grace's two-model review
   is this review; see `.github/skills/team/references/modes/build.md`.) If it raises a class of problem (a
   race, an unhandled state), go to step 3 for the whole class.
3. Look for leftovers: temporary CI changes, debug output, environment switches added for experiments, TODOs, files
   that should be gitignored.

### 2. Promises made earlier

- Collect every "not done", "risky", or "known limitation" note from this work: agent reports, ADR sections, commit
  messages, your own notes.
- Each must be fixed in this PR, or still true and written down in the PR and the docs. **Never switch a default
  on, or remove a fallback, while a related risk is open.**

### 3. States × actions

When the change touches anything with a lifecycle (a process, device, connection, stream, or recording), write the
table before you trust the code:

| | each user action or command (start, stop, record, punch in, change settings, quit) |
|---|---|
| each state (starting, ready, restarting, failed, disconnected, reconfigured, closing) | what must happen |

Also go through each transition that happens *during* an action (the connection drops mid-start, Stop arrives while
a start waits, the stream changes while a graph loads). For every cell:
- the outcome must be defined: it either waits, fails with a message, or is ignored on purpose;
- nothing may be silently dropped;
- the UI state (indicators, enabled commands) must match the real state.

Add a test for each non-obvious cell. When one cell turns out to be wrong, re-check the whole row and column; don't fix
just that cell.

### 4. Concurrency

For each new lock, flag, counter, or async wait:
- Which threads read and write it? Is check-then-act atomic with whatever can invalidate it?
- Does any UI-thread path block on another process or device? It must not.
- On a real-time thread, does anything allocate, take a contended lock, log, or yield? Waits there must be bounded.
- Can an event fire before its subscriber is ready, or after it has gone?

### 5. Claims match evidence

For every number or guarantee in the README, docs, ADRs, PR body, and user-facing messages:
- name the probe or test that shows it and what that metric actually measures (dispatch versus arrival; added latency
  versus total; which input path, device, buffer, and platform);
- word the claim to match exactly what was measured, and say what was not;
- user-facing messages promise only what the code guarantees ("recording ended", not "the take was kept" if it may not
  have been).

### 6. Tests that prove it

- Every changed behavior has a test that fails without the change.
- Timing-sensitive tests must survive a busy 2–3 core CI runner. Judge by the best case or a bounded median, never by
  one wall-clock sample. Allocation tests must tolerate one-time runtime work. Stress anything new that is flaky-prone
  locally (for example 10 runs with every core busy).
- Tests that need hardware, devices, or installed plugins skip with a reason.

### 7. Repository gates

Run them all and record each command with its result:

```sh
dotnet build src/dotnet/Bluestone.slnx -c Release          # warnings are errors
dotnet format src/dotnet/Bluestone.slnx --verify-no-changes
dotnet test -c Release --project src/dotnet/Bluestone.Tests.Unit
dotnet test -c Release --project src/dotnet/Bluestone.Tests.Integration
dotnet test -c Release --project src/dotnet/Bluestone.Tests.Ui
```

- **Rust changed:** in `src/native/`, run `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings` and
  `cargo test`.
- **Engine changed:** `dotnet publish src/dotnet/Bluestone.Engine -c Release -r <rid>` with no trim or AOT warnings,
  and `--check-allocations` reports 0 bytes.
- **Timing or real-time path changed:** run the relevant probes from `docs/hardware-test-plan.md` with nothing else
  running on the machine.
- **User-facing change:** launch the app and try the workflow.

### 8. Docs and PR body

- **Docs:** update ADRs, `docs/architecture.md`, `docs/engineering-handoff.md`, the roadmap, and the README wherever
  the change alters what they say. Search for stale wording such as "opt-in", "to be built", or old numbers.
- **PR body:** use `.github/pull_request_template.md` and keep its headings and checklist. List each command with
  its result. Say what was verified on hardware and what was simulated. Add the version token to the title only when
  asked.

## Ready means

Every step is done, every finding is fixed or written into the PR as a known limitation, and nothing in the diff
surprises you on a final read. If step 3 or 5 turned up more than a couple of problems, run steps 1–6 once more after
fixing them. Problems cluster.
