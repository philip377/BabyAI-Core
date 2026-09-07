from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import asdict

from .agent_primus import AgentWorkspacePrimus
from .agent_runtime import AgentRuntime, ModelDrivenAgentExecutor
from .desktop_commands import DesktopCommandError
from .identity import Identity, IdentityStore
from .jobs import DurableJob, DurableJobStore
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
        self._jobs = DurableJobStore(self.config.jobs_db)
        self._jobs.recover_interrupted()
        self._reconcile_job_approval()

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

    def _job_scope_id(self) -> str | None:
        workspace = self._active_workspace()
        return None if workspace is None else workspace.id

    @staticmethod
    def _job_payload(job: DurableJob) -> dict[str, object]:
        return asdict(job)

    def _job_in_active_scope(
        self,
        job_id: str,
        *,
        require_origin_chat: bool = False,
    ) -> DurableJob:
        try:
            job = self._jobs.get(job_id)
        except (KeyError, ValueError) as exc:
            raise DesktopCommandError(str(exc)) from exc
        if job.workspace_id != self._job_scope_id():
            raise DesktopCommandError("Job belongs to a different workspace")
        if require_origin_chat and job.chat_id is not None:
            if job.chat_id != self._current_chat_id():
                raise DesktopCommandError(
                    "Select the chat that created this job before running or approving it"
                )
        return job

    def _reconcile_job_approval(self) -> None:
        owner = self._jobs.pending_approval_owner()
        if owner is None:
            return
        try:
            job = self._jobs.get(owner)
        except (KeyError, ValueError):
            self._jobs.clear_pending_approval_owner()
            return
        pending = PendingToolApprovalStore(self.config.pending_tool_approval_file).load()
        if job.status == "waiting_permission" and pending is not None:
            return
        if job.status == "waiting_permission":
            try:
                self._jobs.pause(
                    job.id,
                    checkpoint=job.checkpoint,
                    note="Pending approval state was missing after restart",
                )
            except ValueError:
                pass
        self._jobs.clear_pending_approval_owner()

    def _execute_job_command(
        self,
        command: str,
        payload: dict[str, object],
    ) -> dict[str, object]:
        if command == "job.create":
            goal = str(payload.get("goal", "")).strip()
            try:
                job = self._jobs.create(
                    goal,
                    workspace_id=self._job_scope_id(),
                    chat_id=self._current_chat_id(),
                )
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "job": self._job_payload(job)}

        if command == "job.list":
            include_terminal = payload.get("include_terminal", True)
            if not isinstance(include_terminal, bool):
                raise DesktopCommandError("job.list.include_terminal must be true or false")
            limit = payload.get("limit", 50)
            try:
                jobs = self._jobs.list(
                    workspace_id=self._job_scope_id(),
                    include_terminal=include_terminal,
                    limit=limit,
                )
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "jobs": [self._job_payload(job) for job in jobs],
            }

        job = self._job_in_active_scope(str(payload.get("id", "")).strip())

        if command == "job.get":
            return {"ok": True, "command": command, "job": self._job_payload(job)}

        if command == "job.events":
            limit = payload.get("limit", 100)
            try:
                events = self._jobs.events(job.id, limit=limit)
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "events": [asdict(event) for event in events],
            }

        if command == "job.resume":
            if self._jobs.pending_approval_owner() == job.id:
                raise DesktopCommandError(
                    "Resolve or cancel the pending local action before resuming this job"
                )
            try:
                job = self._jobs.resume(job.id)
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "job": self._job_payload(job)}

        if command == "job.cancel":
            if self._jobs.pending_approval_owner() == job.id:
                PendingToolApprovalStore(self.config.pending_tool_approval_file).clear()
                self._jobs.clear_pending_approval_owner()
            try:
                job = self._jobs.cancel(job.id)
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "job": self._job_payload(job)}

        if command != "job.run":
            raise DesktopCommandError(f"Unsupported durable job command: {command}")

        job = self._job_in_active_scope(job.id, require_origin_chat=True)
        if PendingToolApprovalStore(self.config.pending_tool_approval_file).load() is not None:
            raise DesktopCommandError(
                "Resolve or reject the pending local action before starting a job"
            )
        try:
            job = self._jobs.start(job.id)
        except ValueError as exc:
            raise DesktopCommandError(str(exc)) from exc

        trace(
            "job.run.start",
            job_id=job.id,
            workspace=job.workspace_id,
            goal_chars=len(job.goal),
        )
        try:
            reply = self._core().think(job.goal)
        except Exception as exc:
            try:
                self._jobs.fail(job.id, error=f"{type(exc).__name__}: {exc}")
            except (KeyError, ValueError):
                pass
            trace("job.run.error", job_id=job.id, error=type(exc).__name__)
            if isinstance(exc, LLMError):
                raise DesktopCommandError(f"Local brain unavailable: {exc}") from exc
            raise

        pending = PendingToolApprovalStore(self.config.pending_tool_approval_file).load()
        try:
            if pending is not None:
                job = self._jobs.wait_for_permission(job.id, checkpoint=reply)
                self._jobs.set_pending_approval_owner(job.id)
            elif self._jobs.get(job.id).cancel_requested:
                job = self._jobs.cancel(job.id)
            else:
                job = self._jobs.complete(job.id, result=reply)
        except (KeyError, ValueError) as exc:
            raise DesktopCommandError(str(exc)) from exc
        trace("job.run.done", job_id=job.id, status=job.status)
        return {
            "ok": True,
            "command": command,
            "reply": reply,
            "job": self._job_payload(job),
        }

    def _execute_job_approval(
        self,
        command: str,
        payload: dict[str, object],
        owner: str,
    ) -> dict[str, object]:
        job = self._job_in_active_scope(owner, require_origin_chat=True)
        if job.status != "waiting_permission":
            self._jobs.clear_pending_approval_owner()
            raise DesktopCommandError("Durable job is not waiting for permission")

        approvals = PendingToolApprovalStore(self.config.pending_tool_approval_file)
        if command == "approval.reject":
            try:
                result = super().execute(command, payload)
            except Exception:
                approvals.clear()
                self._jobs.clear_pending_approval_owner()
                try:
                    self._jobs.pause(
                        job.id,
                        checkpoint=job.checkpoint,
                        note="Permission rejection could not be reconciled",
                    )
                except (KeyError, ValueError):
                    pass
                raise
            self._jobs.clear_pending_approval_owner()
            try:
                paused = self._jobs.pause(
                    job.id,
                    checkpoint=str(result.get("reply", "")),
                    note="Permission rejected; job paused",
                )
            except (KeyError, ValueError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            result["job"] = self._job_payload(paused)
            return result

        try:
            self._jobs.continue_after_permission(job.id)
            result = super().execute(command, payload)
        except Exception as exc:
            approvals.clear()
            self._jobs.clear_pending_approval_owner()
            try:
                self._jobs.fail(job.id, error=f"{type(exc).__name__}: {exc}")
            except (KeyError, ValueError):
                pass
            raise

        self._jobs.clear_pending_approval_owner()
        try:
            completed = self._jobs.complete(
                job.id,
                result=str(result.get("reply", "")),
            )
        except (KeyError, ValueError) as exc:
            raise DesktopCommandError(str(exc)) from exc
        result["job"] = self._job_payload(completed)
        return result

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
        payload = payload or {}
        if command.startswith("job."):
            return self._execute_job_command(command, payload)

        owner = self._jobs.pending_approval_owner()
        if owner is not None and command in {"approval.approve", "approval.reject"}:
            result = self._execute_job_approval(command, payload, owner)
        else:
            result = super().execute(command, payload)

        if command == "approval.approve":
            activity = self._agent_runtime().last_activity
            if activity:
                result["activity"] = activity
        return result


# Keep the familiar DesktopCommands import name for the worker/CLI entrypoints.
DesktopCommands = AgentDesktopCommands
