from dataclasses import dataclass

import pytest
from typer.testing import CliRunner

from babyai.cli import app
from babyai.hypothesis import Hypothesis, HypothesisProtocolError, HypothesisRecord, HypothesisStore


@dataclass
class ScriptedLLM:
    response: str

    def generate(self, prompt: str) -> str:
        return self.response


def test_hypothesis_parses_strict_schema() -> None:
    record = Hypothesis(ScriptedLLM('{"claim":"A","expected_result":"B","test":"C"}')).propose("q")
    assert record.claim == "A"
    assert record.status == "pending"


def test_hypothesis_rejects_extra_fields() -> None:
    with pytest.raises(HypothesisProtocolError):
        Hypothesis.parse('{"claim":"A","expected_result":"B","test":"C","reasoning":"hidden"}')


def test_hypothesis_store_is_inert_until_status_changed(tmp_path) -> None:
    store = HypothesisStore(tmp_path / "hypothesis.json")
    store.save(HypothesisRecord("A", "B", "C"))
    assert store.load().status == "pending"
    assert store.set_status("confirmed").status == "confirmed"


def test_hypothesis_cli_show_and_clear(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    HypothesisStore(tmp_path / "hypothesis.json").save(HypothesisRecord("A", "B", "C"))
    runner = CliRunner()
    shown = runner.invoke(app, ["hypothesis", "show"])
    assert shown.exit_code == 0
    assert "Claim: A" in shown.stdout
    cleared = runner.invoke(app, ["hypothesis", "clear"])
    assert cleared.exit_code == 0
    assert not (tmp_path / "hypothesis.json").exists()
