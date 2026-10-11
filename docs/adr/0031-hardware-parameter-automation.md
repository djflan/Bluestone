# 0031. Hardware parameter automation through profile parameter maps

- Status: Accepted; amends 0007 and 0024
- Date: 2026-10-11

## Context

Device automation (ADR 0024) targets device parameters and is never MIDI. A parameter of an external instrument
that is reachable only by MIDI (an XG part's filter cutoff by NRPN, a Roland parameter by DT1 SysEx) could only be
automated with a MIDI controller lane, which does not cover RPN, NRPN, 14-bit pairs, or SysEx, and does not name the
parameter. External instruments are project entities with ports (ADR 0023); handoff decision 4 forbids an
"external instrument device kind", because it would be a second way to express a route.

## Decision

- **Profiles map named parameters to MIDI.** A profile's optional `parameterMap` lists parameters by key, each a
  control change (7-bit, or 14-bit as an MSB controller 0–31 and its LSB partner), an RPN or NRPN (CC 101/100 or
  99/98, then data entry CC 6, and CC 38 for 14 bits, then by default the RPN null 101/100 = 127/127), or a SysEx
  template with `{value}` and bit slices such as `{value[7:4]}` (nibbles), `{channel}` (0–15), `{device}` (a byte
  the entry states), and `{checksum}` (Roland or Yamaha, both the 7-bit sum to zero, over a stated range;
  `SysExChecksum`, shared with `GsDialect.Checksum`). Each has an integer range (`min`, `max`) and an optional
  `default`. The format is in `docs/profiles.md`.
- **The profile schema stays at version 1.** `parameterMap` is additive and optional. An older loader warns about
  the unknown property and ignores it, which is what it should do with data it cannot use; a version bump would
  make older versions refuse the whole shipped XG profile. This amends ADR 0007's statement that checksummed SysEx
  and NRPN tables need a new schema version. The older `sysex` templates still have no checksums.
- **An automation lane targets an instrument part's parameter.** `AutomationTarget.ForInstrument(instrument, port,
  channel, key)`: the external instrument entity, its port ID, the part's channel, and the parameter's key in the
  instrument's profile. It is neither a MIDI target nor a device target, and it is not tied to the track's
  connections: any track can carry it, and moving connections does not retarget it. Values are normalized (0 to 1)
  and scaled to the parameter's range: `min + round(fraction × (max − min))`, which inverts MIDI 1.0 value scaling
  exactly for a full 7- or 14-bit range.
- **Project format 4 gains a target type, not a version.** A lane's target may be
  `{ "type": "instrument", "instrument": "<id>", "port": "A", "channel": 3, "parameter": "filter-cutoff" }`.
  Projects without such lanes are written exactly as before. A format 4 reader that predates this refuses a file
  with such a lane with a path error (`"instrument" is not a known automation target`) and never overwrites it, so
  no migration is needed. A lane whose instrument, port, profile, or parameter is missing is kept and reported.
- **Translation happens when the plan is compiled.** `HardwareAutomation` (in `Bluestone.Application/Routing`,
  where profiles and endpoints are resolved) samples each lane like other lanes (every 1/32 quarter note on linear
  segments), scales the value, thins it, and encodes it with the mapping. The result is a `ParameterPart` per lane
  on the slot of the part's endpoint; an endpoint that no connection uses gets a new slot after the others.
- **Thinning.** A value is sent only when the integer sent changes. One parameter is sent at most once per
  20 ms of song time (`HardwareAutomation.MinimumInterval`, at most 50 changes a second): a change that comes sooner
  waits until the interval has passed and is replaced by any newer change while it waits, so every ramp still ends
  on its last value. Holds and jumps are sent at their ticks unless they fall inside the interval.
- **Ordering follows the plan's rules** (handoff section 5 and decision 19): events sort by (tick, phase, origin,
  index, part). A lane's events take its track as origin and indexes from `2^30 + 8 × lane index`, so at the same
  tick and phase they follow the track's own events, lanes keep their order, and one change's messages (select,
  data entry, null) are never interleaved with anything on the same origin. Controllers are in the control phase,
  so they precede notes at the same tick; SysEx is in the SysEx phase. Parameter parts come after the routing parts.
- **Chase.** Controller mappings are chased by the existing controller chase. RPN, NRPN, and SysEx mappings are
  chased whole by the plan: on play, seek, and loop wrap the latest change of each such parameter before the
  position is resent after the controller state; at a wrap, only when it differs from the value at the loop end,
  and a parameter first set inside the loop is left as it is (it has no reset value). While stopped, the engine
  resends chased RPNs and NRPNs with the channel state but not SysEx.
- **Like device automation, it plays whether its track is muted or not.** When two lanes control the same
  parameter of the same part, the first in track and lane order is sent and the other is reported.
- **Standard MIDI File export writes it as the MIDI it becomes** (it is MIDI on the wire), on the lane's track and
  the part's channel (a file has no ports), after the track's own events at the same tick and phase. A lane that
  cannot be translated is reported with SMF204, as device lanes still are.
- **UI.** The add-lane menu of a track routed to an instrument whose profile has a parameter map lists the
  parameters by name under "Instrument parameters"; a lane is created for the track's part (its port and channel).
- **The shipped generic XG profile maps** CC volume, pan, expression, and reverb, chorus, and variation sends; the
  XG NRPNs for vibrato, filter, and EG offsets; RPN pitch bend range and fine tuning; and multi part SysEx
  parameter changes (`08 nn xx`) for volume, pan, sends, filter, and detune (nibbles). It stays `unverified`.

## Consequences

- Hardware parameters reachable only by RPN, NRPN, or SysEx can be automated by name, independent of routing, and
  play and export the same MIDI. Existing projects send byte-identical MIDI (the section 5 hashes hold).
- Limits: the SysEx device number byte comes from the profile entry, not from the instrument, so a second unit with
  another device number needs its own profile entry; `{channel}` stands for the part number, which matches XG's
  default receive channels only; values are integers in the device's own units, without display units or steps;
  a track that only carries instrument automation and has no connection is still reported as "not routed"
  (decision 15); live parameter changes (turning a knob while stopped) are not sent.
- Tests: `ParameterMapTests` (loading, validation, every encoding, Roland and Yamaha checksums, scaling),
  `InstrumentAutomationTests`, `InstrumentAutomationFormatTests`, `HardwareAutomationTests` (thinning, plan
  ordering, slots, diagnostics, chase, loop wrap, engine chase, SMF export, undo),
  `MainViewModelTests.InstrumentParameters_FromTheProfile_CanBeAutomatedByName`, and `AutomationMenuTests` (headless).
