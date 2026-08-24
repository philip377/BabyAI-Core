# BabyAI Core Engine

BabyAI Core is a local-first personal AI engine that keeps its identity and state on your machine, learns only through explicit approval, and uses system capabilities through deny-by-default permissions.

## v0.1.0 Genesis

Genesis is the first MVP milestone. It includes:

- PRIMUS orchestration and one-step agent loop
- MEMORIA typed persistent memory on SQLite
- persistent identity and bounded prompt context
- local Ollama brain (default: `qwen3:8b`)
- Planner v1 and Working Memory task state
- COGNITION proposal-only task updates
- HYPOTHESIS + Evidence reasoning flow
- CURIOSA explicit uncertainty questions
- AUTODIDACT lesson proposals with explicit approval before durable learning
- Learning Loop status and next-step guidance
- OBSERVER plus permissioned read-only filesystem tools
- capability permissions with deny-by-default behavior
- setup diagnostics and end-to-end cognitive smoke coverage
- native Windows Orb desktop shell with chat, approval controls, tray lifecycle, drag persistence and animated states
- capability-gated Windows actions without unrestricted shell access
- bounded session context plus explicit preference, fact and project memory controls
- permissioned screen capture boundary with controlled follow-up approvals
- opt-in local chat history and explicit assistant activity states

Milestone 2 readiness and the next architecture boundary are tracked in
`docs/MILESTONE_2_READINESS.md` and `docs/MILESTONE_3_COMPANION_WORKSPACE_PLAN.md`.
The backward-compatible Desktop streaming contract is documented in
`docs/STREAMING_PROTOCOL_V2.md`.

## First Windows MVP run

Requirements: Windows 10/11 x64, Python 3.11+ and the .NET 10 SDK. Ollama is optional for the first UI smoke test.

From PowerShell in the repository root, the simplest first run is now one command:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\start.ps1 -Provider echo
```

`start.ps1` checks whether Core and the runnable Orb already exist. On the first run (or after a broken/incomplete setup) it automatically runs bootstrap; on later runs it launches the existing executable directly. The first build can take noticeably longer because .NET restores and builds the self-contained WinUI application.

This should open the BabyAI Orb. Click the Orb to expand the Acrylic chat panel. Echo mode is only for verifying that the full Windows application, bridge, state initialization and UI work on your machine.

For the real local AI brain, install Ollama and then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\start.ps1 -Provider ollama
```

If `qwen3:8b` is missing, bootstrap pulls it automatically. The Orb reports whether Core is connected and exposes `Retry Core` if Python/Core/Ollama temporarily becomes unavailable.

For troubleshooting or manual control, the two underlying steps remain available:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\bootstrap.ps1 -Provider echo
powershell -ExecutionPolicy Bypass -File .\scripts\windows\run.ps1 -Provider echo
```

The bootstrap script installs BabyAI Core in editable mode, initializes local state, runs diagnostics, verifies the desktop command bridge and builds the self-contained WinUI client. The run script reuses the built `BabyAI.Desktop.exe` rather than rebuilding on every launch.

If a Windows launch is failing, export a compact support report:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\windows\diagnose.ps1 -Provider echo
```

This creates `babyai-diagnostics.txt` in the repository root. The report contains platform/runtime versions, Core health exit codes, bridge schema health, Ollama reachability when requested, and whether the desktop EXE exists/runs. It deliberately excludes MEMORIA contents, chats, task/identity contents, permission contents, and user-file contents.

BabyAI state lives in `~/.babyai`. Desktop window position is stored separately under `%LocalAppData%\BabyAI`. Initialization grants no system capabilities.

## Core quick start

Requirements: Python 3.11+ and Ollama for the default local provider.

```bash
python -m pip install -e ".[dev]"
ollama pull qwen3:8b
babyai-setup init
babyai-setup doctor
babyai chat
```

For diagnostics without Ollama, use the echo provider:

```bash
BABYAI_PROVIDER=echo babyai-setup doctor
```

## Core workflow

Set a task and inspect the controlled learning loop:

```bash
babyai task set "Understand a problem"
babyai hypothesis propose "What explanation should we test?"
babyai-evidence add "An explicit observation"
babyai-evidence assess
babyai-loop status
```

When evidence is insufficient, CURIOSA can propose one missing question. When a durable lesson is available, AUTODIDACT can propose it, but MEMORIA is changed only after explicit approval:

```bash
babyai-curiosa propose "Current task, hypothesis and evidence context"
babyai-learn propose "Verified learning context"
babyai-learn show
babyai-learn approve
```

## Permissions

BabyAI starts with no system capabilities granted.

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

## Configuration

Environment variables:

- `BABYAI_PROVIDER=ollama|echo`
- `BABYAI_MODEL=qwen3:8b`
- `BABYAI_OLLAMA_URL=http://127.0.0.1:11434`
- `BABYAI_DATA_DIR=...`
- `BABYAI_NAME=BabyAI`
- `BABYAI_OWNER=owner`

## Protocol roadmap

Implemented foundations: PRIMUS · MEMORIA · AUTODIDACT · CURIOSA · HYPOTHESIS · COGNITION · OBSERVER.

Future protocol work: METAMORPHOSIS · LINGUA · TEMPUS · PHANTOM · EVOLUTIO · CURA.

## Principles

- Local-first
- Portable state
- Explicit permissions
- Human-approved durable learning
- Modular providers
- Small, testable capability increments
