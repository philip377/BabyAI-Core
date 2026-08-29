# UNIX Wiki

> **UNIX** is the product name. **BabyAI / BabyAI-Core** remains the repository and technical codename while the project is being built.

This Wiki is the readable map of the project: what UNIX is, how the pieces fit together, what has already been implemented, and why important engineering decisions were made.

## What UNIX is

UNIX is a local-first personal AI runtime for Windows. The long-term goal is not merely a chat window around a model, but an AI participant that can keep project context, work with local tools under explicit permissions, understand documents and screens, speak naturally, and eventually operate inside shared communication/workspace environments.

The project is intentionally built in layers. Each new capability must have a clear ownership boundary, cancellation path, persistence model, and permission model before the next layer is added.

## Current foundations

The current codebase already contains the native/local model path, resident desktop worker, safe Windows actions, explicit permission handoff, memory layers, streaming protocol v2, desktop UX states, screen-capture boundary, and the first persistent Workspace registry.

Voice/VAD and deeper Workspace context isolation are being developed in separate branches so that audio, project context, and the existing desktop runtime can be tested independently.

## Start here

- [Architecture](Architecture.md) — how Desktop, Core, PRIMUS, model, memory and tools connect.
- [Roadmap](Roadmap.md) — the order in which UNIX is being expanded.
- [Workspace](Workspace.md) — project spaces and context isolation.
- [Memory System](Memory-System.md) — session, durable, project and episodic memory.
- [Permissions & Safety](Permissions-and-Safety.md) — why UNIX cannot silently act on the computer.
- [Native AI Runtime](Native-AI-Runtime.md) — local Qwen3-8B, CPU/Vulkan and resident inference.
- [Streaming Protocol v2](Streaming-Protocol-v2.md) — thinking/answering/executing states, deltas and cancellation.
- [Voice](Voice.md) — VAD, STT, TTS and future barge-in.
- [Communication Vision](Communication-Vision.md) — the future AI-centered chat/workspace client.
- [Build & Install](Build-and-Install.md) — Windows build and release pipeline.
- [Troubleshooting](Troubleshooting.md) — how the project is debugged.
- [Engineering Decisions](Engineering-Decisions.md) — short records of important architectural choices.
- [Project Principles](Project-Principles.md) — the rules that should survive implementation changes.

## Canonical technical docs

The Wiki explains the system; implementation contracts remain in the repository `docs/` directory. Useful references include:

- `docs/NATIVE_BRAIN.md`
- `docs/STREAMING_PROTOCOL_V2.md`
- `docs/SECURITY_MODEL.md`
- `docs/MEMORY_MODEL.md`
- `docs/WINDOWS_ACTIONS.md`
- `docs/SCREEN_VISION.md`
- `docs/ASSISTANT_UX.md`

When the Wiki and a protocol document disagree, the protocol document and current code are authoritative.
