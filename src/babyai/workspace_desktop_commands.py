from __future__ import annotations

import time
from collections.abc import Callable
from dataclasses import asdict

from .agent import AgentExecutor
from .config import BabyAIConfig
from .desktop_commands import DesktopCommandError, DesktopCommands
from .history import ChatHistoryStore
from .identity import Identity, IdentityStore
from .llm import LLMError
from .memory import DURABLE_MEMORY_KINDS, MemoryKind, SessionMemoryStore, SQLiteMemoryStore
from .permissions import PermissionStore
from .planner import Planner
from .runtime_trace import process_memory_metrics, trace
from .tool_approval import PendingToolApprovalStore
from .working_memory import TaskState, WorkingMemoryStore
from .workspace import WorkspaceRecord, WorkspaceStore
from .workspace_context import WorkspacePrimus
from .workspace_documents import WorkspaceDocumentStore


class WorkspaceDesktopCommands(DesktopCommands):
    """Desktop command surface with active-workspace context isolation.

    When no workspace is selected, legacy BabyAI behavior is preserved.
    """

    def __init__(self, config: BabyAIConfig | None = None, *, persistent: bool = False) -> None:
        super().__init__(config, persistent=persistent)
        self._session_memories: dict[str, SessionMemoryStore] = {}

    def _workspace_store(self) -> WorkspaceStore:
        return WorkspaceStore(self.config.workspace_file)

    def _active_workspace(self) -> WorkspaceRecord | None:
        return self._workspace_store().active()

    def _task_store(self, workspace: WorkspaceRecord | None = None) -> WorkingMemoryStore:
        workspace = self._active_workspace() if workspace is None else workspace
        if workspace is None:
            return WorkingMemoryStore(self.config.working_memory_file)
        return WorkingMemoryStore(self.config.workspace_tasks_dir / f"{workspace.id}.json")

    def _document_store(
        self, workspace: WorkspaceRecord | None = None
    ) -> WorkspaceDocumentStore:
        workspace = self._active_workspace() if workspace is None else workspace
        if workspace is None:
            raise DesktopCommandError("An active workspace is required for document commands")
        return WorkspaceDocumentStore(
            self.config.workspace_documents_dir / f"{workspace.id}.json",
            workspace.id,
        )

    def _session_store(self, workspace: WorkspaceRecord | None = None) -> SessionMemoryStore:
        workspace = self._active_workspace() if workspace is None else workspace
        key = "__legacy__" if workspace is None else workspace.id
        store = self._session_memories.get(key)
        if store is None:
            store = SessionMemoryStore(max_records=48)
            self._session_memories[key] = store
        return store

    def _active_project(self) -> str:
        workspace = self._active_workspace()
        if workspace is not None:
            return workspace.name
        task = self._task_store(workspace).load()
        return "" if task is None else task.project

    def _require_no_pending_action_for_workspace_switch(self) -> None:
        pending = PendingToolApprovalStore(
            self.config.pending_tool_approval_file
        ).load()
        if pending is not None:
            raise DesktopCommandError(
                "Resolve or reject the pending local action before switching workspace"
            )

    def _core(self) -> WorkspacePrimus:
        workspace = self._active_workspace()
        identity = IdentityStore(self.config.identity_file).load_or_create(
            Identity(name=self.config.name, owner=self.config.owner)
        )
        permissions = PermissionStore(self.config.permissions_file)
        planner = None if self.config.provider == "native" else Planner()
        return WorkspacePrimus(
            llm=self._provider(),
            memory=SQLiteMemoryStore(self.config.memory_db),
            identity=identity,
            agent=AgentExecutor(permissions),
            planner=planner,
            working_memory=self._task_store(workspace),
            tool_approvals=PendingToolApprovalStore(self.config.pending_tool_approval_file),
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
            if state != "executing":
                raise DesktopCommandError("Unsupported streaming core state")
            emit({"event": "state", "state": state})

        trace(
            "chat.core.start",
            provider=self.config.provider,
            message_chars=len(message),
            streaming=True,
            workspace=self._active_project() or None,
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
            **process_memory_metrics(),
        )
        history = self._history()
        project = self._active_project()
        history.add("user", message, project=project)
        history.add("babyai", result.reply, project=project)

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

    def execute(self, command: str, payload: dict[str, object] | None = None) -> dict[str, object]:
        payload = payload or {}

        if command == "chat":
            message = str(payload.get("message", "")).strip()
            if not message:
                raise DesktopCommandError("chat.message is required")
            started = time.monotonic()
            trace(
                "chat.core.start",
                provider=self.config.provider,
                message_chars=len(message),
                workspace=self._active_project() or None,
                **process_memory_metrics(),
            )
            try:
                reply = self._core().think(message)
            except LLMError as exc:
                trace(
                    "chat.core.error",
                    elapsed_ms=round((time.monotonic() - started) * 1000),
                    error=type(exc).__name__,
                )
                raise DesktopCommandError(f"Local brain unavailable: {exc}") from exc
            trace(
                "chat.core.done",
                elapsed_ms=round((time.monotonic() - started) * 1000),
                reply_chars=len(reply),
                workspace=self._active_project() or None,
                **process_memory_metrics(),
            )
            project = self._active_project()
            self._history().add("user", message, project=project)
            self._history().add("babyai", reply, project=project)
            return {"ok": True, "command": command, "reply": reply}

        if command == "workspace.create":
            name = str(payload.get("name", "")).strip()
            root_value = payload.get("root")
            root = None if root_value is None else str(root_value).strip() or None
            try:
                workspace = self._workspace_store().create(name, root=root)
            except (ValueError, FileNotFoundError, NotADirectoryError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "workspace": asdict(workspace)}

        if command == "workspace.list":
            store = self._workspace_store()
            active = store.active()
            return {
                "ok": True,
                "command": command,
                "active_id": None if active is None else active.id,
                "workspaces": [asdict(item) for item in store.list()],
            }

        if command == "workspace.current":
            active = self._active_workspace()
            return {
                "ok": True,
                "command": command,
                "workspace": None if active is None else asdict(active),
            }

        if command == "workspace.select":
            self._require_no_pending_action_for_workspace_switch()
            identifier = str(payload.get("id", payload.get("name", ""))).strip()
            try:
                workspace = self._workspace_store().select(identifier)
            except (ValueError, KeyError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "workspace": asdict(workspace)}

        if command == "workspace.clear_active":
            self._require_no_pending_action_for_workspace_switch()
            self._workspace_store().clear_active()
            return {"ok": True, "command": command}

        if command == "document.add":
            path = str(payload.get("path", "")).strip()
            if not path:
                raise DesktopCommandError("document.add.path is required")
            raw_name = payload.get("name")
            name = None if raw_name is None else str(raw_name)
            try:
                document = self._document_store().add(path, name=name)
            except (ValueError, FileNotFoundError, OSError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "document": asdict(document)}

        if command == "document.list":
            try:
                documents = self._document_store().list()
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "documents": [asdict(item) for item in documents],
            }

        if command == "document.get":
            document_id = str(payload.get("id", "")).strip()
            try:
                document = self._document_store().get(document_id)
            except (ValueError, KeyError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "document": asdict(document)}

        if command == "document.read":
            document_id = str(payload.get("id", "")).strip()
            store = self._document_store()
            try:
                document = store.get(document_id)
                content = store.read_text(
                    document.id,
                    PermissionStore(self.config.permissions_file),
                )
            except (ValueError, KeyError, FileNotFoundError, PermissionError, OSError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "document": asdict(document),
                "content": content,
            }

        if command == "document.remove":
            document_id = str(payload.get("id", "")).strip()
            try:
                document = self._document_store().remove(document_id)
            except (ValueError, KeyError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "document": asdict(document)}

        if command == "task.set":
            goal = str(payload.get("goal", "")).strip()
            if not goal:
                raise DesktopCommandError("task.set.goal is required")
            workspace = self._active_workspace()
            project = (
                workspace.name
                if workspace is not None
                else str(payload.get("project", "")).strip()
            )
            task = self._task_store(workspace).save(
                TaskState(
                    goal=goal,
                    status=str(payload.get("status", "active")).strip() or "active",
                    context=str(payload.get("context", "")).strip(),
                    project=project,
                )
            )
            return {"ok": True, "command": command, "task": asdict(task)}

        if command == "task.clear":
            self._task_store().clear()
            return {"ok": True, "command": command}

        if command == "memory.save":
            content = str(payload.get("content", "")).strip()
            if not content:
                raise DesktopCommandError("memory.save.content is required")
            kind = self._memory_kind(payload.get("kind", MemoryKind.FACT.value))
            if kind not in DURABLE_MEMORY_KINDS:
                raise DesktopCommandError(
                    "Only preference, fact, knowledge, and project memory are durable"
                )
            project = str(payload.get("project", "")).strip()
            workspace = self._active_workspace()
            if kind is MemoryKind.PROJECT and workspace is not None:
                if project and project.casefold() != workspace.name.casefold():
                    raise DesktopCommandError(
                        "Project memory is isolated to the active workspace"
                    )
                project = workspace.name
            if kind is MemoryKind.PROJECT and not project:
                raise DesktopCommandError(
                    "memory.save.project is required when no workspace is active"
                )
            scope = project if kind is MemoryKind.PROJECT else "global"
            record = SQLiteMemoryStore(self.config.memory_db).add(
                "owner",
                content,
                kind=kind,
                scope=scope,
            )
            return {
                "ok": True,
                "command": command,
                "memory": self._memory_payload(record),
            }

        if command == "memory.list":
            raw_kind = payload.get("kind")
            kind = (
                None
                if raw_kind is None or str(raw_kind).strip() == ""
                else self._memory_kind(raw_kind)
            )
            project = str(payload.get("project", "")).strip()
            workspace = self._active_workspace()
            if kind is MemoryKind.PROJECT and workspace is not None:
                if project and project.casefold() != workspace.name.casefold():
                    raise DesktopCommandError(
                        "Project memory is isolated to the active workspace"
                    )
                project = workspace.name
            scope = project or None
            raw_limit = payload.get("limit", 50)
            if (
                isinstance(raw_limit, bool)
                or not isinstance(raw_limit, int)
                or not 1 <= raw_limit <= 200
            ):
                raise DesktopCommandError("memory.list.limit must be between 1 and 200")
            records = SQLiteMemoryStore(self.config.memory_db).recent(
                limit=raw_limit,
                kind=kind,
                scope=scope,
            )
            return {
                "ok": True,
                "command": command,
                "memories": [self._memory_payload(record) for record in records],
            }

        if command == "memory.session.clear":
            self._session_store().clear()
            return {"ok": True, "command": command}

        if command == "status":
            result = super().execute(command, payload)
            snapshot = result.get("snapshot")
            if isinstance(snapshot, dict):
                workspace = self._active_workspace()
                task = self._task_store(workspace).load()
                snapshot["workspace"] = (
                    None if workspace is None else asdict(workspace)
                )
                snapshot["task"] = None if task is None else asdict(task)
                snapshot["documents"] = {
                    "count": 0 if workspace is None else len(self._document_store(workspace).list())
                }
                history = snapshot.get("history")
                if isinstance(history, dict):
                    history["active_project"] = self._active_project() or None
            return result

        return super().execute(command, payload)
