from __future__ import annotations

import json

from typer.testing import CliRunner

from babyai.config import BabyAIConfig
from babyai.workspace import WorkspaceStore
from babyai.workspace_cli import app


runner = CliRunner()


def test_workspace_store_persists_active_root_metadata_without_reading_files(tmp_path) -> None:
    root = tmp_path / "UNIX"
    root.mkdir()
    (root / "private.txt").write_text("do not read me", encoding="utf-8")
    store = WorkspaceStore(tmp_path / "workspaces.json")

    created = store.create("UNIX Core", root=root)
    assert store.active() is None

    selected = store.select(created.id)
    assert selected == created
    assert store.active() == created
    assert selected.root == str(root.resolve())
    assert "do not read me" not in selected.as_context()
    assert "does not grant filesystem access" in selected.as_context()

    payload = json.loads((tmp_path / "workspaces.json").read_text(encoding="utf-8"))
    assert payload["active_id"] == created.id
    assert payload["workspaces"][0]["name"] == "UNIX Core"


def test_workspace_names_are_unique_case_insensitively(tmp_path) -> None:
    store = WorkspaceStore(tmp_path / "workspaces.json")
    store.create("Project Alpha")

    try:
        store.create("project alpha")
    except ValueError as exc:
        assert "already exists" in str(exc)
    else:
        raise AssertionError("duplicate workspace name was accepted")


def test_workspace_root_must_be_an_existing_directory(tmp_path) -> None:
    store = WorkspaceStore(tmp_path / "workspaces.json")
    missing = tmp_path / "missing"

    try:
        store.create("Missing", root=missing)
    except FileNotFoundError:
        pass
    else:
        raise AssertionError("missing workspace root was accepted")

    file_root = tmp_path / "file.txt"
    file_root.write_text("x", encoding="utf-8")
    try:
        store.create("File", root=file_root)
    except NotADirectoryError:
        pass
    else:
        raise AssertionError("file workspace root was accepted")


def test_workspace_cli_create_select_list_and_clear(tmp_path, monkeypatch) -> None:
    monkeypatch.setenv("BABYAI_DATA_DIR", str(tmp_path / "data"))
    root = tmp_path / "project"
    root.mkdir()

    created = runner.invoke(app, ["create", "Demo", "--root", str(root)])
    assert created.exit_code == 0
    assert "Created workspace: Demo" in created.stdout
    assert "active=true" in created.stdout

    current = runner.invoke(app, ["current"])
    assert current.exit_code == 0
    assert "Name: Demo" in current.stdout
    assert "metadata" in current.stdout

    listed = runner.invoke(app, ["list"])
    assert listed.exit_code == 0
    assert "* Demo" in listed.stdout

    cleared = runner.invoke(app, ["clear"])
    assert cleared.exit_code == 0
    assert "cleared" in cleared.stdout.lower()
    assert WorkspaceStore(BabyAIConfig.default().workspace_file).active() is None


def test_config_reserves_workspace_state_and_task_locations(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path)

    assert config.workspace_file == tmp_path / "workspaces.json"
    assert config.workspace_tasks_dir == tmp_path / "workspace_tasks"
