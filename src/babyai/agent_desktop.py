from __future__ import annotations

import time
from collections.abc import Callable

from .agent_primus import AgentWorkspacePrimus
from .agent_runtime import AgentRuntime, ModelDrivenAgentExecutor
from .desktop_commands import DesktopCommandError
from .identity import Identity, IdentityStore
from .llm import LLMError
from .memory import SQLiteMemoryStore
from .permissions import PermissionStore
from .planner import Planner
from .runtime_trace import process_memory_metrics, trace
from .tool_approval import PendingToolApprovalStore
from .workspace import WorkspaceRecord
from .workspace_desktop_retrieval import WorkspaceDesktopCommands as RetrievalDesktopCommands


class AgentDesktopCommands(RetrievalDesktopCommands):
    """Desktop surface backed by a persistent model-driven AgentRuntime."""

    def __init__(self, config=None, *, persistent: bool = False) -> None:
        super().__init__(config, persistent=persistent)
        self._agent_runtimes: dict[str, AgentRuntime] = {}

    def _agent_runtime(self, workspace: WorkspaceRecord | None = None) -> AgentRuntime:
        workspace = self._active_workspace() if workspace is None else workspace
        key = "__legacy__" if workspace is None else workspace.id
        runtime = self._agent_runtimes.get(key)
        if runtime is None:
            permissions = PermissionStore(self.config.permissions_file)
            runtime = AgentRuntime(
                executor=ModelDrivenAgentExecutor(permissions),
                approvals=PendingToolApprovalStore(self.config.pending_tool_approval_file),
            )
            self._agent_runtimes[key] = runtime
        return runtime

    def _core(self) -> AgentWorkspacePrimus:
        workspace = self._active_workspace()
        identity = IdentityStore(self.config.identity_file).load_or_create(
            Identity(name=self.config.name, owner=self.config.owner)
        )
        planner = None if self.config.provider == "native" else Planner()
        runtime = self._agent_runtime(workspace)
        return AgentWorkspacePrimus(
            llm=self._provider(),
            memory=SQLiteMemoryStore(self.config.memory_db),
            identity=identity,
            agent=runtime.executor,
            agent_runtime=runtime,
            planner=planner,
            working_memory=self._task_store(workspace),
            tool_approvals=runtime.approvals,
            # Native may repair malformed model-selected JSON, but the model-driven
            # executor disables the old deterministic pre-LLM tool shortcut.
            repair_tool_calls=self.config.provider == "native",
            max_context_chars=6_000 if self.config.provider == "native" else 12_000,
            session_memory=self._session_store(workspace),
            workspace=workspace,
        )

    def stream_chat(
        self,
        payload: dict[str, object],
        emit: Callable[[dict[str, object]], None],
    ) -> dict[str, object]:
        message = str(payload.get("message", "")).strip()
        if not message:
            raise DesktopCommandError("chat.message is required")

        started = time.monotonic()
        first_delta_ms: int | None = None
        delta_count = 0
        answering = False
        emit({"event": "state", "state": "thinking"})

        def emit_delta(text: str) -> None:
            nonlocal answering, delta_count, first_delta_ms
            if not text:
                return
            if not answering:
                emit({"event": "state", "state": "answering"})
                answering = True
            if first_delta_ms is None:
                first_delta_ms = round((time.monotonic() - started) * 1000)
            emit({"event": "delta", "text": text})
            delta_count += 1

        def emit_core_state(state: str) -> None:
            if state == "executing":
                emit({"event": "state", "state": "executing"})
                return
            if state.startswith("activity:"):
                text = state.removeprefix("activity:").strip()
                if text:
                    emit({"event": "activity", "text": text})
                return
            raise DesktopCommandError("Unsupported streaming core state")

        trace(
            "chat.core.start",
            provider=self.config.provider,
            message_chars=len(message),
            streaming=True,
            workspace=self._active_project() or None,
            agent_runtime=True,
            **process_memory_metrics(),
        )
        try:
            result = self._core().think_stream(message, emit_delta, emit_core_state)
        except LLMError as exc:
            trace(
                "chat.core.error",
                elapsed_ms=round((time.monotonic() - started) * 1000),
                error=type(exc).__name__,
            )
            raise DesktopCommandError(f"Local brain unavailable: {exc}") from exc

        if not answering:
            emit({"event": "state", "state": "answering"})

        total_ms = round((time.monotonic() - started) * 1000)
        trace(
            "chat.core.done",
            elapsed_ms=total_ms,
            reply_chars=len(result.reply),
            streaming=True,
            workspace=self._active_project() or None,
            agent_runtime=True,
            **process_memory_metrics(),
        )
        project = self._active_project()
        self._history().add("user", message, project=project)
        self._history().add("babyai", result.reply, project=project)

        metrics = result.metrics
        return {
            "reply": result.reply,
            "metrics": {
                "visible_ttft_ms": first_delta_ms,
                "native_first_token_ms": metrics.native_first_token_ms,
                "generation_ms": metrics.generation_ms,
                "total_ms": total_ms,
                "generated_tokens": metrics.generated_tokens,
                "delta_count": delta_count,
                "model_calls": metrics.model_calls,
                "stop_reason": metrics.stop_reason or "completed",
            },
        }

    def execute(
        self,
        command: str,
        payload: dict[str, object] | None = None,
    ) -> dict[str, object]:
        result = super().execute(command, payload)
        if command == "approval.approve":
            activity = self._agent_runtime().last_activity
            if activity:
                result["activity"] = activity
        return result


# Keep the familiar DesktopCommands import name for the worker/CLI entrypoints.
DesktopCommands = AgentDesktopCommands
