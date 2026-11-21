# Team roster

Each member is a custom agent in `.github/agents/<file>.agent.md`. The file is the member's charter: expertise,
required reading, quality bar, output contract, and limits. Lucas reads a member's charter before staffing them.

| File | Member | Role | Writes |
|---|---|---|---|
| `lucas` | Lucas | Director: scopes, staffs, picks models, decides, synthesizes | the run log |
| `mike` | Mike | Builder | production code and tests |
| `alice` | Alice | QA | the test files Lucas assigns her |
| `grace` | Grace | Skeptic reviewer | nothing |
| `max` | Max | UI Craft, the dedicated UI owner | XAML, styles, `Theme/`, visual-only controls |
| `caroline` | Caroline | UX and accessibility | nothing |
| `theo` | Theo | Architect | ADRs, `docs/architecture.md` |
| `milo` | Milo | Real-time and timing | the probe and timing-test files Lucas assigns him |
| `kenji` | Kenji | Native and platforms | `src/native/`, `Bluestone.Platform.*` |
| `jules` | Jules | Musician advocate | nothing |
| `sabrina` | Sabrina | Docs and release | `docs/`, README, PR body |
| `iris` | Iris (contract) | Principal Product Designer and UX Architect: design direction, workflows, design system, rendered validation | design specs; design-only scratch scenes Lucas assigns |

**Contract** members are approved, bounded trials. Hiring, performance review, and release/extend/retain decisions
follow `governance.md`. Successful work does not silently make a role permanent, and replacing a current member is a
separate user-approved organizational decision.

## Staffing heuristics

Staff the smallest team that covers the risk. A member with nothing to look at is noise.

| The change touches | Add |
|---|---|
| Directional or material `.axaml`, `Theme/`, `Controls/`, `Views/`, or visible behavior | Max, Caroline |
| Routine visual correction | Max; add Caroline when accessibility, flow, terminology, or focus is involved |
| A new workflow, a default, terminology, or anything a musician sees | Jules |
| Visual identity, a new control family, theme presets, or a look-and-feel direction | Iris (with Max) |
| More than one project layer, a new abstraction, a protocol, or a format | Theo |
| `Bluestone.Engine*`, `Playback`, `Audio`, `Signal`, `Midi` timing, IPC, threads, locks | Milo |
| `src/native/`, `Bluestone.Platform.*`, CoreMIDI, WinMM, ALSA, WASAPI, plugin host | Kenji |
| User-visible docs, ADRs, release notes, release preparation, or a complex PR narrative | Sabrina |

- **build:** Lucas directs. Add the member who owns the changed files and one independent reviewer, plus every
  required signer for the touched areas (`governance.md`, "Definition of done"); staff them at intake, not at the end.
  Add Alice for
  meaningful test design, lifecycle coverage, flaky-risk tests, or final gates; add Grace for non-trivial review;
  add specialists from the table only when their ownership or risk is present. Sabrina joins only when docs, release
  notes, or PR preparation are needed.
- **council:** 3–5 members whose views actually differ on the question. Always include Jules for product questions
  and Theo for structural ones.
- **review:** use one Grace for ordinary localized work. Use independent Grace reviews from two model families for
  high-risk or broad changes: concurrency, real-time paths, protocols or formats, security, data-loss risk, major
  cross-layer work, or a large diff where a miss is expensive. Add specialists for touched risks. Alice joins when
  tests changed, are missing, or need lifecycle/flakiness scrutiny.
- **triage:** Lucas plus the one specialist who owns the affected code; Mike if no specialist fits.

Record one line of rationale for every staffed member. When a specialist looks relevant but is intentionally omitted,
record why. A member with no distinct question, file ownership, or risk to examine is noise.

## Who owns which files

Editing members never work on the same file at the same time. Lucas assigns file ownership in each dispatch. When two
members need the same file, one edits and the other reviews, or they run in sequence.
