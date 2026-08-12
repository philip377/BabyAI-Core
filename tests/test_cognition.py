from __future__ import annotations

from dataclasses import dataclass

import pytest
from typer.testing import CliRunner

from babyai.cli import app
from babyai.cognition import Cognition, CognitionProtocolError, TaskProposalStore
from babyai.working_memory import TaskState, WorkingMemoryStore


@dataclass
class ScriptedLLM:
    response: str

    def generate(self, prompt: str) -> str:
        self.prompt = prompt
        return self.response


def test_cognition_creates_bounded_proposal() -> None:
    llm = ScriptedLLM('{"goal":"Ship BabyAI","status":"testing","context":"CI is green"}')
    proposal = Cognition(llm).propose(
        TaskState(goal="Ship BabyAI", status="active", context="implementing"),
        "Tests now pass",
    )
    assert proposal.goal == "Ship BabyAI"
    assert proposal.status == "testing"
    assert proposal.context == "CI is green"
    assert "Do not include reasoning" in llm.prompt


def test_cognition_rejects_extra_reasoning_field() -> None:
    llm = ScriptedLLM(
        '{"goal":"Ship","status":"active","context":"x","reasoning":"hidden"}'
    )
    with pytest.raises(CognitionProtocolError, match="schema"):
        Cognition(llm).propose(TaskState(goal="Ship"), "update")


def test_proposal_store_does_not_change_working_memory(tmp_path) -> None:
    working = WorkingMemoryStore(tmp_path / "working.json")
    working.save(TaskState(goal="Original", status="active", context="old"))
    from babyai.cognition import TaskProposal

    proposals = TaskProposalStore(tmp_path / "proposal.json")
    proposals.save(TaskProposal(goal="Original", status="done", context="new"))

    assert working.load().status == "active"
    assert proposals.load().status == "done"


def test_cli_apply_requires_explicit_action(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    runner = CliRunner()

    result = runner.invoke(app, ["task", "set", "Ship BabyAI", "--status", "active"])
    assert result.exit_code == 0

    from babyai.cognition import TaskProposal

    TaskProposalStore(tmp_path / "task_proposal.json").save(
        TaskProposal(goal="Ship BabyAI", status="done", context="green")
    )

    show_before = runner.invoke(app, ["task", "show"])
    assert "Status: active" in show_before.stdout

    applied = runner.invoke(app, ["task", "apply"])
    assert applied.exit_code == 0
    assert "Status: done" in applied.stdout

    show_after = runner.invoke(app, ["task", "show"])
    assert "Status: done" in show_after.stdout
    assert not (tmp_path / "task_proposal.json").exists()
