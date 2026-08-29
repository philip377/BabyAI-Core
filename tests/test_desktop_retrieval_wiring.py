from __future__ import annotations

from pathlib import Path

from babyai.config import BabyAIConfig
from babyai.desktop_commands import DesktopCommandError
from babyai.permissions import Capability, PermissionStore
from babyai.workspace_desktop_retrieval import WorkspaceDesktopCommands


def _create_and_select(commands: WorkspaceDesktopCommands, name: str) -> dict[str, object]:
    workspace = commands.execute("workspace.create", {"name": name})["workspace"]
    commands.execute("workspace.select", {"id": workspace["id"]})
    return workspace


def _add_note(
    commands: WorkspaceDesktopCommands,
    path: Path,
    content: str,
    *,
    name: str,
) -> dict[str, object]:
    path.write_text(content, encoding="utf-8")
    return commands.execute(
        "document.add",
        {"path": str(path), "name": name},
    )["document"]


def test_desktop_ingest_search_and_status_use_existing_read_permission(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    _create_and_select(commands, "Alpha")
    document = _add_note(
        commands,
        tmp_path / "alpha.md",
        "Project Falcon uses the copper protocol for the launch sequence.",
        name="Alpha Notes",
    )

    try:
        commands.execute("document.ingest", {"id": document["id"]})
    except DesktopCommandError as exc:
        assert Capability.FILESYSTEM_READ.value in str(exc)
    else:
        raise AssertionError("document.ingest bypassed filesystem.read")

    PermissionStore(config.permissions_file).grant(Capability.FILESYSTEM_READ)
    ingested = commands.execute("document.ingest", {"id": document["id"]})
    assert ingested["document"]["id"] == document["id"]
    assert ingested["document"]["chunks"] == 1

    hits = commands.execute(
        "document.search",
        {"query": "What protocol does Project Falcon use?"},
    )["hits"]
    assert hits
    assert hits[0]["document_id"] == document["id"]
    assert "copper protocol" in hits[0]["text"]

    state = commands.execute("document.retrieval_status")
    assert state["document_count"] == 1
    assert state["chunk_count"] == 1

    snapshot = commands.execute("status")["snapshot"]
    assert snapshot["retrieval"] == {
        "healthy": True,
        "document_count": 1,
        "chunk_count": 1,
    }


def test_desktop_retrieval_isolated_to_active_workspace(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    permissions = PermissionStore(config.permissions_file)
    permissions.grant(Capability.FILESYSTEM_READ)

    alpha = _create_and_select(commands, "Alpha")
    alpha_document = _add_note(
        commands,
        tmp_path / "alpha.txt",
        "Alpha stores the launch phrase emerald lantern.",
        name="Alpha Secret",
    )
    commands.execute("document.ingest", {"id": alpha_document["id"]})

    beta = commands.execute("workspace.create", {"name": "Beta"})["workspace"]
    commands.execute("workspace.select", {"id": beta["id"]})
    assert commands.execute(
        "document.search",
        {"query": "emerald lantern"},
    )["hits"] == []
    assert commands.execute("document.retrieval_status")["document_count"] == 0

    commands.execute("workspace.select", {"id": alpha["id"]})
    assert commands.execute(
        "document.search",
        {"query": "emerald lantern"},
    )["hits"]


def test_document_remove_purges_healthy_retrieval_chunks(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    PermissionStore(config.permissions_file).grant(Capability.FILESYSTEM_READ)
    _create_and_select(commands, "Alpha")
    document = _add_note(
        commands,
        tmp_path / "remove-me.txt",
        "The removable document contains violet engine telemetry.",
        name="Disposable",
    )
    commands.execute("document.ingest", {"id": document["id"]})

    removed = commands.execute("document.remove", {"id": document["id"]})
    assert removed["retrieval"] == {"removed": True, "cache_reset": False}
    assert commands.execute("document.retrieval_status") == {
        "ok": True,
        "command": "document.retrieval_status",
        "document_count": 0,
        "chunk_count": 0,
    }


def test_document_remove_resets_corrupt_derived_cache_without_blocking(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    commands = WorkspaceDesktopCommands(config)
    workspace = _create_and_select(commands, "Alpha")
    document = _add_note(
        commands,
        tmp_path / "corrupt.txt",
        "Source metadata must remain removable even if retrieval cache is broken.",
        name="Corrupt Cache Test",
    )

    cache = config.workspace_retrieval_dir / f"{workspace['id']}.json"
    cache.parent.mkdir(parents=True, exist_ok=True)
    cache.write_text("{ definitely-not-json", encoding="utf-8")

    removed = commands.execute("document.remove", {"id": document["id"]})
    assert removed["document"]["id"] == document["id"]
    assert removed["retrieval"] == {"removed": False, "cache_reset": True}
    assert not cache.exists()
    assert commands.execute("document.list")["documents"] == []


def test_desktop_retrieval_commands_require_active_workspace(tmp_path) -> None:
    commands = WorkspaceDesktopCommands(
        BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    )
    for command, payload in (
        ("document.ingest", {"id": "missing"}),
        ("document.search", {"query": "anything"}),
        ("document.retrieval_status", {}),
    ):
        try:
            commands.execute(command, payload)
        except DesktopCommandError as exc:
            assert "active workspace" in str(exc)
        else:
            raise AssertionError(f"{command} worked without an active workspace")


def test_desktop_entrypoints_use_retrieval_wired_workspace_surface() -> None:
    root = Path(__file__).resolve().parents[1]
    worker = (root / "src" / "babyai" / "desktop_worker.py").read_text(
        encoding="utf-8"
    )
    cli = (root / "src" / "babyai" / "desktop_commands_cli.py").read_text(
        encoding="utf-8"
    )
    assert "workspace_desktop_retrieval" in worker
    assert "workspace_desktop_retrieval" in cli
    assert "WorkspaceDesktopCommands as DesktopCommands" in worker
    assert "WorkspaceDesktopCommands as DesktopCommands" in cli
