# UNIX

> **Product name:** UNIX  
> **Repository / technical codename:** BabyAI Core

UNIX is a local-first personal AI assistant being developed in this repository under the technical name **BabyAI Core**. The product name is now **UNIX**, while existing package names, paths, environment variables, executable names, and internal identifiers continue to use `BabyAI` during development for compatibility and to avoid unnecessary large-scale renames.

The goal is not just a chat window. UNIX is being built as a persistent local companion that can reason with a local model, remember approved information, interact with the computer through explicit capabilities, and gradually take on longer real-world tasks without silently crossing permission boundaries.

## Current status

The project has moved beyond the initial Genesis MVP and completed the main Milestone 2 agent foundation.

Already working:

- **PRIMUS** orchestration and bounded agent loop
- **MEMORIA** typed persistent memory on SQLite
- persistent identity, bounded session context, preferences, facts, and project memory
- native Windows Orb desktop application with Acrylic chat UI, tray lifecycle, drag persistence, and animated states
- local **Qwen3-8B Q4_K_M** execution through the native runtime
- Windows **Vulkan GPU acceleration** with CPU fallback tiers
- safe Desktop protocol v2 with progressive response streaming
- cancellation / Stop without accepting stale or ghost deltas
- fail-closed streaming visibility checks so model reasoning, tool JSON, protocol data, and synthetic role continuations do not become normal chat output
- capability-gated Windows actions with deny-by-default permissions
- one-shot approval flow for protected local actions
- filesystem list/read/write boundaries
- process, application, window, diagnostic command, workstation lock, and screen-capture capabilities
- redirected Windows Desktop / OneDrive Known Folder resolution
- executor-confirmed action results: UNIX does not treat a model claim as proof that an action happened
- permissioned screen capture with a capture-only boundary until a real vision provider is connected
- opt-in local chat history
- Windows installer / portable release pipeline with native CPU and Vulkan runtimes

The currently active next slice is the **voice foundation**: bounded microphone capture plus VAD (voice activity detection). STT, TTS, and interruption/barge-in are intentionally separate later steps.

## Development naming

For now, two names intentionally coexist:

- **UNIX** — the real product and assistant name going forward.
- **BabyAI Core / BabyAI** — the repository name and current internal technical identifiers.

A full code/package/installer rename is **not** being done yet. It will be handled as a dedicated compatibility-conscious migration rather than mixed into feature work.

## Architecture direction

The current development path is deliberately incremental:

1. **Agent foundation** — completed
2. **Safe response streaming** — completed
3. **Voice foundation / VAD** — in progress
4. **Streaming STT** — speech to text while the user is still talking
5. **Streaming TTS** — begin speaking before the whole answer is complete
6. **Barge-in** — user speech interrupts UNIX immediately
7. **Workspace / Projects** — persistent project-aware working context
8. **Documents + retrieval** — read, search, compare, and later edit through preview/diff approval
9. **Durable jobs** — tasks that survive beyond one chat turn and can pause for approval
10. **Vision** — understand captured screen content instead of capture-only behavior
11. **External tools and systems** — GitHub, browser workflows, business software, databases, and other connectors behind explicit action boundaries

The design rule is simple: **add one bounded capability, make it observable and testable, then build the next layer on top of it.**

## First Windows development run

Requirements: Windows 10/11 x64, Python 3.11+ and the .NET 10 SDK. Ollama remains available as a development provider, but the Windows release path also supports the packaged native runtime.

From PowerShell in the repository root, the simplest UI smoke test is:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\start.ps1 -Provider echo
```

`start.ps1` checks whether Core and the runnable Orb already exist. On the first run (or after a broken/incomplete setup) it automatically runs bootstrap; on later runs it launches the existing executable directly.

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

Local state currently lives under `~/.babyai`, while Desktop-specific state is stored under `%LocalAppData%\BabyAI`. These paths retain the technical codename for compatibility.

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

UNIX starts with no system capabilities granted.

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

Native Windows builds additionally use native model/runtime configuration managed by the installer and launch settings.

## Protocols

Implemented foundations:

**PRIMUS · MEMORIA · AUTODIDACT · CURIOSA · HYPOTHESIS · COGNITION · OBSERVER**

Planned / evolving protocol work:

**METAMORPHOSIS · LINGUA · TEMPUS · PHANTOM · EVOLUTIO · CURA**

Architecture notes currently live in:

- `docs/MILESTONE_2_READINESS.md`
- `docs/MILESTONE_3_COMPANION_WORKSPACE_PLAN.md`
- `docs/STREAMING_PROTOCOL_V2.md`

## Principles

- **Local-first** — local capability and local state are the default direction
- **Explicit permissions** — protected actions are deny-by-default
- **Truthful execution** — model output is not treated as proof of an external action
- **Human-approved durable learning** — persistent learning is intentional, not silent
- **Fail closed** — ambiguous internal model output is withheld rather than exposed as trusted UI
- **Portable state** — important state should remain inspectable and movable
- **Modular providers** — model/runtime choices stay replaceable
- **Small, testable increments** — capability growth is intentionally bounded

---

**UNIX is the product. BabyAI Core is the working technical name while we build it.**
