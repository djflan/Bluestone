# Team governance

This file is the canonical policy for the definition of done, UI evidence, constructive disagreement, and contract
members. Agent charters and mode playbooks summarize these rules and point here; they do not create competing
versions.

## Definition of done: team sign-off

"Done" is a team decision, made the way a real development team makes it. Lucas does not decide it alone. Work is
done only when every required signer for the areas the change touches has signed off, in writing, on the current
commit. Lucas defines the acceptance checks, staffs the signers, collects the sign-offs, and records them. He cannot
sign off for a specialist, skip a required signer, or overrule a "not done".

### Required signers

| The change touches | Required signers |
|---|---|
| Any visible UI: `.axaml`, `Theme/`, `Controls/`, `Views/`, styles, visual-only controls | Max (owner); an independent visual signer (Iris for directional or material work whenever she is under contract or retained, otherwise Caroline); Caroline for keyboard, focus, and accessibility |
| A workflow, a default, terminology, or anything a musician sees and judges | Jules (see "Evidence for signers" below) |
| `src/native/`, `Bluestone.Platform.*`, the plugin host, platform MIDI or audio adapters | Kenji |
| `Bluestone.Engine*`, playback, audio, signal, MIDI timing, IPC, threads, locks, or any timing or latency claim | Milo |
| More than one project layer, a new abstraction, a protocol, a file format, or an ADR | Theo |
| Lifecycle, concurrency, or changed behavior that needs new tests | Alice |
| Any code change | Grace (the code review; two model families when risk requires it) |
| User-visible docs, ADRs, release notes, the deferred backlog | Sabrina |

The builder of a slice signs off their own slice, but the builder's own sign-off is never the only one for a visible
or risky area: the independent signer in the table above must also sign. Lucas may add signers; he may not remove a
required one. When a touched area has no staffed signer, staff one before calling the work done. Roster staffing
heuristics and "smallest team" guidance never remove a required signer; they only decide who else joins.

### Evidence for signers

Lucas lists the required states at intake: always a plain, realistic state (a fresh or small project, stopped,
nothing selected, default layout) plus the populated and active states the change affects. Max captures exactly those
states from the running app at compact and normal sizes. The independent visual signer, who has a shell, launches the
app and checks those states and any others they choose; missing or mismatched captures are "not met". Jules, who does
not run the app, signs after the visual signer, from that checked capture set. Whenever a change Jules signs is visible in the
running app, whatever files it touches (a label or default set in a view-model, resource, or setting), Max captures
it and an independent visual signer checks the captures before Jules signs. For a default or terminology change with
no visible UI, Jules signs from the diff, the docs, and the CLI or log output that shows the behavior.

A sign-off is current when no later commit touched that signer's area. Any commit that touches a signed area,
including review fixes, gate fixes, docs edits, and CI fixes after the PR is open, makes that sign-off stale; the same
signer re-checks before the commit is pushed.

### What a sign-off must contain

Each signer returns the sign-off format in `handoff.md` against the pinned commit:

- every acceptance check in their area marked **met** or **not met**, each with evidence (a command and its result,
  a screenshot, a probe, or a code reference);
- for UI: the running app inspected the way a user reaches it, not only a harness or gallery, in BOTH a plain,
  realistic state (a fresh or small project, stopped, nothing selected) AND populated, active states (playing,
  selected, armed, signal present), at compact and normal sizes. Each screenshot is placed side by side with the
  approved direction or supplied references and critiqued per element. A visible difference that the approved
  direction requires but that is missing is "not met";
- any claim that something is "unchanged", "already satisfies the direction", or "out of scope" for an acceptance
  check is verified by the signer against the reference, not taken from the builder's report;
- a verdict: **done**, **not done** with blocking findings in the `handoff.md` finding format, **not done
  (unverifiable)**, or **done (user-accepted gap: <what>)**, as defined in `handoff.md`.

### When a signer says "not done"

A "not done" blocks commit-for-review, opening or updating a PR, and merging. The owner fixes the findings and the
same signer re-checks. Findings attached to a "not done" are outside Lucas's validate-and-drop and known-limitation
paths: only the signer who raised them, or the user, can clear them, whatever their severity. A signer's "not done" is
never an "ordinary delivery question" or "subjective polish" for Lucas to decide.

When a signer cannot obtain the evidence their area requires (hardware, a platform, a quiet machine for probes), they
return "not done (unverifiable)" with what is missing. That triggers the missing-device stop in `SKILL.md`: the user
decides whether to accept the gap. An accepted gap turns the verdict into "done (user-accepted gap: <what>)", which
counts as done and is listed as not verified in the PR and the sign-off table. Only the user can accept a gap. If the signer and the owner still disagree after two fix rounds, or Lucas thinks the objection
is out of scope, Lucas stops and escalates to the user with both positions in the constructive-disagreement format.
He never resolves it by dropping the signer.

### Sign-offs are not waivable by speed

User instructions such as "autopilot", "merge when done", or "don't spend all night" set pace and authority to
proceed; they never waive sign-offs or the UI approvals required by the tier. When time is short, cut scope and say
so, or report that the work is not done. "Merge when done" means merge after every required sign-off and user
approval is in place.

### Recording

The run log and the PR body carry a sign-off table: signer, area, commit, verdict, and the evidence link. A PR with a
missing or stale sign-off (the signed commit is older than a change to that area) is not ready.

## UI change tiers

Lucas classifies a visible change during intake and records the tier in the run log. When uncertain between two
tiers, use the higher tier. A later increase in scope can raise the tier; record why.

### Directional UI

Use this tier for a new or substantially reworked workflow, a broad redesign, a new design language or control
family, or any consequential visual or interaction choice.

Required evidence and stops:

1. Quote the user's relevant reference and hint language in the acceptance checks and preserve supplied references.
2. Produce 2–4 genuinely distinct rendered directions before production implementation.
3. Compare every direction beside the supplied references with a per-element critique. When no references were
   supplied, the user may explicitly agree to a baseline.
4. Stop for the user's direction pick. Lucas and the team may recommend but never choose for the user.
5. Implement the chosen direction, then inspect the running app at compact and normal sizes in a plain, realistic
   state and in populated, active states.
6. After the required team sign-offs (see "Definition of done"), show final screenshots beside the references and
   stop for the user's approval before opening or updating the PR. Internal sign-off comes first, so the user never
   has to find what the team missed.
7. Preserve the direction pick, final approval, screenshots, and comparisons in the PR. Material visual changes
   after approval require refreshed screenshots and approval.

### Material UI

Use this tier for a significant layout, interaction, density, hierarchy, or component change within an already
approved design language.

Required evidence and stops:

1. Quote the user's relevant acceptance language and preserve supplied references.
2. Produce a rendered proposal and compare it beside the references or approved direction. Produce alternatives
   only when there is a meaningful product or visual choice; do not manufacture variants for ceremony.
3. Implement after the proposal is agreed, inspect the running app at compact and normal sizes, and exercise the
   affected workflow and states, including a plain, realistic state.
4. After the required team sign-offs, stop for final user approval before opening or updating the PR. Preserve the
   evidence and approval in the PR.

### Routine UI

Use this tier for a label or copy correction, minor spacing or alignment repair, accessibility fix, incorrect visual
state, or small regression fix that does not change the approved direction or workflow.

Required evidence:

1. Capture appropriate before/after or final screenshot evidence and run the relevant internal visual,
   accessibility, or behavior check.
2. Compare against supplied references or the approved design when the change touches their acceptance criteria.
3. Do not require multiple rendered directions or a user hard stop unless the result changes the approved direction,
   introduces a consequential choice, or the user explicitly requested approval.

Routine does not mean unreviewed. If the evidence shows a broader design problem, raise the tier before continuing.

## Professional parity gate

Bluestone must look and behave credible beside the professional tools its users already trust. For every material or
directional UI review, ask:

> Would a working musician or engineer judge this as a finished professional interface beside the supplied
> references and the approved Bluestone direction?

A "no" is blocking, but it must be supported by observable deficiencies such as hierarchy, alignment, density,
legibility, affordance, state clarity, workflow efficiency, precision, consistency, or rendering quality. Name the
defect and the smallest correction. Competitive comparison is a quality check, not permission to imitate trade
dress, and personal embarrassment is never evidence.

## Constructive disagreement

Every member is expected to surface material concerns inside their expertise, regardless of who proposed the idea.
Do not manufacture dissent, defer automatically to hierarchy, or prolong a settled debate without new evidence.

When disagreeing, state:

- **Concern:** the disputed decision in one sentence.
- **Evidence:** the code, render, test, reference, ADR, product principle, or user workflow that supports the concern.
- **Impact:** what fails or degrades, for whom, and when.
- **Alternative:** the smallest viable better option.
- **Confidence:** high, medium, or low.
- **What would change my mind:** the evidence or constraint that would resolve the disagreement.

Lucas records material dissent and makes ordinary delivery decisions using the product vision and ADRs. After a
decision, the team proceeds unless new evidence appears. Escalate to the user only when the choice changes the
requested outcome, is hard to reverse, crosses an explicit user preference, requires a UI approval above, or leaves
a material specialist objection unresolved.

## Contract members

Contract members cover a demonstrated gap through a bounded trial. A successful one-off assignment does not by
itself justify a permanent role.

### Hire proposal

Before creating a charter, changing the roster, or dispatching the hire, Lucas presents and waits for user approval
of:

- the missing capability and why the current roster cannot cover it adequately;
- alternatives considered, including using or training a current member;
- a draft charter and its authority and file boundaries;
- a bounded trial deliverable and success criteria;
- how the hire collaborates with existing members; and
- the expected number or scale of assignments and model tier when knowable.

The approved trial member is marked **contract**. Trial status is not permanent-team status.

### End-of-trial review

At the end of the bounded trial, Lucas reports:

- the trial objective and outcome;
- validation evidence and remaining limitations;
- strengths and shortcomings;
- collaboration and handoff quality;
- overlap with existing members;
- whether the capability is likely to recur;
- relative cost and latency when available; and
- Lucas's recommendation.

The user chooses one outcome:

- **Release:** end the role. Remove its charter and every active reference to it (roster row and staffing
  heuristics, `model-selection.md`, mode playbooks, other charters, and `docs/agent-team.md`), and reassign any duty
  the playbooks gave it by name to an existing member.
- **Extend:** keep contract status for another explicitly bounded trial.
- **Retain:** remove the contract marker everywhere it appears and make the role permanent.

Applying an approved hire, release, extend, retain, or replacement edits team files, which is team maintenance, not
part of the run. Make those edits on their own branch and pull request, never in a read-only mode's working tree or
inside a build run's product branch. A build run may start that maintenance PR after its own PR is open.

Replacing an existing member is a separate organizational decision, never a routine trial outcome. It requires an
overlap analysis, a responsibility-migration plan, risks, and explicit user approval. Never silently retain, release,
or replace a member.
