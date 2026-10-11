# 0021. Track roles: persisted, reconciled with content, never destructive

- Status: Accepted; amends 0020; amended by 0030
- Date: 2026-10-10

## Context

ADR 0020 made a track type-agnostic: it holds clips and automation lanes, and has no kind. A modern
workflow still needs to know what a track is *for*: an instrument track, an audio track, an effect
(return) track, a group, or a hybrid of notes and audio. That drives defaults (what a new track starts
with, what the inspector shows) and what is safe to add. Bitwig converts roles as content is added;
musicians must never lose clips, devices, automation, or routing because a role changed.

A role cannot always be derived. An empty track, an effect track, and a group track hold no clips, and
an instrument track whose only content is an empty clip is still an instrument track.

## Decision

- `Track` keeps one type and gains `Role` (`Instrument`, `Audio`, `Hybrid`, `Effect`, `Group`) and an
  optional `Group` (the ID of a group track). The role is **persisted** in the project (format 4,
  ADR 0027). There are no track subclasses.
- The domain never rejects a clip because of a role. Roles describe intent; they do not constrain the
  sequencing model.
- `TrackRoleConversion` works out a `RoleChangePlan` before anything changes:
  - `Automatic`: safe and obvious (an audio track gets a note clip and becomes hybrid; an instrument
    track with an audio path gets an audio clip and becomes hybrid);
  - `NeedsConfirmation`: safe only with a change the user did not ask for (an instrument track with no
    audio path gets audio: it becomes hybrid and gains a mixer channel and an audio connection);
  - `Refused`: it would hide something (an effect or group track cannot hold clips; an instrument track
    cannot be made an audio track while it holds notes). Nothing changes, and the message says what to do.
- `TrackRoleConversion.Reconcile` runs when a project is opened and only ever *widens* a role so that
  every clip stays visible; it reports what it changed.
- Commands (`TrackRoleCommands.SetRole`, `AddAudioClip`, `SetGroup`) apply plans and raise
  `CommandRefusedException` for refusals and unconfirmed changes; the UI asks, or explains.

## Consequences

- A track's role is the user's statement, kept even when the track is empty.
- Role changes never discard clips, devices, automation, plugin state, routing, or MIDI settings.
- Group tracks exist in the model (membership, refusal rules) but have no group processing or folded
  UI yet; that is deferred. (ADR 0030 adds them; `SetGroup` moved to `GroupCommands`.)
- Tests: `TrackRoleTests`, `DeviceAndRoutingCommandsTests.Scenario9_*`, `ChangingTheRole_IsAppliedOrExplained`.
