# Engineering Decisions

This page is a lightweight architecture-decision log. It records the reason behind important choices so future changes do not have to reconstruct the original problem from commit history.

## ED-001 — Keep BabyAI-Core as the technical codename during active development

**Decision:** use UNIX as the product name while keeping `BabyAI`, package names, paths and repository identifiers until a deliberate rename phase.

**Why:** a broad rename in the middle of runtime, installer and protocol work creates large noisy diffs with little functional value and unnecessary regression risk.

## ED-002 — Persistent Desktop worker

**Decision:** keep the native model provider resident inside a persistent worker.

**Why:** reloading the model for every user turn dominates latency. The worker can keep inference state expensive-to-create while rebuilding mutable task/memory/permission context per command.

## ED-003 — Add streaming as protocol v2 instead of replacing v1

**Decision:** protocol v2 is additive.

**Why:** existing non-chat commands and compatibility paths remain stable while streaming chat gets stronger ordering, state and cancellation semantics.

## ED-004 — Gate visible streaming text

**Decision:** model output is validated before deltas become visible.

**Why:** a parser that rejects internal tool JSON only after generation is too late if raw tokens have already appeared in the UI.

## ED-005 — One-shot approvals are consumed before execution

**Decision:** remove the pending approval before the protected action runs.

**Why:** cancellation/crash must not make the same authorization replayable after worker restart.

## ED-006 — Workspace root is metadata, not filesystem permission

**Decision:** associating a directory with a Workspace does not read or enumerate it.

**Why:** project identity and access capability are different concepts. Selecting a project should not silently widen local data access.

## ED-007 — Workspace registry before context isolation

**Decision:** establish stable Workspace IDs and persistence first, then wire task/history/memory/session routing in a separate slice.

**Why:** separating identity from behavior makes migrations testable and avoids introducing several new ownership rules in one change.

## ED-008 — Voice foundation before STT

**Decision:** verify microphone capture, VAD, timeout and release on real hardware before choosing/adding STT.

**Why:** STT would hide lower-level audio-device failures and make diagnosis harder. It also introduces additional CPU/GPU/VRAM constraints that should not be mixed into microphone debugging.

## Adding a decision

Use a small format:

```text
ED-NNN — title
Decision: what we chose
Why: the concrete problem/tradeoff
Consequences: optional follow-up constraints
```

Record architecture decisions, not every implementation detail.
