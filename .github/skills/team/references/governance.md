# Team governance

This file is the canonical policy for UI evidence, constructive disagreement, and contract members. Agent charters
and mode playbooks summarize these rules and point here; they do not create competing versions.

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
5. Implement the chosen direction, then inspect the running app at compact and normal sizes with realistic content
   and relevant states.
6. Show final screenshots beside the references and stop for the user's approval before opening or updating the PR.
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
   affected workflow and states.
4. Stop for final user approval before opening or updating the PR. Preserve the evidence and approval in the PR.

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
