# Mode: build

Deliver a feature or fix end to end, ending at an open pull request. This mode runs on its own: no approval pauses
except for the hard stops in `SKILL.md`. It follows `.github/skills/focused-feature-delivery/SKILL.md` and ends with
`.github/skills/pr-readiness/SKILL.md`.

## 1. Intake (Lucas)

1. Restate the request as: the observable outcome, 2–5 acceptance checks, and what is out of scope.
2. Read only enough to find the owning code: repository instructions, the nearest owner, its caller, and its test.
   Use `references/roster.md` to staff the team.
3. Pick a model for each member (`references/model-selection.md`).
4. Choose the branch. If the working tree has uncommitted changes you didn't make, stop and report; never stash or
   discard them.
   - The request names an existing PR or branch (for example, "fix the review findings on #92"): work on that branch
     (`gh pr checkout <n>`, or `git switch <branch>` and `git pull --ff-only`) and update that PR in phase 6. Never
     open a second PR for the same work.
   - Otherwise: `git fetch origin`, then `git switch -c team/<slug> origin/main`.
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

Split the work by file ownership (`references/roster.md`) so that no file has two editors:
- Mike: application and domain code, plus its unit and integration tests.
- Kenji: `src/native/`, `Bluestone.Platform.*`.
- Max: `.axaml`, `Theme/`, styles, visual-only controls.
- Theo: the ADR, when one is needed.

All editors share one checkout, so a build or test run sees everyone's in-progress edits. Run slices **in sequence**
by default. Run two editors in parallel only when their slices are in different projects and neither consumes the
other's API, and treat their focused checks as provisional until both have stopped. Each editor runs the narrowest
useful check after its first substantive edit and returns a build report.

Lucas commits only when every editor in the batch has stopped and the focused checks pass. He stages the reported
paths explicitly (`git add <paths>`, never `git add -A`) and writes a short imperative message with the Copilot
co-author trailer.

## 4. Verify

Lucas assigns test files before dispatch: Milo gets the probe and timing-test files he needs; Alice gets every other
test file she adds or changes. No test file goes to both. Run the steps below in this order, so every check sees a
stable tree; only read-only work (Caroline) may overlap another step.

- Max (when the UI changed): launches the app, captures screenshots at compact and normal sizes, reviews them against
  his quality bar, and polishes until they pass. Caroline then checks keyboard use and accessibility on the same
  build.
- Kenji (when native code changed): `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and `cargo test`
  in `src/native/`, plus the Docker cross-target clippy for every touched crate on Windows or Linux.
- Alice: adds missing tests that fail without the change, writes the states × actions table for lifecycle changes, and
  runs the focused tests.
- Milo (when timing, real-time, or IPC changed): checks allocations and locks on the real-time path, and runs the
  relevant probes from `docs/hardware-test-plan.md`. Probes need a quiet machine, so nothing else runs alongside them.

A failing check goes straight back to the owner of that slice. Fix it, then rerun that check before widening again.
The full repository gates are not run here; they run once, on the final tree, in phase 6.

## 5. Review

1. Commit, then pin the snapshot: `git rev-parse HEAD`, and the diff `git --no-pager diff origin/main...HEAD`.
2. Lucas reads the whole diff himself and sweeps it for leftovers (`pr-readiness` step 1, items 1 and 3): temporary CI
   changes, debug output, experiment switches, TODOs, and files that should be ignored.
3. Dispatch, in parallel and read-only, on the pinned SHA:
   - Grace twice, once on a Claude model and once on a GPT model (`references/model-selection.md`);
   - every specialist who worked on or owns a touched area, reviewing only their area.
4. Lucas validates each finding against the code. Drop findings that are wrong, duplicated, or out of scope, and record
   why. Group the rest by root cause.
5. The owners fix all critical and high findings, and medium findings that are cheap or user-visible, then rerun the
   focused checks those fixes affect. Record the rest as known limitations.
6. **At most 2 fix rounds.** Run a second review round only if the fixes were substantial or created a new class of
   risk. One exception, from `pr-readiness` ("Ready means"): if its states × actions or claims steps turned up more
   than a couple of problems, one more pass of steps 1–6 is allowed after fixing them.

Grace's dual review is this run's code review for `pr-readiness` step 1 item 2 and for the two-model review rule. Do
not also launch the built-in `code-review` agent.

## 6. Ship

Order matters: every edit happens before the final gates, and the PR reports the gates on the SHA it ships.

1. Lucas runs `pr-readiness` steps 2–6 and fixes what they find. They can reuse Alice's, Milo's, and Grace's results
   for the current tree; don't redo work that is already recorded.
2. Sabrina updates the docs the change affects (ADRs, `docs/architecture.md`, `docs/engineering-handoff.md`,
   roadmap, README, user guide). Lucas commits.
3. **Final gates:** with every editor stopped, Alice runs `pr-readiness` step 7 once on the final commit and records
   each command with its result. If anything changes after this, rerun the gates that change affects.
4. Sabrina writes the PR body from `.github/pull_request_template.md`, using the final gate results. It lists each
   command with its result and what was physically verified versus simulated.
5. Push the branch. Open the PR with `gh pr create`, or, when working on an existing PR, update its body with
   `gh pr edit`. Add a version token to the title only if the user asked.
6. Check CI once. If a job fails, read its log, fix it in one follow-up commit, and push. Do not poll an unchanged
   pending state; report that it is pending and finish.

## Done

The PR is open (or the existing PR is updated), the final gates pass on the shipped commit or the failures are
reported, every validated finding is fixed or listed as a known limitation, and the summary names the team, the
models, and the PR link.
