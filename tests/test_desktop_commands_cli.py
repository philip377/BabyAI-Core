from typer.testing import CliRunner

from babyai.desktop_commands_cli import app


runner = CliRunner()


def test_desktop_exec_remains_an_explicit_subcommand() -> None:
    result = runner.invoke(app, ["exec", "unsupported.command"])

    assert result.exit_code == 8
    assert "Unsupported desktop command: unsupported.command" in result.output
