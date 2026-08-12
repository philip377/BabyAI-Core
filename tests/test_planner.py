from __future__ import annotations

from dataclasses import dataclass

from babyai.agent import AgentExecutor
from babyai.identity import Identity
from babyai.memory import SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.planner import Planner
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
        planner=Planner(),
    )
    return core, permissions


def test_planner_direct_answer_skips_tool_execution(tmp_path) -> None:
    llm = ScriptedLLM([
        '{"intent":"answer greeting","action":"answer"}',
        "Hello there.",
    ])
    core, _ = build_core(tmp_path, llm)

    result = core.think("hello")

    assert result == "Hello there."
    assert len(llm.prompts) == 2
    assert "Intent: answer greeting" in llm.prompts[1]
    assert "Do not call a tool" in llm.prompts[1]


def test_planner_tool_path_allows_one_tool(tmp_path) -> None:
    llm = ScriptedLLM([
        '{"intent":"inspect local system","action":"tool"}',
        '{"tool":"system.info","arguments":{}}',
        "You are on the inspected system.",
    ])
    core, permissions = build_core(tmp_path, llm)
    permissions.grant(Capability.SYSTEM_INFO)

    result = core.think("what system is this?")

    assert result == "You are on the inspected system."
    assert len(llm.prompts) == 3
    assert "Intent: inspect local system" in llm.prompts[1]
    assert "TOOL: system.info" in llm.prompts[2]


def test_bad_plan_falls_back_to_existing_agent_loop(tmp_path) -> None:
    llm = ScriptedLLM([
        "not-json",
        '{"tool":"system.info","arguments":{}}',
        "Fallback tool path worked.",
    ])
    core, permissions = build_core(tmp_path, llm)
    permissions.grant(Capability.SYSTEM_INFO)

    result = core.think("inspect system")

    assert result == "Fallback tool path worked."
    assert len(llm.prompts) == 3


def test_planner_schema_does_not_accept_reasoning_field() -> None:
    planner = Planner()
    try:
        planner.parse('{"intent":"x","action":"answer","reasoning":"hidden"}')
    except ValueError as exc:
        assert "invalid schema" in str(exc)
    else:
        raise AssertionError("extra planner fields must be rejected")
