# 0051. Settings as a popover over the workspace

- Status: Accepted; supersedes the Settings-hosting portion of [0038](0038-daw-first-tracks-and-settings.md)
- Date: 2026-11-22

## Context

ADR 0038 put Settings in the workspace's place, hiding the workspace while Settings was open. The user chose
mockup A for a bounded Settings surface that leaves the workspace visible and uses less horizontal space.

## Decision

Settings is a bounded, right-anchored, non-dockable popover in the main window, over the still-visible workspace.
Done, Escape, or a click-away closes it. Merely opening or closing Settings does not change project or workspace
state.

This supersedes ADR 0038 only where it places Settings in the workspace's place. The existing Settings content,
`SettingsViewModel`, and settings apply/save behavior are retained; the view model remains independent of its
Desktop host.
`WorkspaceViewModel` continues to own the workspace and its local layout (ADR 0036). Settings is not a
`WorkspaceViewKind`. There is no project-format change or migration.

## Consequences

- The existing Settings content, including Display and its color picker, must remain usable in the smaller surface
  at compact and normal window sizes.
- Dismissal, keyboard and focus behavior, and nested controls need verification during implementation. This ADR
  records the decision, not runtime test results.
