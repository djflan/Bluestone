# Copilot instructions for Bluestone

Before opening or updating a non-trivial pull request (new behavior, a changed default, concurrency, lifecycle, IPC,
timing claims, or more than a few files), run the `pr-readiness` skill (`.github/skills/pr-readiness/SKILL.md`) and
fix what it finds first. Don't request extra review rounds to make up for skipping it.

Build, test, and contribution rules are in [CONTRIBUTING.md](../CONTRIBUTING.md).

`/team` runs a named multi-agent team (build, council, review, triage) led by Lucas, the Director: usage is in
`docs/agent-team.md`, the workflow in `.github/skills/team/SKILL.md`, and member charters in `.github/agents/`.
