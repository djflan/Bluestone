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

## Staffing heuristics

Staff the smallest team that covers the risk. A member with nothing to look at is noise.

| The change touches | Add |
|---|---|
| Any `.axaml`, `Theme/`, `Controls/`, `Views/`, or a visible behavior | Max, Caroline |
| A new workflow, a default, terminology, or anything a musician sees | Jules |
| More than one project layer, a new abstraction, a protocol, or a format | Theo |
| `Bluestone.Engine*`, `Playback`, `Audio`, `Signal`, `Midi` timing, IPC, threads, locks | Milo |
| `src/native/`, `Bluestone.Platform.*`, CoreMIDI, WinMM, ALSA, WASAPI, plugin host | Kenji |
| User-visible docs, ADRs, release notes, or any shipped build | Sabrina |

- **build:** Lucas, Mike, Alice, and Grace always; add specialists from the table. Sabrina joins for the ship phase.
- **council:** 3–5 members whose views actually differ on the question. Always include Jules for product questions
  and Theo for structural ones.
- **review:** Grace (two model families) plus specialists for the touched paths. Alice joins when tests changed or are
  missing.
- **triage:** Lucas plus the one specialist who owns the affected code; Mike if no specialist fits.

## Who owns which files

Editing members never work on the same file at the same time. Lucas assigns file ownership in each dispatch. When two
members need the same file, one edits and the other reviews, or they run in sequence.
