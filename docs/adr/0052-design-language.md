# 0052. Design language: Machined & Precision Milled

- Status: Accepted; amends 0036
- Date: 2026-11-27

## Context

Identity R6 gave Bluestone a recognizable look, but it is written down only as values in `Theme/Tokens.axaml`,
as behavior in the custom control library in `Controls/`, and as reasoning scattered through pull request history.
ADR 0036 says views use tokens, not raw values; it does not say which token a view should use, how a control
behaves in each state, or what the accessibility floor is. Open-source contributors and agents building UI need one
written, reviewable language to work from, and reviewers need rules to cite instead of taste.

## Decision

### One named language, one normative guide

The design language is named **Machined & Precision Milled** ("Machined" for short).
[`docs/design-language.md`](../design-language.md) is normative for visible UI. It states the principles, the
semantic roles, the control contracts, the states each control shows, the accessibility floor, and the copy
rules. It names tokens by their key and never copies their values. Its standard is a clean, exact, professional
finish.

### Where each kind of truth lives

- **ADRs** decide: what the language is, how themes work (ADR 0053), and who owns what.
- **The guide** states the rules and the intent behind them.
- **The theme** (ADR 0053) and `Tokens.axaml` hold the values.

If the guide and the code disagree, the pull request that finds it fixes whichever is wrong. Neither wins by
default.

### Ownership and change

Iris owns the guide; the maintainer approves changes to it. Any pull request that adds, renames, or removes a
token, a theme member, or a public control in `Controls/`, or changes a rule, updates the guide in the same pull
request. Changing the language's direction is Directional UI (`.github/skills/team/references/governance.md`) and
needs the owner's approval.

This amends ADR 0036's §Design tokens and its Identity R6 amendment only by adding the guide as the written rules
for those tokens and controls. Token ownership, `Palette`, and the control library are otherwise unchanged.

## Consequences

- Reviews of visible UI cite a rule in the guide. A disagreement the guide does not settle is a gap in the guide,
  fixed in the same pull request or recorded as one.
- The guide is a maintained document: every token, theme member, or public control change carries a small doc
  change, and a stale guide is a review finding.
- Tests guard the guide: every token key and control it references must exist, and new hex color literals (and
  `Color.FromRgb`/`FromArgb` with number literals) in views and controls are caught. Named colors such as
  `Colors.White` are not.
- Known contrast and focus-visibility gaps are listed in the guide's Known gaps and in
  `docs/engineering-handoff.md` §7, for a follow-up material UI change. The guide records them; it does not fix
  them.
