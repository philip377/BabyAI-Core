from typer.testing import CliRunner

from babyai.cli import app


def test_chat_cli_still_smokes_with_echo_provider(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_PROVIDER", "echo")
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    result = CliRunner().invoke(app, ["chat", "hello"])
    assert result.exit_code == 0
    assert "[echo]" in result.stdout
