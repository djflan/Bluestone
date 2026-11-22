# Agent team (`/team`)

`/team` puts a named team of specialist Copilot agents on a request. Lucas, the director, scopes the work, picks the
smallest team that covers the risk, chooses a model for each member, runs them, checks their findings, and reports
back.

The workflow is defined in [.github/skills/team/](../.github/skills/team/SKILL.md), and each member's charter is in
[.github/agents/](../.github/agents/). This page is the user guide.

## Running it

Type this in a Copilot chat or CLI session opened at the repository root:

```text
/team [build|council|review|triage] <request>
```

If you leave out the mode, Lucas picks it from the request: a question goes to council, a PR goes to review, a bug or
issue goes to triage, and anything else goes to build. A bare number is looked up to tell a PR from an issue.

| Mode | Use it to | What you get | Changes code? |
|---|---|---|---|
| `build` | implement a feature or fix | a branch, tests, docs, and an open PR (or an update to an existing one) | yes |
| `council` | weigh a design question or idea | independent positions, a recommendation, open disagreements, and an ADR draft if needed | no |
| `review` | review a PR or branch | validated findings by severity, with file and line evidence; two model families for high-risk or broad changes | no |
| `triage` | turn a bug or issue into a plan | root cause, acceptance checks, suggested team, and a ready-to-run `/team build` request | no |

### Examples

```text
/team build Add a "Duplicate track" command to the track header context menu
/team build fix the review findings on #92
/team council Should the Device View show per-device CPU load?
/team review #92
/team triage #88
/team why does the metronome drift after a tempo change?
```

## The team

| Member | Role | Joins when |
|---|---|---|
| **Lucas** | Director: scopes, staffs, picks models, decides, writes the summary | always |
| **Mike** | Builder: implements in focused, test-backed batches | build |
| **Alice** | QA: tests that fail without the change, states × actions tables, final gates | build; review when tests are involved |
| **Grace** | Skeptic reviewer: read-only, adversarial; a second model family for high-risk or broad changes | build, review |
| **Max** | Dedicated UI owner: renders directions for a user pick; reviews reference-compared screenshots at compact and normal sizes | any visible UI change |
| **Caroline** | UX and accessibility: flows, hierarchy, density, defaults, keyboard, screen readers | UI or workflow changes |
| **Theo** | Architect: ownership boundaries, contracts, ADRs | cross-layer changes, protocols, formats |
| **Milo** | Real-time and timing: real-time paths, concurrency, measured claims | engine, playback, audio, MIDI timing, IPC |
| **Kenji** | Native and platforms: Rust crates, FFI, CoreMIDI, WinMM, ALSA, WASAPI | `src/native/`, `Bluestone.Platform.*` |
| **Jules** | DAW-fluent musician advocate: compares workflow, visual quality, and feel against Bitwig, Studio One, and other named DAWs | user-facing behavior, councils |
| **Sabrina** | Docs and release: keeps docs true, writes the PR body | build (ship phase) |
| **Iris** *(contract)* | Principal Product Designer and UX Architect: design direction, workflows, design system, rendered validation | identity and look-and-feel work, new control families, major workflows |

You can also call any member on their own, for example by picking **Max** from the agent list and asking for a UI
pass. The agent list loads when a session starts, so open a new session after pulling changes to the charters.

## What to expect

- **Build runs autonomously between required stops** until the PR is open. Visible work is classified by tier
  (the policy is in [governance.md](../.github/skills/team/references/governance.md)):
  - **Directional** (a new or reworked workflow, a redesign, a new design language or control family): 2-4 genuinely
    distinct RENDERED directions, a stop for your pick before production work, and a stop for your approval of the
    FINAL screenshots before the PR is opened or updated.
  - **Material** (a significant layout, interaction, or component change within the approved language): a rendered
    proposal, then a stop for your approval of the final screenshots.
  - **Routine** (copy, minor spacing, an accessibility fix, a wrong state): screenshot evidence and internal review,
    with no automatic stop.

  Lucas records the tier, picks the higher one when unsure, and never lowers it to avoid evidence. He may recommend,
  never choose or approve for you. Other hard stops remain: a decision that would change the requested outcome, add a
  dependency, or reverse an ADR; the same problem failing twice; uncommitted changes it didn't make; or a missing
  device or credential with no meaningful simulation.
- **Done is a team decision, not Lucas's:** every specialist whose area the change touches signs off on the
  current commit before work is shown to you as final, a PR is opened, or anything is merged. UI needs Max plus an
  independent visual signer (Iris or Caroline) and Caroline for accessibility; native code needs Kenji; timing,
  real-time, or IPC needs Milo; cross-layer work needs Theo; new behavior needs Alice's tests; code needs Grace.
  UI sign-offs compare the running app in a plain, realistic state and in active states against the direction you
  picked. A "not done" blocks; Lucas can't overrule it, only escalate it to you. "Autopilot" or "merge when
  done" never waives sign-offs. The rules are in
  [governance.md](../.github/skills/team/references/governance.md) ("Definition of done").
- **Change size:** add "light", "normal", or "full" to a request, or let Lucas pick. A light change (look and feel,
  copy, docs) needs only Max, one code review, and Sabrina if docs changed. It gets one review round, fixes only
  critical and high findings, runs the full gates once, and does no local stress testing. Gaps that only lack
  running-app evidence are listed in the PR instead of stopping the run. See "Change size" in governance.md.
- **UI references and evidence:** your reference and hint language is quoted as acceptance checks (for example,
  "like Studio One", "reuse X"), not loose inspiration. Directions and review screenshots appear beside your supplied
  references, with per-element critique. An explicitly agreed baseline is allowed only when no references were
  supplied. Material and directional reviews also apply a professional parity gate: would a working musician judge
  this finished beside the references? A "no" must name the observable defect. The PR records the tier and the
  evidence and approvals it requires. If approved UI later changes materially, screenshots and approval are refreshed.
- **Disagreement:** members raise material concerns in a fixed format (concern, evidence, impact, alternative,
  confidence, what would change their mind). Lucas decides ordinary questions and escalates only when the policy
  requires it.
- **Protected UI ownership:** Max stays on UI during a UI phase, never view-model, API, or bug work. Non-UI blockers
  return to Lucas/Mike or the owning specialist instead of diverting Max.
- **Progress** arrives as one line per phase: who is working, on which model, and on what.
- **Bounded loops:** at most two review and fix rounds, the full repository gates once on the final commit, and one
  CI check after pushing.
- **Honest results:** the summary and PR say what was physically verified and what was only simulated.
- **Run log:** each run keeps a log of staffing, model choices, decisions, and findings in the Copilot session folder
  (`files/team/`). It is never written to the repository.

## Guardrails

The team never pushes to `main`, force-pushes, deletes branches it didn't create, touches production data, enables
the ASIO feature, commits third-party plugins or vendor material, or re-runs CI by hand. Reviewers are read-only.
Editing members are given separate files and run one at a time unless their work is fully independent.

## Changing the team

- **A member's behavior:** edit their charter in `.github/agents/<name>.agent.md`.
- **Who joins when:** edit `.github/skills/team/references/roster.md`.
- **How models are chosen:** edit `.github/skills/team/references/model-selection.md`.
- **A mode's steps:** edit `.github/skills/team/references/modes/<mode>.md`.
- **Hiring:** Lucas may propose a new member when no one covers a role the work needs. He always asks you first, with
  the gap, alternatives, a draft charter, and a bounded trial. Hires join as *contract* members. At the end of the
  trial Lucas gives a performance review and you choose to release, extend, or retain them. Replacing an existing
  member is a separate decision. The policy is in
  [governance.md](../.github/skills/team/references/governance.md).
- **Renaming a member:** rename the agent file, its `name:`, and its row in the roster, then search the skill for the
  old name.
