# BabyAI Security Model

BabyAI uses capability-based access. New installations start with zero system capabilities granted.

## Rules

1. Deny by default.
2. The owner grants one named capability at a time.
3. Tools check permission at execution time; prompt text cannot bypass permission checks.
4. Current tools are read-only.
5. File reads are size-limited.
6. Capabilities are stored outside the model in `permissions.json` and can be revoked at any time.
7. Future write, process execution, network, device-control, and remote capabilities must be separate explicit permissions and require additional risk controls.

## Current capabilities

- `system.info`
- `filesystem.list`
- `filesystem.read`
- `process.list`

No command execution, file writes, deletion, arbitrary network access, or remote device control is implemented in this stage.
