# Mode: build

Deliver a feature or fix end to end, ending at an open pull request. Lucas executes eligible Direct work himself or
scopes options with estimates and accepts the user's choice; then the run proceeds autonomously within that option,
between the hard stops in `SKILL.md`.
It follows `.github/skills/focused-feature-delivery/SKILL.md` and ends with `.github/skills/pr-readiness/SKILL.md`,
applied at the depth the option sets. Canonical policy: `.github/skills/team/references/governance.md`.

## 1. Scope and options (Lucas)

1. Restate the request as: the observable outcome, 2–5 acceptance checks, and what is out of scope. One acceptance
   check says how a user reaches the new behavior in the running app (the setting, menu item, or control that turns
   it on); when the old behavior must stay, it stays as the default (Lucas's charter, "Ship it reachable"). For UI
   work, quote the user's reference and hint language as acceptance checks and classify the work as directional,
   material, or routine (`governance.md`).
2. Read only enough to find the owning code: the nearest owner, its caller, and its test. A few reads, a few minutes.
3. If Direct fits, state the outcome and elapsed estimate in one line and proceed alone with focused validation. Do
   not write a formal plan, dispatch specialists, generate alternatives, or investigate unrelated work.
4. Otherwise present 2–3 options (`governance.md`, "Delivery options and the budget"): includes, skips and risk, team
   with a one-line reason per member, elapsed delivery time, aggregate agent effort, and expected user or CI waits.
   Recommend one and name any floor item in play. Stop for the user's pick unless the request already named a safe
   fitting option; accept that choice without redundant confirmation.
5. Choose the branch. If the working tree has uncommitted changes you didn't make, stop and report; never stash or
   discard them.
   - The request names an existing PR or branch: work on that branch (`gh pr checkout <n>`, or `git switch <branch>`
     and `git pull --ff-only`) and update that PR at ship. Never open a second PR for the same work.
   - Otherwise: `git fetch origin`, then `git switch -c team/<slug> origin/main`.
6. For non-Direct work, write the run log with the picked option, elapsed and aggregate estimates, real start
   timestamp, prioritized subtasks, and cut list. Direct uses only the one-line scope/estimate and final report.

## 2. Design (only when the picked option includes it)

Directional UI, cross-layer, or new-workflow work may need a design step. Dispatch only members with a distinct
question, read-only except assigned design-only scratch scenes:
- Theo for structure and ADR impact;
- Iris with Max for directional visual identity, a new control family, or a major workflow;
- Max for the rendered proposal the UI tier requires;
- Caroline for keyboard and accessibility of a new flow;
- Jules for whether a musician would get it, and the defaults.

For directional UI, show 2–4 genuinely distinct rendered directions beside the references and stop for the user's
pick. For material UI, show the rendered proposal. Routine UI skips this phase. Lucas decides non-UI technical
questions and records material dissent.

## 3. Build

Must-haves first, in the order from the run log. Split work by file ownership (`roster.md`) so no file has two
editors; when Lucas can do the work himself faster than dispatching it, he does.
- Mike: application and domain code, plus its tests.
- Kenji: `src/native/`, `Bluestone.Platform.*`.
- Max: `.axaml`, `Theme/`, styles, visual-only controls. During a UI phase Max takes no view-model, API, or bug work.
- Theo: the ADR, when one is needed.

All editors share one checkout. Run slices in sequence by default; run two in parallel only when they are in
different projects and neither consumes the other's API. Each editor runs the narrowest useful check after its first
substantive edit and returns a build report. Lucas commits when the editors in the batch have stopped and the focused
checks pass, staging reported paths explicitly (`git add <paths>`, never `git add -A`) with the Copilot co-author
trailer.

Check the wall-clock budget before every substantial dispatch or investigation and after each batch or commit. At
about 1.5× the elapsed estimate, stop and report (`governance.md`, "Overrun stop").

## 4. Check and review (one pass)

Whoever the option staffed checks the pinned commit once, in parallel where read-only, Milo alone after the others:

- **Visible change:** Lucas or Max builds and launches the app from the CLI
  (`./src/dotnet/Bluestone.Desktop/bin/<Configuration>/net11.0/Bluestone.Desktop`) and looks at the change, reached
  the way a user reaches it, in a plain state and the state the change affects. Capture screenshots for the PR. The
  options decide whether compact and normal sizes, more states, or an accessibility pass are included.
- **Native code changed:** Kenji runs `cargo fmt --check`, `cargo clippy --all-targets -- -D warnings`, and
  `cargo test` in `src/native/`, plus the Docker cross-target clippy for touched Windows or Linux code.
- **Timing, real-time, or IPC changed:** Milo checks the real-time path and runs the relevant probes.
- **Code review:** Grace once, when the option includes review; twice on different model families only when a miss
  is expensive (`model-selection.md`). Do not also launch the built-in `code-review` agent.
- **Tests:** the builder supplies tests for changed behavior; Alice joins when the option includes test design or
  lifecycle coverage.

Lucas validates each finding against the code (`governance.md`, "Decision authority"):
- fix it when this change caused it, it misses an acceptance check, or it touches the floor;
- backlog it (PR limitations and `docs/engineering-handoff.md` section 7) when it is a real defect outside that;
- drop it, with a reason in the run log, when it is wrong, duplicated, or speculative.

Fixes go in one batch; rerun only the checks they affect, and re-consult a member only if a fix substantively changed
their area. Run a second pass only when a fix created a new class of risk.

For directional and material UI, show the final screenshots beside the references and stop for the user's approval
before the PR is opened or updated. Routine UI has no automatic user stop.

## 5. Ship

1. Lucas reads the whole diff once (`git --no-pager diff origin/main...HEAD`) for leftovers: debug output, temporary
   CI changes, experiment switches, TODOs, files that should be ignored.
2. Apply the `pr-readiness` steps that fit the option; reuse results already recorded.
3. Update docs only where the change makes them wrong. Sabrina joins only for substantial docs or release material.
4. Run the **applicable mandatory gates** once, on the final commit, with every editor stopped
   (`pr-readiness` step 7). For cosmetic changes, check the desktop build and affected UI, format-check changed C#
   files if present, and run any applicable focused UI test. Record each command and result. If anything changes
   afterwards, rerun the affected gates.
5. Write the PR body from `.github/pull_request_template.md`, keeping its headings and checklist: the option picked,
   elapsed and aggregate estimates against actuals, itemized waits, each command with its result, what was verified
   versus simulated or skipped, deferred
   items, and the UI evidence and approvals the tier requires.
6. Push and open the PR with `gh pr create`, or update the existing PR with `gh pr edit`. Add a version token to the
   title only if the user asked.
7. CI runs only if the option includes it or the user asks: `gh workflow run ci.yml --ref <branch>`, checked once. If
   a job fails, read its log, fix it in one follow-up commit, push, and run CI once more.

## Done

The picked option's outcome and acceptance checks are met, the floor holds, the applicable mandatory gates pass on
the shipped commit or meet the [pre-existing local test exception](../governance.md#pre-existing-local-test-failure), every validated
finding is fixed, backlogged, or dropped with a reason, and UI approvals
match the recorded tier. The PR is open (or updated) and the summary gives the option, elapsed and aggregate estimates
against actuals, itemized waits, team
and models, findings and outcomes, what was not verified, and what was deferred. When the user has authorized a
merge, proceed after the applicable gates pass. If CI is run, it must pass on the current PR head before merge.
