# 0046. ASIO audio host on Windows x64

- Status: Accepted
- Date: 2026-11-03

## Context

WASAPI shared-mode output works on Windows but does not cover the low-latency driver path required by many audio interfaces. Bluestone's audio device layer already uses `cpal`, which has an optional ASIO host. The host must be selectable without changing macOS/Linux behavior or making the Windows ARM64 build depend on an unavailable class of drivers.

## Decision

- Keep ASIO support optional behind an explicit `BluestoneAsio=true` build property. Standard Windows CI and release builds remain WASAPI-only until the required Steinberg agreement is signed; Windows ARM64 remains WASAPI-only.
- When explicitly enabled, list devices by host and pass the chosen host through the native audio ABI and engine IPC. A clean first run prefers ASIO when a driver is listed; existing settings with no saved host retain the Windows system default (WASAPI). If ASIO cannot open, use WASAPI and report the fallback; retry ASIO only when an ASIO endpoint is listed. Keep the existing WASAPI stream open while it retries.
- Bump the engine wire protocol to version 9 for the added host and device-buffer fields; mismatched engine/app versions fail the existing handshake instead of decoding different layouts.
- Dispatch ASIO enumeration and stream lifecycle calls to one dedicated Windows STA thread. Cache the enumerated driver devices while a stream is active: ASIO permits only one active driver at a time, and cpal's enumerator stops when another driver is already loaded. Reuse the cached device for same-driver input and output.
- Use the ASIO driver's default buffer. `cpal` reports a range but does not expose a driver's discrete buffer-size granularity or control-panel API; a requested size inside the range can still be refused. Hide the app buffer picker for ASIO and direct musicians to the driver's utility. If a driver reports its default rate as 0, select a supported rate, preferring 48 kHz.
- Use the ASIO SDK under Steinberg's proprietary licence. Do not commit or redistribute SDK files, and do not use the GPLv3 licensing option. The `asio-sys` dependency is Apache-2.0. ASIO is not built by standard Windows CI or release workflows pending the signed agreement. An explicitly enabled local build uses a separately obtained SDK via `CPAL_ASIO_DIR` and requires LLVM/libclang. The maintainer must have the proprietary agreement signed by Steinberg before enabling or distributing ASIO binaries; the agreement is not included in this repository.
- Keep host enumeration and stream opening runtime-selectable. One Bluestone process owns the driver at a time (the application by default, or the engine when `BLUESTONE_ENGINE=1`). Audio input, when used with ASIO, must use that same host and driver. The app reuses its pre-open device snapshot for Audio Settings while the engine owns ASIO rather than probing the active driver from another process.

## Consequences

- ASIO x64 support is implemented and was tested locally with the Realtek driver, but is currently excluded from standard Windows CI and releases pending the signed agreement. Windows ARM64 remains WASAPI-only.
- On this Windows x64 workstation the Realtek ASIO driver opened at 48 kHz with its default 960-frame buffer (20 ms). Output and input opened on the same driver, device listing stayed available while the stream was open, and the five-second silent callback probe reported no late callbacks or xruns. This is not a low-latency hardware-interface result and was not an acoustic listening test.
- WASAPI remains a functional fallback. ASIO vendor control panels remain external because `cpal` does not expose `IASIO::controlPanel`. Driver thread-affinity behavior beyond the Realtek workstation check remains unverified.
