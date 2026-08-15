from __future__ import annotations

import time
from dataclasses import asdict

from .agent import AgentExecutor
from .autodidact import LessonCandidateStore
from .brain import BrainProviderError, build_brain_provider
from .config import BabyAIConfig
from .desktop_bridge import build_desktop_snapshot
from .hypothesis import HypothesisStore
from .identity import Identity, IdentityStore
from .llm import LLMError, LLMProvider
from .memory import MemoryKind, SQLiteMemoryStore
from .native_acceleration import select_native_runtime
from .native_runtime import NativeRuntimeError
from .permissions import PermissionStore
from .planner import Planner
from .primus import Primus
from .resident_native_brain import ResidentNativeBrainProvider
from .runtime_trace import trace
from .working_memory import TaskState, WorkingMemoryStore


class DesktopCommandError(ValueError):
    pass


class DesktopCommands:
    """Narrow command surface intended for trusted local desktop clients."""

    def __init__(self, config: BabyAIConfig | None = None, *, persistent: bool = False) -> None:
        self.config = config or BabyAIConfig.default()
        self.persistent = persistent
        self._provider_instance: LLMProvider | None = None

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
                trace(
                    "provider.native.select.done",
                    mode=getattr(route, "mode", "unknown"),
                    runtime=route.runtime_path.name,
                    n_gpu_layers=route.n_gpu_layers,
                    elapsed_ms=round((time.monotonic() - started) * 1000),
                )
                provider: LLMProvider = ResidentNativeBrainProvider(
                    model_path=self.config.native_model_file,
                    runtime_path=route.runtime_path,
                    n_gpu_layers=route.n_gpu_layers,
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
            )
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
