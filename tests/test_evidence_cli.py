from typer.testing import CliRunner

from babyai.evidence_cli import app
from babyai.evidence import EvidenceStore
from babyai.hypothesis import HypothesisRecord, HypothesisStore
from babyai.config import BabyAIConfig


runner = CliRunner()


def test_evidence_cli_add_show_clear(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))

    add_result = runner.invoke(app, ["add", "The service returned HTTP 503"])
    assert add_result.exit_code == 0
    assert "total=1" in add_result.stdout

    show_result = runner.invoke(app, ["show"])
    assert show_result.exit_code == 0
    assert "HTTP 503" in show_result.stdout

    clear_result = runner.invoke(app, ["clear"])
    assert clear_result.exit_code == 0
    assert "Evidence cleared" in clear_result.stdout


def test_assess_requires_hypothesis(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    EvidenceStore(BabyAIConfig.default().evidence_file).add("Observation")
    result = runner.invoke(app, ["assess"])
    assert result.exit_code == 6
    assert "No stored hypothesis" in result.stderr


def test_assess_with_echo_fails_safely_without_mutating_hypothesis(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    monkeypatch.setenv("BABYAI_PROVIDER", "echo")
    config = BabyAIConfig.default()
    HypothesisStore(config.hypothesis_file).save(
        HypothesisRecord(claim="A", expected_result="B", test="C")
    )
    EvidenceStore(config.evidence_file).add("Observation")

    result = runner.invoke(app, ["assess"])
    assert result.exit_code == 6
    stored = HypothesisStore(config.hypothesis_file).load()
    assert stored is not None
    assert stored.status == "pending"
