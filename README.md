# BabyAI Core Engine

Portable personal AI core that learns with its owner, remembers context, develops skills, and safely connects to devices and external systems.

## Current capabilities

- PRIMUS orchestration loop
- MEMORIA typed persistent memory on SQLite
- persistent identity
- local Ollama brain (default model: `qwen3:8b`)
- OBSERVER system snapshot
- capability-based permission store (deny by default)
- permissioned read-only filesystem tools
- diagnostics and CLI smoke tests

## Quick start

```bash
python -m pip install -e ".[dev]"
ollama pull qwen3:8b
babyai doctor
babyai chat
```

BabyAI state lives in `~/.babyai` by default.

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

PRIMUS · MEMORIA · AUTODIDACT · CURIOSA · HYPOTHESIS · METAMORPHOSIS · LINGUA · TEMPUS · COGNITION · OBSERVER · PHANTOM · EVOLUTIO · CURA

## Principles

- Local-first
- Portable state
- Explicit permissions
- Modular providers
- Safe self-improvement through evaluation before promotion
