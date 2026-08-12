from dataclasses import dataclass

from typer.testing import CliRunner

from babyai.cli import app
from babyai.identity import Identity
from babyai.memory import SQLiteMemoryStore
from babyai.primus import Primus
from babyai.working_memory import TaskState, TaskStatus, WorkingMemoryStore


@dataclass
class CaptureLLM:
    response: str = "ok"

    def __post_init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.response


def test_working_memory_persists_between_instances(tmp_path) -> None:
    path = tmp_path / "working-memory.json"
    WorkingMemoryStore(path).save(
        TaskState(goal="Build BabyAI", status=TaskStatus.PAUSED, summary="Planner is done")
    )

    loaded = WorkingMemoryStore(path).load()

    assert loaded is not None
    assert loaded.goal == "Build BabyAI"
    assert loaded.status is TaskStatus.PAUSED
    assert loaded.summary == "Planner is done"


def test_task_state_is_injected_into_primus_prompt(tmp_path) -> None:
    working = WorkingMemoryStore(tmp_path / "working-memory.json")
    working.save(TaskState(goal="Fix CI", summary="Investigate failing test"))
    llm = CaptureLLM()
    core = Primus(
        llm=llm,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(name="BabyAI", owner="tester"),
        working_memory=working,
    )

    assert core.think("continue") == "ok"
    assert "Current task state:" in llm.prompts[0]
    assert "Goal: Fix CI" in llm.prompts[0]
    assert "Working summary: Investigate failing test" in llm.prompts[0]


def test_task_cli_lifecycle(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    runner = CliRunner()

    created = runner.invoke(app, ["task", "set", "Ship MVP", "--summary", "Finish working memory"])
    shown = runner.invoke(app, ["task", "show"])
    paused = runner.invoke(app, ["task", "status", "paused"])
    cleared = runner.invoke(app, ["task", "clear"])
    empty = runner.invoke(app, ["task", "show"])

    assert created.exit_code == 0
    assert "Goal: Ship MVP" in shown.stdout
    assert "Finish working memory" in shown.stdout
    assert paused.exit_code == 0
    assert "task=paused" in paused.stdout
    assert cleared.exit_code == 0
    assert "task=none" in empty.stdout
