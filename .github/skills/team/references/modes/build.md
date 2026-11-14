# Mode: build

Deliver a feature or fix end to end, ending at an open pull request. This mode runs on its own: no approval pauses
except for the hard stops in `SKILL.md`. It follows `.github/skills/focused-feature-delivery/SKILL.md` and ends with
`.github/skills/pr-readiness/SKILL.md`.

## 1. Intake (Lucas)

1. Restate the request as: the observable outcome, 2–5 acceptance checks, and what is out of scope.
2. Read only enough to find the owning code: repository instructions, the nearest owner, its caller, and its test.
   Use `references/roster.md` to staff the team.
3. Pick a model for each member (`references/model-selection.md`).
4. Create the branch `team/<slug>` from an up-to-date `main` (`git fetch origin` and then
   `git switch -c team/<slug> origin/main`). If the working tree has someone else's uncommitted changes, stop and
   report; never stash or discard them.
5. Write the run log.

## 2. Design (only for cross-layer, new-workflow, or UI work)

Dispatch in parallel, read-only, with a short brief and a request for a council position (`references/handoff.md`):
- Theo for structure and the ADR impact;
- Max for the visual approach, plus a sketch of the layout, hierarchy, and states;
- Caroline for the flow, keyboard path, and accessibility;
- Jules for whether a musician would get this, and the defaults.

Lucas decides, writes the decision in the run log, and moves on. If the decision changes a consequential design, Theo
drafts or updates the ADR in phase 3.

## 3. Build

Split the work by file ownership so that no file has two editors:
- Mike: application and domain code, plus its unit and integration tests.
- Kenji: `src/native/`, `Bluestone.Platform.*`.
- Max: `.axaml`, `Theme/`, styles, visual-only controls.
- Theo: the ADR, when one is needed.

Run independent slices in parallel; run them in sequence when one consumes another's API. Each editor runs the
narrowest useful check after its first substantive edit and returns a build report. Lucas commits after each slice
lands and passes its focused check, with a short imperative message and the Copilot co-author trailer.

## 4. Verify

In parallel where possible:
- Alice: adds missing tests that fail without the change, writes the states × actions table for lifecycle changes, and
  runs the focused tests and then the full gates from `pr-readiness` step 7 once the code is stable.
- Milo (when timing, real-time, or IPC changed): checks allocations and locks on the real-time path, and runs the
  relevant probes from `docs/hardware-test-plan.md`.
- Kenji (when native code changed): `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and `cargo test`
  in `src/native/`, plus the Docker cross-target clippy for Windows or Linux code.
- Max (when the UI changed): launches the app, captures screenshots at compact and normal sizes, and reviews them
  against his quality bar. Caroline checks keyboard use and accessibility on the same build.

A failing gate goes straight back to the owner of that slice. Fix it, then rerun that check before widening again.

## 5. Review

1. Pin the snapshot: `git rev-parse HEAD`, and the diff `git --no-pager diff origin/main...HEAD`.
2. Dispatch, in parallel and read-only, on the pinned SHA:
   - Grace on a different model family from Mike;
   - every specialist who worked on or owns a touched area, reviewing only their area.
3. Lucas validates each finding against the code. Drop findings that are wrong, duplicated, or out of scope, and record
   why. Group the rest by root cause.
4. The owners fix all critical and high findings, and medium findings that are cheap or user-visible. Record the rest
   as known limitations.
5. **At most 2 fix rounds.** Run a second review round only if the fixes were substantial or created a new class of
   risk.

This phase satisfies `pr-readiness` step 1's code review. Do not also launch the built-in `code-review` agent.

## 6. Ship

1. Lucas runs `pr-readiness` steps 2–8 once and fixes what they find. Steps 3–6 can reuse Alice's, Milo's, and
   Grace's results; don't redo work that is already recorded.
2. Sabrina updates the docs the change affects (ADRs, `docs/architecture.md`, `docs/engineering-handoff.md`,
   roadmap, README, user guide) and writes the PR body from `.github/pull_request_template.md`. She lists each command
   with its result, and what was physically verified versus simulated.
3. Push the branch and open the PR with `gh pr create`. Add a version token to the title only if the user asked.
4. Check CI once. If a job fails, read its log, fix it in one follow-up commit, and push. Do not poll an unchanged
   pending state; report that it is pending and finish.

## Done

The PR is open, the gates pass locally or the failures are reported, every validated finding is fixed or listed as a
known limitation, and the summary names the team, the models, and the PR link.
