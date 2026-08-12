from dataclasses import dataclass

from typer.testing import CliRunner

from babyai.cli import app
from babyai.identity import Identity
from babyai.memory import SQLiteMemoryStore
from babyai.primus import Primus
from babyai.working_memory import TaskState, WorkingMemoryStore


@dataclass
class RecordingLLM:
    response: str = "ok"

    def __post_init__(self) -> None:
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.response


def test_task_state_persists_between_store_instances(tmp_path) -> None:
    path = tmp_path / "working_memory.json"
    WorkingMemoryStore(path).save(TaskState(goal="ship v0.1", status="active", context="fix tests"))
    loaded = WorkingMemoryStore(path).load()
    assert loaded is not None
    assert loaded.goal == "ship v0.1"
    assert loaded.status == "active"
    assert loaded.context == "fix tests"


def test_task_state_is_injected_into_primus_prompt(tmp_path) -> None:
    llm = RecordingLLM()
    working = WorkingMemoryStore(tmp_path / "working_memory.json")
    working.save(TaskState(goal="build planner", context="keep it bounded"))
    core = Primus(
        llm=llm,
        memory=SQLiteMemoryStore(tmp_path / "memory.sqlite3"),
        identity=Identity(name="BabyAI", owner="tester"),
        working_memory=working,
    )
    assert core.think("continue") == "ok"
    assert "Current task:" in llm.prompts[0]
    assert "Goal: build planner" in llm.prompts[0]
    assert "Context: keep it bounded" in llm.prompts[0]


def test_task_clear_removes_state(tmp_path) -> None:
    store = WorkingMemoryStore(tmp_path / "working_memory.json")
    store.save(TaskState(goal="temporary"))
    store.clear()
    assert store.load() is None


def test_task_cli_roundtrip(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    runner = CliRunner()
    set_result = runner.invoke(app, ["task", "set", "finish milestone", "--context", "green CI"])
    assert set_result.exit_code == 0
    show_result = runner.invoke(app, ["task", "show"])
    assert show_result.exit_code == 0
    assert "Goal: finish milestone" in show_result.stdout
    assert "Context: green CI" in show_result.stdout
    clear_result = runner.invoke(app, ["task", "clear"])
    assert clear_result.exit_code == 0
    assert "Task cleared" in clear_result.stdout
