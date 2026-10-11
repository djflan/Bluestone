# Device profiles

This directory holds the device profiles that ship with Bluestone. A profile describes what an
instrument understands (banks, programs, drum kits, controllers, parameters, SysEx, initialization,
and a parameter map for automation). It never describes where the instrument is connected; that is a
routing decision.

The format is documented in [docs/profiles.md](../docs/profiles.md). Files must be named
`<something>.bluestone-profile.json`.

| Profile | ID | Verification |
| ------- | -- | ------------ |
| [General MIDI (Level 1)](general-midi.bluestone-profile.json) | `bluestone.generic.gm1` | Unverified |
| [Generic XG](generic-xg.bluestone-profile.json) | `bluestone.generic.xg` | Unverified |

## Contributing a profile

1. **Only redistributable facts.** Interface facts (message formats, bank select numbers,
   controller numbers) are fine. Do not copy voice lists, manual text, artwork, screenshots,
   firmware, or ROM data from a vendor unless you hold the right to redistribute them.
2. **Record provenance.** List every source in `provenance.sources`, name the contributors, set
   `redistributionConfirmed` to `true` only if you are sure, and state `verification` honestly:
   `unverified` (from documentation), `simulated` (checked with traces or a compatible synth), or
   `hardware-verified` (checked on the instrument itself).
3. **Mark destructive messages.** SysEx that resets or overwrites state must use effect `reset` or
   `bulk`, so Bluestone asks before sending it. Nothing in a profile is ever sent just because a
   project was opened. Parameter map entries are sent by automation without asking, so each must
   set only its one parameter.
4. **Name trademarks factually.** Add a non-affiliation notice to `notices` when a profile names a
   vendor's product or format.
5. Run `dotnet test src/dotnet/Bluestone.slnx`; the integration suite validates every profile here.
