from __future__ import annotations

import json
from dataclasses import asdict

import typer

from .config import BabyAIConfig
from .permissions import PermissionStore
from .workspace import WorkspaceRecord, WorkspaceStore
from .workspace_documents import WorkspaceDocumentStore
from .workspace_retrieval import WorkspaceRetrievalStore


app = typer.Typer(
    help="Ingest and search explicitly registered UNIX Workspace documents."
)


def _stores() -> tuple[
    BabyAIConfig,
    WorkspaceRecord,
    WorkspaceDocumentStore,
    WorkspaceRetrievalStore,
]:
    config = BabyAIConfig.default()
    workspace = WorkspaceStore(config.workspace_file).active()
    if workspace is None:
        raise ValueError("An active workspace is required for retrieval commands")
    documents = WorkspaceDocumentStore(
        config.workspace_documents_dir / f"{workspace.id}.json",
        workspace.id,
    )
    retrieval = WorkspaceRetrievalStore(
        config.workspace_retrieval_dir / f"{workspace.id}.json",
        workspace.id,
    )
    return config, workspace, documents, retrieval


def _fail(exc: Exception) -> None:
    typer.echo(
        json.dumps({"ok": False, "error": str(exc)}, ensure_ascii=False),
        err=True,
    )
    raise typer.Exit(code=2) from exc


@app.command("ingest")
def ingest(document_id: str) -> None:
    """Read one registered text document through filesystem.read and index it."""
    try:
        config, workspace, documents, retrieval = _stores()
        document = documents.get(document_id)
        content = documents.read_text(
            document.id,
            PermissionStore(config.permissions_file),
        )
        indexed = retrieval.ingest(
            document_id=document.id,
            document_name=document.name,
            path=document.path,
            content=content,
        )
    except (ValueError, KeyError, FileNotFoundError, PermissionError, OSError) as exc:
        _fail(exc)
        return

    typer.echo(
        json.dumps(
            {
                "ok": True,
                "workspace": workspace.name,
                "document": {
                    "id": indexed.document_id,
                    "name": indexed.document_name,
                    "chunks": len(indexed.chunks),
                    "content_sha256": indexed.content_sha256,
                    "indexed_at": indexed.indexed_at,
                },
            },
            ensure_ascii=False,
        )
    )


@app.command("search")
def search(query: str, limit: int = typer.Option(4, min=1, max=8)) -> None:
    """Search the active Workspace's persisted indexed snapshots."""
    try:
        _, workspace, documents, retrieval = _stores()
        allowed_ids = {document.id for document in documents.list()}
        hits = retrieval.search(
            query,
            limit=limit,
            allowed_document_ids=allowed_ids,
        )
    except (ValueError, OSError) as exc:
        _fail(exc)
        return

    typer.echo(
        json.dumps(
            {
                "ok": True,
                "workspace": workspace.name,
                "hits": [asdict(hit) for hit in hits],
            },
            ensure_ascii=False,
        )
    )


@app.command("status")
def status() -> None:
    """Show indexed document/chunk counts for the active Workspace."""
    try:
        _, workspace, documents, retrieval = _stores()
        allowed_ids = {document.id for document in documents.list()}
        state = retrieval.status(allowed_document_ids=allowed_ids)
    except (ValueError, OSError) as exc:
        _fail(exc)
        return

    typer.echo(
        json.dumps(
            {
                "ok": True,
                "workspace": workspace.name,
                **state,
            },
            ensure_ascii=False,
        )
    )


@app.command("purge")
def purge(document_id: str) -> None:
    """Remove one document's persisted retrieval snapshot, not the source file."""
    try:
        _, workspace, _, retrieval = _stores()
        removed = retrieval.remove(document_id)
    except (ValueError, OSError) as exc:
        _fail(exc)
        return

    typer.echo(
        json.dumps(
            {
                "ok": True,
                "workspace": workspace.name,
                "document_id": document_id,
                "removed": removed,
            },
            ensure_ascii=False,
        )
    )


if __name__ == "__main__":
    app()
