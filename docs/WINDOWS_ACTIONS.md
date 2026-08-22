# Controlled Windows actions

Milestone 2 exposes a deliberately small action surface. Every action is deny-by-default,
has its own capability, and uses the existing one-shot approval flow. A one-shot grant is
kept only in the active executor and is revoked even when the action fails.

## Capabilities

- `application.open`: opens only `calculator`, `explorer`, `notepad`, or `settings`.
- `filesystem.write`: writes at most 256 KiB of UTF-8 text. Existing files are refused
  unless the tool call explicitly sets `overwrite: true`. It does not create parent
  directories or delete files.
- `command.run`: runs only `hostname`, `ipconfig`, or `whoami`, as fixed executable/argument
  vectors with no shell, a ten-second timeout, and a 64 KiB output limit.
- `window.list`: returns at most 100 visible titled top-level windows.
- `window.activate`: restores and activates one positive numeric window handle.
- `system.lock`: invokes only the Windows workstation lock action.

The tool catalog does not expose arbitrary command arguments. PowerShell, `cmd.exe`, script
execution, shutdown, restart, delete, registry changes, service control, network writes, and
input automation are intentionally outside this stage.

## Approval contract

The pending approval stores the exact tool, arguments, and capability. The chat prompt names
the application, diagnostic command, path/overwrite intent, window handle, or lock action.
Rejecting clears the pending request without granting or executing anything. Approving checks
that the stored capability still matches the tool immediately before one execution.
