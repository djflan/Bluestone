# Copilot instructions for Bluestone

Before opening or updating a non-trivial pull request (new behavior, a changed default, concurrency, lifecycle, IPC,
timing claims, or more than a few files), run the `pr-readiness` skill (`.github/skills/pr-readiness/SKILL.md`) and
fix regressions introduced by the change, missed acceptance checks, and violations of the engineering floor. Don't request extra review rounds to make up for skipping it.

Build, test, and contribution rules are in [CONTRIBUTING.md](../CONTRIBUTING.md). Visible UI follows the design language,
"Machined & Precision Milled", in [docs/design-language.md](../docs/design-language.md): tokens by key, never raw values.

`/team` runs delivery modes (build, council, review, triage) led by Lucas, the product and delivery lead. Lucas handles
eligible Direct work himself; otherwise he offers options with time estimates and staffs specialists only as needed:
usage is in
`docs/agent-team.md`, the workflow in `.github/skills/team/SKILL.md`, and member charters in `.github/agents/`.
