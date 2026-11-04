# Windows: playing into a VST synth through a MIDI loopback

Bluestone hosts VST 3 and CLAP plugins built for its own architecture ([plugin-hosting.md](plugin-hosting.md)).
This page is for synths it cannot host, such as VST 2 or 32-bit plugins: run the synth in a standalone
host and connect Bluestone to it with a MIDI loopback port, as an external instrument. It records the
setup that was verified and how.

## Verified setup (2026-10-04)

| Part | Used |
| ---- | ---- |
| Machine | Windows 11 ARM64 (build 26200) in Parallels Desktop on Apple silicon |
| Loopback | Windows MIDI Services, MIDI 1.0 loopback endpoint (`Loopback1`) |
| Host | SAVIHost 32-bit (x86), running under ARM64 emulation |
| Synth | Yamaha S-YXG50 VSTi (`syxg50.dll`; it only loads in the 32-bit host) |
| Bluestone output | WinMM (ADR 0014), with SysEx (ADR 0015) |

Result: the Ambient sample played through S-YXG50 correctly, including its XG SysEx setup, so short
messages and SysEx both pass through the loopback to a 32-bit host under emulation.

Bluestone has since also been run natively on Windows 11 Pro x64 (build 26200): playback, imported
files with program changes, and the computer keyboard input with MIDI thru.

On 2026-11-03, the WinMM MIDI 1.0 input/output loopback was also used for short-message and SysEx
round-trip tests, endpoint create/remove polling, MIDI thru timing, and engine live-input timing. No
physical MIDI instrument was connected; the recorded results and hardware checks still open are in
[hardware-test-plan.md](hardware-test-plan.md).

## Why not loopMIDI

loopMIDI (and rtpMIDI, LoopBe1 and similar) install a kernel driver. Their installer failed on ARM64
Windows with MSI error `0x80070643` on the `teVirtualMIDI64` package, because x64 kernel drivers cannot
load on ARM64. Windows MIDI Services needs no third-party driver, so it works on ARM64. On x64
Windows, loopMIDI remains a working alternative.

## Steps

1. Windows 11 already includes the Windows MIDI Services service (`midisrv`), but not the tools to
   configure it. Install **Windows MIDI Services Tools** and the **transports** from
   [github.com/microsoft/MIDI/releases](https://github.com/microsoft/MIDI/releases), choosing the
   installer for your architecture (`arm64` or `x64`). The winget package pointed at a removed
   release when this was written, so download it directly.
2. In **MIDI Settings**, create a **MIDI 1.0 loopback** endpoint. Its name does not matter.
3. Start the VST host (SAVIHost for S-YXG50) and pick the loopback as its MIDI input under
   **Devices → MIDI**. Pick an audio output under **Devices → Wave**.
4. Route the tracks to the loopback output. WinMM endpoint changes are polled about once per second; refresh
   the endpoint list if an application does not yet show the new port.
5. Play. If the song has no XG reset, sending XG System On (`F0 43 10 4C 00 00 7E 00 F7`) first puts
   the synth in a known state.

## Notes

- The playhead may lead what you hear in the VM because of audio buffering in the host and Parallels.
  On macOS the same project lines up closely; see the timing notes in ADR 0013.
- Bluestone's integration tests never target loopback or hardware ports; this check was done by hand.
