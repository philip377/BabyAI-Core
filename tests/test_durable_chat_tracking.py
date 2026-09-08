from __future__ import annotations

from dataclasses import dataclass

from babyai.config import BabyAIConfig
from babyai.durable_chat_desktop import DurableChatDesktopCommands
from babyai.tool_approval import PendingToolApproval, PendingToolApprovalStore


@dataclass
class _Metrics:
    native_first_token_ms: int | None = 10
    generation_ms: int | None = 20
    generated_tokens: int = 3
    model_calls: int = 1
    stop_reason: str = "completed"


@dataclass
class _Result:
    reply: str
    metrics: _Metrics


class _StreamingCore:
    def __init__(self, reply: str, before_return=None) -> None:
        self.reply = reply
        self.before_return = before_return

    def think_stream(self, message, emit_delta, emit_state):
        emit_delta(self.reply)
        if self.before_return is not None:
            self.before_return(message)
        return _Result(self.reply, _Metrics())


class _BlockingCore:
    def __init__(self, reply: str, before_return=None) -> None:
        self.reply = reply
        self.before_return = before_return

    def think(self, message):
        if self.before_return is not None:
            self.before_return(message)
        return self.reply


def _commands(tmp_path) -> DurableChatDesktopCommands:
    return DurableChatDesktopCommands(BabyAIConfig(data_dir=tmp_path))


def test_conversational_stream_turn_does_not_create_job(tmp_path):
    commands = _commands(tmp_path)
    commands._core = lambda: _StreamingCore("Привет")  # type: ignore[method-assign]

    result = commands.stream_chat({"message": "Расскажи о себе"}, lambda event: None)

    assert "job" not in result
    assert commands.execute("job.list", {})["jobs"] == []


def test_actionable_stream_turn_is_persisted_and_completed(tmp_path):
    commands = _commands(tmp_path)
    commands._core = lambda: _StreamingCore("На рабочем столе есть notes.txt")  # type: ignore[method-assign]

    result = commands.stream_chat(
        {"message": "Назови файл на моём рабочем столе"},
        lambda event: None,
    )

    assert result["job"]["status"] == "completed"
    assert result["job"]["result"] == "На рабочем столе есть notes.txt"
    listed = commands.execute("job.list", {})["jobs"]
    assert listed[0]["id"] == result["job"]["id"]
    assert listed[0]["chat_id"] is not None


def test_actionable_turn_waits_on_same_durable_job_for_permission(tmp_path):
    commands = _commands(tmp_path)
    approvals = PendingToolApprovalStore(commands.config.pending_tool_approval_file)

    def request_permission(message: str) -> None:
        approvals.save(
            PendingToolApproval(
                user_input=message,
                tool="filesystem.list",
                arguments={"path": "~/Desktop"},
                capability="filesystem.list",
            )
        )

    commands._core = lambda: _StreamingCore(  # type: ignore[method-assign]
        "Нужно разрешение", request_permission
    )

    result = commands.stream_chat(
        {"message": "Проверь файлы на моём рабочем столе"},
        lambda event: None,
    )

    assert result["job"]["status"] == "waiting_permission"
    assert commands._jobs.pending_approval_owner() == result["job"]["id"]
    assert approvals.load() is not None


def test_non_streaming_actionable_turn_uses_same_tracking_path(tmp_path):
    commands = _commands(tmp_path)
    commands._core = lambda: _BlockingCore("Готово")  # type: ignore[method-assign]

    result = commands.execute("chat", {"message": "Назови файл на моём рабочем столе"})

    assert result["job"]["status"] == "completed"
    assert result["job"]["result"] == "Готово"
