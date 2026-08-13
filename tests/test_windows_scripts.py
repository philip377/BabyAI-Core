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


def test_windows_start_bootstraps_only_when_needed_then_runs() -> None:
    root = Path(__file__).resolve().parents[1]
    start = (root / "scripts" / "windows" / "start.ps1").read_text(encoding="utf-8")

    assert 'Join-Path $PSScriptRoot "bootstrap.ps1"' in start
    assert 'Join-Path $PSScriptRoot "run.ps1"' in start
    assert '-c "import babyai"' in start
    assert 'Filter "BabyAI.Desktop.exe"' in start
    assert "& $bootstrap -Provider $Provider -NativeModel $NativeModel -NativeRuntime $NativeRuntime" in start
    assert "& $run -Provider $Provider -NativeModel $NativeModel -NativeRuntime $NativeRuntime" in start
    assert "babyai-desktop" not in start
    assert "babyai-setup" not in start


def test_windows_launchers_accept_native_without_downloading_a_model() -> None:
    root = Path(__file__).resolve().parents[1]
    scripts = [
        (root / "scripts" / "windows" / name).read_text(encoding="utf-8")
        for name in ("start.ps1", "bootstrap.ps1", "run.ps1")
    ]

    for script in scripts:
        assert 'ValidateSet("echo", "ollama", "native")' in script
        assert "BABYAI_NATIVE_MODEL" in script
        assert "BABYAI_NATIVE_RUNTIME" in script

    bootstrap = scripts[1]
    assert "BabyAI will not download a model automatically" in bootstrap
    assert "ollama pull" in bootstrap
    native_block = bootstrap.split('if ($Provider -eq "native")', 1)[1]
    assert "ollama pull" not in native_block


def test_windows_native_smoke_uses_normal_desktop_chat_path() -> None:
    root = Path(__file__).resolve().parents[1]
    smoke = (root / "scripts" / "windows" / "native-smoke.ps1").read_text(encoding="utf-8")

    assert '$env:BABYAI_PROVIDER = "native"' in smoke
    assert "BABYAI_NATIVE_MODEL" in smoke
    assert "BABYAI_NATIVE_RUNTIME" in smoke
    assert "-m babyai.desktop_commands_cli exec status" in smoke
    assert "-m babyai.desktop_commands_cli exec chat --payload $payload" in smoke
    assert "ConvertTo-Json -Compress" in smoke
    assert "[BabyAI native smoke] PASS" in smoke
    assert "Invoke-WebRequest" not in smoke
    assert "Invoke-RestMethod" not in smoke
    assert "Start-Process" not in smoke


def test_windows_diagnostics_reports_health_without_reading_private_state() -> None:
    root = Path(__file__).resolve().parents[1]
    diagnose = (root / "scripts" / "windows" / "diagnose.ps1").read_text(encoding="utf-8")

    assert "babyai.setup_cli doctor --skip-brain" in diagnose
    assert "babyai.desktop_commands_cli exec status" in diagnose
    assert "bridge.snapshot.schema_version" in diagnose
    assert 'Filter "BabyAI.Desktop.exe"' in diagnose
    assert "privacy_note=" in diagnose
    assert "memory.sqlite" not in diagnose
    assert "working_memory" not in diagnose
    assert "identity.json" not in diagnose


def test_windows_desktop_bundles_windows_app_sdk_runtime() -> None:
    root = Path(__file__).resolve().parents[1]
    project = (root / "desktop" / "BabyAI.Desktop" / "BabyAI.Desktop.csproj").read_text(encoding="utf-8")

    assert "<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>" in project
