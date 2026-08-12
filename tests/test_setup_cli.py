from __future__ import annotations

from pathlib import Path

from typer.testing import CliRunner

from babyai.config import BabyAIConfig
from babyai.diagnostics import initialize_data_dir, run_local_diagnostics
from babyai.permissions import Capability, PermissionStore
from babyai.setup_cli import app


runner = CliRunner()


def config_for(tmp_path: Path) -> BabyAIConfig:
    return BabyAIConfig(data_dir=tmp_path, provider="echo")


def test_initialize_creates_identity_and_memory_without_permissions(tmp_path: Path) -> None:
    config = config_for(tmp_path)
    initialize_data_dir(config)

    assert config.data_dir.exists()
    assert config.identity_file.exists()
    assert config.memory_db.exists()
    permissions = PermissionStore(config.permissions_file)
    assert permissions.list() == []
    for capability in Capability:
        assert not permissions.is_granted(capability)


def test_local_diagnostics_report_ok_on_fresh_directory(tmp_path: Path) -> None:
    report = run_local_diagnostics(config_for(tmp_path))
    assert report.ok
    assert {check.name for check in report.checks} == {
        "data_dir",
        "identity",
        "memory",
        "permissions",
    }


def test_setup_init_and_doctor_smoke(monkeypatch, tmp_path: Path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path))
    monkeypatch.setenv("BABYAI_PROVIDER", "echo")

    init_result = runner.invoke(app, ["init"])
    assert init_result.exit_code == 0
    assert "Permissions remain denied by default." in init_result.stdout

    doctor_result = runner.invoke(app, ["doctor"])
    assert doctor_result.exit_code == 0
    assert "overall=ok" in doctor_result.stdout
    assert "brain=ok" in doctor_result.stdout
