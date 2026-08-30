# UNIX

> **Product name:** UNIX  
> **Repository / technical codename:** BabyAI Core

UNIX is a local-first personal AI assistant being developed in this repository under the technical name **BabyAI Core**.

The project is no longer just an experimental chat window. It now has a native Windows desktop client, a resident local-model runtime, progressive multi-turn chat, explicit memory and permissions, safe local actions, Workspaces, document registration and retrieval, a Windows installer pipeline, and a growing documentation layer.

The product name is **UNIX**. Existing package names, paths, environment variables, executable names, UI labels, and internal identifiers still use `BabyAI` in many places during development so compatibility work is not mixed into feature development.

## Current status — August 2026

The main Milestone 2 foundation is complete, and the first Workspace / Documents / Retrieval foundation is integrated into `main`.

The latest owner-tested native-chat milestone is PR **#128**, which fixed repetition loops and rebuilt the resident native conversation path around genuine ChatML turns. The installed Windows smoke confirmed Russian multi-turn conversation, progressive output, continuity between turns, and no leakage of internal `babyai-visible` transport markers.

The current bounded architecture work adds the first **model-driven Agent Runtime**. Its job is to keep the language model as the component that decides when outside information is needed and writes the user-facing answer, while a separate agent layer performs approved local actions and returns trustworthy observations back to the model.

Already working in `main`:

- **PRIMUS** orchestration and bounded agent loop
- **MEMORIA** typed memory with SQLite-backed durable state and bounded process-local session context
- persistent identity, preferences, facts, learned knowledge, project memory, and opt-in local chat history
- native Windows Orb desktop application with Acrylic-style chat UI, tray lifecycle, drag persistence, and assistant states
- resident native model process so the model does not need to reload for every chat turn
- configurable local GGUF execution through the native llama.cpp-based runtime
- Windows **Vulkan GPU acceleration** plus portable / AVX / AVX2 CPU runtime tiers
- safe Desktop protocol v2 with ordered state events and progressive response deltas
- real ChatML `system -> user -> assistant` conversation formatting for the resident native path
- multi-turn session continuity without treating old assistant replies as system instructions
- repetition-aware persistent sampler chain for native generation
- cancellation / Stop without accepting stale or ghost deltas
- fail-closed streaming visibility checks so reasoning, tool JSON, protocol data, synthetic role continuations, and transport markers do not become normal chat output
- capability-gated Windows actions with deny-by-default permissions
- one-shot approval flow for protected local actions
- filesystem list/read/write boundaries
- process, application, window, fixed diagnostic command, workstation lock, and screen-capture capabilities
- redirected Windows Desktop / OneDrive Known Folder resolution
- executor-confirmed action results: UNIX does not treat a model claim as proof that an action happened
- permissioned screen capture with a capture-only boundary until a real vision provider is connected
- persistent **Workspace registry** with stable IDs and one active Workspace
- Workspace-isolated session context, tasks, project memory, and persisted history
- per-Workspace **document registry** with explicit registration instead of directory scanning
- bounded text document reading behind the existing filesystem permission boundary
- per-Workspace **retrieval index** with deterministic local lexical ranking
- Desktop commands for document ingestion, search, retrieval status, and removal
- retrieved document text treated as untrusted reference data rather than instructions
- structured project Wiki source under `docs/wiki/`
- Windows installer / portable release pipeline with bundled native CPU and Vulkan runtimes
- side-by-side physical install slots so repeated development builds of the same logical version can be installed safely

## Agent architecture

UNIX separates **reasoning**, **coordination**, and **execution** instead of letting the language model pretend that it can directly see or control the computer.

The intended local-action path is:

```text
User
  ↓
LLM / PRIMUS
  ↓ structured agent request
Agent Runtime
  ↓ permission check
AgentExecutor
  ↓
Windows / filesystem / local capability
  ↓ real result
Agent Runtime observation
  ↓
LLM / PRIMUS
  ↓
User-facing answer
```

The roles are deliberately different:

- **LLM** — understands the request, decides whether an outside observation/action is needed, and writes the final conversational answer.
- **PRIMUS** — supplies bounded context and coordinates the conversation turn.
- **Agent Runtime** — accepts model-selected structured actions, handles the permission handoff, records the latest real observation for conversational follow-ups, and returns evidence to the model.
- **AgentExecutor** — low-level deny-by-default executor. It validates capabilities and is the only layer that actually touches protected local resources.
- **Tools / capabilities** — filesystem, process, window, application, diagnostic, screen-capture, and later external integrations.
- **Observations** — real tool results supplied back to the model as evidence, never proof generated from model text itself.

For example, asking `Какие файлы у меня на рабочем столе?` should not be answered from model memory or a Python phrase-specific shortcut. The model requests a filesystem observation, the agent performs the approved scan, and the resulting filenames are returned to the model before it answers.

A follow-up such as `а ещё какие?` should use the previously recorded real observation while it remains relevant. If that observation cannot answer the new question, the model should request the agent again rather than inventing local state.

The superseded PR **#129** explored a host-side shortcut where Python answered filesystem follow-ups directly without another model pass. It was closed without merge because that prevented hallucinated filenames but violated the intended architecture: UNIX should use the agent as the bridge to reality while the model remains the conversational decision-maker and answer author.

## Native model status

UNIX does not hard-code one permanent model architecture into the product design. The native runtime and provider boundary are intentionally replaceable.

Two useful real-world baselines have already been exercised during development:

- **Qwen3-8B Q4_K_M + Vulkan** on an RTX 2060 SUPER, used during Milestone 2 acceptance and streaming work
- **Qwen2.5 1.5B + CPU** on the installed owner test path used to diagnose and verify the post-#127 / #128 conversational fixes

Existing user launch settings are preserved rather than silently overwritten by new builds. A future model/runtime migration should therefore be treated as its own compatibility-conscious change, not hidden inside unrelated feature work.

## Latest native-chat acceptance

The Windows owner smoke after PR #128 verified the important user-visible parts of the resident native path:

- Russian input receives Russian output
- a second and third message are treated as new conversational turns rather than restarting the assistant introduction
- previous assistant replies remain conversational history instead of becoming system instructions
- progressive text appears before generation is complete
- internal `babyai-visible` marker text does not appear in the UI or canonical reply
- local permission requests still remain separate from ordinary answer streaming

The same smoke exposed the next architectural boundary: the model can converse correctly but must not invent facts about local files, processes, windows, or completed actions. Those facts now belong to the Agent Runtime observation path rather than further native-model prompt patches or phrase-specific host answers.

## Workspace, Documents, and Retrieval

Workspace is no longer only a roadmap item.

The current foundation supports:

- creating and selecting persistent Workspaces
- stable Workspace IDs and optional project-root metadata
- no implicit scanning or reading merely because a root path is registered
- separate task files and session context per active Workspace
- isolated project memory and chat history
- explicit document registration with stable document IDs
- document metadata removal without deleting the original file
- explicit text ingestion behind `filesystem.read`
- bounded local chunking and lexical retrieval
- automatic retrieval of relevant chunks into Workspace chat context
- retrieval filtering so documents from another Workspace cannot leak into the active one
- Desktop-level ingest/search/status wiring

The current retrieval layer is deliberately deterministic and local. Semantic embeddings, reranking, richer binary/PDF/DOCX parsing, editing, and preview/diff workflows are later layers.

## Voice status

Voice is being developed independently from the Workspace and agent-runtime work.

The current voice/VAD branch establishes the first bounded microphone foundation: microphone capture, 16 kHz mono audio handling, VAD, and a visible listening state. It is **not yet part of `main`** and still requires integration / owner testing against the current codebase.

Not implemented as completed product features yet:

- streaming STT
- streaming TTS
- interruption / barge-in
- full conversational voice loop

These remain separate stages so microphone capture, speech recognition, speech synthesis, and interruption can each be tested independently.

## Development naming

For now, two names intentionally coexist:

- **UNIX** — the product name and long-term assistant identity.
- **BabyAI Core / BabyAI** — repository name and current technical identifiers used throughout the codebase and installed development builds.

A full code/package/installer rename is **not** being done opportunistically. It should be handled as a dedicated migration with compatibility coverage for paths, state, installer upgrades, shortcuts, environment variables, and user data.

## Architecture direction

The project is being built as bounded layers rather than one large autonomous system.

Current progression:

1. **Core assistant / PRIMUS foundation** — completed
2. **Safe permissions and Windows action boundary** — completed
3. **Resident native runtime and Windows packaging** — completed foundation
4. **Versioned progressive response streaming** — completed
5. **Native multi-turn ChatML conversation path** — completed and owner-smoke tested
6. **Workspace registry and context isolation** — completed foundation
7. **Documents and local retrieval** — completed foundation
8. **First-class Agent Runtime + observation loop** — current bounded integration
9. **Voice capture / VAD foundation** — separate integration track
10. **Streaming STT** — planned
11. **Streaming TTS** — planned
12. **Barge-in** — planned
13. **Durable jobs** — planned; tasks that can survive beyond one chat turn and pause for approval
14. **Vision understanding** — planned beyond the current capture-only boundary
15. **External agent capabilities** — browser workflows, GitHub, business software, databases, and other connectors behind explicit action boundaries

The engineering rule remains simple:

> **Add one bounded capability, make it observable and testable, then build the next layer on top of it.**

## Long-term communication vision

UNIX is intended to grow beyond a one-person assistant window into an **AI-centered communication and work client**.

The long-term idea is to make the AI a native participant in the same space where people communicate and work, rather than keeping it in a separate chatbot tab. A future UNIX client can combine:

- persistent project spaces with their own context, memory, files, goals, and history
- human-to-human and human-to-AI chats inside the same project
- shared conversations where UNIX participates as another member
- integrated documents, files, notes, tasks, and decisions attached to the conversation that produced them
- context that persists across chats so the AI understands the project, not only the latest message
- collaboration between several people and UNIX while retaining explicit permissions for local or external actions
- summaries, unresolved-decision tracking, retrieval of project material, and later approved execution from the same workspace

This remains a long-term product direction. The lower-level Workspace, Documents, Retrieval, permissions, memory, runtime, and agent layers are being built first so the future communication client sits on reliable foundations.

## First Windows development run

Requirements: Windows 10/11 x64, Python 3.11+ and the .NET 10 SDK.

Ollama remains available as a development provider, but the packaged Windows release path supports the native runtime and does not require Ollama for native inference.

From PowerShell in the repository root, the simplest UI smoke test is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\start.ps1 -Provider echo
```

`start.ps1` checks whether Core and the runnable Orb already exist. On the first run, or after an incomplete setup, it runs bootstrap; on later runs it launches the existing executable directly.

Echo mode verifies the Windows application, bridge, state initialization, and UI without needing an LLM.

For the Ollama development provider:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\start.ps1 -Provider ollama
```

For troubleshooting or manual control:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\bootstrap.ps1 -Provider echo
powershell -ExecutionPolicy Bypass -File .\scripts\windows\run.ps1 -Provider echo
```

If Windows launch is failing, export a compact support report:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\diagnose.ps1 -Provider echo
```

This creates `babyai-diagnostics.txt` in the repository root. The report deliberately excludes MEMORIA contents, chats, task/identity contents, permission contents, and user-file contents.

Local Core state currently lives under `~/.babyai`, while Desktop-specific state is stored under `%LocalAppData%\BabyAI`. These paths retain the technical codename for compatibility.

## Core quick start

Requirements: Python 3.11+ and Ollama for the default CLI development provider.

```bash
python -m pip install -e ".[dev]"
ollama pull qwen3:8b
babyai-setup init
babyai-setup doctor
babyai chat
```

For diagnostics without Ollama:

```bash
BABYAI_PROVIDER=echo babyai-setup doctor
```

## Controlled learning workflow

Set a task and inspect the learning loop:

```bash
babyai task set "Understand a problem"
babyai hypothesis propose "What explanation should we test?"
babyai-evidence add "An explicit observation"
babyai-evidence assess
babyai-loop status
```

When evidence is insufficient, CURIOSA can propose one missing question. When a durable lesson is available, AUTODIDACT can propose it, but MEMORIA changes only after explicit approval:

```bash
babyai-curiosa propose "Current task, hypothesis and evidence context"
babyai-learn propose "Verified learning context"
babyai-learn show
babyai-learn approve
```

## Permissions

UNIX starts with no protected system capabilities granted.

```bash
babyai permissions list
babyai permissions grant system.info
babyai observe
babyai permissions grant filesystem.list
babyai ls .
babyai permissions grant filesystem.read
babyai cat README.md
```

Capabilities can be revoked at any time:

```bash
babyai permissions revoke filesystem.read
```

The CLI still uses the `babyai` technical command namespace during development.

## Configuration

Current environment variables retain the BabyAI technical namespace:

- `BABYAI_PROVIDER=native|ollama|echo`
- `BABYAI_MODEL=qwen3:8b`
- `BABYAI_OLLAMA_URL=http://127.0.0.1:11434`
- `BABYAI_DATA_DIR=...`
- `BABYAI_NAME=BabyAI`
- `BABYAI_OWNER=owner`

Native Windows builds additionally use native model/runtime configuration managed by installer and launch settings. Existing explicit model choices are preserved rather than silently replaced during unrelated updates.

## Protocols

Implemented foundations:

**PRIMUS · MEMORIA · AUTODIDACT · CURIOSA · HYPOTHESIS · COGNITION · OBSERVER**

Planned / evolving protocol work:

**METAMORPHOSIS · LINGUA · TEMPUS · PHANTOM · EVOLUTIO · CURA**

## Documentation

The repository contains both canonical technical documents and a readable Wiki layer.

Useful entry points:

- `docs/MILESTONE_2_READINESS.md`
- `docs/MILESTONE_3_COMPANION_WORKSPACE_PLAN.md`
- `docs/STREAMING_PROTOCOL_V2.md`
- `docs/NATIVE_BRAIN.md`
- `docs/SECURITY_MODEL.md`
- `docs/wiki/Home.md`
- `docs/wiki/Architecture.md`
- `docs/wiki/Roadmap.md`
- `docs/wiki/Workspace.md`
- `docs/wiki/Voice.md`
- `docs/wiki/Build-and-Install.md`
- `docs/wiki/Troubleshooting.md`

When readable Wiki text and implementation details disagree, current code and canonical technical contracts are authoritative.

## Principles

- **Local-first** — local capability and local state are the default direction
- **Model decides, agent observes/acts** — the model requests outside work; the agent performs it and returns evidence
- **Explicit permissions** — protected actions are deny-by-default
- **Truthful execution** — model output is not treated as proof of an external action
- **Ground local facts in observations** — local files, processes, windows, and other machine state come from executed agent tools, not model guesses
- **No host-authored fake conversation** — deterministic safety code may block or request approval, but normal local-data answers belong to the LLM after it receives real observations
- **Human-approved durable learning** — persistent learning is intentional, not silent
- **Fail closed** — ambiguous internal model output is withheld rather than exposed as trusted UI
- **Portable state** — important state should remain inspectable and movable
- **Modular providers** — model/runtime choices stay replaceable
- **Small, testable increments** — capability growth is intentionally bounded

---

**UNIX is the product. BabyAI Core is the working technical name while we build it.**
