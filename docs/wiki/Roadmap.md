# Roadmap

UNIX is built in dependency order. The point of the roadmap is not to predict every final feature; it is to avoid stacking fragile layers on top of unverified ones.

## Completed foundations

- Windows desktop shell and explicit assistant UX states.
- Local/native model runtime with CPU/Vulkan paths.
- Persistent desktop worker.
- Safe local actions and one-shot permission handoff.
- Session and durable memory foundations.
- Screen-capture safety boundary.
- Streaming protocol v2 with visible deltas, terminal-event ownership and cancellation.
- Persistent Workspace registry with stable IDs and optional root metadata.

## Current parallel tracks

### Voice foundation

1. Microphone capture.
2. Voice activity detection (VAD).
3. Hardware smoke test and reliable device release.
4. Local streaming STT.
5. Streaming TTS.
6. Barge-in: user speech interrupts generation/playback and returns UNIX to listening.

The VAD layer is intentionally verified before STT is stacked on top of it.

### Workspace foundation

1. Persistent Workspace registry.
2. Active Workspace context.
3. Per-Workspace task state.
4. Per-Workspace session context.
5. Per-Workspace project memory.
6. Per-Workspace persisted history.
7. Read-only document boundary.
8. Retrieval/indexing.

## After Workspace + Voice

### Documents and Retrieval

UNIX should be able to work inside a selected project without reading arbitrary local files by accident. Document access must remain capability-controlled and scoped to an explicit Workspace/user request.

### Durable Jobs

Long-running work needs explicit job identity, persisted state, cancellation and resumability. "Autonomy" without these primitives becomes invisible background behavior, which the project intentionally avoids.

### Vision

Screen capture already has a safety boundary. Real visual understanding comes later, after the model/transport/tool ownership is stable enough to make visual suggestions auditable and cancellable.

### External tools and communication

Only after local boundaries are stable should UNIX expand into external services, shared project spaces and the broader communication-client vision.

## Rule of the roadmap

A feature moves forward when the layer below it is testable and its failure modes are understood. A green CI build is necessary but not sufficient when a capability depends on physical hardware or user interaction.
