# Device profile format (schema version 1)

A device profile is a JSON file named `<name>.bluestone-profile.json` that describes what an
instrument understands. Profiles never say where an instrument is connected: the same profile can
drive a physical instrument, a compatible software synth, or a virtual port. Comments and trailing
commas are allowed.

Profiles are untrusted input. Bluestone rejects any profile with an error and reports every problem
with its JSON path, for example `$.banks[0].programs[3].number: must be between 1 and 128 (found 0)`.
Warnings (unknown properties, unused template parameters) do not block loading.

## Top level

| Property | Required | Description |
| -------- | -------- | ----------- |
| `$schema` | no | Ignored; lets an editor find a JSON schema. |
| `format` | yes | Always `"bluestone-device-profile"`. |
| `schemaVersion` | yes | `1`. Newer versions are refused with a clear message. |
| `id` | yes | Stable identifier: lowercase letters and digits separated by `.` or `-`, e.g. `community.vendor.model`. Projects refer to profiles by this ID. |
| `version` | yes | The profile content's own version, e.g. `"1.2.0"`. |
| `name` | yes | Display name (≤ 128 characters). |
| `manufacturer`, `model`, `description` | no | Display text. |
| `notices` | no | Trademark and non-affiliation notices, shown wherever the profile is described. |
| `provenance` | yes | Where the data came from (below). |
| `protocols` | no | Any of `gm1`, `gm2`, `gs`, `xg`, `custom`. |
| `identity` | no | MIDI Identity Reply match rules: `manufacturerId` (1 byte, or 3 bytes starting `00`), optional 2-byte `family` and `member`, as hex strings such as `"00 20 29"`. |
| `drumChannels` | no | Channels (1–16) that play drum kits by default. |
| `banks` | no | Up to 1024 banks (below). |
| `drumKits` | no | Kits with per-note instrument names. |
| `controllers` | no | Control change numbers the instrument responds to, with optional defaults. |
| `parameters` | no | Registered parameters (RPN): `msb`, `lsb`, `name`, `min`, `max`, optional `default`, as 14-bit values. |
| `sysex` | no | Up to 256 SysEx templates (below). |
| `parameterMap` | no | Up to 512 named parameters and the MIDI that sets each, for automation (below). |
| `initialization` | no | Templates to send, in order, **only when the user explicitly initializes the instrument**: `{ "sysex": "<template id>", "delayAfterMs": 50 }`. |
| `limitations` | no | Known gaps, in plain language. |
| `extensions` | no | Custom data under namespaced keys (`"org.example.feature"`), preserved but not interpreted. |

## Provenance

| Property | Description |
| -------- | ----------- |
| `sources` | At least one source for the data. |
| `contributors` | Who compiled it. |
| `license` | License of the profile data, e.g. `"MIT"`. |
| `redistributionConfirmed` | Must be `true`. |
| `verification` | `unverified` (from documentation), `simulated` (checked against traces or a compatible synth), or `hardware-verified`. |
| `notes` | Optional details. |

## Banks and programs

```json
{ "id": "drums", "name": "Drum kits", "kind": "drums", "msb": 127, "lsb": 0,
  "programs": [ { "number": 1, "name": "Standard", "category": "Kits" } ] }
```

`msb` and `lsb` are the bank select values (0–127); omit them for instruments without banks.
Program `number` is **one-based (1–128)**, as printed in most voice lists; Bluestone sends
`number − 1` on the wire. Selecting a program over MIDI 1.0 sends bank select MSB, then LSB, then the program change.

## SysEx templates

```json
{ "id": "xg-system-on", "name": "XG System On", "effect": "reset",
  "bytes": "F0 43 {device} 4C 00 00 7E 00 F7",
  "parameters": [ { "name": "device", "min": 16, "max": 31, "default": 16 } ] }
```

- `bytes` is space-separated: two-digit hex bytes or `{parameter}` placeholders. It must start with
  `F0`, end with `F7`, and every byte between must be a data byte (`00`–`7F`). At most 512 bytes.
- Each placeholder is a declared 7-bit parameter with `min ≤ default ≤ max`.
- `description` is optional display text.
- `effect` is `parameter`, `reset`, or `bulk`. Resets and bulk transfers always require confirmation
  before sending. Opening a project never sends anything from a profile.

These templates do not compute checksums or split values across bytes. Parameter map entries do (below).

## Parameter map

The parameter map names the parameters of the instrument's parts that automation can set (ADR 0031). An
automation lane names a part (instrument, port, channel) and a parameter by its `id`. Lane values are normalized
(0 to 1) and scaled to the parameter's range: `min + round(value × (max − min))`. They are translated into MIDI
when playback is prepared and when a MIDI file is exported ([architecture.md](architecture.md#automation)).
Properties that do not belong to an entry's `kind` are warnings and are ignored.

```json
"parameterMap": [
  { "id": "volume", "name": "Volume", "kind": "cc", "controller": 7, "default": 100 },
  { "id": "modulation", "name": "Modulation", "kind": "cc", "bits": 14, "controller": 1 },
  { "id": "filter-cutoff", "name": "Filter Cutoff", "kind": "nrpn", "msb": 1, "lsb": 32, "default": 64 },
  { "id": "fine-tuning", "name": "Fine Tuning", "kind": "rpn", "bits": 14, "msb": 0, "lsb": 1, "default": 8192 },
  { "id": "part-detune", "name": "Part Detune", "kind": "sysex", "device": 16, "max": 255, "default": 128,
    "bytes": "F0 43 {device} 4C 08 {channel} 09 {value[7:4]} {value[3:0]} F7" },
  { "id": "master-volume", "name": "Master Volume", "kind": "sysex", "device": 16,
    "bytes": "F0 41 {device} 42 12 40 00 04 {value} {checksum} F7",
    "checksum": { "convention": "roland", "start": 5 } }
]
```

| Property | Kinds | Description |
| -------- | ----- | ----------- |
| `id` | all | Key, unique in the map, at most 64 characters: lowercase letters, digits, `-`, `_`, `.`, starting with a letter or digit. Projects store it. |
| `name` | all | Display name (≤ 64 characters), shown in the add-lane menu. |
| `kind` | all | `cc`, `rpn`, `nrpn`, or `sysex`. |
| `min`, `max` | all | The integers sent for 0 and 1. `min` defaults to 0; `max` to the full range (127, or 16383 for 14 bits; 127 for SysEx, where it may be up to 65535). `min` must be less than `max`. |
| `default` | all | The instrument's own default, if known; between `min` and `max`. |
| `bits` | `cc`, `rpn`, `nrpn` | `7` (default) or `14`. |
| `controller` | `cc` | The controller. For 14 bits, the MSB controller 0–31; its LSB is that number plus 32. Bank select, LSB controllers, data entry and increment, RPN/NRPN selectors, and channel mode messages are refused. |
| `msb`, `lsb` | `rpn`, `nrpn` | The parameter number. 127/127 (the null parameter) is refused. |
| `nullAfter` | `rpn`, `nrpn` | Whether the RPN null (CC 101 and 100 at 127) follows each change, so a stray data entry changes nothing. Default `true`. |
| `bytes` | `sysex` | The message, as for SysEx templates (3 to 512 tokens, `F0` first and `F7` last, data bytes between), with these placeholders between them: `{value}` (bits 6–0), `{value[high:low]}` (1 to 7 bits, high bit first, within bits 15–0, for example nibbles), `{channel}` (the part's channel, 0–15), `{device}`, and `{checksum}` (at most once). At least one value placeholder is required, and together they must send every bit `max` needs. |
| `device` | `sysex` | The byte `{device}` sends (0–127), required when it is used. |
| `checksum` | `sysex` | Required when `{checksum}` is used, and only then: `convention` (`roland` or `yamaha`) and `start`, the position of the first byte summed (F0 is 0; from 1 to the byte before `{checksum}`). The checksum is the 7-bit value that makes the bytes from `start` up to it sum to 0 (mod 128). Roland sums the address and data; Yamaha the byte count, address, and data. A convention that does not match the manufacturer ID is a warning. |

What each kind sends, in order:

- `cc`: the controller; for 14 bits the MSB controller, then the LSB controller.
- `rpn`: CC 101 and 100 (the parameter), CC 6 (data entry MSB), CC 38 for 14 bits, then CC 101 and 100 at 127
  unless `nullAfter` is `false`. `nrpn` is the same with CC 99 and 98.
- `sysex`: one message with the placeholders filled in and the checksum computed last.

Parameter map entries set one parameter. Never map a reset or a message that overwrites more than that
parameter: automation sends without asking. An older Bluestone warns about `parameterMap` and ignores it; the
schema version stays 1.
