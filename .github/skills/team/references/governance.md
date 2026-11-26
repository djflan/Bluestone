# Team governance

This file is the canonical policy for delivery options, decision authority, UI evidence, constructive disagreement,
and contract members. Agent charters and mode playbooks summarize these rules and point here; they do not create
competing versions.

## Context: who Bluestone serves today

Bluestone is a new application with one user, the maintainer. Treat every request like a client who needs it as soon
as possible: deliver the agreed outcome quickly and correctly, and spend verification where a miss would be expensive
or hard to undo. Most other misses are found in use and fixed forward in the next change. Checking that costs more
than the bug it might find is waste.

## Delivery options and the budget

Before any build work, Lucas scopes the request quickly (a few targeted reads, a few minutes; never a council) and
presents 2–3 options. Each option states:

- **Includes:** the outcome and the acceptance checks it delivers.
- **Skips:** what it does not do or verify, and what that risks.
- **Team:** who works on it and why (often Lucas alone).
- **Estimate:** active working time across everyone, plus expected waits on the user or CI, stated separately.

Lucas recommends one, then waits for the user's pick. The picked option is the contract for the run: its scope, its
verification, and its time budget. A typical set:

| Option | Shape |
|---|---|
| **Fast** | The outcome, focused tests and build, one look at it. Known gaps listed. |
| **Right** | Fast, plus the edge cases and review that a miss would make expensive. |
| **Thorough** | Right, plus broader specialist review, platform coverage, or probes. Offer only when the risk warrants it. |

Trivial work (an estimate under about 15 minutes, with no item from "The floor" in play) skips the menu: Lucas states
the estimate and the plan in one line and proceeds unless the user objects.

**Prioritize inside the option.** Order subtasks so the must-haves land first, and keep an explicit cut list. If time
runs short, a working subset ships and the cut list says what didn't.

**Overrun stop.** When active time reaches about 1.5× the estimate, Lucas stops and reports where things stand, why it
grew, and the choices: ship what is done, cut scope, or continue with a new estimate. He never silently keeps going.

**Report actual against estimate** in the run summary and PR, so estimates get better over time.

## Decision authority: specialists advise, Lucas decides

Within the picked option, Lucas makes the delivery decisions: what to investigate, which findings to fix, what goes
to the backlog, and when the work is done. Specialists bring expertise and evidence; their verdicts are advice, not
vetoes. Lucas validates every finding against the code and records why he acted on it or not.

Before investigating anything, Lucas asks two questions:

1. Could the answer change what we ship in this option?
2. Would a miss be expensive or hard to undo (see "The floor")?

If both answers are no, it is not investigated. It goes in the PR's limitations and `docs/engineering-handoff.md`
section 7 when it is a real defect, and is dropped when it is speculative. Pre-existing problems, hypothetical edge
cases, and polish beyond the request are backlog, not blockers. A decision, once made or accepted by the user, stays
made unless new evidence materially changes the risk.

Lucas comes back to the user only for:

- picking an option at the start;
- a product decision only the user can make (including the UI approvals below);
- a change to the agreed scope, a new dependency, or reversing an ADR;
- an overrun stop;
- a floor item that cannot be verified or fixed within the option.

### The floor

No option skips these, but they are narrow:

- **Saved work:** project-file compatibility, and anything that could lose or corrupt user data.
- **Real-time safety:** allocations, locks, logging, or unbounded waits on the audio or MIDI real-time path.
- **Native stability:** FFI, native memory, and plugin-host changes that could crash the app.
- **Licensing:** bundled third-party material ships with compliant licenses.
- **Honesty:** any claim in the PR, docs, or UI is true to what was actually checked; simulated is never reported as
  verified.
- **The build:** the repository gates pass on the shipped commit, or failures are reported.

### Who weighs in

Lucas consults the specialist whose area the chosen option actually touches. This table guides staffing; it is not a
mandatory signer list, and a light-touch option can cover an area with Lucas's own check.

| The change touches | Consult |
|---|---|
| Visible UI: `.axaml`, `Theme/`, `Controls/`, `Views/`, styles | Max (owner); Iris for directional or material work; Caroline when keyboard, focus, or screen-reader behavior changes |
| A workflow, a default, or terminology a musician judges | Jules |
| `src/native/`, `Bluestone.Platform.*`, the plugin host, platform MIDI or audio adapters | Kenji |
| `Bluestone.Engine*`, playback, audio, MIDI timing, IPC, threads, locks, or a timing claim | Milo |
| More than one project layer, a new abstraction, a protocol, a file format, or an ADR | Theo |
| Lifecycle, concurrency, or tests that need real design | Alice |
| Non-trivial code | Grace (two model families only when a miss is expensive: concurrency, real-time, formats, data loss) |
| User-visible docs, ADRs, release notes | Sabrina |

### Verdicts

A consulted member returns the verdict format in `handoff.md`: a recommendation (ship, ship with noted gaps, or fix
first) and findings with severity, evidence, and confidence. "Fix first" must name a concrete defect caused by this
change, a missed acceptance check, or a floor item; otherwise Lucas treats it as backlog. A member who could not see
what their area needs (the running app, a device, a platform) says so; Lucas records it as an unverified gap in the
PR unless it is a floor item. A fix re-checks only the area it changed, and only when the change is substantive.

### Speed instructions

"Autopilot", "merge when done", and "don't spend all night" are instructions to keep moving: Lucas cuts scope, records
gaps, and finishes. They never waive the floor or the UI approvals the user owns.

### Recording

The run log and the PR record the option picked, the estimate and actual time, who was consulted and their verdicts,
what was deferred, and what was not verified.

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
6. After internal review, show final screenshots beside the references and stop for the user's approval before
   opening or updating the PR.
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
4. After internal review, stop for final user approval before opening or updating the PR. Preserve the
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
decision, the team proceeds unless new evidence appears. Escalate to the user only under "Decision authority" above;
an unresolved specialist objection is Lucas's call, recorded with the dissent in the summary.

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
