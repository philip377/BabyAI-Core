# Permissions & Safety

UNIX is designed around explicit capability boundaries. A language model suggesting an action is not the same thing as the system being allowed to perform it.

## Core rule

**No hidden local action.**

When a request requires protected access, UNIX should either already hold an appropriate explicitly granted capability or ask for a one-shot approval tied to the concrete action.

## One-shot approval flow

```text
User request
   ↓
Model / intent layer decides a local action may be needed
   ↓
Tool call is validated
   ↓
Required capability is resolved
   ↓
Pending approval is stored
   ↓
Desktop shows the exact action to the user
   ↓
Approve once / reject
   ↓
Pending approval is consumed before execution
```

Consuming approval before execution is intentional. If the worker is cancelled or crashes during execution, the same approval must not be silently replayed after restart.

## Tool compatibility

The Core does not accept arbitrary model-produced tool JSON at face value. Tool names and arguments are validated against the supported command surface, and tool choice must be compatible with the user's intent.

If the model hallucinates an internal tool call during ordinary conversation, UNIX should recover into natural-language conversation rather than leaking protocol data into the UI or memory.

## Workspace safety

Selecting a Workspace does not grant filesystem permissions. A root path is project metadata only.

A pending protected action should also block Workspace switching. This prevents an approval created while working in Workspace A from being confirmed later under Workspace B without an explicit context transition.

## Vision safety

Screen capture is treated as sensitive local access because screenshots may contain private information. Capture and any later action suggested from an observation are separate boundaries.

## What safety does not mean

Safety is not a claim that the model is infallible. It means the surrounding runtime is designed so that model mistakes do not automatically become hidden operating-system actions.

Canonical references:

- `docs/SECURITY_MODEL.md`
- `docs/WINDOWS_ACTIONS.md`
- `docs/SCREEN_VISION.md`
