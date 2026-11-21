# Mode: review

A multi-perspective review of a branch or pull request. It reports findings; it does not change code, and it does not
post to the pull request unless the user asks.

## 1. Pin the snapshot (Lucas)

1. Resolve the target: a PR number (`gh pr view <n> --json headRefOid,baseRefName,files,title,body`), a branch, or the
   current branch.
2. Fetch both sides into explicit refs, without touching the working tree. For a PR (this works for fork PRs too):
   `git fetch origin +refs/pull/<n>/head:refs/remotes/origin/pr/<n> +refs/heads/<base>:refs/remotes/origin/<base>`.
   For a branch: `git fetch origin +refs/heads/<branch>:refs/remotes/origin/<branch>
   +refs/heads/<base>:refs/remotes/origin/<base>`. Pin the head SHA from the fetched ref (`git rev-parse
   origin/pr/<n>` or `origin/<branch>`; for a PR it must equal `headRefOid`) and the merge base,
   `git merge-base origin/<base> <sha>`. Every reviewer reviews exactly that diff:
   `git --no-pager diff <merge-base> <sha>`. If the branch moves during the review, say so and keep the pinned SHA.
3. Read the PR body and the linked issue, so the review checks what the change claims as well as the code.
   For user-facing UI, collect the recorded tier, the user's relevant acceptance language, supplied references, and
   the evidence and approvals that tier requires under `.github/skills/team/references/governance.md`.

## 2. Staff

- **Grace:** one independent reviewer for ordinary localized work. Use two independent Grace reviews on different
  available model families for the high-risk or broad categories in
  `.github/skills/team/references/model-selection.md`. Both review the same
  snapshot and prompt. If one family is unavailable, say so and do not present the result as dual review.
- **Specialists for touched paths** (`.github/skills/team/references/roster.md`), each reviewing only their area.
- **Alice** when tests changed, or when changed behavior has no test.

Dispatch them all in parallel, read-only. Launch the reviewers as custom agents or as `general-purpose` agents told
to stay read-only. **Never use the built-in `code-review` agent type:** its terse output overrides the finding format.

Ask reviewers for recall: report every plausible problem with evidence and a confidence level. Precision comes from
validation in step 3.
For UI, staff Max, Caroline, Jules, or Iris only when the recorded tier and touched risk give each a distinct question.
They review the evidence beside supplied references and the approved direction; Grace checks that the tier is honest
and that its required evidence and approvals are present.

## 3. Validate (Lucas)

For each finding:
1. Open the cited code at the pinned SHA and check that the claim is true.
2. Deduplicate across reviewers. Note when both Grace models found the same issue, because that raises confidence.
3. Mark it **confirmed**, **plausible** (needs a test or hardware to prove), or **rejected**, with a reason.

Keep rejected findings out of the report, but count them.

The review is not complete without a verdict from every required signer for the touched areas in
`.github/skills/team/references/governance.md` ("Definition of done"). A missing signer is reported as a blocker.

UI evidence and approvals required by the recorded tier are BLOCKING gates. Directional UI needs its alternatives,
user pick, comparisons, and final approval; material UI needs its proposal, comparisons, and final approval; routine
UI needs appropriate internal visual evidence. An incorrectly low tier is itself a finding. Rendering is not user
approval when approval is required, and a generic baseline never replaces supplied references.

## 4. Report

```text
Reviewed <PR/branch> at <sha> (base <sha>). Team: <members and models>. Dual review: yes | no (<why>)

Critical / High / Medium / Low
[severity] path:line — problem
  Evidence · Fix · Found by: <members> · Status: confirmed | plausible

Claims check: <each claim in the PR body that the evidence does or does not support>
UI gates: <recorded tier, required comparisons/evidence/approvals; blockers | not applicable>
Sign-offs: <each required signer for the touched areas (governance.md), verdict on this SHA; missing ones are blockers>
Not reviewed: <areas no one covered>
Rejected: <count> findings (reasons in the run log)
```

Offer next steps, without taking them: fix in place (`/team build` on the branch), or post the findings as review
comments.
