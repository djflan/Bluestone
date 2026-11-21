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
2. **Diversity where it catches expensive bugs.** A reviewer that shares the builder's model family can share its
   blind spots. Use one independent Grace review for ordinary localized work. Use Grace twice, on different available
   model families and the same pinned snapshot, when the change is high-risk or broad: concurrency, real-time paths,
   protocols or formats, security, data-loss risk, major cross-layer work, or a large diff where a miss is expensive.
   If only one family is available, say so and never call the result dual review. In a council, spread members across
   at least two families when it materially improves independent judgment.
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
| Grace | strong reasoning; strongest and cross-family for high-risk work | finds what the builder missed without doubling routine cost |
| Theo, Milo | strongest reasoning | design and concurrency mistakes are costly |
| Max | strong, image-capable | judges screenshots |
| Caroline, Jules | strong general model | judgment on flows and product fit |
| Kenji | strong coding model | Rust, FFI, and platform APIs |
| Alice | strong coding model to write tests; fast model to only run gates | depends on the task |
| Iris | strong, image-capable, a different family from Max | judges renders; different eyes from the builder |
| Sabrina | fast model | docs and PR body from known facts |

## Record it

Write each choice in the run log as `Member: model (one-line reason)`. Report the lineup in the final summary.
