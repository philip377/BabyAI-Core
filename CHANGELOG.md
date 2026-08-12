# Changelog

## v0.1.0 Genesis

First MVP milestone for BabyAI Core.

### Core
- Local-first PRIMUS orchestration with Ollama and echo providers.
- Persistent identity and typed SQLite MEMORIA.
- Bounded context and structured one-step tool calling.
- Planner v1 and permission-gated Agent Loop.

### Cognitive loop
- Working Memory task state.
- COGNITION proposal-only task updates.
- HYPOTHESIS records with explicit confirmation/rejection.
- Evidence collection and `supports` / `contradicts` / `inconclusive` assessment.
- CURIOSA explicit uncertainty questions.
- AUTODIDACT lesson candidates with explicit approval before writing durable knowledge.
- Read-only Learning Loop next-step guidance.
- End-to-end cognitive smoke coverage.

### Safety and system access
- Deny-by-default capability permissions.
- OBSERVER system snapshot behind explicit permission.
- Permissioned read-only filesystem listing and text reading.
- No automatic shell execution, file writes, process control, remote device control, or autonomous learning.

### Setup
- `babyai-setup init` initializes local state without granting capabilities.
- `babyai-setup doctor` validates local state and provider health.
- CLI and installation smoke tests.
