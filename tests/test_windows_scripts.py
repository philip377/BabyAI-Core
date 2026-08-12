from pathlib import Path


def test_windows_scripts_do_not_depend_on_console_scripts_path() -> None:
    root = Path(__file__).resolve().parents[1]
    bootstrap = (root / "scripts" / "windows" / "bootstrap.ps1").read_text(encoding="utf-8")
    run = (root / "scripts" / "windows" / "run.ps1").read_text(encoding="utf-8")

    assert "python -m babyai.setup_cli init" in bootstrap
    assert "python -m babyai.setup_cli doctor" in bootstrap
    assert "python -m babyai.desktop_commands_cli exec status" in bootstrap
    assert "Get-Command babyai-desktop" not in run
    assert "$env:BABYAI_PYTHON" in run
    assert "-m babyai.desktop_commands_cli exec status" in run


def test_windows_scripts_build_and_reuse_release_executable() -> None:
    root = Path(__file__).resolve().parents[1]
    bootstrap = (root / "scripts" / "windows" / "bootstrap.ps1").read_text(encoding="utf-8")
    run = (root / "scripts" / "windows" / "run.ps1").read_text(encoding="utf-8")

    assert "-c Release -p:Platform=x64" in bootstrap
    assert 'Filter "BabyAI.Desktop.exe"' in bootstrap
    assert 'Filter "BabyAI.Desktop.exe"' in run
    assert "Start-Process -FilePath $desktopExe.FullName" in run


def test_windows_desktop_bundles_windows_app_sdk_runtime() -> None:
    root = Path(__file__).resolve().parents[1]
    project = (root / "desktop" / "BabyAI.Desktop" / "BabyAI.Desktop.csproj").read_text(encoding="utf-8")

    assert "<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>" in project
