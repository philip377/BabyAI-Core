from __future__ import annotations

from dataclasses import asdict

from .agent import AgentExecutor
from .autodidact import LessonCandidateStore
from .brain import BrainProviderError, build_brain_provider
from .config import BabyAIConfig
from .desktop_bridge import build_desktop_snapshot
from .hypothesis import HypothesisStore
from .identity import Identity, IdentityStore
from .llm import LLMError
from .memory import MemoryKind, SQLiteMemoryStore
from .permissions import PermissionStore
from .planner import Planner
from .primus import Primus
from .working_memory import TaskState, WorkingMemoryStore


class DesktopCommandError(ValueError):
    pass


class DesktopCommands:
    """Narrow command surface intended for trusted local desktop clients."""

    def __init__(self, config: BabyAIConfig | None = None) -> None:
        self.config = config or BabyAIConfig.default()

    def _provider(self):
        try:
            return build_brain_provider(self.config)
        except BrainProviderError as exc:
            raise DesktopCommandError(str(exc)) from exc

    def _core(self) -> Primus:
        identity = IdentityStore(self.config.identity_file).load_or_create(
            Identity(name=self.config.name, owner=self.config.owner)
        )
        permissions = PermissionStore(self.config.permissions_file)
        # Native generation currently loads the local GGUF for every LLM call. A
        # separate planner call therefore doubles model-load/inference work even for
        # a simple greeting. Primus already supports planner=None and still parses a
        # tool call from the first model answer, so native desktop chat uses that
        # single-call path until a resident native session makes planning cheap.
        planner = None if self.config.provider == "native" else Planner()
        return Primus(
            llm=self._provider(),
            memory=SQLiteMemoryStore(self.config.memory_db),
            identity=identity,
            agent=AgentExecutor(permissions),
            planner=planner,
            working_memory=WorkingMemoryStore(self.config.working_memory_file),
        )

    def execute(self, command: str, payload: dict[str, object] | None = None) -> dict[str, object]:
        payload = payload or {}
        if command == "chat":
            message = str(payload.get("message", "")).strip()
            if not message:
                raise DesktopCommandError("chat.message is required")
            try:
                reply = self._core().think(message)
            except LLMError as exc:
                raise DesktopCommandError(f"Local brain unavailable: {exc}") from exc
            return {"ok": True, "command": command, "reply": reply}

        if command == "task.set":
            goal = str(payload.get("goal", "")).strip()
            if not goal:
                raise DesktopCommandError("task.set.goal is required")
            task = WorkingMemoryStore(self.config.working_memory_file).save(
                TaskState(
                    goal=goal,
                    status=str(payload.get("status", "active")).strip() or "active",
                    context=str(payload.get("context", "")).strip(),
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
