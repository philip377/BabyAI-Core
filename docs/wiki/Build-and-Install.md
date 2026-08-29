# Build & Install

UNIX currently targets a Windows desktop release pipeline that bundles the application, Python Core and native inference runtimes into a distributable package.

## CI layers

The repository uses separate checks so failures can be localized instead of treating every red build as the same problem.

Typical validation includes:

- Python tests across supported versions;
- Windows Desktop build;
- Desktop startup smoke test;
- diagnostics export checks;
- native runtime/shim validation;
- Windows Release Bundle assembly.

## Windows Release Bundle

The release pipeline prepares:

1. the Core Python package and offline dependencies;
2. a self-contained Python runtime;
3. the WinUI Desktop application;
4. portable CPU native runtime;
5. AVX runtime;
6. AVX2 runtime;
7. Vulkan runtime;
8. release directory layout;
9. installer verification;
10. a single EXE setup package.

The native variants exist so UNIX can choose the best supported route on the target machine while retaining a portable fallback.

## Exact-head testing

When testing a candidate build, always record the exact commit SHA that produced it. A green artifact from an older head does not validate a newer fix.

For hardware-dependent features such as microphone capture or GPU inference, CI is only the first gate. The exact build should also be smoke-tested on representative Windows hardware.

## Release discipline

A useful release checklist is:

- all required checks green on the exact head;
- installer artifact belongs to that exact head;
- SHA-256 recorded when distributing a manual test build;
- startup smoke passes;
- native model route is detected correctly;
- cancellation and permission flows still work;
- new hardware capability receives a real-device smoke test.

## Development install

The project remains a Python package (`babyai-core`) with WinUI Desktop code under `desktop/`. Existing command-line entry points are useful for diagnostics and isolated Core testing, but the long-term product boundary is the installed UNIX desktop runtime rather than a collection of manual developer commands.
