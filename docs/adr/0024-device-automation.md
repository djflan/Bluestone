# 0024. Device parameter automation is separate from signal flow

- Status: Accepted; amends 0020; amended by 0031
- Date: 2026-10-10

## Context

ADR 0020 limited automation targets to MIDI parameters (controllers, pitch bend, channel pressure),
rendered into channel events. Device chains (ADR 0022) bring parameters that are not MIDI at all: an
arpeggiator's rate, a synthesizer's release, a filter's cutoff. A chain like
`MIDI FX → Synth → Filter → Delay` needs each of these automated directly, independently of the order of
the devices and of what flows through them.

## Decision

- `AutomationTarget` gains a device-parameter form (`ForDevice(DeviceId, ParameterId)`), addressed by the
  device's stable identity and the definition's parameter ID, never by position.
- Device automation is **not** signal. `TrackRendering` returns it as `ParameterChange`s, separately from
  the track's events. It does not pass through the devices before its target, and it is never encoded
  as MIDI. MIDI controllers stay musical events that do travel through the chain.
- Built-in processors receive their changes at the change's tick (the runner splits processing there,
  before the events at that tick). Every other device (plugins, software instruments) receives them as a
  `ParameterFeed` for delivery through the plugin parameter interfaces in its worker.
- Device automation runs whether its track is muted or not: muting silences a track's content, not its
  devices' parameters.
- A lane whose device or parameter is missing is kept and reported, never dropped.
- Standard MIDI File export reports device lanes it cannot write (SMF204).
- Translating parameter automation into CC, RPN, NRPN, or SysEx for hardware belongs to a future
  external-instrument adapter, not to the automation model.

## Consequences

- Reordering or moving devices never retargets automation (scenarios 5 and 14).
- ADR 0020's statements "there is no track kind" (see ADR 0021) and "automation targets are MIDI
  parameters" no longer hold; the rest of ADR 0020 (clips, lanes, rendering, chase) stands.
- Tests: `DeviceAutomationTests`, `ChainRunnerTests.ParameterChanges_*`, `SignalGraphTests.DeviceAutomation_*`,
  `AcceptanceScenarioTests.Scenario5_*`, `SmfImportExportTests.Export_ReportsDeviceAutomation_ItCannotWrite`.
