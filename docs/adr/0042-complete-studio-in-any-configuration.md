# 0042. Bluestone is a complete studio in any configuration

- Status: Accepted; amends the device-ecosystem direction of 0041
- Date: 2026-11-03

## Context

ADR 0041 made hardware and software peers in the device workflow and said Bluestone would initially depend primarily on third-party plugins and hardware, with a small native device set proving the first-party contract. That framing treats Bluestone's own devices as a validation exercise and implicitly assumes the musician brings the instruments and effects needed to finish a production.

The peer principle is stronger than a statement about hardware versus plugins. A musician may own only Bluestone, only Bluestone and third-party plugins, only hardware and an audio interface, or any mix of these. If timing, accuracy, or workflow quality depends on which of those setups the musician has, the devices are not really peers.

## Decision

- **Every studio configuration is first-class.** Bluestone's own devices, third-party plugins, and hardware are equal participants. Bluestone alone, Bluestone with plugins, Bluestone with hardware, and any combination are all supported setups, not degraded modes of a "real" studio.
- **The timing and accuracy guarantees do not depend on the configuration.** Recording placement, playback, latency compensation, automation, project recall, and export agree with the same rigor whether a project uses only Bluestone devices, only external gear, or both. Measurements and tests cover each configuration rather than assuming one stands in for the others.
- **Bluestone alone is a complete virtual studio.** The destination is that a musician with only Bluestone and its free core devices can compose, record, arrange, mix, and export a finished production: at least instruments for melodic and rhythmic material, and the essential effects for shaping and mixing them.
- **The free core is the complete studio.** Optional premium devices may add depth, character, or specialized instruments, but no part of the complete-studio workflow may require a purchase.
- **This is the destination, not Release 1.0 scope.** Release 1.0 keeps the small native device set of its native-device slice. That set and its contract must be chosen and designed as the first steps toward the complete free kit, not as throwaway proofs.

## Consequences

- The native device contract (Release 1.0 slice 11) is judged by whether it can grow into a complete production kit, not only by whether it can host one example device.
- Product briefs for first-party devices must state which gap in the complete free studio they fill.
- Timing, latency, and export verification gain a software-only configuration alongside hardware and plugin configurations.
- The free-versus-premium boundary is constrained: premium products are additive, and the core catalog must cover a complete production.
- The broader first-party catalog remains after the production loop in the roadmap, but it is now a product commitment rather than an optional extension.
