# Memory System

UNIX uses multiple memory layers because "remember everything" is both technically inefficient and conceptually unsafe.

## Session memory

Session memory keeps recent conversational episodes available to the current runtime. It is bounded and designed for short-term continuity rather than permanent storage.

As Workspace isolation is introduced, each active Workspace receives its own session context so recent discussion from one project does not bleed into another.

## Durable memory

Durable memory lives in the local SQLite store and uses explicit kinds. The important durable classes are:

- **preference** — stable user preferences;
- **fact** — known user/system facts worth retaining;
- **knowledge** — learned reusable knowledge;
- **project** — information belonging to one project/Workspace scope.

Durable memory is intentionally explicit. Not every conversational message becomes a permanent memory record.

## Project memory

Project memory is scope-sensitive. With Workspace context active, project memory should resolve to the active Workspace rather than accepting an arbitrary project name that could cross the active boundary silently.

This is what lets UNIX remember "how this project is structured" without exposing the same note inside a different project.

## Chat history is not the same as model memory

Persisted chat history is a user-facing transcript feature. Model prompt memory is a selected context feature. Keeping those concepts separate allows history to be disabled or cleared without pretending that every transcript line is automatically useful model context.

## Prompt budget

The model prompt has a bounded size. PRIMUS assembles identity, task/workspace context and selected memory sections, then fits them into the available budget. Older memory may be omitted while the newest and most relevant records remain.

## Memory hygiene

Internal tool JSON, protocol artifacts and recovery/error boilerplate should not become remembered conversational knowledge. The Core filters known internal artifacts before they are reused in prompts.

## Design rule

Memory should answer three questions before being retained:

1. **What kind of memory is this?**
2. **Who/which Workspace owns it?**
3. **How long should it survive?**

For implementation details, see `docs/MEMORY_MODEL.md`.
