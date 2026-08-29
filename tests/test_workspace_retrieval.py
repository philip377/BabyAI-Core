from __future__ import annotations

import json

from typer.testing import CliRunner

from babyai.config import BabyAIConfig
from babyai.identity import Identity
from babyai.llm import LLMProvider
from babyai.memory import SessionMemoryStore, SQLiteMemoryStore
from babyai.permissions import Capability, PermissionStore
from babyai.workspace import WorkspaceStore
from babyai.workspace_context import WorkspacePrimus
from babyai.workspace_documents import WorkspaceDocumentStore
from babyai.workspace_retrieval import WorkspaceRetrievalStore
from babyai.workspace_retrieval_cli import app


class CapturingProvider(LLMProvider):
    def __init__(self, reply: str = "ok") -> None:
        self.reply = reply
        self.prompts: list[str] = []

    def generate(self, prompt: str) -> str:
        self.prompts.append(prompt)
        return self.reply


def _workspace(config: BabyAIConfig, name: str = "Alpha"):
    store = WorkspaceStore(config.workspace_file)
    workspace = store.create(name)
    store.select(workspace.id)
    return workspace


def _documents(config: BabyAIConfig, workspace):
    return WorkspaceDocumentStore(
        config.workspace_documents_dir / f"{workspace.id}.json",
        workspace.id,
    )


def _retrieval(config: BabyAIConfig, workspace):
    return WorkspaceRetrievalStore(
        config.workspace_retrieval_dir / f"{workspace.id}.json",
        workspace.id,
    )


def test_retrieval_chunks_are_bounded_and_search_is_deterministic(tmp_path) -> None:
    store = WorkspaceRetrievalStore(tmp_path / "index.json", "alpha")
    content = (
        ("Vulkan acceleration keeps local inference responsive. " * 25)
        + ("Inventory reconciliation belongs to the business database. " * 25)
    )
    indexed = store.ingest(
        document_id="doc-1",
        document_name="Architecture",
        path=str(tmp_path / "architecture.md"),
        content=content,
    )

    assert len(indexed.chunks) >= 2
    assert all(len(chunk) <= store.CHUNK_CHARS for chunk in indexed.chunks)

    hits = store.search("Vulkan acceleration", limit=3)
    assert hits
    assert hits[0].document_id == "doc-1"
    assert "Vulkan acceleration" in hits[0].text

    first = [(item.chunk_index, item.score) for item in hits]
    second = [
        (item.chunk_index, item.score)
        for item in store.search("Vulkan acceleration", limit=3)
    ]
    assert first == second


def test_cli_ingestion_reuses_filesystem_read_permission(tmp_path, monkeypatch) -> None:
    data_dir = tmp_path / "data"
    monkeypatch.setenv("BABYAI_DATA_DIR", str(data_dir))
    config = BabyAIConfig.default()
    workspace = _workspace(config)
    source = tmp_path / "requirements.md"
    source.write_text(
        "UNIX retrieval keeps only explicitly ingested Workspace documents.",
        encoding="utf-8",
    )
    document = _documents(config, workspace).add(source)

    runner = CliRunner()
    denied = runner.invoke(app, ["ingest", document.id])
    assert denied.exit_code == 2
    assert Capability.FILESYSTEM_READ.value in denied.output
    assert not (config.workspace_retrieval_dir / f"{workspace.id}.json").exists()

    PermissionStore(config.permissions_file).grant(Capability.FILESYSTEM_READ)
    ingested = runner.invoke(app, ["ingest", document.id])
    assert ingested.exit_code == 0
    payload = json.loads(ingested.output)
    assert payload["ok"] is True
    assert payload["document"]["id"] == document.id
    assert payload["document"]["chunks"] >= 1

    searched = runner.invoke(app, ["search", "explicitly ingested"])
    assert searched.exit_code == 0
    hits = json.loads(searched.output)["hits"]
    assert hits
    assert hits[0]["document_id"] == document.id


def test_workspace_prompt_retrieves_only_registered_active_workspace_data(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    alpha = _workspace(config, "Alpha")
    alpha_documents = _documents(config, alpha)

    source = tmp_path / "alpha.md"
    source.write_text("Alpha launch code is ORBIT-742.", encoding="utf-8")
    registered = alpha_documents.add(source)
    retrieval = _retrieval(config, alpha)
    retrieval.ingest(
        document_id=registered.id,
        document_name=registered.name,
        path=registered.path,
        content=source.read_text(encoding="utf-8"),
    )
    retrieval.ingest(
        document_id="not-registered",
        document_name="Hidden",
        path=str(tmp_path / "hidden.md"),
        content="Alpha launch code is SECRET-999.",
    )

    # Retrieval uses the explicit persisted snapshot. The original source is not
    # reopened while answering a chat message.
    source.unlink()

    provider = CapturingProvider()
    core = WorkspacePrimus(
        llm=provider,
        memory=SQLiteMemoryStore(config.memory_db),
        identity=Identity(),
        session_memory=SessionMemoryStore(),
        workspace=alpha,
    )

    assert core.think("Какой Alpha launch code?") == "ok"
    prompt = provider.prompts[-1]
    assert "Workspace document excerpts" in prompt
    assert "untrusted reference data" in prompt
    assert "ORBIT-742" in prompt
    assert "SECRET-999" not in prompt


def test_removed_document_is_no_longer_retrievable_from_cached_snapshot(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    workspace = _workspace(config)
    documents = _documents(config, workspace)
    source = tmp_path / "note.txt"
    source.write_text("Project codename is HORIZON-18.", encoding="utf-8")
    document = documents.add(source)
    retrieval = _retrieval(config, workspace)
    retrieval.ingest(
        document_id=document.id,
        document_name=document.name,
        path=document.path,
        content=source.read_text(encoding="utf-8"),
    )

    documents.remove(document.id)
    assert retrieval.search("HORIZON-18")
    allowed_ids = {item.id for item in documents.list()}
    assert retrieval.search("HORIZON-18", allowed_document_ids=allowed_ids) == []

    provider = CapturingProvider()
    core = WorkspacePrimus(
        llm=provider,
        memory=SQLiteMemoryStore(config.memory_db),
        identity=Identity(),
        session_memory=SessionMemoryStore(),
        workspace=workspace,
    )
    core.think("HORIZON-18")
    prompt = provider.prompts[-1]
    assert "Workspace document excerpts" not in prompt
    assert "Project codename is HORIZON-18." not in prompt


def test_retrieval_indexes_are_isolated_by_workspace(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    workspaces = WorkspaceStore(config.workspace_file)
    alpha = workspaces.create("Alpha")
    beta = workspaces.create("Beta")

    _retrieval(config, alpha).ingest(
        document_id="alpha-doc",
        document_name="Alpha notes",
        path="/alpha.txt",
        content="Aurora token belongs only to Alpha.",
    )
    _retrieval(config, beta).ingest(
        document_id="beta-doc",
        document_name="Beta notes",
        path="/beta.txt",
        content="Nebula token belongs only to Beta.",
    )

    assert _retrieval(config, alpha).search("Aurora")
    assert _retrieval(config, alpha).search("Nebula") == []
    assert _retrieval(config, beta).search("Nebula")
    assert _retrieval(config, beta).search("Aurora") == []


def test_retrieval_cache_corruption_does_not_break_primary_chat(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path / "data", provider="echo")
    workspace = _workspace(config)
    source = tmp_path / "note.txt"
    source.write_text("retrieval content", encoding="utf-8")
    _documents(config, workspace).add(source)

    index_path = config.workspace_retrieval_dir / f"{workspace.id}.json"
    index_path.parent.mkdir(parents=True, exist_ok=True)
    index_path.write_text("{not-json", encoding="utf-8")

    provider = CapturingProvider()
    core = WorkspacePrimus(
        llm=provider,
        memory=SQLiteMemoryStore(config.memory_db),
        identity=Identity(),
        session_memory=SessionMemoryStore(),
        workspace=workspace,
    )

    assert core.think("retrieval content") == "ok"
    assert "Workspace document excerpts" not in provider.prompts[-1]


def test_config_reserves_workspace_retrieval_location(tmp_path) -> None:
    config = BabyAIConfig(data_dir=tmp_path)
    assert config.workspace_retrieval_dir == tmp_path / "workspace_retrieval"
