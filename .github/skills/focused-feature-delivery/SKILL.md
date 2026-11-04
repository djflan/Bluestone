---
name: focused-feature-delivery
description: 'Use for feature implementation, cross-platform work, broad bug fixes, or long-running coding tasks when speed and scope control matter. Keep discovery bounded, make an evidence-based first change, validate in layers, batch review fixes, and stop repetitive CI polling. Use with the repository’s PR-readiness workflow before opening a non-trivial PR.'
argument-hint: 'Describe the outcome, constraints, and any hard deadline'
---

# Focused Feature Delivery

Deliver the requested outcome with the smallest safe change set. Optimize for short feedback loops and a clear finish, not for exhaustive exploration or repeated reassurance.

## When to Use

- A feature spans multiple files or layers.
- The user emphasizes speed, a deadline, or avoiding drawn-out review cycles.
- A cross-platform, lifecycle, real-time, protocol, or release change risks expanding while implementation proceeds.

## 1. Lock the Scope

1. Restate the observable outcome, hard constraints, and acceptance checks in a few bullets.
2. Separate required work from adjacent ideas. Put deferred ideas in a short list; do not implement them without authorization.
3. Resolve only decisions that block safe implementation. For decisions that can be reversed cheaply, choose the repository convention and proceed.
4. If new information changes a constraint, revise the scope once and continue from the new constraint. Do not reopen settled decisions without new evidence.

## 2. Route to the Owner

1. Read repository instructions and the nearest owning abstraction, caller, and test. Avoid mapping the whole repository.
2. State one local hypothesis that can be disproved and one cheap check that would disprove it.
3. Make the smallest edit that exercises that check. If the behavior is unclear, use a reversible probe or a focused test as the first edit.
4. If two targeted reads do not identify the owner, ask one concise question or report the blocker; do not keep searching broadly.

## 3. Implement in Batches

- Keep each edit batch within one behavior or ownership boundary.
- Preserve existing APIs and compatibility unless changing them is required by the acceptance checks.
- Add a test that would fail without the behavior change. Cover error and recovery paths where state, lifecycle, retries, or concurrency change.
- Do not add abstractions, cleanup, documentation, or tests unrelated to the requested behavior.
- For a multi-platform feature, separate shared behavior from platform-specific enablement and verify each platform's intended default explicitly.

## 4. Validate in Layers

After the first substantive edit, run its narrowest useful check before reading or editing another slice. Then use this ladder:

1. Focused test or behavior check for the touched code.
2. Focused build/typecheck for the owning project.
3. Full required format, build, and test gates once after implementation stabilizes.
4. Runtime, hardware, allocation, or performance probes only where the change makes those claims or changes those paths.

Do not rerun the full suite after every small edit. When a focused check fails, repair that slice and rerun that check; widen only after it passes. Report skipped hardware/platform tests and unverified physical behavior explicitly.

## 5. Bound Review Work

- Run the repository's required pre-PR readiness workflow once after local gates pass.
- Group review findings by root cause. Fix the complete related set in one batch, then run the narrow checks they affect and the required gates once.
- For each review thread, verify the comment, make or explain the change, post one specific reply, and confirm the reply exists before resolving the thread. Do not bulk-resolve unverified comments.
- Do not repeat a review solely for reassurance. Repeat only when substantial code changed after the review or a new concrete issue was found.

## 6. Bound CI Monitoring

- After pushing, inspect the workflow run once and identify its run ID and required jobs.
- Poll only after a meaningful interval or a notification. Do not repeatedly query an unchanged pending state.
- Act immediately on a failed job: read its log, identify the failing command, fix the cause, and push one follow-up commit.
- Never bypass required checks unless the user explicitly authorizes a specific waiver. Distinguish queued, running, failed, canceled, and waived jobs accurately.
- Stop once the agreed checks are green, review threads are handled, and the requested merge/release action is complete.

## Progress Updates

Keep updates short and state only: current work, the next check, and any real blocker or scope decision. Avoid narrating every command or repeating unchanged status. When waiting on external CI, report that it is pending and continue with other authorized work only if there is useful work to do.

## Done Means

- The requested behavior and explicit constraints are satisfied.
- Focused validation and required repository gates pass, or remaining failures are clearly reported.
- Review comments are replied to and resolved only after verification.
- The branch/PR/merge state matches the user's request.
- Deferred ideas remain deferred.
