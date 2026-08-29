# Workspace

Workspace is the project-context boundary for UNIX.

The goal is simple: when the user switches from project **Alpha** to project **Beta**, UNIX should stop carrying Alpha's active task, recent project conversation and project-specific memory into Beta.

## Workspace identity

A Workspace record has:

- a stable generated ID;
- a human-readable unique name;
- optional root-directory metadata;
- creation metadata;
- one globally selected active Workspace.

Names are treated case-insensitively for uniqueness. Workspace state is persisted locally.

## Root directory is metadata, not permission

A Workspace can point at a project directory, but that association does **not** grant UNIX filesystem access.

Creating or selecting a Workspace must not enumerate the directory, read files, index documents or silently add a filesystem capability. File access remains behind the existing permission model.

This distinction is important because a useful project context and a security capability are two different concepts.

## Context isolation

The planned/runtime boundary is:

```text
Active Workspace
    ├─ task state
    ├─ session memory
    ├─ project memory
    ├─ persisted chat history
    └─ future document/retrieval scope
```

Global identity, global preferences and appropriate global knowledge may still be shared where their memory type explicitly allows it.

## Safe switching

Workspace switching must not move a pending one-shot tool approval from one project context into another. If a protected local action is awaiting confirmation, the switch should fail closed until the approval is resolved or cancelled.

## Compatibility

When no Workspace is active, legacy project/task behavior remains available during migration. This makes Workspace adoption incremental rather than forcing a destructive rewrite of existing working state.

## Future document layer

The next major Workspace capability after context isolation is read-only document access. The intended sequence is:

1. user selects/creates Workspace;
2. root association exists as metadata;
3. explicit read capability is requested when a task requires it;
4. document readers operate inside a clearly defined scope;
5. retrieval/indexing is added only after the read boundary is reliable.

Workspace is therefore not merely a UI folder. It is the future unit of context, permissions, documents and collaborative work.
