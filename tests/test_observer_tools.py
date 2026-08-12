from pathlib import Path

import pytest
from typer.testing import CliRunner

from babyai.cli import app
from babyai.observer import Observer
from babyai.permissions import Capability, PermissionStore
from babyai.tools import Toolset


def test_permissions_default_to_deny(tmp_path) -> None:
    store = PermissionStore(tmp_path / "permissions.json")
    assert store.list() == []
    with pytest.raises(PermissionError):
        store.require(Capability.SYSTEM_INFO)


def test_grant_and_revoke_persist(tmp_path) -> None:
    path = tmp_path / "permissions.json"
    store = PermissionStore(path)
    store.grant(Capability.FILESYSTEM_READ)
    assert PermissionStore(path).is_granted(Capability.FILESYSTEM_READ)
    store.revoke(Capability.FILESYSTEM_READ)
    assert not PermissionStore(path).is_granted(Capability.FILESYSTEM_READ)


def test_observer_requires_capability(tmp_path) -> None:
    store = PermissionStore(tmp_path / "permissions.json")
    observer = Observer(store)
    with pytest.raises(PermissionError):
        observer.system_snapshot()
    store.grant(Capability.SYSTEM_INFO)
    snapshot = observer.system_snapshot()
    assert snapshot.os_name
    assert snapshot.python_version


def test_read_file_requires_permission(tmp_path) -> None:
    permissions = PermissionStore(tmp_path / "permissions.json")
    tools = Toolset(permissions)
    target = tmp_path / "hello.txt"
    target.write_text("hello", encoding="utf-8")
    with pytest.raises(PermissionError):
        tools.read_text(target)
    permissions.grant(Capability.FILESYSTEM_READ)
    assert tools.read_text(target) == "hello"


def test_cli_permission_flow(monkeypatch, tmp_path) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path / "state"))
    runner = CliRunner()

    denied = runner.invoke(app, ["observe"])
    assert denied.exit_code == 3
    assert "not granted" in denied.output

    granted = runner.invoke(app, ["permissions", "grant", "system.info"])
    assert granted.exit_code == 0

    observed = runner.invoke(app, ["observe"])
    assert observed.exit_code == 0
    assert "os=" in observed.output
