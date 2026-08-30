from __future__ import annotations

from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SQLiteMemoryStore
from babyai.native_brain import _normalise_native_reply
from babyai.permissions import PermissionStore
from babyai.primus import Primus
from babyai.streaming import VisibleTextGate, new_visible_marker
from babyai.tool_approval import PendingToolApprovalStore


class FailIfCalledProvider(LLMProvider):
    def __init__(self) -> None:
        self.calls = 0

    def generate(self, prompt: str) -> str:
        self.calls += 1
        raise AssertionError("Natural desktop-list requests must not reach the model")


def test_natural_desktop_file_request_creates_real_pending_approval_without_model(tmp_path) -> None:
    provider = FailIfCalledProvider()
    permissions = PermissionStore(tmp_path / "permissions.json")
    approvals = PendingToolApprovalStore(tmp_path / "approval.json")
    primus = Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=approvals,
        repair_tool_calls=True,
    )
    deltas: list[str] = []

    result = primus.think_stream(
        "молоток, назови любой файл на моем рабочем столе",
        deltas.append,
    )

    pending = approvals.load()
    assert provider.calls == 0
    assert pending is not None
    assert pending.tool == "filesystem.list"
    assert pending.arguments == {"path": "~/Desktop"}
    assert "разреш" in result.reply.casefold()
    assert "~/Desktop" in result.reply
    assert "".join(deltas) == result.reply


def test_visible_stream_quarantines_native_prompt_echo_and_meta_reasoning() -> None:
    marker = new_visible_marker()
    gate = VisibleTextGate(marker=marker)
    answer = "Это нормальный ответ пользователю. " * 8

    assert gate.feed(marker + answer)
    leaked = (
        "\nAnswer directly in the user's language. Do not reveal reasoning. "
        "Do not add a translation unless the user requested one. "
        "Okay, let me try to figure out what the user is asking for here."
    )
    assert gate.feed(leaked) == ""

    visible = gate.emitted
    assert "Answer directly" not in visible
    assert "Do not reveal reasoning" not in visible
    assert "Okay" not in visible
    assert "figure out" not in visible


def test_native_normalizer_removes_prompt_echo_before_reasoning_tail() -> None:
    answer = 'Папка "Документы", файл "отчет.docx".'
    raw = (
        answer
        + "\nAnswer directly in the user's language. Do not reveal reasoning. "
        + "Do not add a translation unless the user requested one. "
        + "Okay, let me try to figure out what the user is asking for here."
    )

    assert _normalise_native_reply(raw) == answer


def test_visible_stream_quarantines_any_okay_the_user_reasoning_tail() -> None:
    marker = new_visible_marker()
    gate = VisibleTextGate(marker=marker)
    answer = "Я рад, что ты починил! Что тебя интересует? " * 6

    assert gate.feed(marker + answer)
    leaked = (
        "\nOkay, the user mentioned that there was some mess with files on their desktop "
        "but they fixed it. They're asking what else I can tell them."
    )
    assert gate.feed(leaked) == ""

    visible = gate.emitted
    assert "Okay, the user" not in visible
    assert "They're asking" not in visible


def test_native_normalizer_removes_any_okay_the_user_reasoning_tail() -> None:
    answer = "Я рад, что ты починил! Что тебя интересует?"
    raw = (
        answer
        + "\nOkay, the user mentioned that there was some mess with files on their desktop "
        + "but they fixed it. They're asking what else I can tell them."
    )

    assert _normalise_native_reply(raw) == answer
