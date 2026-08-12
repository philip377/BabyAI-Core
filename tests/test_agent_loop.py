from __future__ import annotations

from dataclasses import dataclass

from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.primus import Primus


@dataclass
class ScriptedLLM:
    responses: list[str]

    def __post_init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.responses.pop(0)


def build_core(tmp_path, llm: ScriptedLLM) -> tuple[Primus, PermissionStore]:
    permissions = PermissionStore(tmp_path / "permissions.json")
    core = Primus(
        llm=llm,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(name="BabyAI", owner="tester"),
        agent=AgentExecutor(permissions),
    )
    return core, permissions


def test_normal_answer_does_not_execute_tool(tmp_path) -> None:
    llm = ScriptedLLM(["Just a normal answer."])
    core, _ = build_core(tmp_path, llm)

    assert core.think("hello") == "Just a normal answer."
    assert len(llm.prompts) == 1


def test_allowed_tool_is_executed_and_result_returns_to_llm(tmp_path) -> None:
    llm = ScriptedLLM([
        '{"tool":"system.info","arguments":{}}',
        "I inspected the local system.",
    ])
    core, permissions = build_core(tmp_path, llm)
    permissions.grant(Capability.SYSTEM_INFO)

    result = core.think("what system am I on?")

    assert result == "I inspected the local system."
    assert len(llm.prompts) == 2
    assert "TOOL: system.info" in llm.prompts[1]
    assert "RESULT:" in llm.prompts[1]


def test_denied_tool_never_reaches_second_model_pass(tmp_path) -> None:
    llm = ScriptedLLM(['{"tool":"system.info","arguments":{}}'])
    core, _ = build_core(tmp_path, llm)

    result = core.think("inspect the system")

    assert "Capability 'system.info' is not granted" in result
    assert len(llm.prompts) == 1


def test_unknown_tool_is_rejected(tmp_path) -> None:
    llm = ScriptedLLM(['{"tool":"shell.exec","arguments":{"command":"whoami"}}'])
    core, _ = build_core(tmp_path, llm)

    result = core.think("run whoami")

    assert "Unknown tool: shell.exec" in result
    assert len(llm.prompts) == 1


def test_file_read_requires_permission(tmp_path) -> None:
    target = tmp_path / "note.txt"
    target.write_text("secret-ish test text", encoding="utf-8")
    llm = ScriptedLLM([
        '{"tool":"filesystem.read","arguments":{"path":"%s"}}' % target.as_posix()
    ])
    core, _ = build_core(tmp_path, llm)

    result = core.think("read note")

    assert "filesystem.read" in result
    assert "not granted" in result
