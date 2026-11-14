# Mode: council

Get several independent views on a design question, idea, or trade-off. No code changes and no branch.

## 1. Frame (Lucas)

1. Restate the question in one sentence and list 2–4 concrete options, including "do nothing" when it's realistic.
2. Collect the evidence the members need: the relevant ADRs, `docs/product-vision.md`, `docs/roadmap.md`,
   `docs/release-1.0.md`, and the owning code. Pass paths, not summaries of opinions.
3. Staff 3–5 members whose views will actually differ (`references/roster.md`). Spread them across at least two model
   families where possible.

## 2. Independent positions

Dispatch all members in parallel, read-only. No member sees another member's answer in this round. Each returns a
council position (`references/handoff.md`), grounded in repo paths.

## 3. One rebuttal round (optional)

Run it only when the positions conflict on a point that matters. Send each member a summary of the other positions,
without names, and ask: "What, if anything, changes your position, and what do the others miss?" One round only.

## 4. Recommendation (Lucas)

Report:
- **Recommendation:** one sentence, plus the decisive reason.
- **Where the team agreed.**
- **Open disagreements:** who holds which view and why, in a line each. Don't hide a dissent.
- **Risks and how to check them early.**
- **Next step:** usually a `/team triage` or `/team build` request, written out ready to run.

If the decision is consequential under `docs/adr/0001-record-architecture-decisions.md`, Theo drafts the ADR text in
the reply. It is not written to the repository unless the user asks.
