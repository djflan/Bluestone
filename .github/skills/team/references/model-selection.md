# Model selection

Lucas picks each member's model on every run and passes it in the dispatch (`model` on the task tool). Agent files
never pin a model, so this choice is the only one.

**Which models exist:** the ones the task tool's `model` parameter accepts in this client. Never invent a name. If a
dispatch is rejected for its model, pick another from the same family. If no model of that family is accepted, treat
the family as unavailable, record that in the run log, and say so in the summary.

## Principles

1. **Match the work, not the title.** Use the strongest reasoning model where a miss is expensive: an unsound design,
   a real-time race, or a missed review finding. Use a fast model for mechanical work, such as running known commands
   or formatting a PR body.
2. **Diversity where it catches bugs.** A reviewer that shares the builder's model family tends to share its blind
   spots.
   - **build:** Grace runs twice, once on a Claude model and once on a GPT model. Mike's family is therefore always
     covered by a reviewer from the other family.
   - **review:** Grace runs twice, once on a Claude model and once on a GPT model, on the same pinned snapshot. If
     one family is unavailable, say so and do not call the result a dual review.
   - **council:** spread the members across at least two families where possible.
3. **Vision when there are pixels.** Max reviews screenshots, so he needs an image-capable model.
4. **Long context only when needed.** Use a long-context tier only for a member who must read a very large diff or
   many ADRs at once.
5. **Cheap by default for repeats.** A follow-up dispatch to the same member reuses the previous choice unless that
   member failed or the work changed in kind.

## Starting points

These are defaults to adjust, not rules.

| Member | Usual choice | Why |
|---|---|---|
| Mike | strong coding model | most of the code |
| Grace | strongest reasoning, one Claude and one GPT | finds what Mike missed |
| Theo, Milo | strongest reasoning | design and concurrency mistakes are costly |
| Max | strong, image-capable | judges screenshots |
| Caroline, Jules | strong general model | judgment on flows and product fit |
| Kenji | strong coding model | Rust, FFI, and platform APIs |
| Alice | strong coding model to write tests; fast model to only run gates | depends on the task |
| Sabrina | fast model | docs and PR body from known facts |

## Record it

Write each choice in the run log as `Member: model (one-line reason)`. Report the lineup in the final summary.
