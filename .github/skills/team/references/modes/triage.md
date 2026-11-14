# Mode: triage

Turn a bug report, issue, or vague request into a scoped plan that `/team build` can run. No production code changes.
A reproduction test may be written on a scratch branch only when that is the cheapest way to confirm the bug; delete
the branch afterward unless the user wants it.

## 1. Read the report (Lucas)

1. Fetch the issue if one is given (`gh issue view <n> --comments`).
2. Pull out: the expected behavior, the actual behavior, the platform, the devices or plugins involved, the version,
   and the steps.
3. Name what's missing. If the report can't be acted on without it, list the exact questions to ask the reporter and
   still produce the best plan the evidence allows.

## 2. Locate and reproduce (owner specialist)

Dispatch one owner (`references/roster.md`; Mike if no specialist fits) with a bounded search budget:
- find the owning code, its caller, and its test;
- state one hypothesis that a single cheap check could disprove, and run that check (a focused test, a log, a probe, or
  a code read);
- after two checks that don't confirm anything, stop and report the evidence.

Hardware-only bugs: say what would reproduce it on hardware (`docs/hardware-test-plan.md`) and what can be simulated.

## 3. Plan (Lucas)

```text
Summary: <one line>
Severity: critical | high | medium | low — <why>
Root cause: <confirmed | likely | unknown> — <evidence with path:line>
Outcome: <observable fix>
Acceptance checks: <2–5, including the test that fails today>
Scope: <files and areas> · Out of scope: <list>
Risks: <lifecycle, timing, platform, compatibility>
Suggested team: <members, with a one-line reason each>
Next: /team build <a ready-to-run request>
```

When an issue exists, offer to post the plan as a comment; don't post without being asked.
