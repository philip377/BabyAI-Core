# Milestone 2 Agent readiness

Status: candidate, not ready to merge automatically. PR #118 must remain draft until the
owner reviews the product surface and the remaining vision/streaming decisions.

## Ready in this candidate

- Fast local response: Vulkan route is confirmed on the target RTX 2060 SUPER; warm native
  responses are 3.56-4.03 seconds and 44.77-46.03 generated tokens/s in the recorded baseline.
- Conversation stability: ordinary chat hides the tool catalog/JSON, rejects incompatible
  tool calls, follows the user's language, and keeps the deterministic Desktop path.
- Read tools: `system.info`, Windows `process.list`, `filesystem.list`, and bounded
  `filesystem.read` are capability-gated and covered.
- Permissions: deny-by-default, specific prompts, allow-once/reject, executor-local temporary
  grants, consume-before-execute, and revoke-on-failure/cancellation behavior are covered.
- PC actions: allowlisted app launch and diagnostics, bounded text file write, visible-window
  list/activation, and workstation lock. No arbitrary shell, delete, shutdown, or input injection.
- Memory: bounded process-local conversation, explicit global preferences/facts/knowledge,
  scoped project memory, and list/update/delete controls.
- Screen boundary: permissioned active-window/primary-screen capture, local observation list
  and delete, honest `capture_only` status, and second approval for controlled actions.
- Assistant UX: explicit thinking/executing/approval/done/error states, concrete approval text,
  cancellable chat/action requests, project labels, and opt-in local history.
- Installer pipeline: CI defines portable CPU, AVX, AVX2, Vulkan, full ZIP, bundle verification,
  and single-EXE Setup outputs. A new artifact is valid only after the current head passes.

## Still required before calling Milestone 2 complete

- Select and validate an explicit local OCR or multimodal provider. The current text model does
  not understand pixels, and BabyAI must not silently change it.
- Decide whether completed-response transport is sufficient or authorize a versioned streaming
  bridge protocol. The current UI gives immediate state/elapsed feedback but renders the answer
  when complete.
- Product-test the new capabilities and opt-in history settings on the installed build, including
  redirected Desktop, allow/reject/cancel, capture deletion, and a project-memory correction.
- Review the expanded capability wording and decide whether any action should remain experimental.

## Regression gates

- Python suite on Windows and Python 3.11/3.12/3.13.
- Windows Desktop Release build and startup smoke.
- Native CPU shim and Native Vulkan shim.
- Windows Release Bundle, verification, and single-EXE creation.
- Real installed Vulkan route and the same baseline prompts; no timeout increase as a fix.
- No draft PR merge without the owner's explicit decision.
