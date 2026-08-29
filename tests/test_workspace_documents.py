from __future__ import annotations

import json

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError
from babyai.permissions import Capability, PermissionStore
from babyai.workspace_desktop_commands import WorkspaceDesktopCommands


def _create_and_select(commands: WorkspaceDesktopCommands, name: str) -> dict[str, object]:
    workspace = commands.execute("workspace.create", {"name": name})["workspace"]
    commands.execute("workspace.select", {"id": workspace["id"]})
    return workspace


def test_document_commands_require_an_active_workspace(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    note = tmp_path / "note.txt"
    note.write_text("hello", encoding="utf-8")

    for command, payload in (
        ("document.add", {"path": str(note)}),
        ("document.list", {}),
        ("document.get", {"id": "missing"}),
        ("document.read", {"id": "missing"}),
        ("document.remove", {"id": "missing"}),
    ):
        try:
            commands.execute(command, payload)
        except DesktopCommandError as exc:
            assert "active workspace" in str(exc)
        else:
            raise AssertionError(f"{command} worked without an active workspace")


def test_registering_document_does_not_grant_content_access(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    workspace = _create_and_select(commands, "Alpha")
    note = tmp_path / "requirements.md"
    note.write_text("Alpha secret requirements", encoding="utf-8")

    added = commands.execute(
        "document.add",
        {"path": str(note), "name": "Requirements"},
    )["document"]

    assert added["workspace_id"] == workspace["id"]
    assert added["name"] == "Requirements"
    assert added["path"] == str(note.resolve())
    assert added["size_bytes"] == len("Alpha secret requirements".encode("utf-8"))
    assert PermissionStore(config.permissions_file).list() == []

    listed = commands.execute("document.list")["documents"]
    assert [item["id"] for item in listed] == [added["id"]]

    try:
        commands.execute("document.read", {"id": added["id"]})
    except DesktopCommandError as exc:
        assert Capability.FILESYSTEM_READ.value in str(exc)
    else:
        raise AssertionError("Registered document content was readable without permission")

    PermissionStore(config.permissions_file).grant(Capability.FILESYSTEM_READ)
    result = commands.execute("document.read", {"id": added["id"]})
    assert result["content"] == "Alpha secret requirements"


def test_documents_are_isolated_by_active_workspace(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    alpha = _create_and_select(commands, "Alpha")
    alpha_note = tmp_path / "alpha.txt"
    alpha_note.write_text("alpha", encoding="utf-8")
    alpha_document = commands.execute(
        "document.add", {"path": str(alpha_note)}
    )["document"]

    beta = commands.execute("workspace.create", {"name": "Beta"})["workspace"]
    commands.execute("workspace.select", {"id": beta["id"]})
    assert commands.execute("document.list")["documents"] == []

    beta_note = tmp_path / "beta.txt"
    beta_note.write_text("beta", encoding="utf-8")
    beta_document = commands.execute(
        "document.add", {"path": str(beta_note)}
    )["document"]
    assert beta_document["workspace_id"] == beta["id"]

    try:
        commands.execute("document.get", {"id": alpha_document["id"]})
    except DesktopCommandError as exc:
        assert "active workspace" in str(exc)
    else:
        raise AssertionError("Beta resolved an Alpha document id")

    commands.execute("workspace.select", {"id": alpha["id"]})
    alpha_list = commands.execute("document.list")["documents"]
    assert [item["id"] for item in alpha_list] == [alpha_document["id"]]
    assert all(item["id"] != beta_document["id"] for item in alpha_list)


def test_document_registry_does_not_scan_workspace_root(tmp_path) -> None:
    project_root = tmp_path / "project"
    project_root.mkdir()
    registered = project_root / "registered.txt"
    registered.write_text("registered", encoding="utf-8")
    unregistered = project_root / "unregistered.txt"
    unregistered.write_text("must stay invisible", encoding="utf-8")

    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    workspace = commands.execute(
        "workspace.create",
        {"name": "Project", "root": str(project_root)},
    )["workspace"]
    commands.execute("workspace.select", {"id": workspace["id"]})
    commands.execute("document.add", {"path": str(registered)})

    listed = commands.execute("document.list")["documents"]
    assert [item["path"] for item in listed] == [str(registered.resolve())]
    assert all(item["path"] != str(unregistered.resolve()) for item in listed)


def test_document_read_is_bounded_to_text_foundation(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    _create_and_select(commands, "Alpha")
    binary = tmp_path / "manual.pdf"
    binary.write_bytes(b"%PDF-foundation-does-not-parse-this-yet")
    document = commands.execute("document.add", {"path": str(binary)})["document"]
    PermissionStore(config.permissions_file).grant(Capability.FILESYSTEM_READ)

    try:
        commands.execute("document.read", {"id": document["id"]})
    except DesktopCommandError as exc:
        assert "text-based file types" in str(exc)
    else:
        raise AssertionError("Binary document was decoded as text")


def test_document_status_and_remove_touch_registry_only(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    workspace = _create_and_select(commands, "Alpha")
    note = tmp_path / "note.txt"
    note.write_text("keep the original file", encoding="utf-8")
    document = commands.execute("document.add", {"path": str(note)})["document"]

    status = commands.execute("status")["snapshot"]
    assert status["documents"]["count"] == 1

    removed = commands.execute("document.remove", {"id": document["id"]})["document"]
    assert removed["id"] == document["id"]
    assert note.read_text(encoding="utf-8") == "keep the original file"
    assert commands.execute("document.list")["documents"] == []
    registry = config.workspace_documents_dir / f"{workspace['id']}.json"
    payload = json.loads(registry.read_text(encoding="utf-8"))
    assert payload["workspace_id"] == workspace["id"]
    assert payload["documents"] == []
