import json

import pytest
from typer.testing import CliRunner

from babyai.autodidact import Autodidact, AutodidactProtocolError, LessonCandidate, LessonCandidateStore
from babyai.config import BabyAIConfig
from babyai.learn_cli import app
from babyai.memory import MemoryKind, SQLiteMemoryStore


class StubLLM:
    def __init__(self, response: str) -> None:
        self.response = response

    def generate(self, prompt: str) -> str:
        return self.response


def test_parse_strict_lesson_schema() -> None:
    candidate = Autodidact.parse(json.dumps({
        "knowledge": "Service X requires token Y.",
        "rationale": "Repeated failures disappeared after token Y was supplied.",
        "source": "explicit observation",
    }))
    assert candidate.knowledge == "Service X requires token Y."

    with pytest.raises(AutodidactProtocolError):
        Autodidact.parse(json.dumps({
            "knowledge": "x",
            "rationale": "y",
            "source": "z",
            "extra": "no",
        }))


def test_candidate_store_does_not_write_memory(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path)
    LessonCandidateStore(config.lesson_candidate_file).save(
        LessonCandidate("Reusable fact", "Supported", "conversation")
    )
    memory = SQLiteMemoryStore(config.memory_db)
    assert memory.recent(kind=MemoryKind.KNOWLEDGE) == []


def test_approve_writes_one_knowledge_record(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    config = BabyAIConfig.default()
    LessonCandidateStore(config.lesson_candidate_file).save(
        LessonCandidate("Reusable fact", "Supported", "conversation")
    )

    result = CliRunner().invoke(app, ["approve"])
    assert result.exit_code == 0
    records = SQLiteMemoryStore(config.memory_db).recent(kind=MemoryKind.KNOWLEDGE)
    assert len(records) == 1
    assert records[0].content == "Reusable fact"
    assert LessonCandidateStore(config.lesson_candidate_file).load() is None


def test_second_approve_cannot_duplicate_memory(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    config = BabyAIConfig.default()
    LessonCandidateStore(config.lesson_candidate_file).save(
        LessonCandidate("Reusable fact", "Supported", "conversation")
    )
    runner = CliRunner()
    assert runner.invoke(app, ["approve"]).exit_code == 0
    second = runner.invoke(app, ["approve"])
    assert second.exit_code == 7
    assert len(SQLiteMemoryStore(config.memory_db).recent(kind=MemoryKind.KNOWLEDGE)) == 1


def test_reject_discards_candidate_without_learning(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    config = BabyAIConfig.default()
    LessonCandidateStore(config.lesson_candidate_file).save(
        LessonCandidate("Do not learn", "Unsupported", "conversation")
    )
    result = CliRunner().invoke(app, ["reject"])
    assert result.exit_code == 0
    assert LessonCandidateStore(config.lesson_candidate_file).load() is None
    assert SQLiteMemoryStore(config.memory_db).recent(kind=MemoryKind.KNOWLEDGE) == []
