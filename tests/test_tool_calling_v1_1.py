from __future__ import annotations

from dataclasses import dataclass

import pytest

from babyai.agent import AgentExecutor, ToolProtocolError
from babyai.identity import Identity
from babyai.memory import MemoryKind, SQLiteMemoryStore
from babyai.permissions import PermissionStore
from babyai.primus import Primus


@dataclass
class CaptureLLM:
    response: str = "ok"

    def __post_init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.response


def test_tool_parser_accepts_json_fence(tmp_path) -> None:
    agent = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))
    call = agent.parse_tool_call('```json\n{"tool":"system.info","arguments":{}}\n```')
    assert call is not None
    assert call.name == "system.info"
    assert call.arguments == {}


def test_tool_parser_rejects_extra_fields(tmp_path) -> None:
    agent = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))
    with pytest.raises(ToolProtocolError, match="Unexpected tool call fields"):
        agent.parse_tool_call('{"tool":"system.info","arguments":{},"extra":true}')


def test_tool_parser_ignores_non_json_answer(tmp_path) -> None:
    agent = AgentExecutor(PermissionStore(tmp_path / "permissions.json"))
    assert agent.parse_tool_call("Here is a normal answer") is None


def test_context_budget_keeps_prompt_bounded(tmp_path) -> None:
    memory = SQLiteMemoryStore(tmp_path / "memory.sqlite3")
    for index in range(40):
        memory.add("user", f"memory-{index}-" + ("x" * 300), kind=MemoryKind.EPISODIC)

    llm = CaptureLLM()
    core = Primus(
        llm=llm,
        memory=memory,
        identity=Identity(name="BabyAI", owner="tester"),
        max_context_chars=2_000,
    )

    core.think("hello")

    assert len(llm.prompts) == 1
    assert len(llm.prompts[0]) <= 2_000
    assert "USER: hello" in llm.prompts[0]
    assert "memory-39-" in llm.prompts[0]


def test_context_budget_prefers_recent_memory(tmp_path) -> None:
    memory = SQLiteMemoryStore(tmp_path / "memory.sqlite3")
    for index in range(10):
        memory.add("user", f"item-{index}-" + ("y" * 200), kind=MemoryKind.EPISODIC)

    llm = CaptureLLM()
    core = Primus(
        llm=llm,
        memory=memory,
        identity=Identity(name="BabyAI", owner="tester"),
        max_context_chars=900,
    )

    core.think("budget test")
    prompt = llm.prompts[0]

    assert "item-9-" in prompt
    assert "item-0-" not in prompt
