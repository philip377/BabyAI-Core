from __future__ import annotations

import json
import re
import time
from dataclasses import dataclass

from .agent import AgentExecutor, ToolCall
from .config import BabyAIConfig
from .desktop_commands import DesktopCommandError
from .identity import IdentityStore
from .permissions import PermissionStore
from .workspace_desktop_retrieval import WorkspaceDesktopCommands


@dataclass(slots=True)
class FileListObservation:
    path: str
    files: tuple[str, ...]
    cursor: int = 0
    armed: bool = True


class SessionToolObservationStore:
    """Process-local evidence for short follow-ups to trusted local reads.

    Raw local observations never enter durable memory or the model prompt. The store is
    deliberately narrow: today it only retains the most recent ``filesystem.list``
    result and only while the conversation remains immediately focused on that result.
    """

    _NEXT_MARKERS = (
        "а еще",
        "а ещё",
        "еще какие",
        "ещё какие",
        "еще есть",
        "ещё есть",
        "что еще",
        "что ещё",
        "кроме него",
        "кроме этого",
        "кроме этого файла",
        "следующий",
        "другие",
        "what else",
        "another one",
        "another file",
        "besides it",
        "besides that",
    )
    _CORRECTION_MARKERS = (
        "нет там",
        "там нет",
        "нет такого",
        "нет таких",
        "не существует",
        "no such",
        "not there",
        "isn't there",
        "is not there",
    )

    def __init__(self) -> None:
        self._file_list: FileListObservation | None = None

    def clear(self) -> None:
        self._file_list = None

    def disarm(self) -> None:
        if self._file_list is not None:
            self._file_list.armed = False

    def record(self, call: ToolCall, result: str) -> None:
        if call.name != "filesystem.list":
            return
        try:
            entries = json.loads(result)
        except (json.JSONDecodeError, TypeError):
            self.clear()
            return
        if not isinstance(entries, list) or not all(isinstance(item, str) for item in entries):
            self.clear()
            return

        files = tuple(item for item in entries if item and not item.endswith("/"))
        path = str(call.arguments.get("path", ".")).strip() or "."
        self._file_list = FileListObservation(path=path, files=files)

    def mark_consumed_from_reply(self, reply: str) -> None:
        observation = self._file_list
        if observation is None or not reply:
            return
        folded = reply.casefold()
        consumed = [
            index + 1
            for index, filename in enumerate(observation.files)
            if filename.casefold() in folded
        ]
        if consumed:
            observation.cursor = max(observation.cursor, max(consumed))
            observation.armed = True

    def consume_followup(self, user_input: str) -> str | None:
        observation = self._file_list
        if observation is None or not observation.armed:
            return None

        text = re.sub(r"\s+", " ", user_input.casefold()).strip()
        if any(marker in text for marker in self._CORRECTION_MARKERS):
            path = observation.path
            self.clear()
            if re.search(r"[а-яё]", text):
                return (
                    "Понял. Тогда не буду утверждать это по старому результату. "
                    f"Если нужно, я могу заново проверить {path}."
                )
            return (
                "Understood. I will not keep asserting the old result. "
                f"I can check {path} again if needed."
            )

        if not any(marker in text for marker in self._NEXT_MARKERS):
            return None

        if observation.cursor < len(observation.files):
            filename = observation.files[observation.cursor]
            observation.cursor += 1
            if re.search(r"[а-яё]", text):
                return f"Ещё: {filename}"
            return f"Another one: {filename}"

        if re.search(r"[а-яё]", text):
            return f"В сохранённом списке файлов из {observation.path} больше файлов не было."
        return f"There were no more files in the saved listing for {observation.path}."


class SessionObservingAgentExecutor(AgentExecutor):
    """Agent executor that records successful local observations in RAM only."""

    def __init__(
        self,
        permissions: PermissionStore,
        observations: SessionToolObservationStore,
    ) -> None:
        super().__init__(permissions)
        self._observations = observations

    def execute(self, call: ToolCall) -> str:
        result = super().execute(call)
        self._observations.record(call, result)
        return result


class TruthfulWorkspaceDesktopCommands(WorkspaceDesktopCommands):
    """Desktop surface that keeps local follow-ups grounded in observed data."""

    def __init__(self, config: BabyAIConfig | None = None, *, persistent: bool = False) -> None:
        super().__init__(config, persistent=persistent)
        self._tool_observations = SessionToolObservationStore()

    def _core(self):
        core = super()._core()
        core.agent = SessionObservingAgentExecutor(
            PermissionStore(self.config.permissions_file),
            self._tool_observations,
        )
        return core

    def _identity_reply(self, message: str) -> str | None:
        identity = IdentityStore(self.config.identity_file).load_or_create(
            self._default_identity()
        )
        return identity.provenance_reply(message)

    def _default_identity(self):
        from .identity import Identity

        return Identity(name=self.config.name, owner=self.config.owner)

    def _prepare_fast_reply(self, message: str) -> str | None:
        direct = AgentExecutor.infer_safe_local_intent(message)
        if direct is not None and direct.name == "filesystem.list":
            # A new explicit listing supersedes any previous listing immediately,
            # including while a new one is still awaiting one-time approval.
            self._tool_observations.clear()
            return None

        followup = self._tool_observations.consume_followup(message)
        if followup is not None:
            return followup

        identity = self._identity_reply(message)
        if identity is not None:
            self._tool_observations.disarm()
            return identity

        # Any unrelated turn closes the short-lived local observation context so a
        # later generic "what else?" cannot accidentally refer to an old file list.
        self._tool_observations.disarm()
        return None

    def _remember_fast_turn(self, message: str, reply: str) -> None:
        self._session_memory.add("user", message)
        self._session_memory.add("babyai", reply)
        history = self._history()
        project = self._active_project()
        history.add("user", message, project=project)
        history.add("babyai", reply, project=project)

    def stream_chat(self, payload: dict[str, object], emit) -> dict[str, object]:
        message = str(payload.get("message", "")).strip()
        if not message:
            raise DesktopCommandError("chat.message is required")

        fast_reply = self._prepare_fast_reply(message)
        if fast_reply is None:
            result = super().stream_chat(payload, emit)
            reply = str(result.get("reply", ""))
            self._tool_observations.mark_consumed_from_reply(reply)
            return result

        started = time.monotonic()
        emit({"event": "state", "state": "thinking"})
        emit({"event": "state", "state": "answering"})
        emit({"event": "delta", "text": fast_reply})
        self._remember_fast_turn(message, fast_reply)
        total_ms = round((time.monotonic() - started) * 1000)
        return {
            "reply": fast_reply,
            "metrics": {
                "visible_ttft_ms": total_ms,
                "native_first_token_ms": None,
                "generation_ms": 0,
                "total_ms": total_ms,
                "generated_tokens": 0,
                "delta_count": 1,
                "model_calls": 0,
                "stop_reason": "session_context",
            },
        }

    def execute(
        self,
        command: str,
        payload: dict[str, object] | None = None,
    ) -> dict[str, object]:
        payload = payload or {}
        if command == "chat":
            message = str(payload.get("message", "")).strip()
            if not message:
                raise DesktopCommandError("chat.message is required")
            fast_reply = self._prepare_fast_reply(message)
            if fast_reply is not None:
                self._remember_fast_turn(message, fast_reply)
                return {"ok": True, "command": command, "reply": fast_reply}

        result = super().execute(command, payload)
        if command in {"chat", "approval.approve"}:
            reply = result.get("reply")
            if isinstance(reply, str):
                self._tool_observations.mark_consumed_from_reply(reply)
        elif command == "memory.session.clear":
            self._tool_observations.clear()
        return result
