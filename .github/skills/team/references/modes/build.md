# Mode: build

Deliver a feature or fix end to end, ending at an open pull request. This mode runs autonomously between the hard
stops in `SKILL.md`, including the user UI decisions required by the directional, material, or routine tier in
`.github/skills/team/references/governance.md`. It follows
`.github/skills/focused-feature-delivery/SKILL.md` and ends with `.github/skills/pr-readiness/SKILL.md`.

## 1. Intake (Lucas)

1. Restate the request as: the observable outcome, 2–5 acceptance checks, and what is out of scope. One acceptance
   check says how a user reaches the new behavior in the running app (the setting, menu item, or control that turns
   it on). It is never out of scope; when the old behavior must stay, it stays as the default (see Lucas's charter,
   "Ship it reachable"). For UI work, quote the user's relevant reference and hint language as acceptance checks,
   record the supplied references, and classify the work as directional, material, or routine under
   `.github/skills/team/references/governance.md`.
   Only when no references were supplied may the user explicitly agree a comparison baseline.
2. Read only enough to find the owning code: repository instructions, the nearest owner, its caller, and its test.
   Use `.github/skills/team/references/roster.md` to staff the team.
3. Classify technical risk as routine, elevated, or high. Pick the smallest team and a model for each member, and
   record one-line staffing reasons (`.github/skills/team/references/roster.md` and
   `.github/skills/team/references/model-selection.md`).
4. Choose the branch. If the working tree has uncommitted changes you didn't make, stop and report; never stash or
   discard them.
   - The request names an existing PR or branch (for example, "fix the review findings on #92"): work on that branch
     (`gh pr checkout <n>`, or `git switch <branch>` and `git pull --ff-only`) and update that PR in phase 6. Never
     open a second PR for the same work.
   - Otherwise: `git fetch origin`, then `git switch -c team/<slug> origin/main`.
5. Write the run log.

## 2. Design (only for cross-layer, new-workflow, directional UI, or material UI work)

Dispatch only members with a distinct question, in parallel where independent, with a council position request
(`.github/skills/team/references/handoff.md`). Members are read-only except explicitly assigned design-only scratch
scenes; no production
implementation happens in this phase:
- Theo for structure and the ADR impact;
- Iris with Max for directional visual identity, a new control family, or a major workflow: Iris defines and critiques
  the directions; Max proves feasibility and renders the scratch scenes;
- Max for the rendered proposal and implementation plan required by the recorded UI tier;
- Caroline for the flow, keyboard path, and accessibility;
- Jules for whether a musician would get this, and the defaults.

Lucas decides non-UI technical questions and records material dissent. For directional UI, show 2-4 genuinely
distinct rendered directions beside the references, with per-element critique, and stop for the user's pick. For
material UI, show the rendered proposal; create alternatives only for a meaningful choice. Routine UI skips this
phase unless evidence raises its tier. Record the decisions and evidence required by
`.github/skills/team/references/governance.md`. If the
decision changes a consequential design, Theo drafts or updates the ADR in phase 3.

## 3. Build

Split the work by file ownership (`.github/skills/team/references/roster.md`) so that no file has two editors.
Dispatch only owners whose files or
expertise are actually required:
- Mike: application and domain code, plus its unit and integration tests.
- Kenji: `src/native/`, `Bluestone.Platform.*`.
- Max: `.axaml`, `Theme/`, styles, visual-only controls.
- Theo: the ADR, when one is needed.

During a UI phase Max takes NO view-model, API, or bug work. Return non-UI blockers to Lucas/Mike or the owning
specialist; never divert the UI owner to unblock non-UI work.

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

- Max (when the UI changed): launches the app and collects the evidence required by the recorded UI tier. For
  directional and material work, capture compact and normal screenshots and review them against the approved direction
  and supplied references with per-element critique. For routine work, capture appropriate before/after or final
  evidence. Capture BOTH a plain, realistic state (a fresh or small project, stopped, nothing selected, the default
  docked layout) AND populated, active states (long names, selection, playing, armed, signal present). A showcase
  scene alone hides what a user sees on first launch.
  He reaches the feature the way a user would, starting from the app,
  and tries every option it offers; an option that looks the same as another, or a feature with no way to reach it,
  is a finding. A gallery or test harness is extra evidence, not a substitute. Caroline then checks keyboard use and accessibility on the same
  build.
- Kenji (when native code changed): `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and `cargo test`
  in `src/native/`, plus the Docker cross-target clippy for every touched crate on Windows or Linux.
- Alice (when test design, lifecycle coverage, or flaky-risk work warrants her, and always when she is a required
  signer under `governance.md`): adds missing tests that fail without the change, writes the states × actions table
  for lifecycle changes, and runs the focused tests. When she is not a required signer, the owning builder supplies
  the changed-behavior tests and Grace checks them in review.
- Milo (when timing, real-time, or IPC changed): checks allocations and locks on the real-time path, and runs the
  relevant probes from `docs/hardware-test-plan.md`. Probes need a quiet machine, so nothing else runs alongside them.

A failing check goes straight back to the owner of that slice. Fix it, then rerun that check before widening again.
The full repository gates are not run here; they run once, on the final tree, in phase 6.

### Team sign-off

Before anything is shown to the user as final, committed for review, or put in a PR, Lucas commits the batch, pins the
SHA, and dispatches every required signer from `.github/skills/team/references/governance.md` ("Definition of
done") in parallel, read-only (except Milo, who signs alone after the others finish, because probes need a quiet
machine), with the acceptance checks quoted verbatim and the approved direction and references
attached. Each returns the sign-off format in `handoff.md`.

- Any "not done" goes back to the owner of that slice; the same signer re-checks the fix on the new SHA.
- A builder's claim that an acceptance check is "unchanged" or "already satisfied" is a claim for the signer to
  verify, never a reason to skip it.
- Lucas records every sign-off in the run log. He cannot sign off for a member or proceed past a "not done"; after
  two fix rounds without agreement he escalates to the user under `governance.md`.

For directional and material UI, only after every required signer has said "done" does Max capture FINAL compact and
normal screenshots beside the same references and Lucas stops for the user approval required by
`.github/skills/team/references/governance.md`. For routine UI, preserve the internal evidence and sign-offs without
an automatic user stop. If later edits materially change approved UI, re-sign and recapture it and obtain the
approval required by the tier before opening or updating the PR.

## 5. Review

1. Commit, then pin the snapshot: `git rev-parse HEAD`, and the diff `git --no-pager diff origin/main...HEAD`.
2. Lucas reads the whole diff himself and sweeps it for leftovers (`pr-readiness` step 1, items 1 and 3): temporary CI
   changes, debug output, experiment switches, TODOs, and files that should be ignored.
3. Dispatch, in parallel and read-only, on the pinned SHA:
   - Grace once for ordinary localized work, or twice on different available model families for the high-risk and
     broad categories in `.github/skills/team/references/model-selection.md`;
   - every specialist who worked on or owns a touched area, reviewing only their area.
   For UI work, include the recorded tier and all evidence and approvals that tier requires. Missing required evidence
   or an incorrectly low tier is a BLOCKING gate failure.
4. Lucas validates each finding against the code. Drop findings that are wrong, duplicated, or out of scope, and record
   why. Group the rest by root cause. Findings attached to a signer's "not done" are not Lucas's to drop or defer;
   see `governance.md`.
5. The owners fix all critical and high findings, and medium findings that are cheap or user-visible, then rerun the
   focused checks those fixes affect. Record the rest as known limitations; blocking UI gates cannot be waived as
   known limitations. A fix that touches a signed-off area makes that sign-off stale: the same signer re-checks it.
6. **At most 2 fix rounds.** Run a second review round only if the fixes were substantial or created a new class of
   risk. One exception, from `pr-readiness` ("Ready means"): if its states × actions or claims steps turned up more
   than a couple of problems, one more pass of steps 1–6 is allowed after fixing them.

Grace's review is this run's code review for `pr-readiness` step 1 item 2. When risk requires dual review, both Grace
passes use the same snapshot and independent model families when available. Do not also launch the built-in
`code-review` agent.

## 6. Ship

Order matters: every edit happens before the final gates, and the PR reports the gates on the SHA it ships.

1. Lucas runs `pr-readiness` steps 2–6 and fixes what they find. They can reuse Alice's, Milo's, and Grace's results
   for the current tree; don't redo work that is already recorded.
2. When documentation changes, Sabrina updates the affected ADRs, `docs/architecture.md`,
   `docs/engineering-handoff.md`, roadmap, README, or user guide. Lucas commits. Do not staff her when no docs or
   release material need work.
3. **Re-sign:** any commit in this phase (docs, gate fixes, CI fixes) that touches a signed area goes back to that
   signer before it is pushed.
4. **Final gates:** with every editor stopped, Alice runs `pr-readiness` step 7 when she is staffed; otherwise Lucas
   runs it. Record each command and result once on the final commit. If anything changes after this, rerun the gates
   that change affects.
5. Sabrina writes the PR body when she is staffed; otherwise Lucas writes it from
   `.github/pull_request_template.md`, using the final gate results. It lists each
   command with its result and what was physically verified versus simulated. Keep the template headings and
   checklist intact. For UI, attach the evidence required by the recorded tier beside supplied references.
   Directional UI includes the chosen direction, final screenshots, comparisons, and both user decisions; material
   UI includes its proposal, final screenshots, comparisons, and final approval; routine UI includes its internal
   visual evidence and any applicable comparison. These must survive in the PR, not only the run log. If approval
   evidence no longer matches the current UI, return to Max and the user for refreshed screenshots and approval.
   The body includes the sign-off table (signer, area, commit, verdict, evidence) from the run log.
6. Push the branch. Open the PR with `gh pr create`, or, when working on an existing PR, update its body with
   `gh pr edit`. Do not open or update a PR until every required sign-off is current and all approvals required by
   the recorded UI tier are present. Add a version token to the title only if the user asked.
7. CI does not start on push. Once the review rounds are finished and the branch is pushed, start it once with
   `gh workflow run ci.yml --ref <branch>` and check it once. If a job fails, read its log, fix it in one follow-up
   commit, get that area's signer to re-check it, push, and run CI once more. Do not poll an unchanged pending state; report that it is pending and finish.

## Done

Done is the team's verdict, not Lucas's: every required signer in `governance.md` has a current "done" (no later
commit touched their area) on the shipped commit. The PR is open (or the existing PR is updated), the final gates pass on the shipped commit or the failures are
reported, every validated finding is fixed or listed as a known limitation, and the summary names the team, models,
staffing reasons, sign-offs, and PR link. UI evidence and approvals match the recorded tier and current UI; missing
required gates or sign-offs are blockers, not known limitations. When the user has authorized a merge, merge only
after all of this holds.
