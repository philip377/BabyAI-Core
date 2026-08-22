# BabyAI Security Model

BabyAI uses capability-based access. New installations start with zero system capabilities granted.

## Rules

1. Deny by default.
2. The owner grants one named capability at a time.
3. Tools check permission at execution time; prompt text cannot bypass permission checks.
4. Baseline tools are read-only; later action capabilities are separate and explicit.
5. File reads are size-limited.
6. Capabilities are stored outside the model in `permissions.json` and can be revoked at any time.
7. Future write, process execution, network, device-control, and remote capabilities must be separate explicit permissions and require additional risk controls.

## Current capabilities

- `system.info`
- `filesystem.list`
- `filesystem.read`
- `filesystem.write`
- `process.list`
- `application.open`
- `command.run`
- `window.list`
- `window.activate`
- `system.lock`

One-shot grants are kept in the active executor only and are never persisted to
`permissions.json`. Windows process listing invokes `tasklist.exe` with fixed arguments,
a five-second timeout, and a 200-entry response limit.

No unrestricted command execution, arbitrary network access, or remote device control
is granted by these baseline capabilities.
