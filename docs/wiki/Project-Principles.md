# Project Principles

These principles describe what UNIX should preserve even as models, UI and implementation details change.

## Local-first by default

Core functionality should work locally whenever practical. Network services may be added later for collaboration or optional capabilities, but UNIX should not require a remote dependency merely to be a personal assistant.

## Explicit capability boundaries

A model deciding that an action is useful does not automatically authorize that action. Protected local access should remain explicit, scoped and visible to the user.

## No hidden autonomy

Future background/durable work must have identity, state, cancellation and user-visible ownership. "Autonomous" must not mean invisible processes doing unspecified work.

## Understand failures before replacing architecture

When a local component fails, first find the failure mechanism. Rewriting a working interface or subsystem to make a symptom disappear often hides the real bug and creates a second one.

## Build in verifiable layers

Microphone before STT. Workspace registry before context isolation. Read-only document access before retrieval and write access. Each layer should be independently testable.

## Keep context ownership clear

Every persistent piece of information should have an owner/scope: global user state, session state, Workspace/project state, or future shared-space state.

## Separate metadata from permission

Knowing that a project directory exists is not permission to read it. Knowing that an application window exists is not permission to manipulate it. Keep description/context and capability separate.

## Prefer observable behavior

Important runtime state should be visible through UI status, diagnostics, protocol events or logs. Silent fallback makes debugging and trust harder.

## Measure performance

Optimize model load, TTFT, generation and resource pressure using measurements. Perceived latency can often improve through streaming even before raw token speed changes.

## Backward-compatible migrations where reasonable

New foundations should not force destructive rewrites without need. Additive protocols, legacy fallback and incremental Workspace migration reduce regression risk.

## Product name vs implementation name

UNIX is the product identity. BabyAI/BabyAI-Core remains a technical codename until a controlled rename can be performed without turning active feature work into a large mechanical refactor.

## AI should live inside the work

The long-term direction is not an isolated chatbot. UNIX should eventually become a participant in project spaces, documents, tasks and communication while preserving the same permission and context-ownership rules developed in the local runtime.
