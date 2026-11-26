# Agent team (`/team`)

`/team` puts a named team of specialist Copilot agents on a request. Lucas, the product and delivery lead, scopes the
work and offers you options with time estimates. After you pick one, he staffs the smallest team it needs, decides
what's worth investigating, and delivers.

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
| `build` | implement a feature or fix | options with estimates, then a branch, tests, docs, and an open PR (or an update to an existing one) | yes |
| `council` | weigh a design question or idea | independent positions, a recommendation, open disagreements, and an ADR draft if needed | no |
| `review` | review a PR or branch | validated findings by severity, with file and line evidence; two model families for high-risk or broad changes | no |
| `triage` | turn a bug or issue into a plan | root cause, acceptance checks, options with estimates, and a ready-to-run `/team build` request | no |

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
| **Lucas** | Product and delivery lead: scopes, estimates options, staffs, picks models, decides, writes the summary | always |
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
| **Iris** | Principal Product Designer and UX Architect: design direction, workflows, design system, rendered validation | identity and look-and-feel work, new control families, major workflows |

You can also call any member on their own, for example by picking **Max** from the agent list and asking for a UI
pass. The agent list loads when a session starts, so open a new session after pulling changes to the charters.

## What to expect

- **Options first.** Lucas spends a few minutes scoping, then offers 2–3 options, typically Fast, Right, and
  Thorough. Each says what it includes, what it skips and what that risks, who works on it, and the estimated active
  time, with waits on you or CI shown separately. He recommends one and waits for your pick. Trivial work (under about
  15 minutes) skips the menu: he states the estimate and goes.
- **Lucas decides inside the option.** Specialists advise; their verdicts are not vetoes. Lucas fixes what the change
  caused, what misses an acceptance check, and anything on the floor. Real defects outside that go to the backlog;
  speculative ones are dropped. Pre-existing problems and polish beyond the request never block. Settled decisions and
  gaps you accepted stay settled unless new evidence changes the risk.
- **The floor.** No option skips these: saved-project compatibility and data loss, real-time audio and MIDI safety,
  native crash risk, license compliance for bundled material, honest claims, and passing repository gates.
- **When Lucas comes back to you:** to pick an option; for a product decision only you can make (including UI
  approvals); for a scope change, a new dependency, or reversing an ADR; when a floor item can't be verified; and at
  the overrun stop.
- **Overrun stop.** At about 1.5× the estimate, Lucas stops and reports where things stand, why it grew, and your
  choices: ship what's done, cut scope, or continue with a new estimate. The summary and PR report actual time against
  the estimate.
- **UI tiers.** Visible work is classified (the policy is in
  [governance.md](../.github/skills/team/references/governance.md)):
  - **Directional** (a new or reworked workflow, a redesign, a new design language or control family): 2–4 genuinely
    distinct RENDERED directions, a stop for your pick, and a stop for your approval of the final screenshots.
  - **Material** (a significant layout, interaction, or component change within the approved language): a rendered
    proposal, then a stop for your approval of the final screenshots.
  - **Routine** (copy, minor spacing, an accessibility fix, a wrong state): screenshot evidence and internal review,
    with no automatic stop.

  Your reference and hint language is quoted as acceptance checks (for example, "like Studio One").
- **Disagreement:** members raise material concerns in a fixed format (concern, evidence, impact, alternative,
  confidence, what would change their mind). Lucas decides and records the dissent in the summary.
- **Protected UI ownership:** Max stays on UI during a UI phase, never view-model, API, or bug work.
- **Progress** arrives as one line per phase: who is working, on which model, and on what.
- **Bounded loops:** one review pass, one batch of fixes, the full repository gates once on the final commit, and CI
  only when the option includes it or you ask.
- **Honest results:** the summary and PR say what was verified, what was simulated, and what was skipped.
- **Run log:** each run keeps a log of the option, estimate, staffing, decisions, and findings in the Copilot session
  folder (`files/team/`). It is never written to the repository.

## Guardrails

The team never pushes to `main`, force-pushes, deletes branches it didn't create, touches production data, enables
the ASIO feature, commits third-party plugins or vendor material, or re-runs a CI run that already reported. Reviewers are read-only.
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
