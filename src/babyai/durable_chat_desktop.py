from __future__ import annotations

from collections.abc import Callable

from .agent_desktop import AgentDesktopCommands
from .desktop_commands import DesktopCommandError
from .jobs import DurableJob
from .tool_approval import PendingToolApprovalStore


class DurableChatDesktopCommands(AgentDesktopCommands):
    """Track actionable desktop chat turns as explicit durable jobs.

    Ordinary conversational turns stay lightweight. A turn that the existing
    Agent Runtime classifies as requesting a local action is wrapped in the
    same persisted job state machine used by explicit ``job.*`` commands.
    This adds durability without reintroducing the old pre-LLM action shortcut:
    the model still decides whether and which tool to call.
    """

    def _begin_chat_job(self, message: str) -> DurableJob | None:
        message = str(message).strip()
        if not message:
            return None
        runtime = self._agent_runtime()
        if not runtime.requests_local_action(message):
            return None
        # Do not stack a second tracked action on top of an unresolved global
        # one-shot approval. The desktop UI already blocks this path, while this
        # guard keeps direct protocol callers deterministic too.
        if PendingToolApprovalStore(self.config.pending_tool_approval_file).load() is not None:
            return None
        job = self._jobs.create(
            message,
            workspace_id=self._job_scope_id(),
            chat_id=self._current_chat_id(),
        )
        return self._jobs.start(job.id)

    def _finish_chat_job(self, job: DurableJob | None, reply: str) -> DurableJob | None:
        if job is None:
            return None
        current = self._jobs.get(job.id)
        if current.status != "running":
            return current

        pending = PendingToolApprovalStore(self.config.pending_tool_approval_file).load()
        if pending is not None:
            waiting = self._jobs.wait_for_permission(job.id, checkpoint=reply)
            self._jobs.set_pending_approval_owner(job.id)
            return waiting
        if current.cancel_requested:
            return self._jobs.cancel(job.id)
        return self._jobs.complete(job.id, result=reply)

    def _fail_chat_job(self, job: DurableJob | None, exc: Exception) -> None:
        if job is None:
            return
        try:
            current = self._jobs.get(job.id)
            if current.status in {"running", "waiting_permission"}:
                self._jobs.fail(job.id, error=f"{type(exc).__name__}: {exc}")
        except (KeyError, ValueError):
            pass

    def stream_chat(
        self,
        payload: dict[str, object],
        emit: Callable[[dict[str, object]], None],
    ) -> dict[str, object]:
        message = str(payload.get("message", "")).strip()
        if not message:
            raise DesktopCommandError("chat.message is required")

        job = self._begin_chat_job(message)
        try:
            result = super().stream_chat(payload, emit)
        except Exception as exc:
            self._fail_chat_job(job, exc)
            raise

        completed = self._finish_chat_job(job, str(result.get("reply", "")))
        if completed is not None:
            result["job"] = self._job_payload(completed)
        return result

    def execute(
        self,
        command: str,
        payload: dict[str, object] | None = None,
    ) -> dict[str, object]:
        payload = payload or {}
        if command != "chat":
            return super().execute(command, payload)

        message = str(payload.get("message", "")).strip()
        if not message:
            raise DesktopCommandError("chat.message is required")
        job = self._begin_chat_job(message)
        try:
            result = super().execute(command, payload)
        except Exception as exc:
            self._fail_chat_job(job, exc)
            raise

        completed = self._finish_chat_job(job, str(result.get("reply", "")))
        if completed is not None:
            result["job"] = self._job_payload(completed)
        return result


DesktopCommands = DurableChatDesktopCommands
