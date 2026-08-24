# Controlled memory model

BabyAI separates three kinds of context instead of silently saving every message.

- Short-term conversation context lives only in the active Desktop/CLI process. It is
  capped at 48 records and disappears when that process exits or the session is cleared.
- Global durable memory contains only explicit `preference`, `fact`, or approved
  `knowledge` records.
- Project memory uses kind `project` plus an exact project scope. PRIMUS recalls it only
  when the active working task names that project.

Ordinary Desktop chat no longer adds episodic rows to SQLite. Existing episodic rows from
older versions are preserved during migration, but the new Desktop path does not recall
them when a process-local session store is active.

## User control

The local command surface supports `memory.save`, `memory.list`, `memory.update`,
`memory.delete`, and `memory.session.clear`. The CLI exposes the same durable operations:

```text
babyai memory add "Отвечай кратко" --kind preference
babyai memory add "Use Vulkan auto" --kind project --project BabyAI
babyai memory list --project BabyAI
babyai memory update 12 "Prefer Vulkan auto"
babyai memory delete 12
```

`remember` and `memories` remain compatibility aliases. They now follow the same explicit
durable-kind and project-scope rules. Empty memories and unscoped project memories are
rejected. Schema migration adds the scope column in place and does not delete old rows.
