# Handoff contracts

## Dispatch prompt

A subagent sees only what Lucas sends it. Every dispatch uses this template and must stand on its own:

```text
You are <Member>, <role> on the Bluestone /team. Your charter is .github/agents/<file>.agent.md. Read it first and
follow it.

Run log: <absolute path of the run log>  Mode: <mode>  Phase: <phase>  Model: <model chosen for this dispatch>
Goal: <one sentence, observable outcome>
Context: <what has been decided so far; links to files, ADRs, issues; the pinned SHA for reviews>
Risk/UI tier: <routine, elevated, or high risk; and directional, material, routine, or not applicable>
Scope: <what to look at or change>
You own: <files or globs you may edit, or "read-only">
Do not touch: <files other members own right now>
Stop when: <concrete stop condition>
Return: <the output format below that applies>
```

When the client exposes the member as a custom agent, dispatch that agent type. Otherwise dispatch a
`general-purpose` agent with the prompt above. Either way, pass the chosen model as the task tool's `model`. A
read-only member must also be told: "Read-only: do not edit, create, or delete tracked files, do not change the index
or the checked-out branch (no `sed -i`, `git checkout`, `git switch`, `git stash`), and do not commit. Build and
test output in ignored folders is fine."

## Constructive disagreement

Use the canonical policy in `governance.md`. A material objection is not a vague preference or a refusal. Report it
as: Concern, Evidence, Impact, Alternative, Confidence, and What would change my mind. Lucas validates and records
material dissent, decides ordinary delivery questions, and escalates only under the governance criteria. Once a
decision is made, proceed unless new evidence appears.

## Finding format (reviews)

One entry per finding, highest severity first:

```text
[severity] path/to/file.cs:123 — <one-line problem>
Evidence: <the code, a command and its output, or the doc line that shows it>
Impact: <what breaks, for whom, and when>
Fix: <the smallest change that resolves it>
Confidence: high | medium | low
```

Severity:
- **critical:** data loss, a crash, a security hole, or a broken build or gate.
- **high:** wrong behavior on a normal path, a race, a real-time violation, or a claim not backed by evidence.
- **medium:** an edge case, a missing test for changed behavior, or a UI or UX issue a musician will notice.
- **low:** clarity, naming, or minor polish.

Report "no findings in my area" when that is the result. Never pad.

## Build report (editing members)

```text
Changed: <files, one line each, with what changed>
Tests: <tests added or changed, and what each proves>
Ran: <exact command> → <passed | failed: summary>
Not verified: <hardware, platforms, or paths you could not exercise, and why>
Open: <anything deferred or unresolved>
```

A build report is not a sign-off. Lucas never treats "done" from a builder as the team's verdict.

## Sign-off (definition of done)

Every required signer in `governance.md` ("Definition of done") returns this against the pinned commit:

```text
Signer: <member>  Area: <what you signed for>  Commit: <SHA>
Acceptance checks:
  - <check, quoted> → met | not met. Evidence: <command+result, screenshot path, probe, or file:line>
Compared against: <approved direction / references, with paths>  (UI: plain state AND active state, compact AND normal)
Verified claims: <any "unchanged" or "already satisfies" claim from the builder, and what you checked>
Verdict: done | not done
Blocking findings: <finding format above, or "none">
```

A signer who could not inspect what their area requires (for example the running app, a probe, or a platform)
returns "not done (unverifiable)" with what is missing, which triggers the missing-device stop; they never sign off on
a description. Jules, who does not run the app, signs from the independent visual signer's plain-state and active-state
screenshots of the running app, never from the builder's chosen scenes.

## Council position

```text
Position: <one sentence>
Reasoning: <3–6 bullets grounded in repo docs, ADRs, or code; cite paths>
Risks if we do it: <bullets>
Risks if we don't: <bullets>
What would change my mind: <one line>
```

## Run log

Lucas keeps one run log per `/team` invocation at `<session folder>/files/team/<YYYYMMDD-HHMM>-<slug>.md`. It is
never written inside the repository. Sections: Request, Mode, Scope (outcome, acceptance checks, out of scope), Risk
and UI tier, Team and models with staffing reasons, Decisions and material dissent, Findings and outcomes, Sign-offs
(signer, area, commit, verdict, evidence), Deferred, and Result.
