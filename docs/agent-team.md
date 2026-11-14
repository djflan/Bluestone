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
| `review` | review a PR or branch | validated findings by severity, with file and line evidence, from two model families | no |
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
| **Grace** | Skeptic reviewer: read-only, adversarial, runs on a Claude and a GPT model | build, review |
| **Max** | UI Craft: owns how the UI looks; reviews screenshots at compact and normal sizes | any visible UI change |
| **Caroline** | UX and accessibility: flows, defaults, keyboard, screen readers | UI or workflow changes |
| **Theo** | Architect: ownership boundaries, contracts, ADRs | cross-layer changes, protocols, formats |
| **Milo** | Real-time and timing: real-time paths, concurrency, measured claims | engine, playback, audio, MIDI timing, IPC |
| **Kenji** | Native and platforms: Rust crates, FFI, CoreMIDI, WinMM, ALSA, WASAPI | `src/native/`, `Bluestone.Platform.*` |
| **Jules** | Musician advocate: real studio workflows, product fit | user-facing behavior, councils |
| **Sabrina** | Docs and release: keeps docs true, writes the PR body | build (ship phase) |

You can also call any member on their own, for example by picking **Max** from the agent list and asking for a UI
pass. The agent list loads when a session starts, so open a new session after pulling changes to the charters.

## What to expect

- **Build runs on its own** until the PR is open. It pauses only at a hard stop: a decision that would change the
  requested outcome, add a dependency, or reverse an ADR; the same problem failing twice; uncommitted changes it
  didn't make; or a missing device or credential with no meaningful simulation.
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
- **Renaming a member:** rename the agent file, its `name:`, and its row in the roster, then search the skill for the
  old name.
