from __future__ import annotations

import os
import time
from dataclasses import asdict

from .agent import AgentExecutor, ToolCall, ToolProtocolError
from .autodidact import LessonCandidateStore
from .brain import BrainProviderError, build_brain_provider
from .config import BabyAIConfig
from .desktop_bridge import build_desktop_snapshot
from .hypothesis import HypothesisStore
from .history import ChatHistoryStore
from .identity import Identity, IdentityStore
from .llm import LLMError, LLMProvider
from .memory import DURABLE_MEMORY_KINDS, MemoryKind, MemoryRecord, SessionMemoryStore, SQLiteMemoryStore
from .native_acceleration import select_native_runtime
from .native_runtime import NativeRuntimeError
from .native_threads import preferred_native_thread_count
from .permissions import PermissionStore
from .planner import Planner
from .primus import Primus
from .resident_native_brain import ResidentNativeBrainProvider
from .runtime_trace import process_memory_metrics, trace
from .screen_vision import ScreenCaptureStore
from .tool_approval import PendingToolApproval, PendingToolApprovalStore
from .windows_actions import APPLICATIONS
from .working_memory import TaskState, WorkingMemoryStore


class DesktopCommandError(ValueError):
    pass


class DesktopCommands:
    """Narrow command surface intended for trusted local desktop clients."""

    def __init__(self, config: BabyAIConfig | None = None, *, persistent: bool = False) -> None:
        self.config = config or BabyAIConfig.default()
        self.persistent = persistent
        self._provider_instance: LLMProvider | None = None
        self._session_memory = SessionMemoryStore(max_records=48)

    def _provider(self) -> LLMProvider:
        if self.persistent and self._provider_instance is not None:
            trace("provider.reuse", provider=self.config.provider)
            return self._provider_instance

        started = time.monotonic()
        try:
            if self.persistent and self.config.provider == "native":
                trace(
                    "provider.native.select.start",
                    acceleration=self.config.native_acceleration,
                )
                route = select_native_runtime(
                    self.config.native_acceleration,
                    self.config.native_runtime_file,
                    self.config.native_vulkan_runtime_file,
                )
                logical_cpu_count = os.cpu_count() or 1
                native_threads = preferred_native_thread_count(logical_cpu_count=logical_cpu_count)
                trace(
                    "provider.native.select.done",
                    mode=getattr(route, "mode", "unknown"),
                    runtime=route.runtime_path.name,
                    cpu_profile=os.getenv("BABYAI_NATIVE_CPU_PROFILE", "unknown"),
                    logical_cpu_count=logical_cpu_count,
                    native_threads=native_threads,
                    n_gpu_layers=route.n_gpu_layers,
                    elapsed_ms=round((time.monotonic() - started) * 1000),
                    **process_memory_metrics(),
                )
                provider: LLMProvider = ResidentNativeBrainProvider(
                    model_path=self.config.native_model_file,
                    runtime_path=route.runtime_path,
                    n_gpu_layers=route.n_gpu_layers,
                    n_threads=native_threads,
                )
            else:
                provider = build_brain_provider(self.config)
        except BrainProviderError as exc:
            raise DesktopCommandError(str(exc)) from exc
        except NativeRuntimeError as exc:
            raise LLMError(f"Native brain inference failed: {exc}") from exc

        if self.persistent:
            self._provider_instance = provider
        trace(
            "provider.ready",
            provider=self.config.provider,
            elapsed_ms=round((time.monotonic() - started) * 1000),
            **process_memory_metrics(),
        )
        return provider

    def _core(self) -> Primus:
        identity = IdentityStore(self.config.identity_file).load_or_create(
            Identity(name=self.config.name, owner=self.config.owner)
        )
        permissions = PermissionStore(self.config.permissions_file)
        # Native generation keeps the single-call path. A persistent desktop worker
        # can keep the provider/model resident without freezing the surrounding
        # mutable stores, which are rebuilt for each command.
        planner = None if self.config.provider == "native" else Planner()
        return Primus(
            llm=self._provider(),
            memory=SQLiteMemoryStore(self.config.memory_db),
            identity=identity,
            agent=AgentExecutor(permissions),
            planner=planner,
            working_memory=WorkingMemoryStore(self.config.working_memory_file),
            tool_approvals=PendingToolApprovalStore(self.config.pending_tool_approval_file),
            repair_tool_calls=self.config.provider == "native",
            # CPU-native chat benefits materially from a bounded prompt. The tool
            # catalog and newest memories remain intact; only older memory is cut.
            max_context_chars=6_000 if self.config.provider == "native" else 12_000,
            session_memory=self._session_memory,
        )

    def close(self) -> None:
        provider = self._provider_instance
        self._provider_instance = None
        if provider is None:
            return
        close = getattr(provider, "close", None)
        if callable(close):
            close()

    def __enter__(self) -> DesktopCommands:
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

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
                **process_memory_metrics(),
            )
            history = ChatHistoryStore(
                self.config.history_db,
                self.config.history_settings_file,
            )
            task = WorkingMemoryStore(self.config.working_memory_file).load()
            project = "" if task is None else task.project
            history.add("user", message, project=project)
            history.add("babyai", reply, project=project)
            return {"ok": True, "command": command, "reply": reply}

        if command == "approval.approve":
            try:
                reply = self._core().approve_pending_tool()
            except (ToolProtocolError, LLMError) as exc:
                raise DesktopCommandError(str(exc)) from exc
            self._history().add("babyai", reply, project=self._active_project())
            return {"ok": True, "command": command, "reply": reply}

        if command == "approval.reject":
            try:
                reply = self._core().reject_pending_tool()
            except ToolProtocolError as exc:
                raise DesktopCommandError(str(exc)) from exc
            self._history().add("babyai", reply, project=self._active_project())
            return {"ok": True, "command": command, "reply": reply}

        if command == "history.set_enabled":
            enabled = payload.get("enabled")
            if not isinstance(enabled, bool):
                raise DesktopCommandError("history.set_enabled.enabled must be true or false")
            self._history().set_enabled(enabled)
            return {"ok": True, "command": command, "enabled": enabled}

        if command == "history.list":
            project_value = payload.get("project")
            project = None if project_value is None else str(project_value).strip()
            limit = payload.get("limit", 100)
            if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 500:
                raise DesktopCommandError("history.list.limit must be between 1 and 500")
            return {
                "ok": True,
                "command": command,
                "enabled": self._history().is_enabled(),
                "messages": [asdict(item) for item in self._history().list(project=project, limit=limit)],
            }

        if command == "history.clear":
            project_value = payload.get("project")
            project = None if project_value is None else str(project_value).strip()
            deleted = self._history().clear(project=project)
            return {"ok": True, "command": command, "deleted": deleted}

        if command == "memory.save":
            content = str(payload.get("content", "")).strip()
            if not content:
                raise DesktopCommandError("memory.save.content is required")
            kind = self._memory_kind(payload.get("kind", MemoryKind.FACT.value))
            if kind not in DURABLE_MEMORY_KINDS:
                raise DesktopCommandError("Only preference, fact, knowledge, and project memory are durable")
            project = str(payload.get("project", "")).strip()
            if kind is MemoryKind.PROJECT and not project:
                raise DesktopCommandError("memory.save.project is required for project memory")
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
            kind = None if raw_kind is None or str(raw_kind).strip() == "" else self._memory_kind(raw_kind)
            project = str(payload.get("project", "")).strip()
            scope = project or None
            raw_limit = payload.get("limit", 50)
            if isinstance(raw_limit, bool) or not isinstance(raw_limit, int) or not 1 <= raw_limit <= 200:
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

        if command == "memory.update":
            memory_id = self._memory_id(payload, command)
            content = str(payload.get("content", "")).strip()
            if not content:
                raise DesktopCommandError("memory.update.content is required")
            try:
                record = SQLiteMemoryStore(self.config.memory_db).update(memory_id, content)
            except KeyError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {
                "ok": True,
                "command": command,
                "memory": self._memory_payload(record),
            }

        if command == "memory.delete":
            memory_id = self._memory_id(payload, command)
            deleted = SQLiteMemoryStore(self.config.memory_db).delete(memory_id)
            if not deleted:
                raise DesktopCommandError(f"Memory #{memory_id} does not exist")
            return {"ok": True, "command": command, "memory_id": memory_id}

        if command == "memory.session.clear":
            self._session_memory.clear()
            return {"ok": True, "command": command}

        if command == "vision.observations.list":
            store = ScreenCaptureStore(
                self.config.screen_captures_dir,
                PermissionStore(self.config.permissions_file),
            )
            return {
                "ok": True,
                "command": command,
                "observations": [asdict(item) for item in store.list()],
            }

        if command == "vision.observations.delete":
            observation_id = str(payload.get("id", "")).strip()
            if not observation_id:
                raise DesktopCommandError("vision.observations.delete.id is required")
            store = ScreenCaptureStore(
                self.config.screen_captures_dir,
                PermissionStore(self.config.permissions_file),
            )
            if not store.delete(observation_id):
                raise DesktopCommandError(f"Screen observation {observation_id} does not exist")
            return {"ok": True, "command": command, "observation_id": observation_id}

        if command == "vision.action.propose":
            observation_id = str(payload.get("observation_id", "")).strip()
            store = ScreenCaptureStore(
                self.config.screen_captures_dir,
                PermissionStore(self.config.permissions_file),
            )
            if not observation_id or store.get(observation_id) is None:
                raise DesktopCommandError("vision.action.propose requires an existing observation_id")
            tool = str(payload.get("tool", "")).strip()
            arguments = payload.get("arguments", {})
            if not isinstance(arguments, dict):
                raise DesktopCommandError("vision.action.propose.arguments must be an object")
            if tool == "application.open":
                name = arguments.get("name")
                if not isinstance(name, str) or name.strip().casefold() not in APPLICATIONS:
                    raise DesktopCommandError("vision action application must be allowlisted")
                arguments = {"name": name.strip().casefold()}
            elif tool == "window.activate":
                handle = arguments.get("handle")
                if isinstance(handle, bool) or not isinstance(handle, int) or handle <= 0:
                    raise DesktopCommandError("vision action window handle must be a positive integer")
                arguments = {"handle": handle}
            else:
                raise DesktopCommandError(
                    "vision action must be application.open or window.activate"
                )
            executor = AgentExecutor(PermissionStore(self.config.permissions_file))
            call = ToolCall(tool, arguments)
            capability = executor.required_capability(call)
            PendingToolApprovalStore(self.config.pending_tool_approval_file).save(
                PendingToolApproval(
                    user_input=f"Предложенное действие после снимка {observation_id}: {tool}",
                    tool=tool,
                    arguments=arguments,
                    capability=capability.value,
                )
            )
            return {
                "ok": True,
                "command": command,
                "reply": Primus._permission_prompt(call),
                "observation_id": observation_id,
            }

        if command == "task.set":
            goal = str(payload.get("goal", "")).strip()
            if not goal:
                raise DesktopCommandError("task.set.goal is required")
            task = WorkingMemoryStore(self.config.working_memory_file).save(
                TaskState(
                    goal=goal,
                    status=str(payload.get("status", "active")).strip() or "active",
                    context=str(payload.get("context", "")).strip(),
                    project=str(payload.get("project", "")).strip(),
                )
            )
            return {"ok": True, "command": command, "task": asdict(task)}

        if command == "task.clear":
            WorkingMemoryStore(self.config.working_memory_file).clear()
            return {"ok": True, "command": command}

        if command in {"hypothesis.confirm", "hypothesis.reject"}:
            status = "confirmed" if command.endswith("confirm") else "rejected"
            try:
                record = HypothesisStore(self.config.hypothesis_file).set_status(status)
            except ValueError as exc:
                raise DesktopCommandError(str(exc)) from exc
            return {"ok": True, "command": command, "status": record.status}

        if command == "lesson.approve":
            store = LessonCandidateStore(self.config.lesson_candidate_file)
            candidate = store.load()
            if candidate is None:
                raise DesktopCommandError("No pending lesson candidate")
            record = SQLiteMemoryStore(self.config.memory_db).add(
                "autodidact", candidate.knowledge, kind=MemoryKind.KNOWLEDGE
            )
            store.clear()
            return {"ok": True, "command": command, "memory_id": record.id}

        if command == "lesson.reject":
            LessonCandidateStore(self.config.lesson_candidate_file).clear()
            return {"ok": True, "command": command}

        if command == "status":
            return {"ok": True, "command": command, "snapshot": build_desktop_snapshot(self.config).as_dict()}

        raise DesktopCommandError(f"Unsupported desktop command: {command}")

    @staticmethod
    def _memory_kind(value: object) -> MemoryKind:
        try:
            return MemoryKind(str(value).strip().lower())
        except ValueError as exc:
            raise DesktopCommandError(f"Unknown memory kind: {value}") from exc

    @staticmethod
    def _memory_id(payload: dict[str, object], command: str) -> int:
        value = payload.get("id")
        if isinstance(value, bool) or not isinstance(value, int) or value <= 0:
            raise DesktopCommandError(f"{command}.id must be a positive integer")
        return value

    @staticmethod
    def _memory_payload(record: MemoryRecord) -> dict[str, object]:
        return {
            "id": record.id,
            "kind": record.kind.value,
            "scope": record.scope,
            "role": record.role,
            "content": record.content,
            "created_at": record.created_at.isoformat(),
        }

    def _history(self) -> ChatHistoryStore:
        return ChatHistoryStore(self.config.history_db, self.config.history_settings_file)

    def _active_project(self) -> str:
        task = WorkingMemoryStore(self.config.working_memory_file).load()
        return "" if task is None else task.project
