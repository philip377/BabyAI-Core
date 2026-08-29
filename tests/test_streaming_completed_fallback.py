from __future__ import annotations

from dataclasses import dataclass

import pytest

from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.llm import LLMError, LLMProvider
from babyai.memory import MemoryKind, SQLiteMemoryStore
from babyai.permissions import PermissionStore
from babyai.primus import Primus
from babyai.streaming import StreamingSafetyError, VisibleTextGate, new_visible_marker
from babyai.tool_approval import PendingToolApprovalStore


@dataclass(frozen=True)
class _Generation:
    text: str
    first_token_ms: int = 10
    generation_ms: int = 20
    generated_tokens: int = 12
    stop_reason: str = "eog"


class _CompletedOnlyProvider(LLMProvider):
    def __init__(self, text: str) -> None:
        self.text = text

    def generate(self, prompt: str) -> str:
        return self.text

    def generate_stream(self, prompt: str, on_candidate) -> _Generation:
        # Reproduce the installed failure: the model omits the per-turn visible
        # marker, so no progressive channel opens, then returns a completed prompt
        # echo that used to bypass the display gate through done.reply.
        return _Generation(self.text)


def _primus(tmp_path, provider: LLMProvider) -> Primus:
    permissions = PermissionStore(tmp_path / "permissions.json")
    return Primus(
        llm=provider,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(),
        agent=AgentExecutor(permissions),
        tool_approvals=PendingToolApprovalStore(tmp_path / "approval.json"),
    )


def test_missing_marker_completed_reply_still_passes_display_boundary() -> None:
    gate = VisibleTextGate(marker=new_visible_marker())

    assert gate.strip_marker("Привет! Чем могу помочь?") == "Привет! Чем могу помочь?"

    echoed_contract = (
        "Привет! Как я могу помочь вам сегодня?\n\n"
        "The marker must be the first output after any optional <think> block. "
        "Never put the marker before JSON, a tool call, reasoning, protocol data, or a code fence."
    )
    with pytest.raises(StreamingSafetyError, match="completed reply failed safety validation"):
        gate.strip_marker(echoed_contract)


def test_contract_echo_without_marker_fails_before_memory_or_delta(tmp_path) -> None:
    raw = (
        "Привет! Как я могу помочь вам сегодня?\n\n"
        "The marker must be the first output after any optional <think> block. "
        "Never put the marker before JSON, a tool call, reasoning, protocol data, or a code fence. "
        "Emit exactly one assistant turn; never continue with USER: or BABYAI: role labels."
    )
    primus = _primus(tmp_path, _CompletedOnlyProvider(raw))
    deltas: list[str] = []

    with pytest.raises(LLMError, match="Streaming response failed safety validation"):
        primus.think_stream("привет", deltas.append)

    assert deltas == []
    assert primus.memory.recent(limit=10, kind=MemoryKind.EPISODIC) == []
