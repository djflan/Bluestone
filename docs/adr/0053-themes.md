# 0053. Themes

- Status: Accepted; amends 0036
- Date: 2026-11-27

## Context

ADR 0036 defined one dark theme as tokens in `Theme/Tokens.axaml`. Its Identity R6 amendment made meter colors a
user preference: the signal gradients and the MIDI color are edited in Settings ▸ Display, saved in
`display-settings.json`, and applied through `Theme/MeterGradient`. So the look's colors live in two places with
two persistence rules, and the name "Machined" referred to the theme. ADR 0052 gives that name to the design
language, which separates the rules (the guide) from the values (a theme).

## Decision

### A theme is a JSON file

A theme holds:

- `name` and `version` (the theme format version);
- `variant`: `light`, `midtoned`, or `dark`. It describes the overall scheme and maps to Avalonia's
  `RequestedThemeVariant`: `light` to Light, `midtoned` and `dark` to Dark;
- `colors`: every color token, keyed by its exact `Color` resource key in `Tokens.axaml` (for example
  `Surface.BaseColor`), plus the Fluent palette colors in `App.axaml` under `Fluent.*` keys;
- the three meter gradients (mixer, chain, and gain) and the MIDI color.

### One shipped theme, one source of defaults

Bluestone ships one theme, **Dark Sodalite** (variant `dark`), and it is the default. Its values are the colors in
`Tokens.axaml` and `App.axaml` plus the gradient and MIDI defaults in code (`SignalGradient.Default`,
`SignalGradient.GainDefault`, and `#3CCBF4`). There is no separate default theme file to keep in step.

### The user's copy

The user's copy is `<settings folder>/themes/dark-sodalite.json`, written on first run. Editing a gradient or the
MIDI color in Settings ▸ Display writes into the active theme file; there is no separate override layer. Every
theme member has its shipped default: each gradient's reset and the MIDI color's reset revert one member, and
**Reset theme** (after confirmation) reverts them all.

Reading is forgiving. Unknown keys are ignored. A missing or invalid value takes its default. A missing file gives
the defaults. An unreadable file is kept aside under a new name, never silently overwritten, and the defaults are
used.

### Applying a theme

Colors apply at startup, before any window opens. The token `Color` resources are replaced and the token brushes
are updated in place, because styles hold those brush instances; `Palette`, for custom-drawn controls, reads the
resources. Hand edits to colors therefore apply on the next launch. Gradients and the MIDI color apply live, as
before.

Live theme switching (`DynamicResource`), a theme picker, import and export, and Light or Midtoned themes are
deferred.

### Display settings

`display-settings.json` no longer stores the gradients or the MIDI color; its old keys are ignored. There is no
migration, because the owner's saved values equal the shipped defaults. The non-color display settings (level
style, LED fade, warm-up, glow, and glow strength) stay there.

This amends ADR 0036's §Design tokens (tokens remain the only place views get colors, but their values may now
come from the theme) and its Identity R6 amendment (meter colors are theme members, not a separate preference).

## Consequences

- A theme changes colors only. It cannot change geometry, type, roles, or rules; those belong to the tokens and
  the design language (ADR 0052).
- Colors hard-coded outside the tokens, listed in the design guide's Known gaps, do not follow the theme until
  they are moved into tokens.
- A test keeps every token brush following its color, so a brush that stops tracking its `Color` resource fails
  the build rather than ignoring the theme.
- A user with custom gradients from an earlier build loses them once; they are set again in Settings ▸ Display.
- Adding a theme later is a data file plus a picker, not a format change; switching without a restart needs
  `DynamicResource` throughout and is its own decision.
