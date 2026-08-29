# Architecture

UNIX is deliberately split into boundaries rather than built as one giant assistant process.

## High-level flow

```text
WinUI Desktop
    ↓ JSONL / streaming protocol
Persistent Desktop Worker
    ↓ trusted command surface
Core / PRIMUS
    ├─ Identity
    ├─ Working task / Workspace context
    ├─ Memory
    ├─ Planner / intent boundary
    ├─ Tool approval state
    └─ Model provider
           ├─ Native local runtime
           └─ compatibility providers

Tool requests
    ↓
Permission gate
    ↓
Windows/local executor
```

## Desktop

The WinUI application owns visible UX state: idle, thinking, answering, executing, approval, listening, done and error. It should not contain model reasoning or duplicate Core policy.

The Desktop talks to a persistent worker instead of launching a fresh model process for every turn. This keeps the native model resident and reduces repeated startup cost.

## Persistent worker

The worker is the transport boundary between WinUI and Python Core. It accepts narrow JSON commands and keeps ordering, Unicode safety, protocol ownership and terminal-event rules explicit.

Protocol v2 adds streamed state/delta events while preserving a terminal `done` or `error` event. Legacy protocol v1 remains a compatibility path.

## PRIMUS / Core

PRIMUS assembles the model context and owns the conversation/tool decision path. It combines identity, task/workspace context, selected memory and the user's current input. Tool protocol artifacts are filtered out of remembered/prompted content.

PRIMUS does not bypass permissions. A model request for a local action still has to pass through the executor capability model and, when required, a one-shot approval.

## Model runtime

The native path uses a local GGUF model through the project's native runtime. The current baseline is Qwen3-8B with CPU and Vulkan acceleration paths. The resident provider exists so the Desktop can keep the model loaded between requests.

## Workspace direction

Workspace is becoming the top-level project boundary. A Workspace has a stable identity and optional root-directory metadata. The root is not permission: merely selecting a Workspace must never silently enumerate or read its files.

The next layer isolates task, chat history, project memory and session context by active Workspace.

## Canonical references

- `docs/NATIVE_BRAIN.md`
- `docs/STREAMING_PROTOCOL_V2.md`
- `docs/SECURITY_MODEL.md`
- `docs/MEMORY_MODEL.md`
- `docs/ASSISTANT_UX.md`
