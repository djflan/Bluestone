# 0030. Group tracks: mute and solo reach members, membership routes, folding is saved

- Status: Accepted; amends 0021 and handoff decision 11 (for groups only)
- Date: 2026-10-11

## Context

ADR 0021 added group tracks to the model: a `Group` role and a `Track.Group` membership, with no processing and no
user interface. Group processing already worked by connecting members into the group track (ADR 0023): events
routed into a track pass through its chain and go on through its connections. What was missing was a way to put
tracks in a group from the UI, folding in the arrangement, routing members to their group automatically, and what a
group's mute and solo mean.

Handoff decision 11 says mute and solo apply to a track's own content (solo overrides mute), and events routed into
a muted track still pass through its chain. The owner decided (2026-10-11) that a group's mute and solo must reach
its members: muting a group silences its members, and soloing a group solos it and its members.

Projects without groups must produce byte-identical plans (`Format3EquivalenceTests`, `MidiOneOutputGoldenTests`,
`PlanEquivalenceTests`).

## Decision

### Mute and solo

`GroupTracks.Playing` decides whether each track's own content plays. It is used by the signal graph, the plan's
"not routed" check, and the piano roll's sounding notes.

- **Without groups, the old rule is unchanged**: while any track is soloed only soloed tracks play (solo overrides
  mute); otherwise every unmuted track plays.
- **With groups, the nearest explicit state wins.** Walk out from the track through its groups, nearest first:
  - while anything is soloed, the track plays if it or a group around it is soloed before any of them is muted;
  - otherwise it plays unless it or a group around it is muted.
- So a muted group silences all its members at any depth; a soloed group solos itself and all its members; a soloed
  member of a muted group plays (solo overrides mute, as before); a muted member of a soloed group stays silent; a
  muted group inside a soloed one silences its own members.
- Decision 11 still holds for anything that is not a member: events routed into a muted group from a track outside
  it still pass through the group's chain. Only members' own content follows their group.
- A missing group or a loop of groups (which no command creates) ends the walk.

### Membership routes

`GroupCommands.SetGroup` changes membership and routing in one undoable step, through the one routing model
(`SignalConnection`s), and is refused with a message (`CommandRefusedException`) if it would add a routing error,
such as a loop.

- **Joining** sets the member aside: its outgoing connections (from its output; taps on its devices are left alone)
  move to `Project.ParkedConnections`, and one connection into the group replaces them per kind of signal it sent
  (events, audio), or per kind its output carries when it sent nothing. An events connection keeps the channel
  mapping of the member's only events connection, so a forced channel still applies. Direct connections to external
  instruments are therefore replaced by the group connection while the track is grouped.
- **Leaving** removes the member's connections into its old group and restores its parked connections, each only if
  it still works (an instrument may have been removed, or it may now close a loop); what does not work is left out.
  A member left with no output is reported "not routed", as any track.
- **Moving** to another group is leaving then joining, so the original outputs are still the ones restored later.
- **Groups nest.** A track cannot go into itself or into a group inside it (refused). Only group tracks take members.
- **Creating a group** from tracks (`CreateGroup`) inserts a group track above the first of them, puts them in it,
  and, for each kind of signal, connects the group to the members' former destination when they all went to one
  place (so two tracks on one MU2000, forced to channels 1 and 2, still reach it on those channels). Otherwise the
  group is left unrouted and says so. The new group joins the members' common group, if they had one, last, so its
  own output is parked like any member's.
- **Deleting a group** first moves its members to the group around it, or out of groups, with their own outputs back.
  Deleting a member drops its parked connections; duplicating one copies them.
- Voice selections on parked connections are not sent while the track is grouped (the group connection has none).

### Folding

- `Track.IsFolded` is saved. Folding is an edit like any other: it marks the project edited and is one undo step
  (`SetFolded`). Only group tracks fold. Folding changes nothing that plays.
- The track list hides the rows of tracks inside a folded group (at any depth), and the arrangement leaves their
  lanes out. A selection that would be hidden moves to the group. Keyboard navigation skips hidden rows.

### Project format

Format 4 gains two optional fields, without a version change, because readers ignore unknown properties inside the
project object (ADR 0027): a track's `folded` (written only when true) and the project's `parkedConnections`
(written only when not empty; the same shape as `connections`; an ID in both lists is a format error). Files of
projects without groups are written exactly as before. An older Bluestone reads such a file but drops both fields:
the tracks stay grouped and routed into their group, nothing is folded, and taking a track out of its group then
leaves it unrouted.

### UI

The track list's context menu (and Track ▸ Group Tracks, ⌘G) groups the selected tracks; the menu also puts the
selection in an existing group or takes it out. Members are indented under their group and their status shows the
group. A group's header shows how many tracks it holds, a fold button, and its own mute and solo, which now act on its
members; members silenced by their group are dimmed, in the list and in the arrangement.

## Consequences

- Existing projects without groups play exactly as before (the regression tests are unchanged and green).
- Existing projects whose tracks were already members (format 4 allowed it) keep their connections on load; only the
  group's mute and solo now reach those members. Taking such a track out of its group removes its connection into
  the group (if any) and restores nothing, because nothing was parked.
- There is no drag-and-drop into groups yet, and a folded group's lane does not summarize its members' clips.
- Tests: `GroupTracksTests`, `SignalGraphTests.AGroupsMuteAndSolo_ReachItsMembers_ButNotTracksMerelyRoutedIntoIt`,
  `GroupCommandsTests`, `ProjectSerializerTests` (round trip, optional fields), `GroupsViewModelTests`, and the
  headless `GroupTests` in `Bluestone.Tests.Ui`.
