from pathlib import Path


def test_windows_scripts_do_not_depend_on_console_scripts_path() -> None:
    root = Path(__file__).resolve().parents[1]
    bootstrap = (root / "scripts" / "windows" / "bootstrap.ps1").read_text(encoding="utf-8")
    run = (root / "scripts" / "windows" / "run.ps1").read_text(encoding="utf-8")

    assert "python -m babyai.setup_cli init" in bootstrap
    assert "python -m babyai.setup_cli doctor" in bootstrap
    assert "python -m babyai.desktop_commands_cli exec status" in bootstrap
    assert "Get-Command babyai-desktop" not in run
    assert "python -m babyai.desktop_commands_cli exec status" in run
