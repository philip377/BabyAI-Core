from __future__ import annotations

import re
from dataclasses import dataclass

import pytest

from babyai.agent import AgentExecutor
from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError, DesktopCommands
from babyai.history import ChatHistoryStore
from babyai.identity import Identity
from babyai.llm import LLMError, LLMProvider
from babyai.memory import MemoryKind, SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus
from babyai.streaming import VisibleTextGate, new_visible_marker
from babyai.tool_approval import PendingToolApprovalStore


@dataclass(frozen=True)
class Generation:
    text: str
    first_token_ms: int = 12
    generation_ms: int = 34
    generated_tokens: int = 5
    stop_reason: str = "eog"


class StreamingProvider(LLMProvider):
    def __init__(self, text: str, candidates: list[str]) -> None:
        self.text = text
        self.candidates = candidates
        self.stream_calls = 0
        self.generate_calls = 0

    def generate(self, prompt: str) -> str:
        self.generate_calls += 1
        return self.text

    def generate_stream(self, prompt: str, on_candidate) -> Generation:
        self.stream_calls += 1
        for candidate in self.candidates:
            on_candidate(candidate)
        return Generation(self.text)


class MarkerProvider(StreamingProvider):
    saw_callback_effect_before_return = False

    def generate_stream(self, prompt: str, on_candidate) -> Generation:
        self.stream_calls += 1
        marker = re.search(r"<babyai-visible-[0-9a-f]{32}>", prompt)
        assert marker is not None
        raw = marker.group(0) + self.text
        for index in range(0, len(raw), 17):
            on_candidate(raw[index : index + 17])
            if getattr(self, "callback_effect", lambda: False)():
                self.saw_callback_effect_before_return = True
        return Generation(raw)


class LateToolProvider(StreamingProvider):
    def generate_stream(self, prompt: str, on_candidate) -> Generation:
        marker = re.search(r"<babyai-visible-[0-9a-f]{32}>", prompt)
        assert marker is not None
        visible = marker.group(0) + ("Обычный ответ пользователю. " * 8)
        tool = '\n```json\n{"tool":"system.info","arguments":{}}\n```'
        on_candidate(visible)
        on_candidate(tool)
        return Generation(visible + tool)


class PreambleNonceProvider(StreamingProvider):
    def generate_stream(self, prompt: str, on_candidate) -> Generation:
        marker = re.search(r"<babyai-visible-[0-9a-f]{32}>", prompt)
        assert marker is not None
        raw = "private preamble " + marker.group(0) + " safe-looking answer"
        on_candidate(raw)
        return Generation(raw)


class OrderedAgent(AgentExecutor):
    def __init__(self, permissions: PermissionStore, timeline: list[str]) -> None:
        super().__init__(permissions)
        self.timeline = timeline

    def execute(self, call):
        self.timeline.append("executor:start")
        result = '["example.txt"]'
        self.timeline.append("executor:return")
        return result


def build_primus(tmp_path, provider: LLMProvider) -> Primus:
    permissions = PermissionStore(tmp_path / "permissions.json")
    return Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=PendingToolApprovalStore(tmp_path / "approval.json"),
    )


def test_visible_gate_never_exposes_thinking_or_tool_json() -> None:
    gate = VisibleTextGate(tool_names=("system.info",))

    assert gate.feed("<thi") == ""
    assert gate.feed("nk>private reasoning</think>") == ""
    assert gate.feed('{"tool":"system.info","arguments":{}}') == ""
    assert gate.finish('{"tool":"system.info","arguments":{}}') == ""
    assert "private" not in gate.emitted
    assert "tool" not in gate.emitted


def test_visible_gate_requires_unpredictable_marker_before_streaming() -> None:
    marker = new_visible_marker()
    gate = VisibleTextGate(marker=marker)
    internal = "private preamble " * 20 + "```json\n{\"response\":\"safe\"}\n```"

    assert gate.feed(internal) == ""
    assert gate.emitted == ""

    opened = VisibleTextGate(marker=marker)
    delta = opened.feed(marker + ("Безопасный ответ пользователю. " * 8))
    assert delta
    assert marker not in delta
    assert "Безопасный ответ" in delta


def test_answer_only_stream_uses_safe_canonical_reply(tmp_path) -> None:
    provider = StreamingProvider(
        "Привет! Чем помочь?",
        ["<think>секрет</think>", "Привет! ", "Чем помочь?"],
    )
    primus = build_primus(tmp_path, provider)
    deltas: list[str] = []

    result = primus.think_stream("Привет", deltas.append)

    assert result.reply == "Привет! Чем помочь?"
    assert "".join(deltas) == result.reply
    assert "секрет" not in "".join(deltas)
    assert provider.stream_calls == 1
    assert result.metrics.native_first_token_ms == 12
    assert result.metrics.generated_tokens == 5


def test_answer_only_marker_opens_incremental_visible_channel(tmp_path) -> None:
    reply = "Это проверенный пользовательский ответ. " * 8
    provider = MarkerProvider(reply, [])
    primus = build_primus(tmp_path, provider)
    deltas: list[str] = []
    provider.callback_effect = lambda: bool(deltas)

    result = primus.think_stream("Расскажи подробнее", deltas.append)

    assert len(deltas) > 1
    assert "".join(deltas) == reply
    assert all("babyai-visible" not in delta for delta in deltas)
    assert result.reply == reply
    assert provider.saw_callback_effect_before_return is True


def test_open_marker_with_late_tool_json_fails_before_executor(tmp_path) -> None:
    provider = LateToolProvider("", [])
    primus = build_primus(tmp_path, provider)
    deltas: list[str] = []

    with pytest.raises(LLMError, match="safety validation"):
        primus.think_stream("Расскажи что-нибудь", deltas.append)

    provisional = "".join(deltas)
    assert "system.info" not in provisional
    assert "arguments" not in provisional
    assert primus.tool_approvals is not None
    assert primus.tool_approvals.load() is None


def test_preamble_before_nonce_fails_without_delta_memory_or_history(tmp_path, monkeypatch) -> None:
    provider = PreambleNonceProvider("", [])
    primus = build_primus(tmp_path, provider)
    config = BabyAIConfig(data_dir=tmp_path, provider="echo")
    ChatHistoryStore(config.history_db, config.history_settings_file).set_enabled(True)
    commands = DesktopCommands(config)
    monkeypatch.setattr(commands, "_core", lambda: primus)
    events: list[dict[str, object]] = []

    with pytest.raises(DesktopCommandError, match="Local brain unavailable"):
        commands.stream_chat({"message": "Привет"}, events.append)

    assert [event["event"] for event in events] == ["state"]
    assert events[0]["state"] == "thinking"
    assert primus.memory.recent(limit=10, kind=MemoryKind.EPISODIC) == []
    assert ChatHistoryStore(config.history_db, config.history_settings_file).list() == []


def test_local_action_path_stays_buffered_and_hides_tool_json(tmp_path) -> None:
    provider = StreamingProvider(
        '{"tool":"system.info","arguments":{}}',
        ['{"tool":"system.info","arguments":{}}'],
    )
    primus = build_primus(tmp_path, provider)
    deltas: list[str] = []
    states: list[str] = []

    result = primus.think_stream(
        "Покажи сведения о компьютере",
        deltas.append,
        states.append,
    )

    assert provider.stream_calls == 0
    assert provider.generate_calls == 1
    assert "разреш" in result.reply.casefold()
    assert "tool" not in "".join(deltas)
    assert "".join(deltas) == result.reply
    assert states == []


def test_pregranted_direct_action_emits_truthful_executing_order(tmp_path, monkeypatch) -> None:
    timeline: list[str] = []
    permissions = PermissionStore(tmp_path / "permissions.json")
    permissions.grant(Capability.FILESYSTEM_LIST)
    provider = StreamingProvider("unused", [])
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=OrderedAgent(permissions, timeline),
        tool_approvals=PendingToolApprovalStore(tmp_path / "approval.json"),
        repair_tool_calls=True,
    )
    commands = DesktopCommands(BabyAIConfig(data_dir=tmp_path, provider="echo"))
    monkeypatch.setattr(commands, "_core", lambda: primus)

    def emit(event: dict[str, object]) -> None:
        if event["event"] == "state":
            timeline.append(f"state:{event['state']}")
        else:
            timeline.append(f"delta:{event['text']}")

    result = commands.stream_chat(
        {"message": "Назови любое имя файла на моём рабочем столе"},
        emit,
    )

    assert timeline == [
        "state:thinking",
        "state:executing",
        "executor:start",
        "executor:return",
        "state:answering",
        "delta:Например: example.txt",
    ]
    assert result["reply"] == "Например: example.txt"
    assert provider.generate_calls == 0
    assert provider.stream_calls == 0
